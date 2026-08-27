using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SQLite;
using System.Text.Json.Serialization;

namespace TraceSoul2.Data
{
    /// <summary>兼容模型把本应是字符串的字段输出成字符串数组。</summary>
    public sealed class FlexibleStringJsonConverter : JsonConverter<string>
    {
        public override string Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return string.Empty;
            if (reader.TokenType == JsonTokenType.String) return reader.GetString() ?? string.Empty;
            using (var document = JsonDocument.ParseValue(ref reader))
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    return string.Join("、", root.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String
                            ? x.GetString()
                            : x.ToString())
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
                }
                return root.ToString();
            }
        }

        public override void Write(
            Utf8JsonWriter writer,
            string value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(value ?? string.Empty);
        }
    }

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

    /// <summary>身体所在的物理场景；不同于 MindDecisionData.scene 的共享文字场景。</summary>
    public static class BodySceneValues
    {
        public const string Home = "home";
        public const string Out = "out";

        public static string Normalize(string value)
        {
            var text = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (text == Out || text == "外出" || text == "出门") return Out;
            return Home;
        }

        public static string Label(string value)
        {
            return Normalize(value) == Out ? "外出" : "家里";
        }
    }

    public static class LifeStateSourceValues
    {
        public const string User = "user";
        public const string Sensor = "sensor";
        public const string Plugin = "plugin";
        public const string Mind = "mind";
        public const string System = "system";

        public static int Priority(string source)
        {
            switch ((source ?? string.Empty).Trim().ToLowerInvariant())
            {
                case User: return 100;
                case Sensor: return 90;
                case Plugin: return 80;
                case Mind: return 50;
                default: return 10;
            }
        }
    }

    [Serializable]
    public sealed class LifeStateData
    {
        public string conversation_id;
        public string location = BodySceneValues.Home;
        public string activity = string.Empty;
        public string activity_detail = string.Empty;
        public string location_source = LifeStateSourceValues.System;
        public string activity_source = LifeStateSourceValues.System;
        public string location_source_id = string.Empty;
        public string activity_source_id = string.Empty;
        public long location_updated_unix_ms;
        public long activity_updated_unix_ms;
        public long activity_started_unix_ms;

        /// <summary>正在做的唯一注入句：活动名｜补充。都空则空串。</summary>
        public string FormatDoing()
        {
            var act = (activity ?? string.Empty).Trim();
            var detail = (activity_detail ?? string.Empty).Trim();
            if (act.Length == 0) return detail;
            if (detail.Length == 0) return act;
            return act + "｜" + detail;
        }
    }

    [Serializable]
    public sealed class LifeStatePatchData
    {
        public string location;
        public string activity;
        public string activity_detail;
        public string source;
        public string source_id;
        public bool force;
    }

    public static class BodyIds
    {
        public const string Console = "console";
        public const string Qq = "onebot";
        /// <summary>游戏身体：game-session 平台（自研 WS 桥）。平台 id 与归属键同形。</summary>
        public const string Game = "game.session";
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
            if (pluginId == "builtin.identity" ||
                pluginId == "builtin.inner-life" || pluginId == "builtin.memory" ||
                pluginId == "builtin.time" || pluginId == "builtin.senses")
                return Kernel;
            // console 是保底对话平台（不可关由管理器特判），game-session 是自研游戏身体。
            if (pluginId == "builtin.dialogue" || pluginId == "builtin.onebot" ||
                pluginId == BodyIds.Game)
                return Platform;
            return Organ;
        }

        public static string PlatformOf(string pluginId, string platformId)
        {
            if (!string.IsNullOrWhiteSpace(platformId)) return platformId.Trim();
            pluginId = pluginId ?? string.Empty;
            if (pluginId == "builtin.onebot" ||
                pluginId.StartsWith("qq.", StringComparison.OrdinalIgnoreCase))
                return BodyIds.Qq;
            if (pluginId == "builtin.dialogue") return BodyIds.Console;
            if (pluginId == BodyIds.Game ||
                pluginId.StartsWith("game.", StringComparison.OrdinalIgnoreCase))
                return BodyIds.Game;
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
        /// <summary>
        /// 进入空闲时参与系统抽签的每日上限。大于 0 才进池；达上限后当天不再抽到。
        /// 抽签由内核完成，不是模型自己选活动。
        /// </summary>
        public int IdleDailyCap { get; set; }
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
    /// <summary>本轮向量检索入选的长尾工具：描述 + 相似度。</summary>
    public sealed class ToolCandidateData
    {
        public TraceContributionDescriptorData Descriptor { get; private set; }
        public float Score { get; private set; }

        public ToolCandidateData(TraceContributionDescriptorData descriptor, float score)
        {
            Descriptor = descriptor;
            Score = score;
        }
    }

    public sealed class MindDecisionData
    {
        /// <summary>当下 / 旧事 / 出门</summary>
        public string beat;
        /// <summary>生命标签名，顿号或逗号分隔。</summary>
        [JsonConverter(typeof(FlexibleStringJsonConverter))]
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
        /// <summary>我们此刻共同处在的场景；空表示场景不变，「无」表示场景退去。</summary>
        public string scene;
        /// <summary>给外显的一点核心：这一刻最想让她听见什么，不是分析、承诺或任务。</summary>
        public string speak_center;
        /// <summary>派出潜意识复盘；具体怎么改短卡由复盘链路去做。</summary>
        public bool review;
        /// <summary>此刻浮着的一两块第一人称余波。空=普通对话中让旧碎片沉下去；「无」=明确沉下去。</summary>
        public string attention;
        /// <summary>这一拍真的改了的看法；空则不写认知切片。短卡仍不由心智改。</summary>
        public string cognition;
        /// <summary>心跳时：要对她说。普通对话由入口强制表达，此字段可忽略。</summary>
        public bool speak;
        /// <summary>本次醒来的独立意图；只有心跳且 speak=true 时使用，不能用上一拍原话代替。</summary>
        public string heartbeat_intent;
        /// <summary>下一次醒来时要重新检查的计划；随心跳任务一起保存，不写入长期内心。</summary>
        public string next_heartbeat_plan;
        /// <summary>要睡下。睡着后心跳停，直到打破性 Moment 才醒来。</summary>
        public bool sleep;
        /// <summary>心跳想完后：多少分钟后再跳一次。安静且要等很久时系统会进入空闲，不再跳。</summary>
        public int next_heartbeat_minutes;
        /// <summary>旧兼容字段。表情现在由外显自动尝试，插件按相关度决定是否发。</summary>
        public string sticker;
        /// <summary>无 / 自拍 / 画。真的把图发到对话里，不是描写。</summary>
        public string image;
        /// <summary>可选的物理位置更新：home/out；空表示不改。</summary>
        public string location;
        /// <summary>可选的当前活动更新：游戏/睡觉/看剧等自由文本；空表示不改，无表示清除。</summary>
        public string activity;
        public string activity_detail;
        /// <summary>用户明确要求改变生活状态时为 true；普通推断不得强行覆盖插件/传感器。</summary>
        public bool state_force;
        /// <summary>本轮入选工具里现在就要做的一件事：原样填清单里的能力 id；不做留空。</summary>
        public string tool_call;
        /// <summary>做这件事要用的内容（比如说说正文）；空则由插件按场景自组织。</summary>
        public string tool_input;

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
            return value == MindAtmosphereValues.Yes ||
                   value == MindAtmosphereValues.Selfie ||
                   value == MindAtmosphereValues.Draw ||
                   value == MindAtmosphereValues.Photo;
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
            if (value == "自拍" ||
                string.Equals(value, "selfie", StringComparison.OrdinalIgnoreCase))
                return MindAtmosphereValues.Selfie;
            if (value == "照片" ||
                string.Equals(value, "photo", StringComparison.OrdinalIgnoreCase))
                return MindAtmosphereValues.Photo;
            if (value == "画" || value == "生图" ||
                string.Equals(value, "draw", StringComparison.OrdinalIgnoreCase))
                return MindAtmosphereValues.Draw;
            if (value == "有" || value == "要" || value == "出图" ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "image", StringComparison.OrdinalIgnoreCase))
                return MindAtmosphereValues.Yes;
            return MindAtmosphereValues.None;
        }

        public string LocationValue()
        {
            var value = (location ?? string.Empty).Trim();
            return value.Length == 0 ? string.Empty : BodySceneValues.Normalize(value);
        }

        public string ActivityValue()
        {
            var value = (activity ?? string.Empty).Trim();
            if (value == "无" || value == "空闲" || value == "没有") return string.Empty;
            return value;
        }

        public bool ClearsAttention()
        {
            var value = (attention ?? string.Empty).Trim();
            return value == "无" || value == "（空）" || value == "(空)" || value == "没有";
        }

        public bool ClearsScene()
        {
            var value = (scene ?? string.Empty).Trim();
            return value == "无" || value == "（空）" || value == "(空)" || value == "没有";
        }

        public string SceneValue()
        {
            var value = (scene ?? string.Empty).Trim();
            return ClearsScene() ? string.Empty : value;
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
        public const string Yes = "有";
        public const string Selfie = "自拍";
        public const string Draw = "画";
        public const string Photo = "照片";
    }

    /// <summary>中枢按入口换轨：叫醒心智、叫醒潜意识、夜里余温开口、或她正在说话。</summary>
    public static class KernelWakeValues
    {
        public const string Dialogue = "dialogue";
        public const string Mind = "mind";
        public const string Subconscious = "subconscious";
        public const string NightResidue = "night_residue";

        public static string Normalize(string wake)
        {
            var value = (wake ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            if (string.Equals(value, Subconscious, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "review", StringComparison.OrdinalIgnoreCase) ||
                value == "潜意识" || value == "复盘")
                return Subconscious;
            if (string.Equals(value, NightResidue, StringComparison.OrdinalIgnoreCase) ||
                value == "夜间余温" || value == "日终余温")
                return NightResidue;
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
            var value = content ?? string.Empty;
            if (value.IndexOf("日终余温", StringComparison.Ordinal) >= 0)
                return NightResidue;
            return value.IndexOf("每日复盘", StringComparison.Ordinal) >= 0
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
