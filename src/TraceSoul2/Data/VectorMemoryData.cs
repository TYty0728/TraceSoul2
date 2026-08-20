using System;
using System.Collections.Generic;
using SQLite;

namespace TraceSoul2.Data
{
    // 向量不是正式记忆，只是从一句话导航到图中入口的索引。
    public enum VectorNodeLevel
    {
        Domain,
        Dimension,
        Concept
    }

    public enum VectorTextPurpose
    {
        Query,
        Index
    }

    public sealed class VectorIndexNode
    {
        public string Id { get; private set; }
        public VectorNodeLevel Level { get; private set; }
        public string Label { get; private set; }
        public string Definition { get; private set; }
        public string DimensionKey { get; private set; }
        public IReadOnlyList<string> ApplicableDomains { get; private set; }
        public IReadOnlyList<string> ParentIds { get; private set; }
        public IReadOnlyList<string> PositiveExamples { get; private set; }
        public IReadOnlyList<string> NegativeExamples { get; private set; }
        public int ActivationCount { get; private set; }

        public VectorIndexNode(
            string id,
            VectorNodeLevel level,
            string label,
            string definition,
            string dimensionKey,
            IEnumerable<string> applicableDomains,
            IEnumerable<string> parentIds,
            IEnumerable<string> positiveExamples,
            IEnumerable<string> negativeExamples,
            int activationCount = 0)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Vector node id is required.", "id");
            if (string.IsNullOrWhiteSpace(definition)) throw new ArgumentException("Vector node definition is required.", "definition");

            Id = id;
            Level = level;
            Label = label ?? id;
            Definition = definition;
            DimensionKey = dimensionKey ?? string.Empty;
            ApplicableDomains = ToList(applicableDomains);
            ParentIds = ToList(parentIds);
            PositiveExamples = ToList(positiveExamples);
            NegativeExamples = ToList(negativeExamples);
            ActivationCount = Math.Max(0, activationCount);
        }

        private static IReadOnlyList<string> ToList(IEnumerable<string> values)
        {
            return values == null ? new List<string>() : new List<string>(values);
        }
    }

    public sealed class VectorRouteSettings
    {
        public int DomainTopK { get; set; } = 2;
        public int DimensionTopK { get; set; } = 5;
        public int ConceptTopK { get; set; } = 10;
        public float DomainMinimumScore { get; set; } = 0.18f;
        public float DimensionMinimumScore { get; set; } = 0.18f;
        // 概念比域和维度具体；宁可为空，也不展示“电影 → 进食”这类弱匹配。
        public float ConceptMinimumScore { get; set; } = 0.30f;
        public float ScoreWindowFromBest { get; set; } = 0.16f;
        // 第二层仅为第三层提供方向奖励，不再作为禁止候选进入的硬门禁。
        public float ActiveDimensionConceptBonus { get; set; } = 0.06f;
        public float DefinitionWeight { get; set; } = 0.7f;
        public float PositiveExampleWeight { get; set; } = 0.3f;
        public float NegativePenalty { get; set; } = 0.25f;
        // 弱匹配的域/维度保持沉寂，不再保底点亮一格骨架。
        public bool KeepBestDomainWhenBelowThreshold { get; set; }
        public bool KeepBestDimensionWhenBelowThreshold { get; set; }
        // 经常被点亮的人生 Tag 更容易再次进入候选，但不能压过明显的语义不匹配。
        public float ActivationCountBonus { get; set; } = 0.04f;
    }

    public sealed class VectorRouteHit
    {
        public VectorIndexNode Node { get; private set; }
        public float Score { get; private set; }
        public float DefinitionScore { get; private set; }
        public float PositiveScore { get; private set; }
        public float NegativeScore { get; private set; }

        public VectorRouteHit(VectorIndexNode node, float score, float definitionScore, float positiveScore, float negativeScore)
        {
            Node = node;
            Score = score;
            DefinitionScore = definitionScore;
            PositiveScore = positiveScore;
            NegativeScore = negativeScore;
        }
    }

    public sealed class VectorRouteResult
    {
        public string Query { get; private set; }
        public IReadOnlyList<VectorRouteHit> Domains { get; private set; }
        public IReadOnlyList<VectorRouteHit> Dimensions { get; private set; }
        public IReadOnlyList<VectorRouteHit> Concepts { get; private set; }

        public VectorRouteResult(
            string query,
            IReadOnlyList<VectorRouteHit> domains,
            IReadOnlyList<VectorRouteHit> dimensions,
            IReadOnlyList<VectorRouteHit> concepts)
        {
            Query = query;
            Domains = domains;
            Dimensions = dimensions;
            Concepts = concepts;
        }
    }

    /// <summary>
    /// 时间阶梯条目：比较晋升制榜单中的指针（day/week/month/year/forever）。
    /// 只存 RefId + 一句原因，内容永远留在事实/认知网中，榜单更新不新增数据。
    /// </summary>
    [Table("ladder_items")]
    public sealed class LadderItemRecord
    {
        [PrimaryKey]
        public string Id { get; set; }
        public string Tier { get; set; }
        public string PeriodKey { get; set; }
        public string ListKind { get; set; }
        public int Rank { get; set; }
        public string RefId { get; set; }
        public string RefKind { get; set; }
        public string Label { get; set; }
        public string Reason { get; set; }
        public long CreatedUnixMs { get; set; }
    }

    /// <summary>
    /// 第四层：多维索引。时间（程序确定性翻译）x 地点 x 人物 x 事件 x 心情，全部按事实书写。
    /// 它是事件的稳定索引行；血肉在 event_entries 的追加条目里。
    /// </summary>
    [Table("event_indexes")]
    public sealed class EventIndexRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        public string TagIds { get; set; }

        /// <summary>时段标签（清晨/上午/晚上…，不含「今天/昨天」）；日关系在注入时按当前时间动态渲染。</summary>
        public string TimeLabel { get; set; }
        public string DayKindLabel { get; set; }

        /// <summary>事件锚点时间戳：注入时用来计算「今天/昨天/很久以前」。</summary>
        public long TimeUnixMs { get; set; }

        public string PlaceLabel { get; set; }
        public string PersonLabel { get; set; }
        public string EventSummary { get; set; }

        /// <summary>心情维度：未来由内心实时更新补入；观察期只填证据里明确读到的情绪，读不到留空。</summary>
        public string MoodLabel { get; set; }

        [Indexed]
        public string FirstMomentId { get; set; }

        public string Status { get; set; }
        public long CreatedUnixMs { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    /// <summary>
    /// 多维索引下不停追加的条目：一句话总结（客观）+ 细节正文（第一人称视角），带证据链。
    /// 永不覆盖，只追加。
    /// </summary>
    [Table("event_entries")]
    public sealed class EventEntryRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string IndexId { get; set; }

        public string Summary { get; set; }
        public string Detail { get; set; }

        [Indexed]
        public string SourceMomentId { get; set; }

        public string Realm { get; set; }
        public long CreatedUnixMs { get; set; }
    }
}
