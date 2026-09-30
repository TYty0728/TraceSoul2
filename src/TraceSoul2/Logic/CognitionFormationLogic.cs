using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;

namespace TraceSoul2.Logic
{
    /// <summary>日构建认知输出只可引用此次实际提供的原始经历，不用最后一句代替所有依据。</summary>
    public static class CognitionFormationLogic
    {
        public static List<MomentRecord> SelectEvidence(IEnumerable<MomentRecord> moments)
        {
            var result = new List<MomentRecord>();
            var chars = 0;
            foreach (var m in (moments ?? Array.Empty<MomentRecord>()).Where(x => x != null &&
                x.MemoryStatus != "operational" && !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Content))
                .GroupBy(x => x.Id).Select(x => x.First()).OrderByDescending(x => x.CreatedUnixMs))
            {
                // 只提供完整消息，不能把截断原文当成完整依据。
                var cost = m.Content.Length + m.Id.Length + 120;
                if (chars + cost > 24000) continue;
                result.Add(m); chars += cost;
                if (result.Count == 200) break;
            }
            return result.OrderBy(x => x.CreatedUnixMs).ToList();
        }

        public static bool Valid(IEnumerable<BrainCognitionWriteData> writes, IReadOnlyList<MomentRecord> evidence,
            IReadOnlyList<CognitionSliceRecord> existing, IReadOnlyList<LifeTagRecord> tags)
        {
            if (writes == null) return false;
            var list = writes.ToList();
            if (list.Count > 3) return false;
            var moments = new HashSet<string>((evidence ?? Array.Empty<MomentRecord>()).Select(x => x.Id));
            var nodes = new HashSet<string>((existing ?? Array.Empty<CognitionSliceRecord>()).Select(x => x.Id));
            var tagIds = new HashSet<string>((tags ?? Array.Empty<LifeTagRecord>()).Select(x => x.Id));
            var changedTargets = new HashSet<string>();
            var unavailable = new HashSet<string>();
            foreach (var w in list)
            {
                if (w == null || !PuzzleViewLogic.IdentitySlot(w.identity_slot)) return false;
                if (!string.IsNullOrEmpty(w.identity_slot) && w.identity_slot is "self" or "personality" or "expression_habit" &&
                    !(w.domains ?? new List<string>()).Contains("ass")) return false;
                if (!new[] { "create", "reinforce", "weaken", "revise", "retire", "link" }.Contains(w.operation)) return false;
                if (w.evidence_moment_ids == null || w.evidence_moment_ids.Count == 0 || w.evidence_moment_ids.Count > 16 ||
                    w.evidence_moment_ids.Any(id => !moments.Contains(id)) || (w.evidence_fact_ids?.Count ?? 0) > 0) return false;
                if (w.operation != "create" && !nodes.Contains(w.target_id ?? "")) return false;
                if (unavailable.Contains(w.target_id ?? "") ||
                    (w.operation == "link" && unavailable.Contains(w.related_id ?? ""))) return false;
                if (w.operation != "create" && w.operation != "link" && !changedTargets.Add(w.target_id)) return false;
                if (w.operation == "create" || w.operation == "revise")
                {
                    if (string.IsNullOrWhiteSpace(w.summary) || w.summary.Length > 600 ||
                        w.domains == null || w.domains.Count == 0 || w.domains.Count > 4) return false;
                }
                if ((w.domains ?? new List<string>()).Any(x => !LifeRouteValues.IsDomain(x)) ||
                    (w.tag_ids ?? new List<string>()).Any(x => !tagIds.Contains(x))) return false;
                if (float.IsNaN(w.confidence) || w.confidence < 0 || w.confidence > 1 ||
                    float.IsNaN(w.strength) || w.strength < 0 || w.strength > 1) return false;
                if ((w.about?.Length ?? 0) > 160 || (w.scope?.Length ?? 0) > 300 || (w.exceptions?.Length ?? 0) > 300) return false;
                if (w.operation == "link" && (w.target_id == w.related_id || !nodes.Contains(w.related_id ?? "") ||
                    !new[] { "related_to", "abstracts", "exemplifies", "contradicts" }.Contains(w.relation))) return false;
                if (w.operation == "retire" || w.operation == "revise") unavailable.Add(w.target_id);
            }
            return true;
        }
    }
}
