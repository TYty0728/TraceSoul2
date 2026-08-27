using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Prompts;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 工具检索：每轮用当前 Moment（拼此刻内心）对长尾可调用能力做向量预选，
    /// 命中才注入心智动态段；不命中则 prompt 与旧形状一致。全程本地，不多打模型。
    /// 极常用通道（文字/表情/图）与系统内部能力不进池——它们有自己的直注通道。
    /// </summary>
    public static class ToolLookupLogic
    {
        public const int CandidateCap = 2;
        public const float MinScore = 0.30f;
        public const float WindowFromBest = 0.14f;

        private static readonly ConcurrentDictionary<string, float[]> IndexVectors =
            new ConcurrentDictionary<string, float[]>(StringComparer.Ordinal);

        public static bool IsLookupEligible(TraceContributionDescriptorData item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id)) return false;
            if (!TraceContributionKindValues.IsBrainCallable(item.Kind)) return false;
            var id = item.Id.Trim();
            if (id.StartsWith("memory.", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("identity.", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("time.", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("dialogue.", StringComparison.OrdinalIgnoreCase))
                return false;
            var organ = MouthLogic.OrganOf(item);
            if (string.Equals(organ, BodyOrganValues.Text, StringComparison.Ordinal) ||
                string.Equals(organ, BodyOrganValues.Sticker, StringComparison.Ordinal) ||
                string.Equals(organ, BodyOrganValues.Image, StringComparison.Ordinal))
                return false;
            return true;
        }

        /// <summary>检索 query：当前消息为主，拼一句此刻内心，心跳轮也有东西可匹配。</summary>
        public static string BuildQuery(TraceTurnContext turn)
        {
            if (turn == null) return string.Empty;
            var content = turn.Moment == null ? string.Empty : (turn.Moment.Content ?? string.Empty).Trim();
            var builder = new StringBuilder(content);
            var runtime = turn.Services == null || turn.Services.Storage == null
                ? null
                : turn.Services.Storage.LoadOrCreateInnerRuntime(turn.ConversationId);
            var narrative = runtime == null ? string.Empty : (runtime.Narrative ?? string.Empty).Trim();
            if (narrative.Length > 0)
            {
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(narrative.Length <= 80 ? narrative : narrative.Substring(0, 80));
            }
            return builder.ToString();
        }

        public static async Task<List<ToolCandidateData>> SelectAsync(
            string query,
            IEmbeddingService embedding,
            IEnumerable<TraceContributionDescriptorData> catalog,
            CancellationToken cancellationToken)
        {
            if (embedding == null)
                return Select(query, new BagOfCharsVectorEncoder(), catalog);
            var pool = Pool(catalog);
            if (pool.Count == 0 || string.IsNullOrWhiteSpace(query))
                return new List<ToolCandidateData>();
            var queryVector = await embedding.EmbedAsync(query, cancellationToken);
            VectorMathUtil.NormalizeInPlace(queryVector);
            var scored = new List<KeyValuePair<TraceContributionDescriptorData, float>>();
            foreach (var item in pool)
            {
                var vector = await IndexVectorAsync(embedding, item, cancellationToken);
                var best = VectorMathUtil.Cosine(queryVector, vector);
                best = Math.Max(best, Blend(query, IndexText(item), best));
                scored.Add(new KeyValuePair<TraceContributionDescriptorData, float>(item, best));
            }
            return Take(scored, CandidateCap);
        }

        public static List<ToolCandidateData> Select(
            string query,
            IVectorEncoder encoder,
            IEnumerable<TraceContributionDescriptorData> catalog)
        {
            var pool = Pool(catalog);
            if (encoder == null || pool.Count == 0 || string.IsNullOrWhiteSpace(query))
                return new List<ToolCandidateData>();
            var queryVector = encoder.Encode(query, VectorTextPurpose.Query);
            VectorMathUtil.NormalizeInPlace(queryVector);
            var scored = pool.Select(item =>
            {
                var vector = encoder.Encode(IndexText(item), VectorTextPurpose.Index);
                VectorMathUtil.NormalizeInPlace(vector);
                var best = VectorMathUtil.Cosine(queryVector, vector);
                best = Math.Max(best, Blend(query, IndexText(item), best));
                return new KeyValuePair<TraceContributionDescriptorData, float>(item, best);
            }).ToList();
            return Take(scored, CandidateCap);
        }

        /// <summary>心智动态段的入选清单：id + 一行人能读的用途。</summary>
        public static string FormatForMind(IEnumerable<ToolCandidateData> candidates)
        {
            var list = (candidates ?? Enumerable.Empty<ToolCandidateData>())
                .Where(x => x != null && x.Descriptor != null).ToList();
            if (list.Count == 0) return string.Empty;
            var builder = new StringBuilder();
            builder.AppendLine(CorePrompts.Mind.ToolCandidatesHeader);
            foreach (var item in list)
                builder.AppendLine("- " + item.Descriptor.Id + "：" + OneLineOf(item.Descriptor));
            builder.AppendLine(CorePrompts.Mind.ToolCandidatesHint);
            return builder.ToString().TrimEnd();
        }

        private static List<TraceContributionDescriptorData> Pool(
            IEnumerable<TraceContributionDescriptorData> catalog)
        {
            return (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .Where(IsLookupEligible)
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
        }

        private static string IndexText(TraceContributionDescriptorData item)
        {
            var builder = new StringBuilder();
            builder.Append(item.DisplayName).Append('。');
            builder.Append(item.Description).Append('。');
            if (!string.IsNullOrWhiteSpace(item.WhenToUse))
                builder.Append("什么时候用：").Append(item.WhenToUse);
            return builder.ToString();
        }

        private static string OneLineOf(TraceContributionDescriptorData item)
        {
            var text = !string.IsNullOrWhiteSpace(item.Description) ? item.Description : item.DisplayName;
            text = string.Join(" ", (text ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            return text.Length <= 48 ? text : text.Substring(0, 48).TrimEnd();
        }

        private static async Task<float[]> IndexVectorAsync(
            IEmbeddingService embedding,
            TraceContributionDescriptorData item,
            CancellationToken cancellationToken)
        {
            var key = embedding.ModelId + "\n" + item.Id + "\n" + IndexText(item);
            float[] cached;
            if (IndexVectors.TryGetValue(key, out cached)) return cached;
            var vector = await embedding.EmbedAsync(IndexText(item), cancellationToken);
            VectorMathUtil.NormalizeInPlace(vector);
            return IndexVectors.GetOrAdd(key, vector);
        }

        private static float Blend(string query, string prototype, float cosine)
        {
            query = query ?? string.Empty;
            prototype = prototype ?? string.Empty;
            if (query.Length == 0 || prototype.Length == 0) return cosine;
            var display = prototype.Split('。')[0];
            if (display.Length >= 2 &&
                (query.IndexOf(display, StringComparison.Ordinal) >= 0 ||
                 display.IndexOf(query, StringComparison.Ordinal) >= 0))
                return Math.Max(cosine, 0.99f);
            return cosine;
        }

        private static List<ToolCandidateData> Take(
            List<KeyValuePair<TraceContributionDescriptorData, float>> scored, int cap)
        {
            cap = Math.Max(1, Math.Min(CandidateCap, cap));
            var ordered = (scored ?? new List<KeyValuePair<TraceContributionDescriptorData, float>>())
                .OrderByDescending(x => x.Value)
                .ToList();
            if (ordered.Count == 0 || ordered[0].Value < MinScore)
                return new List<ToolCandidateData>();
            var floor = Math.Max(MinScore, ordered[0].Value - WindowFromBest);
            return ordered
                .Where(x => x.Value + 0.0001f >= floor)
                .Take(cap)
                .Select(x => new ToolCandidateData(x.Key, x.Value))
                .ToList();
        }
    }
}
