using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins
{
    /// <summary>
    /// QQ 心情器官：改个性签名和/或在线状态。
    /// 走 NapCat set_self_longnick / set_online_status。
    /// </summary>
    public sealed class QqStatusPlugin : ITracePlugin
    {
        private const string PluginId = "qq.status";
        private int moodDailyCap = 1;

        private static readonly Dictionary<string, StatusCode> StatusCodes =
            new Dictionary<string, StatusCode>(StringComparer.Ordinal)
            {
                { "在线", new StatusCode(10, 0) },
                { "Q我吧", new StatusCode(60, 0) },
                { "离开", new StatusCode(30, 0) },
                { "忙碌", new StatusCode(50, 0) },
                { "请勿打扰", new StatusCode(70, 0) },
                { "隐身", new StatusCode(40, 0) },
                { "听歌中", new StatusCode(10, 1028) },
                { "被掏空", new StatusCode(10, 2014) },
                { "爱你", new StatusCode(10, 2006) },
                { "恋爱中", new StatusCode(10, 1051) },
                { "嗨到飞起", new StatusCode(10, 1056) },
                { "元气满满", new StatusCode(10, 1058) },
                { "一言难尽", new StatusCode(10, 1063) },
                { "emo中", new StatusCode(10, 1401) },
                { "我太难了", new StatusCode(10, 1062) },
                { "我没事", new StatusCode(10, 1052) },
                { "想静静", new StatusCode(10, 1061) },
                { "悠哉哉", new StatusCode(10, 1059) },
                { "学习中", new StatusCode(10, 1018) },
                { "搬砖中", new StatusCode(10, 2023) },
                { "摸鱼中", new StatusCode(10, 1300) },
                { "无聊中", new StatusCode(10, 1060) },
                { "睡觉中", new StatusCode(10, 1016) },
                { "熬夜中", new StatusCode(10, 1032) },
                { "追剧中", new StatusCode(10, 1021) }
            };

        private static readonly Dictionary<string, string> StatusAliases =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "难过", "emo中" },
                { "伤心", "emo中" },
                { "开心", "元气满满" },
                { "高兴", "元气满满" },
                { "快乐", "嗨到飞起" },
                { "疲惫", "被掏空" },
                { "累了", "被掏空" },
                { "困了", "睡觉中" },
                { "困", "睡觉中" },
                { "无聊", "无聊中" },
                { "摸鱼", "摸鱼中" },
                { "学习", "学习中" },
                { "工作", "搬砖中" },
                { "忙", "忙碌" },
                { "勿扰", "请勿打扰" }
            };

        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = PluginId,
            DisplayName = "QQ 心情",
            Version = "1.0.0",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Organ,
            PlatformId = BodyIds.Qq,
            Description = "QQ 感官：改个性签名和在线状态。"
        };

        public void Register(TracePluginContext context)
        {
            LoadConfig(context.PackageDirectory, context.PluginDataDirectory);
            context.AddMountedFacet(new UsageFacet());
            context.AddCallable(new MoodEffector(this));
        }

        public void Shutdown() { }

        private void LoadConfig(string packageDirectory, string pluginDataDirectory)
        {
            ApplyConfig(Path.Combine(packageDirectory ?? string.Empty, "plugin.json"));
            ApplyConfig(Path.Combine(pluginDataDirectory ?? string.Empty, "config.json"));
        }

        private void ApplyConfig(string path)
        {
            if (!File.Exists(path)) return;
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                moodDailyCap = ReadCap(doc.RootElement, "mood_daily_cap", moodDailyCap);
            }
        }

        internal async Task<TraceCapabilityResultData> ApplyFromCallAsync(
            BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
        {
            var idle = IsIdleCall(call);
            var signature = call == null ? string.Empty : FirstNonEmpty(
                call.GetArgument("signature"), call.GetArgument("longnick"), call.GetArgument("text"));
            var status = call == null ? string.Empty : FirstNonEmpty(
                call.GetArgument("status"), call.GetArgument("mood"));
            if (idle && LooksLikeNone(signature) && LooksLikeNone(status))
            {
                var composed = await ComposeAsync(
                    call == null ? string.Empty : call.GetArgument("seed"), context, cancellationToken);
                signature = composed.Signature;
                status = composed.Status;
            }
            signature = NormalizeText(signature);
            status = NormalizeStatusName(status);
            if (LooksLikeNone(signature) && LooksLikeNone(status))
            {
                return new TraceCapabilityResultData
                {
                    Status = "skipped",
                    Summary = idle ? "空闲抽到改心情，没有想改的。" : "改心情需要 signature 或 status。",
                    Payload = string.Empty,
                    EvidenceRefs = new List<string>()
                };
            }

            var adapter = context.Services.PlatformAdapters
                .FirstOrDefault(x => x != null && x.PlatformId == "builtin.onebot");
            if (adapter == null) throw new InvalidOperationException("QQ 平台适配器不可用。");

            var done = new List<string>();
            if (!LooksLikeNone(signature))
            {
                var json = await adapter.CallActionAsync("set_self_longnick",
                    new Dictionary<string, object> { { "longNick", signature } }, cancellationToken);
                EnsureActionOk(json, "改签名");
                done.Add("签名");
            }
            if (!LooksLikeNone(status))
            {
                StatusCode code;
                if (!StatusCodes.TryGetValue(status, out code))
                    throw new InvalidOperationException("不支持的在线状态：" + status);
                var json = await adapter.CallActionAsync("set_online_status",
                    new Dictionary<string, object>
                    {
                        { "status", code.Status },
                        { "ext_status", code.ExtStatus },
                        { "battery_status", 0 }
                    }, cancellationToken);
                EnsureActionOk(json, "改状态");
                done.Add(status);
            }

            var summary = "已改 QQ 心情：" + string.Join("、", done) + "。";
            return new TraceCapabilityResultData
            {
                Status = "success",
                Summary = summary,
                Payload = (LooksLikeNone(signature) ? string.Empty : signature) +
                          (LooksLikeNone(status) ? string.Empty : "\n" + status),
                ProducedEvent = new PluginEventData
                {
                    PluginId = PluginId,
                    ExternalEventId = Guid.NewGuid().ToString("N"),
                    Role = "system_event",
                    Content = summary,
                    Realm = TraceRealmValues.ExternalWorld,
                    EvidenceType = EvidenceTypeValues.AssPerformed,
                    // 空闲时自主改的签名也是真实发生的行为：入 Moment（物理痕迹），不只留运行回执。
                    IsOperational = false,
                    OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                },
                EvidenceRefs = new List<string>()
            };
        }

        private async Task<MoodDraft> ComposeAsync(
            string seed, TraceTurnContext context, CancellationToken cancellationToken)
        {
            var empty = new MoodDraft();
            if (context == null || context.Services == null || context.Services.Llm == null)
                return empty;
            var llm = context.Services.Llm;
            var names = string.Join("、", StatusCodes.Keys);
            var instructions = QqStatusPrompts.IdleInstructions +
                               QqStatusPrompts.StatusNamesHeader + names + "。";
            var user = (seed ?? string.Empty).Trim();
            if (user.Length == 0) user = "（没有更多此刻材料）";
            List<DeepSeekMessageData> messages;
            var packer = context.Services.ContextPack;
            string cacheKey = null;
            if (packer != null)
            {
                var memory = context.Workspace == null ? string.Empty : context.Workspace.SharedMemory;
                messages = packer.Assemble(
                    llm, context, memory ?? string.Empty, user,
                    QqStatusPrompts.IdleRoleHeader, instructions);
                cacheKey = packer.BuildPromptCacheKey(llm, context.ConversationId);
            }
            else
            {
                messages = new List<DeepSeekMessageData>
                {
                    new DeepSeekMessageData("system", instructions),
                    new DeepSeekMessageData("user", user)
                };
            }
            var raw = await llm.CompleteTextAsync(messages, cancellationToken, cacheKey);
            return ParseDraft(raw);
        }

        public static MoodDraft ParseDraft(string raw)
        {
            var text = StripFence(raw);
            return new MoodDraft
            {
                Signature = ReadLabeledLine(text, "签名"),
                Status = ReadLabeledLine(text, "状态")
            };
        }

        public static string NormalizeStatusName(string value)
        {
            var text = NormalizeText(value);
            if (LooksLikeNone(text)) return string.Empty;
            string mapped;
            if (StatusAliases.TryGetValue(text, out mapped)) text = mapped;
            if (StatusCodes.ContainsKey(text)) return text;
            foreach (var name in StatusCodes.Keys)
            {
                if (text.IndexOf(name, StringComparison.Ordinal) >= 0) return name;
            }
            return string.Empty;
        }

        private static void EnsureActionOk(string json, string action)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var status) &&
                        status.ValueKind == JsonValueKind.String &&
                        string.Equals(status.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                            ? m.GetString()
                            : string.Empty;
                        throw new InvalidOperationException(action + "失败" +
                            (string.IsNullOrWhiteSpace(message) ? string.Empty : "：" + message));
                    }
                    if (root.TryGetProperty("retcode", out var code) &&
                        code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
                        throw new InvalidOperationException(action + "失败：retcode=" + code.GetInt32());
                }
            }
            catch (JsonException)
            {
                /* 非 JSON 当已投递 */
            }
        }

        private static string ReadLabeledLine(string text, string label)
        {
            var prefix = label + "：";
            var alt = label + ":";
            foreach (var line in (text ?? string.Empty).Split(new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var value = line.Trim();
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                    return value.Substring(prefix.Length).Trim();
                if (value.StartsWith(alt, StringComparison.Ordinal))
                    return value.Substring(alt.Length).Trim();
            }
            return string.Empty;
        }

        private static string StripFence(string raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var start = text.IndexOf('\n');
                var end = text.LastIndexOf("```", StringComparison.Ordinal);
                if (start >= 0 && end > start) text = text.Substring(start + 1, end - start - 1).Trim();
            }
            return text.Trim().Trim('"');
        }

        private static string NormalizeText(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static bool LooksLikeNone(string value)
        {
            var text = NormalizeText(value).Trim('。', '.', '！', '!', '～', '~', ' ');
            if (text.Length == 0) return true;
            return text == "无" || text == "没有" || text == "不改" || text == "不想改" ||
                   text == "（无）" || text == "(无)" ||
                   string.Equals(text, "none", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "skip", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIdleCall(BrainCapabilityCallData call)
        {
            var raw = call == null ? string.Empty : (call.GetArgument("idle") ?? string.Empty).Trim();
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
                   raw == "1" ||
                   string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return string.Empty;
        }

        private static int ReadCap(JsonElement root, string name, int fallback)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
                return fallback;
            int parsed;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out parsed))
                return ClampCap(parsed);
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out parsed))
                return ClampCap(parsed);
            return fallback;
        }

        private static int ClampCap(int value)
        {
            if (value < 0) return 0;
            return value > 20 ? 20 : value;
        }

        private sealed class StatusCode
        {
            public StatusCode(int status, int extStatus)
            {
                Status = status;
                ExtStatus = extStatus;
            }
            public int Status;
            public int ExtStatus;
        }

        public sealed class MoodDraft
        {
            public string Signature = string.Empty;
            public string Status = string.Empty;
        }

        private sealed class UsageFacet : ITraceMountedFacet
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "qq.status.usage",
                Kind = TraceContributionKindValues.MountedFacet,
                DisplayName = "QQ 心情用法",
                Description = "QQ 心情插件注入：什么时候该改签名或状态。",
                Provides = "platform.qq.status_usage",
                RefreshMode = TraceFacetRefreshValues.OncePerTurn,
                Priority = 90,
                MaxContextChars = 280
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceContextBlockData> BuildContextAsync(
                TraceTurnContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult(new TraceContextBlockData
                {
                    Title = "QQ 心情用法",
                    Content = QqStatusPrompts.Usage
                });
            }

            public Task<TraceCapabilityResultData> ApplyOutputAsync(
                BrainFacetOutputData output, TraceTurnContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult<TraceCapabilityResultData>(null);
            }
        }

        private sealed class MoodEffector : ITraceCallableContribution
        {
            private readonly QqStatusPlugin owner;
            public MoodEffector(QqStatusPlugin owner)
            {
                this.owner = owner;
                Descriptor = new TraceContributionDescriptorData
                {
                    Id = "qq.status.mood",
                    Kind = TraceContributionKindValues.Effector,
                    DisplayName = "QQ 改心情",
                    Description = QqStatusPrompts.Description,
                    Provides = "expression.qq.status",
                    WhenToUse = QqStatusPrompts.WhenToUse,
                    WhenNotToUse = QqStatusPrompts.WhenNotToUse,
                    Boundary = QqStatusPrompts.Boundary,
                    BodyId = BodyIds.Qq,
                    BodyTier = BodyTierValues.Chat,
                    Organ = "status",
                    ParametersJsonSchema = "{signature:string,status:string}",
                    HasExternalSideEffect = true,
                    IdleDailyCap = owner.moodDailyCap
                };
            }

            public TraceContributionDescriptorData Descriptor { get; }

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
            {
                return owner.ApplyFromCallAsync(call, context, cancellationToken);
            }
        }
    }
}
