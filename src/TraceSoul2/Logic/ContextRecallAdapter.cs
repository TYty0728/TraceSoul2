using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TraceSoul2.Manager;
using TraceSoul2.Data;


namespace TraceSoul2.Logic
{
    /// <summary>跨记忆、认知、内心和器官的统一召回查询。</summary>
    public sealed class ContextRecallQuery
    {
        public string Text { get; set; } = string.Empty;
        public string Scene { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public IReadOnlyList<string> Cues { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> RelatedIds { get; set; } = Array.Empty<string>();
        public int MaxItems { get; set; } = 8;
        public int MaxChars { get; set; } = 2400;
    }

    /// <summary>来源返回的领域无关候选；RenderedText 是给模型看的自然语言。</summary>
    public sealed class ContextRecallCandidate
    {
        public string Id { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string RenderedText { get; set; } = string.Empty;
        public float Relevance { get; set; }
        public float Strength { get; set; }
        public float Confidence { get; set; }
        public float Freshness { get; set; }
        public bool ContradictsCurrent { get; set; }
        public IReadOnlyList<string> RelatedIds { get; set; } = Array.Empty<string>();
    }

    public interface IContextRecallSource
    {
        string SourceId { get; }
        IReadOnlyList<ContextRecallCandidate> Retrieve(ContextRecallQuery query);
    }

    /// <summary>将已有领域召回函数适配为统一来源，迁移期间避免重写旧逻辑。</summary>
    public sealed class DelegateContextRecallSource : IContextRecallSource
    {
        private readonly Func<ContextRecallQuery, IReadOnlyList<ContextRecallCandidate>> retrieve;
        public string SourceId { get; private set; }
        public DelegateContextRecallSource(string sourceId,
            Func<ContextRecallQuery, IReadOnlyList<ContextRecallCandidate>> retrieve)
        {
            SourceId = sourceId ?? string.Empty;
            this.retrieve = retrieve;
        }
        public IReadOnlyList<ContextRecallCandidate> Retrieve(ContextRecallQuery query)
        {
            return retrieve == null ? Array.Empty<ContextRecallCandidate>() :
                retrieve(query) ?? Array.Empty<ContextRecallCandidate>();
        }
    }

    /// <summary>把认知网的 Tag/Cue 召回转换为通用候选。</summary>
    public sealed class CognitionContextRecallSource : IContextRecallSource
    {
        private readonly IMemoryStore store;
        public string SourceId { get { return "cognition"; } }
        public CognitionContextRecallSource(IMemoryStore store) { this.store = store; }

        public IReadOnlyList<ContextRecallCandidate> Retrieve(ContextRecallQuery query)
        {
            if (store == null || query == null || query.MaxItems <= 0 || query.MaxChars <= 0)
                return Array.Empty<ContextRecallCandidate>();
            var graph = store as ICognitionGraphStore;
            var nodes = graph?.GetCognitionNodes(5000) ?? new List<CognitionSliceRecord>();
            var seeds = new Dictionary<string, float>();
            foreach (var id in (query.RelatedIds ?? Array.Empty<string>()).Take(6)) seeds[id] = 0.35f;
            foreach (var c in store.GetCognitionCandidates(query.Cues, 40)) seeds[c.Id] = 0.65f;
            foreach (var hit in store.FindCognitionsByCue(query.Text, 20))
            {
                seeds[hit.Cognition.Id] = Math.Max(0.7f, hit.AssociationStrength);
                if (nodes.All(x => x.Id != hit.Cognition.Id)) nodes.Add(hit.Cognition);
            }
            if (graph == null)
                nodes.AddRange(store.GetCognitionCandidates(query.Cues, 40).Where(c => nodes.All(x => x.Id != c.Id)));
            var terms = Terms(query.Text);
            foreach (var c in nodes.Where(c => PuzzleDomains.Live(c.Status)))
            {
                var targetTerms = Terms(c.Summary + " " + c.About + " " + c.Scope);
                var matches = terms.Count(targetTerms.Contains);
                var score = terms.Count == 0 || targetTerms.Count == 0 ? 0f :
                    (float)matches / Math.Min(terms.Count, targetTerms.Count);
                if (score >= 0.18f) seeds[c.Id] = Math.Max(seeds.TryGetValue(c.Id, out var prior) ? prior : 0, score);
            }
            var byId = nodes.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
            var selected = seeds.Where(x => byId.ContainsKey(x.Key)).OrderByDescending(x => x.Value)
                .Take(Math.Min(24, query.MaxItems * 2)).ToDictionary(x => x.Key, x => x.Value);
            var edges = graph?.GetCognitionEdges(selected.Keys) ?? new List<CognitionEdgeRecord>();
            var conflicts = new HashSet<string>();
            // 一跳有界扩展：相关、抽象、反证和新版替代关系。禁止无限漫游。
            var seedScores = new Dictionary<string, float>(selected);
            foreach (var e in edges)
            {
                var source = seedScores.ContainsKey(e.FromCognitionId) ? e.FromCognitionId : e.ToCognitionId;
                var other = source == e.FromCognitionId ? e.ToCognitionId : e.FromCognitionId;
                if (!seedScores.TryGetValue(source, out var relevance) || !byId.ContainsKey(other)) continue;
                if (e.Relation == "contradicts") { conflicts.Add(source); conflicts.Add(other); }
                if (!selected.ContainsKey(other)) selected[other] = Math.Max(0.20f, relevance * 0.85f);
            }
            var evidence = graph?.GetCognitionEvidence(selected.Keys) ?? new List<CognitionEvidenceRecord>();
            var moments = (graph?.GetEvidenceMoments(evidence.Select(x => x.MomentId)) ?? new List<MomentRecord>())
                .ToDictionary(x => x.Id);
            return selected.Where(x => byId.ContainsKey(x.Key) && PuzzleDomains.Live(byId[x.Key].Status))
                .Select(x =>
                {
                    var c = byId[x.Key];
                    var refs = evidence.Where(e => e.CognitionId == c.Id).ToList();
                    var text = "[认知:" + c.Id + "｜" + PuzzleDomains.Label(c.Domains) + "] 我目前理解：" + c.Summary;
                    if (!string.IsNullOrWhiteSpace(c.Scope)) text += "；适用范围：" + c.Scope;
                    if (!string.IsNullOrWhiteSpace(c.Exceptions)) text += "；例外：" + c.Exceptions;
                    text += "；置信=" + c.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    if (c.Status == "weakened") text += "；已有反证，理解已削弱";
                    if (conflicts.Contains(c.Id)) text += "；与相关理解有冲突，需对照判断";
                    var connected = edges.Where(e => e.FromCognitionId == c.Id || e.ToCognitionId == c.Id).Take(4).ToList();
                    foreach (var e in connected) text += "；关联 " + e.Relation + ":" + (e.FromCognitionId == c.Id ? e.ToCognitionId : e.FromCognitionId);
                    // 两条摘要优先分别保留反证和支持，关联依据不能冒充内容被证明。
                    var shownEvidence = refs.Where(e => e.Relation == "challenges").Take(1)
                        .Concat(refs.Where(e => e.Relation == "supports").Take(1)).ToList();
                    if (shownEvidence.Count == 0) shownEvidence.AddRange(refs.Take(2));
                    foreach (var e in shownEvidence)
                    {
                        text += "；依据 " + e.Relation + " moment:" + e.MomentId;
                        if (moments.TryGetValue(e.MomentId ?? "", out var m))
                            text += " [" + m.Realm + "/" + m.EvidenceType + "] " + Clip(m.Content, 100);
                        else text += "（原始来源当前不可读取，不能当作已核实）";
                    }
                    if (refs.Count == 0) text += "；旧认知尚无可追溯证据";
                    return new ContextRecallCandidate { Id = c.Id, SourceId = "cognition", Kind = c.Subtype,
                        Text = c.Summary, RenderedText = text, Relevance = x.Value, Strength = c.Strength,
                        Confidence = c.Confidence, Freshness = 0, ContradictsCurrent = conflicts.Contains(c.Id) || c.Status == "weakened",
                        RelatedIds = connected.Select(e => e.FromCognitionId == c.Id ? e.ToCognitionId : e.FromCognitionId).ToList() };
                }).ToList();
        }
        // 本地双字片段匹配兜底；语义入口仍由既有 Tag 路由负责，避免32维字符哈希碰撞召回无关认知。
        private static HashSet<string> Terms(string value)
        {
            var terms = new HashSet<string>(StringComparer.Ordinal);
            var run = new StringBuilder();
            foreach (var ch in (value ?? "").ToLowerInvariant() + " ")
            {
                if (char.IsLetterOrDigit(ch)) { run.Append(ch); continue; }
                for (var i = 0; i + 1 < run.Length; i++) terms.Add(run.ToString(i, 2));
                run.Clear();
            }
            return terms;
        }
        private static string Clip(string value, int max) => (value ?? "").Length <= max ? value ?? "" : value.Substring(0, max) + "…";
    }

    /// <summary>
    /// 统一做候选去重、语义阈值、冲突保留和字符预算；不决定领域数据如何检索。
    /// </summary>
    public sealed class ContextRecallAdapter
    {
        public float MinimumRelevance { get; set; } = 0.18f;
        public float ContradictionFloor { get; set; } = 0.30f;

        public IReadOnlyList<ContextRecallCandidate> Recall(
            ContextRecallQuery query, IEnumerable<IContextRecallSource> sources)
        {
            query = query ?? new ContextRecallQuery();
            if (query.MaxItems <= 0 || query.MaxChars <= 0) return Array.Empty<ContextRecallCandidate>();
            var all = new List<ContextRecallCandidate>();
            foreach (var source in sources ?? Enumerable.Empty<IContextRecallSource>())
            {
                if (source == null) continue;
                try { all.AddRange(source.Retrieve(query) ?? Array.Empty<ContextRecallCandidate>()); }
                catch { /* 单个可选来源故障不应使整轮对话失败。 */ }
            }

            var ranked = all.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Text) &&
                                        x.Relevance >= MinimumRelevance)
                .GroupBy(x => x.SourceId + ":" + (string.IsNullOrWhiteSpace(x.Id) ? x.Text : x.Id),
                          StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(Score).First())
                .OrderByDescending(Score)
                .ThenByDescending(x => x.Freshness)
                .ToList();

            var selected = new List<ContextRecallCandidate>();
            var chars = 0;
            // 至少给一条达到相关阈值的反证候选参与预算选择的机会。
            var conflict = ranked.FirstOrDefault(x => x.ContradictsCurrent && x.Relevance >= ContradictionFloor);
            if (conflict != null) { ranked.Remove(conflict); ranked.Insert(0, conflict); }
            foreach (var item in ranked)
            {
                var text = string.IsNullOrWhiteSpace(item.RenderedText) ? item.Text : item.RenderedText;
                var cost = text.Trim().Length + 2 + Environment.NewLine.Length;
                if (chars + cost > query.MaxChars) continue;
                if (selected.Count >= query.MaxItems) break;
                selected.Add(item);
                chars += cost;
            }
            return selected;
        }

        public string RenderNatural(string heading, IReadOnlyList<ContextRecallCandidate> selected)
        {
            if (selected == null || selected.Count == 0) return string.Empty;
            var b = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(heading)) b.AppendLine(heading.Trim());
            foreach (var item in selected)
            {
                var text = string.IsNullOrWhiteSpace(item.RenderedText) ? item.Text : item.RenderedText;
                if (string.IsNullOrWhiteSpace(text)) continue;
                b.Append("- ").AppendLine(text.Trim());
            }
            return b.ToString().TrimEnd();
        }

        private float Score(ContextRecallCandidate x)
        {
            var score = Math.Max(0f, x.Relevance) * 0.55f +
                        Math.Max(0f, x.Strength) * 0.20f +
                        Math.Max(0f, x.Confidence) * 0.15f +
                        Math.Max(0f, x.Freshness) * 0.10f;
            // 情绪或当前理解不一致不是降低证据相关度的理由。
            return score;
        }
    }
}
