using System;
using System.Collections.Generic;
using SQLite;

namespace TraceSoul2.Data
{
    /// <summary>
    /// TraceSoul2 的数据边界：外部感官插件产生 Moment，内部神经只能产生事实，
    /// 唯一 Brain 才能产生认知和修改内心 Runtime。
    /// </summary>
    public static class TraceRealmValues
    {
        public const string ExternalWorld = "external_world";
        public const string SharedScene = "shared_scene";
        public const string Meta = "meta";
        public const string ExplicitFiction = "explicit_fiction";
        public const string Unclassified = "unclassified";

        public static bool IsMemoryRealm(string value)
        {
            return value == ExternalWorld || value == SharedScene ||
                   value == Meta || value == ExplicitFiction;
        }
    }

    /// <summary>第一、二层允许使用的稳定路由键；第三层 Tag 只能连接到这里。</summary>
    public static class LifeRouteValues
    {
        public static readonly string[] Domains = { "ass", "user", "relation", "world" };
        public static readonly string[] Dimensions =
        {
            "owner", "subject", "about", "predicate", "object", "scope", "context", "quality",
            "time", "place", "affect", "goal", "state", "realm", "modality", "source"
        };

        public static bool IsDomain(string value)
        {
            return Array.Exists(Domains, x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsDimension(string value)
        {
            return Array.Exists(Dimensions, x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static class EvidenceTypeValues
    {
        public const string UserReported = "user_reported";
        public const string PluginObserved = "plugin_observed";
        public const string SharedSceneDeclared = "shared_scene_declared";
        public const string AssPerformed = "ass_performed";
        public const string ExplicitFiction = "explicit_fiction";
        public const string DialogueExplicit = "explicit_dialogue";

        public static bool IsKnown(string value)
        {
            return value == UserReported || value == PluginObserved ||
                   value == SharedSceneDeclared || value == AssPerformed ||
                   value == ExplicitFiction || value == DialogueExplicit;
        }

        public static string Canonicalize(string value)
        {
            var raw = (value ?? string.Empty).Trim();
            var lower = raw.ToLowerInvariant();
            if (IsKnown(lower)) return lower;
            if (lower == "spoken" || lower == "said" || raw == "亲口" || raw == "自述")
                return UserReported;
            if (lower == "enacted" || lower == "did" || raw == "自己做的")
                return AssPerformed;
            if (lower == "seen" || lower == "observed") return PluginObserved;
            if (lower == "shared" || lower == "shared_scene") return SharedSceneDeclared;
            if (lower == "fiction") return ExplicitFiction;
            if (lower == "dialogue") return DialogueExplicit;
            return lower;
        }
    }

    public static class CognitionOperationValues
    {
        public const string Create = "create";
        public const string Reinforce = "reinforce";
        public const string Revise = "revise";
        public const string Weaken = "weaken";
    }

    /// <summary>
    /// 外部感官或表达器交给系统的统一事件。PayloadJson 留给未来视觉、定位、游戏等插件扩展，
    /// 核心 Runtime 不需要知道每个平台的私有字段。
    /// </summary>
    public sealed class PluginEventData
    {
        /// <summary>跨入站、Brain、器官与出站日志的本轮短追踪号；不参与业务语义。</summary>
        public string TraceId { get; set; }
        public string PluginId { get; set; }
        public string ConversationId { get; set; }
        public string ExternalEventId { get; set; }
        public string Role { get; set; }
        public string Content { get; set; }
        public string Realm { get; set; }
        public string EvidenceType { get; set; }
        public string PayloadJson { get; set; }
        /// <summary>
        /// 仅作运行留痕、不应进入语义 Moment 的事件（例如 QQ 图片/表情发送回执、定时器触发）。
        /// 默认 false：未声明的插件事件仍按语义 Moment 处理，保持旧插件兼容。
        /// </summary>
        public bool IsOperational { get; set; }
        /// <summary>入站器官；只给身体路由用，不写入 Moment。</summary>
        public string Organ { get; set; }
        /// <summary>中枢叫醒谁：dialogue / mind / subconscious / night_residue。空则由角色与内容推断。</summary>
        public string Wake { get; set; }
        /// <summary>打破性 Moment：睡着时也能把他叫醒。用户发来的话现在都是。</summary>
        public bool Breaking { get; set; }
        public long OccurredUnixMs { get; set; }
    }

    [Table("pair_identity")]
    public sealed class PairIdentityRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        public string Username { get; set; }
        public string Assname { get; set; }
        public string CallName { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    [Table("life_tags")]
    public sealed class LifeTagRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string Label { get; set; }

        public string Definition { get; set; }
        public string Status { get; set; }
        public string Origin { get; set; }
        public string SourceMomentId { get; set; }
        public int ActivationCount { get; set; }
        public long CreatedUnixMs { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    [Table("life_tag_routes")]
    public sealed class LifeTagRouteRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string TagId { get; set; }

        [Indexed]
        public string RouteNodeId { get; set; }

        // domain 或 dimension。
        public string RouteLevel { get; set; }
        public float Weight { get; set; }
    }

    [Table("life_tag_examples")]
    public sealed class LifeTagExampleRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string TagId { get; set; }

        public string Role { get; set; }
        public string Text { get; set; }
        public int ExampleIndex { get; set; }
    }

    /// <summary>
    /// 第四层的事实人生切片。Summary 是一次事实的短句，不因语义相似而自动合并。
    /// </summary>
    [Table("fact_slices")]
    public sealed class FactSliceRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        public string Summary { get; set; }
        public string Realm { get; set; }
        public string EvidenceType { get; set; }
        public float Confidence { get; set; }

        [Indexed]
        public string SourceMomentId { get; set; }

        [Indexed]
        public string SourcePluginId { get; set; }

        public string Status { get; set; }
        public int WakeCount { get; set; }
        public long LastWokenUnixMs { get; set; }
        public long CreatedUnixMs { get; set; }
    }

    [Table("fact_tag_links")]
    public sealed class FactTagLinkRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string FactId { get; set; }

        [Indexed]
        public string TagId { get; set; }

        public float Weight { get; set; }
    }

    [Table("fact_wakes")]
    public sealed class FactWakeRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string FactId { get; set; }

        [Indexed]
        public string TriggerMomentId { get; set; }

        public string Reason { get; set; }
        public float Relevance { get; set; }
        public long CreatedUnixMs { get; set; }
    }

    /// <summary>
    /// Brain 的第一人称认知。它和事实分表，数据库层面阻止感官越权写认知。
    /// </summary>
    [Table("cognition_slices")]
    public sealed class CognitionSliceRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string OwnerId { get; set; }

        public string Summary { get; set; }
        public string Subtype { get; set; }
        public float Confidence { get; set; }
        public string Status { get; set; }
        public int Revision { get; set; }
        public long CreatedUnixMs { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    [Table("cognition_tag_links")]
    public sealed class CognitionTagLinkRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string CognitionId { get; set; }

        [Indexed]
        public string TagId { get; set; }

        public float Weight { get; set; }
    }

    [Table("cognition_evidence")]
    public sealed class CognitionEvidenceRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string CognitionId { get; set; }

        [Indexed]
        public string FactId { get; set; }

        [Indexed]
        public string MomentId { get; set; }

        public string Relation { get; set; }
        public float Weight { get; set; }
    }

    [Table("cognition_edges")]
    public sealed class CognitionEdgeRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string FromCognitionId { get; set; }

        [Indexed]
        public string ToCognitionId { get; set; }

        public string Relation { get; set; }
        public float Weight { get; set; }
        public long CreatedUnixMs { get; set; }
    }

    [Table("cognition_cues")]
    public sealed class CognitionCueRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string CognitionId { get; set; }

        [Indexed]
        public string Cue { get; set; }

        public float AssociationStrength { get; set; }
        public string SourceMomentId { get; set; }
        public long CreatedUnixMs { get; set; }
    }

    [Table("base_personalities")]
    public sealed class BasePersonalityRecord
    {
        [PrimaryKey]
        public string ConversationId { get; set; }

        public string Narrative { get; set; }
        public int Revision { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    [Table("memory_observation_runs")]
    public sealed class MemoryObservationRunRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string MomentId { get; set; }

        public string ObserverId { get; set; }
        public string PerceptionSummary { get; set; }
        public string FactDecision { get; set; }
        public long CreatedUnixMs { get; set; }
    }

    [Serializable]
    public sealed class NewLifeTagWriteData
    {
        public string name;
        public string definition;
        public List<string> domain_ids = new List<string>();
        public List<string> dimension_ids = new List<string>();
        public List<string> positive_examples = new List<string>();
        public List<string> negative_examples = new List<string>();
    }

    [Serializable]
    public sealed class SensoryFactWriteData
    {
        public string summary;
        public string realm;
        public string evidence_type;
        public float confidence;
        public List<string> tag_ids = new List<string>();
        public List<string> new_tag_names = new List<string>();
    }

    [Serializable]
    public sealed class SensoryFactWakeData
    {
        public string fact_id;
        public string reason;
        public float relevance;
    }

    [Serializable]
    public sealed class MemoryObservationOutputData
    {
        public string perception_summary;
        public string fact_decision;
        public List<string> selected_tag_ids = new List<string>();
        public List<NewLifeTagWriteData> new_tags = new List<NewLifeTagWriteData>();
        public List<SensoryFactWriteData> fact_writes = new List<SensoryFactWriteData>();
        public List<SensoryFactWakeData> fact_wakes = new List<SensoryFactWakeData>();
    }

    [Serializable]
    public sealed class BrainCognitionWriteData
    {
        public string operation;
        public string target_id;
        public string summary;
        public string subtype;
        public float confidence;
        public List<string> tag_ids = new List<string>();
        public List<string> evidence_fact_ids = new List<string>();
        public List<string> trace_cues = new List<string>();
        public float association_strength;
    }

    public sealed class CognitionCueRecallData
    {
        public CognitionSliceRecord Cognition { get; set; }
        public string Cue { get; set; }
        public float AssociationStrength { get; set; }
    }

    public sealed class MemoryObservationCommitData
    {
        public IReadOnlyList<LifeTagRecord> SelectedTags { get; private set; }
        public IReadOnlyList<FactSliceRecord> WrittenFacts { get; private set; }
        public IReadOnlyList<FactSliceRecord> AwakenedFacts { get; private set; }
        public bool OntologyChanged { get; private set; }

        public MemoryObservationCommitData(
            List<LifeTagRecord> selectedTags,
            List<FactSliceRecord> writtenFacts,
            List<FactSliceRecord> awakenedFacts,
            bool ontologyChanged)
        {
            SelectedTags = selectedTags ?? new List<LifeTagRecord>();
            WrittenFacts = writtenFacts ?? new List<FactSliceRecord>();
            AwakenedFacts = awakenedFacts ?? new List<FactSliceRecord>();
            OntologyChanged = ontologyChanged;
        }
    }
}
