using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins
{
    public sealed class QqTtsPlugin : ITracePlugin
    {
        private const string PluginId = "qq.tts";
        private static readonly string[] ToneTags =
        {
            "laughs", "chuckle", "coughs", "clear-throat", "groans", "breath", "pant",
            "inhale", "exhale", "gasps", "sniffs", "sighs", "snorts", "burps",
            "lip-smacking", "humming", "hissing", "emm", "whistles", "sneezes"
        };
        private static readonly Regex ToneTagPattern = new Regex(
            "\\((" + string.Join("|", ToneTags.Select(Regex.Escape)) + ")\\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Dictionary<string, string> EmotionMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "开心", "happy" }, { "高兴", "happy" }, { "快乐", "happy" }, { "happy", "happy" },
                { "难过", "sad" }, { "悲伤", "sad" }, { "伤心", "sad" }, { "sad", "sad" },
                { "生气", "angry" }, { "愤怒", "angry" }, { "angry", "angry" },
                { "害怕", "fearful" }, { "恐惧", "fearful" }, { "fearful", "fearful" },
                { "厌恶", "disgusted" }, { "disgusted", "disgusted" },
                { "惊讶", "surprised" }, { "surprised", "surprised" },
                { "平静", "calm" }, { "中性", "calm" }, { "calm", "calm" }
            };

        private string providerId = string.Empty;
        private string configApiKey = string.Empty;
        private string configApiUrl = "https://xyzapi0613.online/v1/audio/speech";
        private string configModel = "voice-3.2-fs";
        private string apiKey = string.Empty;
        private string apiUrl = string.Empty;
        private string model = string.Empty;
        private string proxy = string.Empty;
        private string voice = "nova";
        private string audioFormat = "wav";
        private string promptMode = "auto";
        private int maxTextLength = 500;
        private int timeoutSeconds = 120;
        private int cleanupDelaySeconds = 1800;
        private bool failureFallbackText = true;
        private bool debug;
        private string dataDirectory = string.Empty;
        private TracePluginServices services;
        private readonly object filesGate = new object();
        private readonly HashSet<string> pendingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();

        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = PluginId,
            DisplayName = "QQ 语音（情感 TTS）",
            Version = "2.0.1",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Organ,
            PlatformId = BodyIds.Qq,
            Description = "完整情感语音器官：多段语音、情绪/语气词/raw 模式、文字回退与临时文件清理。"
        };

        public void Register(TracePluginContext context)
        {
            services = context.Services;
            dataDirectory = context.PluginDataDirectory ?? string.Empty;
            Directory.CreateDirectory(dataDirectory);
            LoadConfig(context.PackageDirectory, context.PluginDataDirectory);
            ApplyEndpoint(context.Services);
            Metadata.Note = IsReady(context.Services) ? string.Empty :
                "未配置：在齿轮里选供应商，或在「大脑 · LLM」设置默认语音槽。";
            context.AddMountedFacet(new UsageFacet(this));
            context.AddCallable(new VoiceEffector(this));
        }

        public void Shutdown()
        {
            shutdown.Cancel();
            List<string> files;
            lock (filesGate) files = pendingFiles.ToList();
            foreach (var path in files) TryDelete(path);
            shutdown.Dispose();
        }

        internal bool IsReady(TracePluginServices currentServices)
        {
            ApplyEndpoint(currentServices);
            return !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiUrl) &&
                   !string.IsNullOrWhiteSpace(model);
        }

        private void LoadConfig(string packageDirectory, string pluginDataDirectory)
        {
            ApplyConfig(Path.Combine(packageDirectory ?? string.Empty, "plugin.json"));
            ApplyConfig(Path.Combine(pluginDataDirectory ?? string.Empty, "config.json"));
            apiKey = configApiKey;
            apiUrl = configApiUrl;
            model = configModel;
        }

        private void ApplyConfig(string path)
        {
            if (!File.Exists(path)) return;
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var root = doc.RootElement;
                providerId = ReadString(root, "provider_id") ?? providerId;
                configApiKey = ReadString(root, "api_key") ?? configApiKey;
                configApiUrl = ReadString(root, "api_url") ?? ReadString(root, "tts_api_url") ?? configApiUrl;
                configModel = ReadString(root, "model") ?? ReadString(root, "model_id") ?? configModel;
                // voice_id 是正式配置；voice 只兼容 2.0.0 及更早的数据。
                voice = ReadString(root, "voice_id") ?? ReadString(root, "voice") ?? voice;
                audioFormat = (ReadString(root, "audio_format") ?? audioFormat).ToLowerInvariant();
                promptMode = (ReadString(root, "prompt_mode") ?? promptMode).ToLowerInvariant();
                proxy = ReadString(root, "proxy") ?? proxy;
                maxTextLength = ReadInt(root, "max_text_length", maxTextLength, 20, 4000);
                timeoutSeconds = ReadInt(root, "timeout", timeoutSeconds, 10, 600);
                cleanupDelaySeconds = ReadInt(root, "cleanup_delay_seconds", cleanupDelaySeconds, 10, 3600);
                failureFallbackText = ReadBool(root, "failure_fallback_text", failureFallbackText);
                debug = ReadBool(root, "debug", debug);
            }
        }

        private void ApplyEndpoint(TracePluginServices currentServices)
        {
            var endpoint = ResolveEndpoint(currentServices);
            if (endpoint != null && !string.IsNullOrWhiteSpace(endpoint.ApiKey))
            {
                apiKey = endpoint.ApiKey;
                model = string.IsNullOrWhiteSpace(configModel) ? endpoint.Model : configModel;
                apiUrl = SpeechUrl(endpoint.BaseUrl, configApiUrl);
                timeoutSeconds = endpoint.TimeoutSeconds > 0 ? endpoint.TimeoutSeconds : timeoutSeconds;
                if (string.IsNullOrWhiteSpace(proxy)) proxy = endpoint.Proxy ?? string.Empty;
            }
            else
            {
                apiKey = configApiKey ?? string.Empty;
                apiUrl = configApiUrl ?? string.Empty;
                model = configModel ?? string.Empty;
            }
        }

        private LlmEndpointData ResolveEndpoint(TracePluginServices currentServices)
        {
            if (currentServices?.Providers == null) return null;
            return string.IsNullOrWhiteSpace(providerId)
                ? currentServices.Providers.ResolveSlot(LlmSlotNames.Speech)
                : currentServices.Providers.Resolve(providerId, configModel);
        }

        private async Task<TraceCapabilityResultData> SendVoiceAsync(
            string text,
            string emotion,
            TraceTurnContext context,
            CancellationToken cancellationToken)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) throw new InvalidOperationException("QQ 语音需要要念出来的 text。");
            var adapter = FindAdapter(context);
            ApplyEndpoint(context.Services);
            if (!IsReady(context.Services))
                throw new InvalidOperationException("QQ 语音未配置可用供应商、模型或 API Key。");

            var timer = Stopwatch.StartNew();
            try
            {
                var path = await SynthesizeAsync(text, emotion, context.TraceId, cancellationToken);
                var result = await adapter.SendAsync(new TraceOutboundMessageData
                {
                    Kind = TraceOutboundKinds.Voice,
                    File = path
                }, context, cancellationToken);
                result.Summary = "QQ 语音：" + (string.IsNullOrWhiteSpace(emotion) ? "默认情绪" : emotion) +
                                 "（" + voice + " / " + model + " / " + promptMode + "）";
                ScheduleDelete(path);
                context.Services.LogTiming(context.TraceId, "QQ 语音合成发送完成", timer.ElapsedMilliseconds,
                    "chars=" + text.Length + "｜model=" + model);
                return result;
            }
            catch (Exception exception)
            {
                context.Services.LogTiming(context.TraceId, "QQ 语音合成发送失败", timer.ElapsedMilliseconds,
                    SafeMessage(exception));
                if (!failureFallbackText) throw;
                var fallback = await adapter.SendAsync(new TraceOutboundMessageData
                {
                    Kind = TraceOutboundKinds.Text,
                    Text = "（语音刚才没有成功发出来）\n" + text
                }, context, cancellationToken);
                fallback.Status = "failed";
                fallback.Summary = "语音失败，已回退文字：" + SafeMessage(exception);
                return fallback;
            }
        }

        private async Task<string> SynthesizeAsync(
            string sourceText,
            string emotion,
            string traceId,
            CancellationToken cancellationToken)
        {
            var text = sourceText.Trim();
            string engineEmotion = null;
            var isV32 = model.StartsWith("voice-3.2", StringComparison.OrdinalIgnoreCase);
            var effectiveMode = promptMode == "auto" ? (isV32 ? "tone_tags" : "emotion") : promptMode;
            if (effectiveMode == "raw")
            {
                engineEmotion = null;
            }
            else if (isV32 || effectiveMode == "tone_tags")
            {
                engineEmotion = null;
                if (!isV32) text = CleanToneTags(text);
            }
            else
            {
                text = CleanToneTags(text);
                if (!string.IsNullOrWhiteSpace(emotion) && EmotionMap.TryGetValue(emotion.Trim(), out var mapped))
                    engineEmotion = mapped;
            }
            if (text.Length > maxTextLength) text = text.Substring(0, maxTextLength);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("语音文本清洗后为空。");

            var payload = new Dictionary<string, object>
            {
                { "model", model }, { "input", text }, { "voice", voice },
                { "response_format", audioFormat }
            };
            if (!string.IsNullOrWhiteSpace(engineEmotion)) payload["emotion"] = engineEmotion;
            if (debug)
                services?.LogTiming(traceId, "TTS 请求", detail: "model=" + model + "｜mode=" + effectiveMode +
                    "｜emotion=" + (engineEmotion ?? "none") + "｜chars=" + text.Length);

            using (var handler = CreateHandler(proxy))
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) })
            using (var request = new HttpRequestMessage(HttpMethod.Post, apiUrl))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, cancellationToken))
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        var detail = TryDecodeText(bytes);
                        throw new InvalidOperationException("TTS HTTP " + (int)response.StatusCode +
                            (detail.Length == 0 ? string.Empty : "：" + Truncate(detail, 300)));
                    }
                    if (bytes.Length < 1000)
                        throw new InvalidOperationException("TTS 返回内容过小（" + bytes.Length + " bytes）：" +
                            Truncate(TryDecodeText(bytes), 200));
                    var dir = Path.Combine(dataDirectory, "generated");
                    Directory.CreateDirectory(dir);
                    var extension = audioFormat == "mp3" ? ".mp3" : ".wav";
                    var path = Path.Combine(dir, "voice_" + Guid.NewGuid().ToString("N") + extension);
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                    lock (filesGate) pendingFiles.Add(path);
                    return path;
                }
            }
        }

        private void ScheduleDelete(string path)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(cleanupDelaySeconds), shutdown.Token);
                    TryDelete(path);
                }
                catch (OperationCanceledException) { }
            });
        }

        private void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
            catch { }
            lock (filesGate) pendingFiles.Remove(path ?? string.Empty);
        }

        private static ITracePlatformAdapter FindAdapter(TraceTurnContext context)
        {
            var adapter = context?.Services?.PlatformAdapters?.FirstOrDefault(x =>
                x != null && string.Equals(x.PlatformId, "builtin.onebot", StringComparison.Ordinal));
            if (adapter == null) throw new InvalidOperationException("QQ 平台适配器不可用。");
            return adapter;
        }

        private static HttpClientHandler CreateHandler(string proxy)
        {
            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                handler.Proxy = new WebProxy(proxy.Trim());
                handler.UseProxy = true;
            }
            return handler;
        }

        private static string SpeechUrl(string baseUrl, string overrideUrl)
        {
            if (!string.IsNullOrWhiteSpace(overrideUrl) &&
                overrideUrl.IndexOf("/audio/speech", StringComparison.OrdinalIgnoreCase) >= 0)
                return overrideUrl.Trim();
            var root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (root.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase)) return root;
            return root.Length == 0 ? (overrideUrl ?? string.Empty) : root + "/audio/speech";
        }

        private static string CleanToneTags(string text)
        {
            text = ToneTagPattern.Replace(text ?? string.Empty, string.Empty);
            text = Regex.Replace(text, " +", " ");
            return Regex.Replace(text, " ([，。！？、：；])", "$1").Trim();
        }

        private static string ReadString(JsonElement root, string key)
        {
            return root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }

        private static int ReadInt(JsonElement root, string key, int fallback, int min, int max)
        {
            return root.TryGetProperty(key, out var value) && value.TryGetInt32(out var parsed)
                ? Math.Max(min, Math.Min(max, parsed)) : fallback;
        }

        private static bool ReadBool(JsonElement root, string key, bool fallback)
        {
            return root.TryGetProperty(key, out var value) &&
                   (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                ? value.GetBoolean() : fallback;
        }

        private static string TryDecodeText(byte[] bytes)
        {
            try { return bytes == null ? string.Empty : Encoding.UTF8.GetString(bytes); }
            catch { return string.Empty; }
        }

        private static string Truncate(string text, int max)
        {
            text = text ?? string.Empty;
            return text.Length <= max ? text : text.Substring(0, max);
        }

        private static string SafeMessage(Exception exception)
        {
            return Truncate(exception?.Message ?? "未知错误", 500);
        }

        private sealed class UsageFacet : ITraceMountedFacet
        {
            private readonly QqTtsPlugin owner;
            public UsageFacet(QqTtsPlugin owner) { this.owner = owner; }
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "qq.voice.usage", Kind = TraceContributionKindValues.MountedFacet,
                DisplayName = "QQ 语音用法", Provides = "platform.qq.voice_usage",
                RefreshMode = "once_per_turn", Priority = 90, MaxContextChars = 800
            };
            public bool IsAvailable(TraceTurnContext context) => context != null && owner.IsReady(context.Services);
            public Task<TraceContextBlockData> BuildContextAsync(TraceTurnContext context, CancellationToken token)
            {
                return Task.FromResult(new TraceContextBlockData
                {
                    Title = "QQ 语音用法",
                    Content = QqTtsPrompts.Usage
                });
            }
            public Task<TraceCapabilityResultData> ApplyOutputAsync(BrainFacetOutputData output,
                TraceTurnContext context, CancellationToken token) => Task.FromResult<TraceCapabilityResultData>(null);
        }

        private sealed class VoiceEffector : ITraceCallableContribution
        {
            private readonly QqTtsPlugin owner;
            public VoiceEffector(QqTtsPlugin owner) { this.owner = owner; }
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "qq.voice.send", Kind = TraceContributionKindValues.Effector,
                DisplayName = "QQ 发语音", Description = QqTtsPrompts.EffectorDescription,
                Provides = "expression.qq.voice", Boundary = QqTtsPrompts.EffectorBoundary,
                BodyId = BodyIds.Qq, BodyTier = BodyTierValues.Chat, Organ = BodyOrganValues.Voice,
                ParametersJsonSchema = "{text:string,emotion?:string}", HasExternalSideEffect = true
            };
            public bool IsAvailable(TraceTurnContext context) => context != null && owner.IsReady(context.Services);
            public Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,
                TraceTurnContext context, CancellationToken token)
            {
                return owner.SendVoiceAsync(call.GetArgument("text"), call.GetArgument("emotion"), context, token);
            }
        }
    }
}
