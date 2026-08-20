using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 分层多标签导航：域 -> 维度 -> 概念。它只给图召回提供激活种子，
    /// 不直接把命中的文本当成最终记忆返回给 LLM。
    /// </summary>
    public sealed class HierarchicalVectorRouterLogic : IHierarchicalVectorRouter
    {
        private sealed class IndexedNode
        {
            public VectorIndexNode Node;
            public float[] Definition;
            public readonly List<float[]> Positive = new List<float[]>();
            public readonly List<float[]> Negative = new List<float[]>();
        }

        private readonly IVectorEncoder encoder;
        private readonly IVectorCacheStore cache;
        private readonly List<IndexedNode> indexed = new List<IndexedNode>();

        public int NodeCount { get { return indexed.Count; } }

        public HierarchicalVectorRouterLogic(IVectorEncoder encoder, IVectorCacheStore cache = null)
        {
            this.encoder = encoder ?? throw new ArgumentNullException("encoder");
            this.cache = cache;
        }

        public void Build(IEnumerable<VectorIndexNode> nodes)
        {
            if (nodes == null) throw new ArgumentNullException("nodes");
            indexed.Clear();
            foreach (var node in nodes)
            {
                var item = new IndexedNode
                {
                    Node = node,
                    Definition = EncodeCached(node, "definition", -1, node.Definition)
                };

                for (var i = 0; i < node.PositiveExamples.Count; i++)
                    item.Positive.Add(EncodeCached(node, "positive", i, node.PositiveExamples[i]));
                for (var i = 0; i < node.NegativeExamples.Count; i++)
                    item.Negative.Add(EncodeCached(node, "negative", i, node.NegativeExamples[i]));
                indexed.Add(item);
            }
        }

        public VectorRouteResult Route(string query, VectorRouteSettings settings = null)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Query is required.", "query");
            if (indexed.Count == 0) throw new InvalidOperationException("Build the vector index before routing.");
            settings = settings ?? new VectorRouteSettings();

            var queryVector = encoder.Encode(query, VectorTextPurpose.Query);
            VectorMathUtil.NormalizeInPlace(queryVector);

            var domainHits = Select(
                Score(VectorNodeLevel.Domain, queryVector, settings, null),
                settings.DomainTopK,
                settings.DomainMinimumScore,
                settings.ScoreWindowFromBest,
                settings.KeepBestDomainWhenBelowThreshold);
            var activeDomains = new HashSet<string>(domainHits.Select(x => DomainKey(x.Node.Id)), StringComparer.OrdinalIgnoreCase);

            var dimensionHits = Select(
                Score(VectorNodeLevel.Dimension, queryVector, settings, node => AppliesToAnyDomain(node, activeDomains)),
                settings.DimensionTopK,
                settings.DimensionMinimumScore,
                settings.ScoreWindowFromBest,
                settings.KeepBestDimensionWhenBelowThreshold);
            var activeDimensions = new HashSet<string>(dimensionHits.Select(x => x.Node.DimensionKey), StringComparer.OrdinalIgnoreCase);

            var conceptCandidates = Score(
                VectorNodeLevel.Concept,
                queryVector,
                settings,
                node => activeDomains.Count == 0 || AppliesToAnyDomain(node, activeDomains));
            var conceptHits = Select(
                AddActiveDimensionBonus(
                    conceptCandidates,
                    activeDimensions,
                    settings.ActiveDimensionConceptBonus),
                settings.ConceptTopK,
                settings.ConceptMinimumScore,
                settings.ScoreWindowFromBest,
                false);

            return new VectorRouteResult(query, domainHits, dimensionHits, conceptHits);
        }

        public IReadOnlyList<VectorRouteHit> RankConcepts(string query, VectorRouteSettings settings = null)
        {
            if (string.IsNullOrWhiteSpace(query) || indexed.Count == 0)
                return new List<VectorRouteHit>();
            settings = settings ?? new VectorRouteSettings();
            var queryVector = encoder.Encode(query, VectorTextPurpose.Query);
            VectorMathUtil.NormalizeInPlace(queryVector);
            return Score(VectorNodeLevel.Concept, queryVector, settings, null)
                .OrderByDescending(x => x.Score)
                .ToList();
        }

        private List<VectorRouteHit> Score(VectorNodeLevel level, float[] query, VectorRouteSettings settings, Func<VectorIndexNode, bool> filter)
        {
            var result = new List<VectorRouteHit>();
            foreach (var item in indexed)
            {
                if (item.Node.Level != level || (filter != null && !filter(item.Node))) continue;
                var definition = VectorMathUtil.Cosine(query, item.Definition);
                var positive = MaxSimilarity(query, item.Positive);
                var negative = MaxSimilarity(query, item.Negative);
                var score = settings.DefinitionWeight * definition +
                            settings.PositiveExampleWeight * positive -
                            settings.NegativePenalty * Math.Max(0f, negative);
                if (level == VectorNodeLevel.Concept && settings.ActivationCountBonus > 0f)
                    score += settings.ActivationCountBonus *
                             (float)Math.Log(1d + Math.Max(0, item.Node.ActivationCount));
                result.Add(new VectorRouteHit(item.Node, score, definition, positive, negative));
            }
            return result;
        }

        private static List<VectorRouteHit> AddActiveDimensionBonus(
            IEnumerable<VectorRouteHit> source,
            HashSet<string> activeDimensions,
            float bonus)
        {
            return source.Select(hit =>
            {
                var dimensionMatched = activeDimensions.Contains(hit.Node.DimensionKey) ||
                    hit.Node.ParentIds.Any(x => x.StartsWith("dimension.", StringComparison.OrdinalIgnoreCase) &&
                        activeDimensions.Contains(x.Substring("dimension.".Length)));
                var adjusted = dimensionMatched
                    ? hit.Score + Math.Max(0f, bonus)
                    : hit.Score;
                return new VectorRouteHit(
                    hit.Node,
                    adjusted,
                    hit.DefinitionScore,
                    hit.PositiveScore,
                    hit.NegativeScore);
            }).ToList();
        }

        private static IReadOnlyList<VectorRouteHit> Select(
            List<VectorRouteHit> source,
            int topK,
            float minimumScore,
            float scoreWindowFromBest,
            bool keepBestWhenBelowThreshold)
        {
            var ordered = source.OrderByDescending(x => x.Score).ToList();
            if (ordered.Count == 0 || topK <= 0) return new List<VectorRouteHit>();
            var cutoff = Math.Max(minimumScore, ordered[0].Score - scoreWindowFromBest);
            var selected = ordered.Where(x => x.Score >= cutoff).Take(topK).ToList();
            // 弱匹配保持沉寂。概念、域、维度都可以为空，避免保底点亮未到达的骨架。
            if (selected.Count == 0 && keepBestWhenBelowThreshold) selected.Add(ordered[0]);
            return selected;
        }

        private float[] EncodeCached(VectorIndexNode node, string role, int index, string text)
        {
            var id = node.Id + "/" + role + "/" + index;
            var hash = VectorMathUtil.Sha256(text);
            float[] vector;
            if (cache != null && cache.TryGet(id, encoder.ModelId, hash, out vector)) return vector;

            vector = encoder.Encode(text, VectorTextPurpose.Index);
            if (vector == null || vector.Length != encoder.Dimensions)
                throw new InvalidOperationException("Encoder returned an invalid vector for " + id + ".");
            VectorMathUtil.NormalizeInPlace(vector);
            if (cache != null) cache.Put(id, node.Id, role, index, encoder.ModelId, hash, vector);
            return vector;
        }

        private static float MaxSimilarity(float[] query, List<float[]> candidates)
        {
            if (candidates.Count == 0) return 0f;
            var best = float.MinValue;
            foreach (var candidate in candidates) best = Math.Max(best, VectorMathUtil.Cosine(query, candidate));
            return best;
        }

        private static bool AppliesToAnyDomain(VectorIndexNode node, HashSet<string> domains)
        {
            return node.ApplicableDomains.Count == 0 || node.ApplicableDomains.Any(domains.Contains);
        }

        private static string DomainKey(string id)
        {
            const string prefix = "domain.";
            return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? id.Substring(prefix.Length) : id;
        }
    }
}
