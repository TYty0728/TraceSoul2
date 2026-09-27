using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;

namespace TraceSoul2.Manager
{
    public sealed partial class SqliteMemoryManager
    {
        public List<CognitionSliceRecord> GetCognitionNodes(int take) => connection.Table<CognitionSliceRecord>()
            .OrderByDescending(x => x.UpdatedUnixMs).Take(Math.Max(0, Math.Min(5000, take))).ToList();

        public List<CognitionEdgeRecord> GetCognitionEdges(IEnumerable<string> nodeIds)
        {
            var result = new Dictionary<string, CognitionEdgeRecord>();
            foreach (var id in (nodeIds ?? Array.Empty<string>()).Distinct().Take(64))
                foreach (var edge in connection.Table<CognitionEdgeRecord>()
                    .Where(x => x.FromCognitionId == id || x.ToCognitionId == id).Take(64)) result[edge.Id] = edge;
            return result.Values.ToList();
        }

        public List<CognitionEvidenceRecord> GetCognitionEvidence(IEnumerable<string> nodeIds)
        {
            var result = new List<CognitionEvidenceRecord>();
            foreach (var id in (nodeIds ?? Array.Empty<string>()).Distinct().Take(64))
            {
                // 支持证据再多，也不能挤掉后来出现的反证。
                result.AddRange(connection.Table<CognitionEvidenceRecord>()
                    .Where(x => x.CognitionId == id && x.Relation == "challenges")
                    .OrderByDescending(x => x.CreatedUnixMs).Take(8));
                result.AddRange(connection.Table<CognitionEvidenceRecord>()
                    .Where(x => x.CognitionId == id && x.Relation != "challenges")
                    .OrderByDescending(x => x.CreatedUnixMs).Take(8));
            }
            return result;
        }

        public List<MomentRecord> GetEvidenceMoments(IEnumerable<string> momentIds) =>
            (momentIds ?? Array.Empty<string>()).Distinct().Take(256)
            .Select(id => connection.Find<MomentRecord>(id)).Where(x => x != null && x.MemoryStatus != "operational").ToList();

        private List<CognitionEvidenceRecord> ResolveCognitionEvidence(BrainCognitionWriteData write, string trigger)
        {
            var result = new List<CognitionEvidenceRecord>();
            var moments = (write.evidence_moment_ids ?? new List<string>()).Distinct().ToList();
            var facts = (write.evidence_fact_ids ?? new List<string>()).Distinct().ToList();
            // 兼容旧调用：仅未指定任何证据时才使用真实触发 Moment；显式错误 ID 不兜底。
            if (moments.Count == 0 && facts.Count == 0) moments.Add(trigger);
            if (moments.Count + facts.Count > 16) throw new InvalidOperationException("认知证据数量超过 16。");
            foreach (var id in moments)
            {
                var moment = string.IsNullOrWhiteSpace(id) ? null : connection.Find<MomentRecord>(id);
                if (moment == null || moment.MemoryStatus == "operational")
                    throw new InvalidOperationException("认知引用了不存在或非经历的 Moment。");
                result.Add(new CognitionEvidenceRecord { MomentId = id, FactId = "" });
            }
            foreach (var id in facts)
            {
                var fact = string.IsNullOrWhiteSpace(id) ? null : connection.Find<FactSliceRecord>(id);
                var moment = fact == null ? null : connection.Find<MomentRecord>(fact.SourceMomentId);
                if (fact == null || moment == null || moment.MemoryStatus == "operational" || fact.Status != "active")
                    throw new InvalidOperationException("认知引用了无效事实或缺少真实来源。");
                result.Add(new CognitionEvidenceRecord { FactId = id, MomentId = fact.SourceMomentId });
            }
            return result;
        }

        private CognitionSliceRecord ApplyCognitionMutation(BrainCognitionWriteData write, string trigger, long now)
        {
            if (write == null) return null;
            var op = (write.operation ?? "").Trim().ToLowerInvariant();
            if (!new[] { "create", "reinforce", "weaken", "revise", "retire", "link" }.Contains(op)) return null;
            var target = string.IsNullOrWhiteSpace(write.target_id) ? null : connection.Find<CognitionSliceRecord>(write.target_id);
            if (op != "create" && (target == null || !PuzzleDomains.Live(target.Status)))
                throw new InvalidOperationException("认知操作目标不存在或已失效。");
            if (float.IsNaN(write.confidence) || float.IsInfinity(write.confidence) ||
                float.IsNaN(write.strength) || float.IsInfinity(write.strength))
                throw new InvalidOperationException("认知权重必须是有限数值。");
            if ((write.domains ?? new List<string>()).Any(x => !LifeRouteValues.IsDomain(x)))
                throw new InvalidOperationException("认知领域只能是 user/world/ass/relation。");
            var evidence = ResolveCognitionEvidence(write, trigger);
            var relation = op == "weaken" || op == "retire" ? "challenges" : "supports";
            if (op == "link")
            {
                var other = string.IsNullOrWhiteSpace(write.related_id) ? null : connection.Find<CognitionSliceRecord>(write.related_id);
                if (other == null || !PuzzleDomains.Live(other.Status) || other.Id == target.Id ||
                    !new[] { "related_to", "abstracts", "exemplifies", "contradicts" }.Contains(write.relation))
                    throw new InvalidOperationException("认知关联目标或关系无效。");
                SaveCognitionEdge(target.Id, other.Id, write.relation, now);
                // 关联的依据不等于节点内容再次被证明。
                AddGraphEvidence(target.Id, evidence, "link:" + write.relation + ":" + other.Id);
                return target;
            }
            if (op == "reinforce" || op == "weaken" || op == "retire")
            {
                // 重放相同证据不能反复改变置信与强度。
                if (op != "retire" && evidence.All(e => connection.Table<CognitionEvidenceRecord>().Any(x =>
                    x.CognitionId == target.Id && x.MomentId == e.MomentId && x.Relation == relation)))
                {
                    AddGraphEvidence(target.Id, evidence, relation);
                    return target;
                }
                var confidence = Clamp01(write.confidence);
                target.Confidence = op == "reinforce" ? Math.Max(target.Confidence, confidence) : Math.Min(target.Confidence, confidence);
                target.Strength = Clamp01(target.Strength + (op == "reinforce" ? 0.08f : -0.08f));
                target.Status = op == "retire" ? "retired" : op == "weaken" ? "weakened" : "active";
                if (op == "reinforce") target.LastConfirmedUnixMs = now; else target.LastChallengedUnixMs = now;
                target.Revision++; target.UpdatedUnixMs = now;
                connection.Update(target); AddGraphEvidence(target.Id, evidence, relation);
                return target;
            }
            var summary = LoadPairIdentity().RewriteRecordedText((write.summary ?? "").Trim());
            if (summary.Length == 0 || summary.Length > 600) throw new InvalidOperationException("认知正文须为 1～600 字。");
            var domains = PuzzleDomains.Pack(write.domains);
            if (domains.Length == 0 && target != null) domains = target.Domains;
            var tags = (write.tag_ids ?? new List<string>()).Distinct().Take(8)
                .Where(id => connection.Find<LifeTagRecord>(id) != null).ToList();
            // 旧记录可沿用 Tag；新认知只要有领域与证据即可，不受标签生成进度阻塞。
            if (domains.Length == 0 && tags.Count == 0) throw new InvalidOperationException("认知需要领域或有效旧标签。");
            var about = Limit((write.about ?? "").Trim(), 160);
            var scope = Limit((write.scope ?? "").Trim(), 300);
            var exceptions = Limit((write.exceptions ?? "").Trim(), 300);
            var existing = op == "create" ? connection.Table<CognitionSliceRecord>()
                .Where(x => x.Summary == summary && x.Domains == domains && (x.Status == "active" || x.Status == "weakened") &&
                    x.About == about && x.Scope == scope && x.Exceptions == exceptions).FirstOrDefault() : null;
            if (existing != null) { AddGraphEvidence(existing.Id, evidence, "supports"); return existing; }
            var node = new CognitionSliceRecord
            {
                Id = Guid.NewGuid().ToString("N"), OwnerId = "ass", Domains = domains,
                About = about, Scope = scope, Exceptions = exceptions, Summary = summary,
                Subtype = string.IsNullOrWhiteSpace(write.subtype) ? "standard" : Limit(write.subtype, 40),
                Confidence = Clamp01(write.confidence), Strength = Clamp01(write.strength > 0 ? write.strength : write.confidence),
                Status = "active", Revision = target == null ? 0 : target.Revision + 1,
                CreatedUnixMs = now, UpdatedUnixMs = now, LastConfirmedUnixMs = now
            };
            connection.Insert(node);
            foreach (var id in tags) connection.Insert(new CognitionTagLinkRecord
                { Id = node.Id + "|" + id, CognitionId = node.Id, TagId = id, Weight = 1f });
            AddGraphEvidence(node.Id, evidence, "supports");
            if (op == "revise")
            {
                target.Status = "superseded"; target.UpdatedUnixMs = now; target.LastChallengedUnixMs = now;
                connection.Update(target); AddGraphEvidence(target.Id, evidence, "challenges");
                SaveCognitionEdge(node.Id, target.Id, "revises", now);
            }
            foreach (var cue in (write.trace_cues ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Limit(x.Trim(), 40)).Distinct().Take(5))
                connection.Insert(new CognitionCueRecord { Id = Guid.NewGuid().ToString("N"), CognitionId = node.Id,
                    Cue = cue, AssociationStrength = Clamp01(write.association_strength),
                    SourceMomentId = evidence[0].MomentId, CreatedUnixMs = now });
            return node;
        }

        private void SaveCognitionEdge(string from, string to, string relation, long now)
        {
            var id = from + "|" + relation + "|" + to;
            if (connection.Find<CognitionEdgeRecord>(id) == null)
                connection.Insert(new CognitionEdgeRecord { Id = id, FromCognitionId = from, ToCognitionId = to,
                    Relation = relation, Weight = 1f, CreatedUnixMs = now });
        }
        private bool HasGraphEvidence(string node, CognitionEvidenceRecord e, string relation) =>
            connection.Table<CognitionEvidenceRecord>().Any(x => x.CognitionId == node && x.MomentId == e.MomentId &&
                x.FactId == e.FactId && x.Relation == relation);
        private void AddGraphEvidence(string node, IEnumerable<CognitionEvidenceRecord> evidence, string relation)
        {
            foreach (var e in evidence)
            {
                if (HasGraphEvidence(node, e, relation)) continue;
                connection.Insert(new CognitionEvidenceRecord { Id = Guid.NewGuid().ToString("N"), CognitionId = node,
                    MomentId = e.MomentId, FactId = e.FactId, Relation = relation, Weight = 1f,
                    CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            }
        }
    }
}
