using System;
using System.Collections.Generic;
using System.Linq;
using SQLite;

namespace TraceSoul2.Data
{
    /// <summary>插件是容器；Contribution 才是它挂到 Kernel 上的具体形态。</summary>
    public static class TraceContributionKindValues
    {
        public const string MomentSource = "moment_source";
        public const string MountedFacet = "mounted_facet";
        public const string CallableNerve = "callable_nerve";
        public const string Effector = "effector";
        public const string BackgroundService = "background_service";

        public static bool IsBrainCallable(string value)
        {
            return value == CallableNerve || value == Effector;
        }
    }

    /// <summary>
    /// 身体远近：近的压过远的。同层才打分。
    /// 控制台是最低的文字壳，不算「自己的软件」。
    /// </summary>
    public static class BodyTierValues
    {
        public const string Physical = "physical";
        public const string App = "app";
        public const string Chat = "chat";
        public const string Shell = "shell";

        public static int Nearness(string tier)
        {
            if (tier == Physical) return 4;
            if (tier == App) return 3;
            if (tier == Chat) return 2;
            if (tier == Shell) return 1;
            return 0;
        }
    }

    /// <summary>同层物理身体的体量：家里等身压过娃娃；出门多半只有小的。</summary>
    public static class BodyScaleValues
    {
        public const string Life = "life";
        public const string Large = "large";
        public const string Small = "small";
    }

    public static class BodyIds
    {
        public const string Console = "console";
        public const string Qq = "onebot";
    }

    /// <summary>器官是开放类型；说话（文字/语音）才移动激活的身体。</summary>
    public static class BodyOrganValues
    {
        public const string Text = "text";
        public const string Image = "image";
        public const string Sticker = "sticker";
        public const string Voice = "voice";
        public const string Video = "video";
        public const string Qzone = "qzone";

        public static bool IsSpeak(string organ)
        {
            return organ == Text || organ == Voice;
        }
    }

    public static class TraceFacetRefreshValues
    {
        public const string OncePerTurn = "once_per_turn";
        public const string EveryBrainStep = "every_brain_step";
    }

    public static class BrainStepStateValues
    {
        public const string Call = "call";
        public const string Finish = "finish";
    }

    public static class BrainModeValues
    {
        public const string Reflex = "reflex";
        public const string Focused = "focused";
        public const string Deep = "deep";

        public static bool IsKnown(string value)
        {
            return value == Reflex || value == Focused || value == Deep;
        }
    }

    /// <summary>插件分层：内核不是插件；插件页按平台收口，下面才是器官。</summary>
    public static class PluginRoleValues
    {
        public const string Kernel = "kernel";
        public const string Platform = "platform";
        public const string Organ = "organ";
        /// <summary>旧角色名，加载时归一成 platform。</summary>
        public const string Body = "body";

        public static bool IsKernel(string role)
        {
            return role == Kernel;
        }

        public static bool IsPlatform(string role)
        {
            return role == Platform || role == Body;
        }

        public static string Normalize(string role, string pluginId)
        {
            if (role == Body) role = Platform;
            if (role == Kernel || role == Platform || role == Organ) return role;
            pluginId = pluginId ?? string.Empty;
            if (pluginId == "builtin.dialogue" || pluginId == "builtin.identity" ||
                pluginId == "builtin.inner-life" || pluginId == "builtin.memory" ||
                pluginId == "builtin.time" || pluginId == "builtin.senses")
                return Kernel;
            if (pluginId == "builtin.onebot") return Platform;
            return Organ;
        }

        public static string PlatformOf(string pluginId, string platformId)
        {
            if (!string.IsNullOrWhiteSpace(platformId)) return platformId.Trim();
            pluginId = pluginId ?? string.Empty;
            if (pluginId == "builtin.onebot" ||
                pluginId.StartsWith("qq.", StringComparison.OrdinalIgnoreCase))
                return BodyIds.Qq;
            return string.Empty;
        }
    }

    public sealed class TracePluginMetadataData
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        /// <summary>kernel / platform / organ。空则按 Id 推断。</summary>
        public string Role { get; set; }
        /// <summary>器官所属平台（onebot 等）。内核为空。</summary>
        public string PlatformId { get; set; }
        public bool Enabled { get; set; }
        public string LoadError { get; set; }
        /// <summary>给人看的运行备注（如未配置 api_key）；空则没有。</summary>
        public string Note { get; set; }
    }

    /// <summary>Brain 和观察界面看到的统一贡献说明。</summary>
    public sealed class TraceContributionDescriptorData
    {
        public string Id { get; set; }
        public string PluginId { get; set; }
        public string Kind { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Provides { get; set; }
        public string WhenToUse { get; set; }
        public string WhenNotToUse { get; set; }
        public string ParametersJsonSchema { get; set; }
        public string OutputJsonSchema { get; set; }

        /// <summary>给 Brain 的一行边界描述（如「告诉我情绪词」）；空则回退到 DisplayName。</summary>
        public string Boundary { get; set; }
        /// <summary>这只贡献长在哪具身体上（console / onebot / 以后的娃娃）。</summary>
        public string BodyId { get; set; }
        /// <summary>身体远近层：physical / app / chat / shell（控制台最低）。</summary>
        public string BodyTier { get; set; }
        /// <summary>同层体量：life / large / small。非物理可空。</summary>
        public string BodyScale { get; set; }
        /// <summary>器官：text / image / sticker / voice / video / qzone。</summary>
        public string Organ { get; set; }
        public string RefreshMode { get; set; }
        public int Priority { get; set; }
        public int MaxContextChars { get; set; }
        public bool HasInternalMutation { get; set; }
        public bool HasExternalSideEffect { get; set; }
    }

    [Table("plugin_states")]
    public sealed class PluginStateRecord
    {
        [PrimaryKey]
        public string PluginId { get; set; }
        public bool Enabled { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    [Table("plugin_documents")]
    public sealed class PluginDocumentRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string PluginId { get; set; }
        public string DocumentKey { get; set; }
        public string Json { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    public sealed class TraceContextBlockData
    {
        public string FacetId { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public int Priority { get; set; }
    }

    [Serializable]
    public sealed class BrainFacetFieldData
    {
        public string name;
        public string value;
    }

    [Serializable]
    public sealed class BrainFacetOutputData
    {
        public string facet_id;
        public bool changed;
        public string summary;
        public List<BrainFacetFieldData> fields = new List<BrainFacetFieldData>();

        public string GetField(string name, string fallback = null)
        {
            foreach (var field in fields ?? new List<BrainFacetFieldData>())
                if (field != null && string.Equals(field.name, name, StringComparison.OrdinalIgnoreCase))
                    return field.value;
            return fallback;
        }
    }

    [Serializable]
    public sealed class BrainCallArgumentData
    {
        public string name;
        public string value;
    }

    [Serializable]
    public sealed class BrainCapabilityCallData
    {
        public string call_id;
        public string capability_id;
        public string purpose;
        public List<BrainCallArgumentData> arguments = new List<BrainCallArgumentData>();

        public string GetArgument(string name, string fallback = "")
        {
            foreach (var argument in arguments ?? new List<BrainCallArgumentData>())
                if (argument != null && string.Equals(argument.name, name, StringComparison.OrdinalIgnoreCase))
                    return argument.value ?? fallback;
            return fallback;
        }
    }

    public sealed class TraceCapabilityResultData
    {
        public string CallId { get; set; }
        public string CapabilityId { get; set; }
        public string Status { get; set; }
        public string Summary { get; set; }
        public string Payload { get; set; }
        public List<string> EvidenceRefs { get; set; } = new List<string>();

        // 仅运行时使用，不进入 LLM JSON。
        public PluginEventData ProducedEvent { get; set; }
    }

    [Serializable]
    public sealed class BrainStructuredOutputData
    {
        public string state;
        public string mode;
        public string intent;
        public string decision_summary;
        public List<BrainCapabilityCallData> calls = new List<BrainCapabilityCallData>();
        public bool should_express;
        public string expression_capability_id;
        public string reply;

        /// <summary>主通道之外的附加表达（表情/图片/语音/动作等），数量由 Brain 自行决定。</summary>
        public List<BrainCapabilityCallData> expressions = new List<BrainCapabilityCallData>();
        public List<BrainFacetOutputData> facet_outputs = new List<BrainFacetOutputData>();
    }

    /// <summary>
    /// 心智决策卡：安静、理性、好读好填。
    /// 心智只组织这一拍怎么想，不写对她说的台词。
    /// </summary>
    [Serializable]
    public sealed class MindDecisionData
    {
        /// <summary>当下 / 旧事 / 出门</summary>
        public string beat;
        /// <summary>生命标签名，顿号或逗号分隔。</summary>
        public string tags;
        /// <summary>旧事检索句；空则用当前 Moment。</summary>
        public string query;
        public string mood;
        public bool mood_changed;
        public bool archive;
        public string new_fact;
        /// <summary>出门要办的事；空表示不出门。</summary>
        public string leave;
        /// <summary>给外显的组织说明，不是台词。</summary>
        public string note;
        /// <summary>今天轨迹要补的一句；空则不改。</summary>
        public string today;
        /// <summary>这一拍的当前时；空表示没有往前挪，不改内心。</summary>
        public string inner;
        /// <summary>派出潜意识复盘；具体怎么改短卡由复盘链路去做。</summary>
        public bool review;
        /// <summary>在场工作台：一两件正搁在手里的事。空=不改；「无」=放下。</summary>
        public string attention;
        /// <summary>这一拍真的改了的看法；空则不写认知切片。短卡仍不由心智改。</summary>
        public string cognition;
        /// <summary>心跳时：要对她说。普通对话由入口强制表达，此字段可忽略。</summary>
        public bool speak;
        /// <summary>要睡下。睡着后心跳停，直到打破性 Moment 才醒来。</summary>
        public bool sleep;
        /// <summary>心跳想完后：多少分钟后再跳一次。0 表示不再跳，等下一个入站。</summary>
        public int next_heartbeat_minutes;
        /// <summary>无 / 贴。贴则按 mood 丢一张表情。</summary>
        public string sticker;
        /// <summary>无 / 自拍 / 画。真的把图发到对话里，不是描写。</summary>
        public string image;

        public bool WantsMemory()
        {
            return BeatValue() == MindBeatValues.Memory || ParseTags().Count > 0 ||
                   !string.IsNullOrWhiteSpace(query);
        }

        public bool WantsLeave()
        {
            return BeatValue() == MindBeatValues.Leave && !string.IsNullOrWhiteSpace(leave);
        }

        public bool WantsReview()
        {
            return review;
        }

        public bool WantsSticker()
        {
            return StickerValue() == MindAtmosphereValues.Stick;
        }

        public bool WantsImage()
        {
            var value = ImageValue();
            return value == MindAtmosphereValues.Selfie || value == MindAtmosphereValues.Draw;
        }

        public string StickerValue()
        {
            var value = (sticker ?? string.Empty).Trim();
            if (value == "贴" || value == "要" || value == "发" ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "sticker", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
                return MindAtmosphereValues.Stick;
            return MindAtmosphereValues.None;
        }

        public string ImageValue()
        {
            var value = (image ?? string.Empty).Trim();
            if (value == "自拍" || value == "照片" ||
                string.Equals(value, "selfie", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "photo", StringComparison.OrdinalIgnoreCase))
                return MindAtmosphereValues.Selfie;
            if (value == "画" || value == "生图" ||
                string.Equals(value, "draw", StringComparison.OrdinalIgnoreCase))
                return MindAtmosphereValues.Draw;
            return MindAtmosphereValues.None;
        }

        public bool ClearsAttention()
        {
            var value = (attention ?? string.Empty).Trim();
            return value == "无" || value == "（空）" || value == "(空)" || value == "没有";
        }

        public List<string> ParseAttention()
        {
            if (ClearsAttention()) return new List<string>();
            var raw = (attention ?? string.Empty).Trim();
            if (raw.Length == 0) return new List<string>();
            return raw.Split(new[] { '、', ',', '，', ';', '；', '\n', '|', '｜' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && x != "无")
                .Distinct()
                .Take(2)
                .ToList();
        }

        public string BeatValue()
        {
            var value = (beat ?? string.Empty).Trim();
            if (value == "旧事" || value == "memory" || value == "recall") return MindBeatValues.Memory;
            if (value == "出门" || value == "leave" || value == "search") return MindBeatValues.Leave;
            return MindBeatValues.Now;
        }

        public List<string> ParseTags()
        {
            var raw = (tags ?? string.Empty).Trim();
            if (raw.Length == 0) return new List<string>();
            return raw.Split(new[] { '、', ',', '，', ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct()
                .Take(3)
                .ToList();
        }
    }

    public static class MindBeatValues
    {
        public const string Now = "当下";
        public const string Memory = "旧事";
        public const string Leave = "出门";
    }

    public static class MindAtmosphereValues
    {
        public const string None = "无";
        public const string Stick = "贴";
        public const string Selfie = "自拍";
        public const string Draw = "画";
    }

    /// <summary>中枢按入口换轨：叫醒心智、叫醒潜意识、或她正在说话。</summary>
    public static class KernelWakeValues
    {
        public const string Dialogue = "dialogue";
        public const string Mind = "mind";
        public const string Subconscious = "subconscious";

        public static string Normalize(string wake)
        {
            var value = (wake ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            if (string.Equals(value, Subconscious, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "review", StringComparison.OrdinalIgnoreCase) ||
                value == "潜意识" || value == "复盘")
                return Subconscious;
            if (string.Equals(value, Mind, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "inner", StringComparison.OrdinalIgnoreCase) ||
                value == "心智")
                return Mind;
            if (string.Equals(value, Dialogue, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "user", StringComparison.OrdinalIgnoreCase) ||
                value == "她")
                return Dialogue;
            return Dialogue;
        }

        public static string InferFromContent(string content)
        {
            return (content ?? string.Empty).IndexOf("每日复盘", StringComparison.Ordinal) >= 0
                ? Subconscious
                : Mind;
        }
    }

    /// <summary>外显输出：只决定怎么说，不决定调哪些内部神经。</summary>
    [Serializable]
    public sealed class ExpressorVoiceOutputData
    {
        public string text;
        public string emotion;
    }

    [Serializable]
    public sealed class ExpressorImageOutputData
    {
        public string prompt;
        /// <summary>photo / selfie / draw / edit / url；空时由相机器官判断。</summary>
        public string mode;
        /// <summary>逗号分隔的参考图分类名；空时由相机器官按自拍/人物规则自动选择。</summary>
        public string refs;
        public string aspect_ratio;
        public string url;
    }

    [Serializable]
    public sealed class ExpressorOutputData
    {
        public bool should_express = true;
        public string reply;
        public string sticker;
        public string qzone;
        /// <summary>旧兼容：单段语音文字。</summary>
        public string voice;
        public string voice_emotion;
        /// <summary>需要多段声音时使用；每段独立合成并按顺序发送。</summary>
        public List<ExpressorVoiceOutputData> voices = new List<ExpressorVoiceOutputData>();
        /// <summary>旧兼容：单张图片提示词。</summary>
        public string image;
        public string image_mode;
        public string image_refs;
        public string image_aspect_ratio;
        /// <summary>需要多张图或不同相机动作时使用。</summary>
        public List<ExpressorImageOutputData> images = new List<ExpressorImageOutputData>();
        public string mood;
    }
}
