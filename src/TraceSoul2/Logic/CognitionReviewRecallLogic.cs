using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>复盘前按本批完整材料检索旧理解；向量只找候选，是否修订由复盘模型判断。</summary>
    public static class CognitionReviewRecallLogic
    {
        public const int CandidateLimit = 12;
        public const int TextBudget = 8000;

        public static List<CognitionSliceRecord> Select(SqliteMemoryManager store, IReadOnlyList<MomentRecord> evidence,
            Func<IVectorEncoder> encoderFactory, IVectorCacheStore cache = null, string context = null,
            CancellationToken token = default, IReadOnlyList<string> queryOverride = null)
        {
            token.ThrowIfCancellationRequested();
            if (evidence == null || evidence.Count == 0) return new();
            var scope = evidence[0].ConversationId ?? "";
            var isPublic = evidence[0].MemoryVisibility == "public";
            if (evidence.Any(x => (x.ConversationId ?? "") != scope || (x.MemoryVisibility == "public") != isPublic))
                throw new InvalidOperationException("认知检索必须使用同一环境的证据。");
            // 环境与状态先过滤，再限制扫描窗口；其他环境的新节点不挤占候选。
            var nodes = store.GetCognitionReviewCandidates(scope, isPublic);
            if (nodes.Count == 0) return new();
            var encoder = encoderFactory?.Invoke() ?? throw new InvalidOperationException("认知复盘缺少语义编码器，未进行认知写入。");
            var queries = (queryOverride ?? evidence.Select(x => x.Content).Append(context).ToList()).SelectMany(Chunks)
                .Distinct(StringComparer.Ordinal).Select(x => Encode(encoder, x, VectorTextPurpose.Query, token)).ToList();
            if (queries.Count == 0) return new();
            var hits = new List<(CognitionSliceRecord Node, float[] Scores)>();
            foreach (var node in nodes)
            {
                token.ThrowIfCancellationRequested();
                var scores = Enumerable.Repeat(float.MinValue, queries.Count).ToArray();
                var part = 0;
                // 分块覆盖长正文和后面的范围/例外，避免编码器的token窗口只看到开头。
                foreach (var text in Chunks(node.Summary + "\n" + node.About + "\n" + node.Scope + "\n" + node.Exceptions))
                {
                    var id = "cognition-review/v1/" + node.Id + "/" + part++;
                    var hash = VectorMathUtil.Sha256(text);
                    float[] vector = null;
                    if (cache == null || !cache.TryGet(id, encoder.ModelId, hash, out vector) || !Valid(vector, encoder.Dimensions))
                    {
                        vector = Encode(encoder, text, VectorTextPurpose.Index, token);
                        cache?.Put(id, node.Id, "cognition-review", part - 1, encoder.ModelId, hash, vector);
                    }
                    for (var q = 0; q < queries.Count; q++)
                        scores[q] = Math.Max(scores[q], VectorMathUtil.Cosine(queries[q], vector));
                }
                hits.Add((node, scores));
            }
            // 每个材料片段先有一个候选名额，再补其余近邻；重复说同一件事不累加权重。
            var ranked = Enumerable.Range(0, queries.Count).Select(q => hits
                    .Where(x => x.Scores[q] >= 0.45f)
                    .OrderByDescending(x => x.Scores[q]).ThenBy(x => x.Node.Id, StringComparer.Ordinal)
                    .Take(3).Select(x => (x.Node, Score: x.Scores[q])).ToList()).ToList();
            var selected = new List<CognitionSliceRecord>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var chars = 2;
            for (var rank = 0; rank < 3; rank++)
                foreach (var hit in ranked.Where(x => x.Count > rank).Select(x => x[rank])
                    .OrderByDescending(x => x.Score).ThenBy(x => x.Node.Id, StringComparer.Ordinal))
                {
                    if (!seen.Add(hit.Node.Id)) continue;
                    var cost = Describe(hit.Node).Length + 1;
                    if (selected.Count >= CandidateLimit || chars + cost > TextBudget) continue;
                    selected.Add(hit.Node); chars += cost;
                }
            return selected;
        }

        public static string Describe(CognitionSliceRecord node) => TraceJson.ToJson(new {
            id = node.Id, summary = node.Summary, about = node.About, scope = node.Scope, exceptions = node.Exceptions,
            domains = node.Domains, status = node.Status, revision = node.Revision, identity_slot = node.IdentitySlot
        });

        private static IEnumerable<string> Chunks(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            // 96字符窗口、16字符重叠，给BGE的128 token上限和查询前缀留空间；不改原件或提示正文。
            for (var offset = 0; offset < text.Length; offset += 80)
            {
                var length = Math.Min(96, text.Length - offset);
                yield return text.Substring(offset, length);
                if (offset + length == text.Length) yield break;
            }
        }

        private static float[] Encode(IVectorEncoder encoder, string text, VectorTextPurpose purpose, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var vector = encoder.Encode(text, purpose);
            if (!Valid(vector, encoder.Dimensions)) throw new InvalidOperationException("认知语义向量无效，未进行认知写入。");
            return vector;
        }

        private static bool Valid(float[] vector, int dimensions) => vector != null && vector.Length == dimensions &&
            vector.Length > 0 && vector.All(float.IsFinite) && vector.Any(x => x != 0);
    }
}
