using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>出门办的事：按事由语义预选外出神经，不只认名字里的 search/web。</summary>
    public static class LeaveNerveLogic
    {
        public const float MinScore = 0.18f;

        private static readonly string[] BlockedPrefixes =
        {
            "identity.", "memory.", "time.", "inner.", "senses.", "dialogue."
        };

        public static bool IsCandidate(TraceContributionDescriptorData item)
        {
            if (item == null || item.Kind != TraceContributionKindValues.CallableNerve) return false;
            var id = item.Id ?? string.Empty;
            foreach (var prefix in BlockedPrefixes)
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return LooksLikeLeave(item);
        }

        public static bool LooksLikeLeave(TraceContributionDescriptorData item)
        {
            var blob = Blob(item).ToLowerInvariant();
            return blob.IndexOf("search", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("web", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("browse", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("fetch", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("lookup", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("query", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("外出", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("去查", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("去搜", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("上网", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("检索", StringComparison.Ordinal) >= 0;
        }

        public static async Task<TraceContributionDescriptorData> SelectAsync(
            IEnumerable<TraceContributionDescriptorData> catalog,
            string want,
            IEmbeddingService embedding,
            CancellationToken cancellationToken)
        {
            var candidates = (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .Where(IsCandidate)
                .ToList();
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1 || string.IsNullOrWhiteSpace(want))
                return candidates[0];

            if (embedding != null)
            {
                try
                {
                    var queryVector = await embedding.EmbedAsync(want, cancellationToken);
                    VectorMathUtil.NormalizeInPlace(queryVector);
                    var best = (TraceContributionDescriptorData)null;
                    var bestScore = float.MinValue;
                    foreach (var item in candidates)
                    {
                        var vector = await embedding.EmbedAsync(Blob(item), cancellationToken);
                        VectorMathUtil.NormalizeInPlace(vector);
                        var score = VectorMathUtil.Cosine(queryVector, vector);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = item;
                        }
                    }
                    if (best != null && bestScore >= MinScore) return best;
                }
                catch
                {
                    /* 退回字符向量 */
                }
            }

            return Select(catalog, want, new BagOfCharsVectorEncoder());
        }

        public static TraceContributionDescriptorData Select(
            IEnumerable<TraceContributionDescriptorData> catalog,
            string want,
            IVectorEncoder encoder)
        {
            var candidates = (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .Where(IsCandidate)
                .ToList();
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1 || string.IsNullOrWhiteSpace(want) || encoder == null)
                return candidates[0];

            var queryVector = encoder.Encode(want, VectorTextPurpose.Query);
            VectorMathUtil.NormalizeInPlace(queryVector);
            var scored = candidates.Select(item =>
            {
                var vector = encoder.Encode(Blob(item), VectorTextPurpose.Index);
                VectorMathUtil.NormalizeInPlace(vector);
                return new KeyValuePair<TraceContributionDescriptorData, float>(
                    item, VectorMathUtil.Cosine(queryVector, vector));
            })
            .OrderByDescending(x => x.Value)
            .ToList();
            if (scored.Count == 0) return null;
            if (scored[0].Value < MinScore)
            {
                var named = candidates.FirstOrDefault(LooksLikeNamedSearch);
                return named ?? scored[0].Key;
            }
            return scored[0].Key;
        }

        private static bool LooksLikeNamedSearch(TraceContributionDescriptorData item)
        {
            var blob = ((item.Id ?? string.Empty) + " " + (item.Provides ?? string.Empty))
                .ToLowerInvariant();
            return blob.IndexOf("search", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("web", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("browse", StringComparison.Ordinal) >= 0 ||
                   blob.IndexOf("fetch", StringComparison.Ordinal) >= 0;
        }

        private static string Blob(TraceContributionDescriptorData item)
        {
            if (item == null) return string.Empty;
            return string.Join(" ", new[]
            {
                item.DisplayName,
                item.Description,
                item.WhenToUse,
                item.Id,
                item.Provides
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
    }
}
