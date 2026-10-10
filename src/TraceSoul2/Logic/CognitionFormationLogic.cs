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
            => ValidationError(writes, evidence, existing, tags) == null;

        /// <summary>与写入校验共用规则；只返回字段路径和修正要求，不回显正文或未知ID。</summary>
        public static string ValidationError(IEnumerable<BrainCognitionWriteData> writes, IReadOnlyList<MomentRecord> evidence,
            IReadOnlyList<CognitionSliceRecord> existing, IReadOnlyList<LifeTagRecord> tags)
        {
            if (writes == null) return "cognitions 必须是数组；无变化请显式输出 []，不能省略或填 null。";
            var list = writes.ToList();
            if (list.Count > 3) return "cognitions 最多3条操作；请只保留本日有依据的变化。";
            var moments = new HashSet<string>((evidence ?? Array.Empty<MomentRecord>()).Select(x => x.Id));
            var nodes = new HashSet<string>((existing ?? Array.Empty<CognitionSliceRecord>()).Select(x => x.Id));
            var tagIds = new HashSet<string>((tags ?? Array.Empty<LifeTagRecord>()).Select(x => x.Id));
            var changedTargets = new HashSet<string>();
            var unavailable = new HashSet<string>();
            for (var i = 0; i < list.Count; i++)
            {
                var w = list[i];
                var path = "cognitions[" + i + "]";
                if (w == null) return path + " 必须是对象，不能填 null。";
                if (!PuzzleViewLogic.IdentitySlot(w.identity_slot))
                    return path + ".identity_slot 必须为 self/personality/expression_habit/other/relation 或空字符串。";
                if (!string.IsNullOrEmpty(w.identity_slot) && w.identity_slot is "self" or "personality" or "expression_habit" &&
                    !(w.domains ?? new List<string>()).Contains("ass"))
                    return path + ".domains：self/personality/expression_habit 用途必须包含 ass 领域；请核对用途与领域。";
                if (!new[] { "create", "reinforce", "weaken", "revise", "retire", "link" }.Contains(w.operation))
                    return path + ".operation 必须为 create/reinforce/weaken/revise/retire/link。";
                if (w.evidence_moment_ids == null || w.evidence_moment_ids.Count == 0 || w.evidence_moment_ids.Count > 16)
                    return path + ".evidence_moment_ids 必须有1～16个本批原始Moment ID；无真实依据请移除该操作。";
                for (var j = 0; j < w.evidence_moment_ids.Count; j++)
                    if (!moments.Contains(w.evidence_moment_ids[j]))
                        return path + ".evidence_moment_ids[" + j + "] 未在本批原始经历中展示。请核对所有证据引用，只复制确实支持该理解的完整Moment ID；无依据请移除该操作，不能编造或拿任意原句兜底。";
                if ((w.evidence_fact_ids?.Count ?? 0) > 0)
                    return path + ".evidence_fact_ids 必须为空数组；本任务使用 evidence_moment_ids 引用本批原始经历。";
                if (w.operation != "create" && !nodes.Contains(w.target_id ?? ""))
                    return path + ".target_id 必须从本次展示的现有认知中复制完整ID；不能填名称或自造编号。";
                if (unavailable.Contains(w.target_id ?? ""))
                    return path + ".target_id 已在本批前面的操作中 retire/revise，后续不能继续操作旧节点。";
                if (w.operation == "link" && unavailable.Contains(w.related_id ?? ""))
                    return path + ".related_id 已在本批前面的操作中 retire/revise，不能再关联旧节点。";
                if (w.operation != "create" && w.operation != "link" && !changedTargets.Add(w.target_id))
                    return path + ".target_id 在本批被重复修改；请将同一目标的修改合为一次操作。";
                if (w.operation == "create" || w.operation == "revise")
                {
                    if (string.IsNullOrWhiteSpace(w.summary)) return path + ".summary 必须是非空的完整理解。";
                    if (w.summary.Length > 600) return path + ".summary 当前" + w.summary.Length + "字，最多600字；请保留范围和例外，重新写短。";
                    if (w.domains == null || w.domains.Count == 0 || w.domains.Count > 4)
                        return path + ".domains 必须为 user/world/ass/relation 的非空子集，最多4项。";
                }
                for (var j = 0; j < (w.domains?.Count ?? 0); j++)
                    if (!LifeRouteValues.IsDomain(w.domains[j]))
                        return path + ".domains[" + j + "] 必须为 user/world/ass/relation 之一。";
                for (var j = 0; j < (w.tag_ids?.Count ?? 0); j++)
                    if (!tagIds.Contains(w.tag_ids[j]))
                        return path + ".tag_ids[" + j + "] 不在本次展示的生命标签列表中。请核对所有操作的 tag_ids，从列表每行竖线左侧复制完整ID；不能填右侧名称，也不能拼接 concept.life. 与名称。没有合适标签可填 []；保持其他有效内容不变。";
                if (float.IsNaN(w.confidence) || w.confidence < 0 || w.confidence > 1)
                    return path + ".confidence 必须为0～1的有限数字。";
                if (float.IsNaN(w.strength) || w.strength < 0 || w.strength > 1)
                    return path + ".strength 必须为0～1的有限数字。";
                if ((w.about?.Length ?? 0) > 160) return path + ".about 当前" + w.about.Length + "字，最多160字；请重新写短。";
                if ((w.scope?.Length ?? 0) > 300) return path + ".scope 当前" + w.scope.Length + "字，最多300字；请重新写短。";
                if ((w.exceptions?.Length ?? 0) > 300) return path + ".exceptions 当前" + w.exceptions.Length + "字，最多300字；请重新写短。";
                if (w.operation == "link")
                {
                    if (w.target_id == w.related_id) return path + ".related_id 不能与 target_id 相同，不能关联自身。";
                    if (!nodes.Contains(w.related_id ?? "")) return path + ".related_id 必须从本次展示的现有认知中复制完整ID。";
                    if (!new[] { "related_to", "abstracts", "exemplifies", "contradicts" }.Contains(w.relation))
                        return path + ".relation 必须为 related_to/abstracts/exemplifies/contradicts。";
                }
                if (w.operation == "retire" || w.operation == "revise") unavailable.Add(w.target_id);
            }
            return null;
        }
    }
}
