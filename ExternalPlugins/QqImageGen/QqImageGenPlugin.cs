using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins
{
    public sealed class QqImageGenPlugin : ITracePlugin
    {
        private const string PluginId = "qq.imagegen";
        private static readonly Regex AppearanceDetails = new Regex(
            @"(?:白发|银发|黑发|棕发|金发|红发|蓝发|紫发|长发|短发|卷发|直发|双马尾|马尾|刘海|红瞳|蓝瞳|金瞳|紫瞳|绿瞳|黑瞳|棕瞳|狐狸耳朵?|狐耳|猫耳|兽耳|尾巴)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private string providerId = string.Empty;
        private string plannerProviderId = string.Empty;
        private string plannerModel = string.Empty;
        private string configApiKey = string.Empty;
        private readonly List<string> configApiKeys = new List<string>();
        private string configBaseUrl = string.Empty;
        private string configModel = string.Empty;
        private string apiFormat = "auto";
        private string apiMode = "auto";
        private string proxy = string.Empty;
        private string safetySettings = "BLOCK_NONE";
        private string imageSize = "2K";
        private string standardSize = "1024x1024";
        private string defaultAspectRatio = "4:3";
        private string defaultMode = "auto";
        private string selfieMode = "lockface";
        private string stylePrompt = string.Empty;
        private string characterDetails = string.Empty;
        private readonly List<string> characterCategories = new List<string>();
        private readonly List<string> characterRefUrls = new List<string>();
        private int timeoutSeconds = 600;
        private int maxRetries = 3;
        private int pollIntervalSeconds = 5;
        private int maxRefsPerCategory = 4;
        private int maxReferenceImages = 12;
        private int cleanupDelaySeconds = 600;
        private bool aiDecideAspectRatio = true;
        private bool strictReferenceMode;
        private bool enableThinking;
        private bool failureFallbackText = true;
        private bool logRequestBody;
        private bool debug;
        private string dataDirectory = string.Empty;
        private TracePluginServices services;
        private ReferenceLibrary references;
        private readonly object filesGate = new object();
        private readonly HashSet<string> pendingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private Func<TraceTurnContext, string> mindUsageAppend;
        private Func<TraceTurnContext, string> mindJsonField;

        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = PluginId,
            DisplayName = "QQ 相机与生图",
            Version = "2.2.0",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Organ,
            PlatformId = BodyIds.Qq,
            Description = "完整相机器官：自拍、锁脸参考、服饰参考、画图、改图、URL 发图及多协议生图。"
        };

        public void Register(TracePluginContext context)
        {
            services = context.Services;
            dataDirectory = context.PluginDataDirectory ?? string.Empty;
            Directory.CreateDirectory(dataDirectory);
            LoadConfig(context.PackageDirectory, context.PluginDataDirectory);
            references = new ReferenceLibrary(dataDirectory);
            Metadata.Note = IsReady(context.Services) ? string.Empty :
                "未配置：在齿轮里选供应商，或在「大脑 · LLM」设置默认生图槽。";
            var runtime = ResolveSettings(context.Services);
            context.Services.LogTiming(null, "TA的相机 插件已加载", detail:
                "version=" + Metadata.Version + "｜model=" + (runtime.Model ?? "(空)") +
                "｜base_url=" + SafeEndpoint(runtime.BaseUrl) + "｜api_keys=" + runtime.ApiKeys.Count +
                "｜planner=" + (string.IsNullOrWhiteSpace(plannerProviderId)
                    ? "(开口模型)" : plannerProviderId + "/" + plannerModel) +
                "｜图库=" + references.Describe());
            context.AddCallable(new ImageEffector(this));
            AttachMindHooks(context.Services);
        }

        public void Shutdown()
        {
            DetachMindHooks();
            shutdown.Cancel();
            List<string> files;
            lock (filesGate) files = pendingFiles.ToList();
            foreach (var path in files) TryDelete(path);
            shutdown.Dispose();
        }

        private void AttachMindHooks(TracePluginServices current)
        {
            if (current == null) return;
            try
            {
                if (mindUsageAppend == null)
                    mindUsageAppend = turn =>
                        turn != null && IsReady(turn.Services) ? QqImageGenPrompts.MindUsage : null;
                if (mindJsonField == null)
                    mindJsonField = turn =>
                        turn != null && IsReady(turn.Services) ? "\"image\":\"有|无\"" : null;
                if (!current.MindPromptAppends.Contains(mindUsageAppend))
                    current.MindPromptAppends.Add(mindUsageAppend);
                if (!current.MindJsonFields.Contains(mindJsonField))
                    current.MindJsonFields.Add(mindJsonField);
            }
            catch (MissingMethodException)
            {
                // 宿主 PluginApi 尚未包含心智扩展槽时跳过挂载。
            }
        }

        private void DetachMindHooks()
        {
            if (services == null) return;
            try
            {
                if (mindUsageAppend != null) services.MindPromptAppends.Remove(mindUsageAppend);
                if (mindJsonField != null) services.MindJsonFields.Remove(mindJsonField);
            }
            catch (MissingMethodException)
            {
                // 宿主 PluginApi 比插件旧时，关机不应再炸。
            }
        }

        internal bool IsReady(TracePluginServices currentServices)
        {
            var settings = ResolveSettings(currentServices);
            return settings.ApiKeys.Count > 0 && !string.IsNullOrWhiteSpace(settings.BaseUrl) &&
                   !string.IsNullOrWhiteSpace(settings.Model);
        }

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
                var root = doc.RootElement;
                providerId = ReadString(root, "provider_id") ?? ReadString(root, "image_provider_id") ?? providerId;
                plannerProviderId = ReadString(root, "planner_provider_id") ?? plannerProviderId;
                plannerModel = ReadString(root, "planner_model") ?? plannerModel;
                configApiKey = ReadString(root, "api_key") ?? configApiKey;
                ReplaceList(configApiKeys, ReadStrings(root, "api_keys"));
                configBaseUrl = ReadString(root, "base_url") ?? configBaseUrl;
                configModel = ReadString(root, "model") ?? configModel;
                apiFormat = (ReadString(root, "image_api_format") ?? apiFormat).ToLowerInvariant();
                apiMode = (ReadString(root, "api_mode") ?? apiMode).ToLowerInvariant();
                defaultMode = (ReadString(root, "default_mode") ?? defaultMode).ToLowerInvariant();
                defaultAspectRatio = ReadString(root, "aspect_ratio") ?? defaultAspectRatio;
                selfieMode = (ReadString(root, "selfie_mode") ?? selfieMode).ToLowerInvariant();
                stylePrompt = ReadString(root, "style_prompt") ?? stylePrompt;
                characterDetails = ReadString(root, "character_details") ?? characterDetails;
                ReplaceList(characterCategories, ReadStrings(root, "character_categories"));
                ReplaceList(characterRefUrls, ReadStrings(root, "character_ref_urls"));
                imageSize = ReadString(root, "image_size") ?? imageSize;
                standardSize = ReadString(root, "standard_size") ?? ReadString(root, "size") ?? standardSize;
                safetySettings = ReadString(root, "safety_settings") ?? safetySettings;
                proxy = ReadString(root, "proxy") ?? proxy;
                timeoutSeconds = ReadInt(root, "timeout", timeoutSeconds, 30, 1800);
                maxRetries = ReadInt(root, "max_retry_attempts", maxRetries, 1, 10);
                pollIntervalSeconds = ReadInt(root, "poll_interval", pollIntervalSeconds, 2, 30);
                maxRefsPerCategory = ReadInt(root, "max_refs_per_category", maxRefsPerCategory, 1, 12);
                maxReferenceImages = ReadInt(root, "max_reference_images", maxReferenceImages, 1, 24);
                cleanupDelaySeconds = ReadInt(root, "cleanup_delay_seconds", cleanupDelaySeconds, 30, 7200);
                aiDecideAspectRatio = ReadBool(root, "ai_decide_aspect_ratio", aiDecideAspectRatio);
                strictReferenceMode = ReadBool(root, "strict_reference_mode",
                    ReadBool(root, "strict_ref_mode", strictReferenceMode));
                enableThinking = ReadBool(root, "enable_thinking", enableThinking);
                failureFallbackText = ReadBool(root, "failure_fallback_text", failureFallbackText);
                logRequestBody = ReadBool(root, "log_request_body", logRequestBody);
                debug = ReadBool(root, "debug", debug);
            }
        }

        private ImageGenerationSettings ResolveSettings(TracePluginServices currentServices)
        {
            var endpoint = ResolveEndpoint(currentServices);
            var keys = new List<string>();
            if (endpoint != null && !string.IsNullOrWhiteSpace(endpoint.ApiKey)) keys.Add(endpoint.ApiKey);
            if (!string.IsNullOrWhiteSpace(configApiKey)) keys.Add(configApiKey);
            keys.AddRange(configApiKeys);
            return new ImageGenerationSettings
            {
                BaseUrl = endpoint != null && !string.IsNullOrWhiteSpace(endpoint.BaseUrl)
                    ? endpoint.BaseUrl : configBaseUrl,
                Model = !string.IsNullOrWhiteSpace(configModel) ? configModel : endpoint?.Model,
                ApiKeys = keys.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                    .Distinct(StringComparer.Ordinal).ToList(),
                ApiFormat = apiFormat,
                ApiMode = apiMode,
                Proxy = string.IsNullOrWhiteSpace(proxy) ? endpoint?.Proxy : proxy,
                SafetySettings = safetySettings,
                ImageSize = imageSize,
                StandardSize = standardSize,
                TimeoutSeconds = endpoint != null && endpoint.TimeoutSeconds > 0 ?
                    Math.Max(timeoutSeconds, endpoint.TimeoutSeconds) : timeoutSeconds,
                MaxRetries = maxRetries,
                PollIntervalSeconds = pollIntervalSeconds,
                EnableThinking = enableThinking,
                LogRequestBody = logRequestBody,
                Debug = debug
            };
        }

        private LlmEndpointData ResolveEndpoint(TracePluginServices currentServices)
        {
            if (currentServices?.Providers == null) return null;
            return string.IsNullOrWhiteSpace(providerId)
                ? currentServices.Providers.ResolveSlot(LlmSlotNames.Image)
                : currentServices.Providers.Resolve(providerId, configModel);
        }

        private async Task<TraceCapabilityResultData> DispatchImageAsync(
            BrainCapabilityCallData call,
            TraceTurnContext context,
            CancellationToken cancellationToken)
        {
            var dispatch = (call.GetArgument("dispatch") ?? "all").Trim().ToLowerInvariant();
            if (dispatch == "send")
                return await SendPreparedFilesAsync(call, context, cancellationToken);
            return await GenerateThenMaybeSendAsync(call, context, cancellationToken, send: dispatch != "generate");
        }

        private async Task<TraceCapabilityResultData> GenerateThenMaybeSendAsync(
            BrainCapabilityCallData call,
            TraceTurnContext context,
            CancellationToken cancellationToken,
            bool send)
        {
            var timer = Stopwatch.StartNew();
            var prompt = (call.GetArgument("prompt") ?? string.Empty).Trim();
            var mode = NormalizeMode(call.GetArgument("mode"), prompt, context);
            var directUrl = (call.GetArgument("url") ?? string.Empty).Trim();
            context.Services.LogTiming(context.TraceId, send ? "TA的相机 === 开始生图 ===" : "TA的相机 === 后台生图 ===",
                detail: "mode=" + mode + "｜description=" + TruncateForLog(prompt, 300));
            if (mode == "url" || directUrl.Length > 0)
            {
                var url = directUrl.Length > 0 ? directUrl : prompt;
                if (!send) return PreparedFilesResult(new[] { url }, mode, "url");
                return await SendDirectUrlAsync(url, context, cancellationToken);
            }
            if (prompt.Length == 0) throw new InvalidOperationException("生图需要 prompt。URL 发图请同时给 url。 ");
            var plannedReferences = new List<string>();
            if (mode != "url" && mode != "edit")
            {
                var planned = await PlanSceneAsync(prompt, context, cancellationToken);
                prompt = planned.Scene;
                mode = planned.Mode;
                plannedReferences.AddRange(planned.ReferenceCategories);
            }
            var aspectRatio = NormalizeAspectRatio(call.GetArgument("aspect_ratio"), mode);

            try
            {
                references.Reload();
                context.Services.LogTiming(context.TraceId, "TA的相机 参考图库已刷新", detail: references.Describe());
                var requested = SplitCategories(call.GetArgument("refs"));
                foreach (var planned in plannedReferences)
                    if (!requested.Contains(planned, StringComparer.OrdinalIgnoreCase))
                        requested.Add(planned);
                foreach (var configured in characterCategories)
                    if ((mode == "selfie" || mode == "photo") && !requested.Contains(configured, StringComparer.OrdinalIgnoreCase))
                        requested.Insert(0, configured);
                var needsCharacter = mode == "selfie" || mode == "photo" ||
                                     prompt.IndexOf("阿循", StringComparison.OrdinalIgnoreCase) >= 0;
                var selected = references.Resolve(requested, needsCharacter, maxRefsPerCategory, maxReferenceImages);
                if (requested.Any(x => string.Equals(x, "当前消息", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(x, "用户图片", StringComparison.OrdinalIgnoreCase)))
                {
                    var inboundReference = await LoadInboundImagesAsync(context, cancellationToken);
                    selected.InsertRange(0, inboundReference);
                    selected = selected.Take(maxReferenceImages).ToList();
                }
                if (needsCharacter && characterRefUrls.Count > 0)
                {
                    var fixedOutfit = await LoadLocationsAsync(characterRefUrls.Take(4), "日常服饰参考", "服饰",
                        context, cancellationToken);
                    selected.AddRange(fixedOutfit);
                    selected = selected.Take(maxReferenceImages).ToList();
                }
                if (mode == "edit")
                {
                    var inbound = await LoadInboundImagesAsync(context, cancellationToken);
                    selected.InsertRange(0, inbound);
                    selected = selected.Take(maxReferenceImages).ToList();
                    if (selected.Count == 0)
                        throw new InvalidOperationException("改图需要当前消息中的图片，或 refs 指向参考图库分类。 ");
                }
                var finalPrompt = BuildPrompt(prompt, mode, selected, aspectRatio);
                var settings = ResolveSettings(context.Services);
                context.Services.LogTiming(context.TraceId, "TA的相机 本次参考图", detail:
                    "requested=" + (requested.Count == 0 ? "(无)" : string.Join(",", requested)) +
                    "｜selected=" + (selected.Count == 0 ? "(无)" : string.Join("；", selected.Select(x =>
                        x.Category + "/" + x.Role + "/" + x.FileName))) +
                    "｜count=" + selected.Count);
                context.Services.LogTiming(context.TraceId, "TA的相机 最终描述", detail:
                    TruncateForLog(finalPrompt, logRequestBody ? 1600 : 500));
                if (settings.ApiKeys.Count == 0 || string.IsNullOrWhiteSpace(settings.BaseUrl) ||
                    string.IsNullOrWhiteSpace(settings.Model))
                    throw new InvalidOperationException("生图供应商未配置完整（base_url、model、api_key）。");
                context.Services.LogTiming(context.TraceId, "TA的相机 本次请求运行参数", detail:
                    "model=" + settings.Model + "｜base_url=" + SafeEndpoint(settings.BaseUrl) +
                    "｜api_mode=" + settings.ApiMode + "｜format=" + settings.ApiFormat +
                    "｜api_key_count=" + settings.ApiKeys.Count + "｜api_key_len=" +
                    string.Join(",", settings.ApiKeys.Select(x => x == null ? 0 : x.Length)) +
                    "｜timeout=" + settings.TimeoutSeconds + "s｜retries=" + settings.MaxRetries);

                using (var client = new ImageGenerationClient(settings, context.Services, context.TraceId))
                {
                    var generated = await client.GenerateAsync(finalPrompt, selected, aspectRatio, cancellationToken);
                    if (!generated.Success) throw new InvalidOperationException(generated.Error);
                    var paths = new List<string>();
                    foreach (var image in generated.Images)
                    {
                        var path = await SaveImageAsync(image, cancellationToken);
                        paths.Add(path);
                        context.Services.LogTiming(context.TraceId, "TA的相机 图片已落盘", detail:
                            "source=" + (image.Source ?? "unknown") + "｜bytes=" + (image.Bytes?.Length ?? 0) +
                            "｜mime=" + (image.MimeType ?? string.Empty) + "｜file=" + path);
                    }
                    if (!send) return PreparedFilesResult(paths, mode, generated.Protocol);
                    return await SendFileListAsync(paths, mode, context, cancellationToken, timer);
                }
            }
            catch (Exception exception)
            {
                var detail = ExceptionDetail(exception);
                context.Services.LogTiming(context.TraceId, "QQ 相机生成发送失败", timer.ElapsedMilliseconds, detail);
                if (!send || !failureFallbackText)
                    return new TraceCapabilityResultData
                    {
                        Status = "failed",
                        Summary = "生图失败：" + detail,
                        Payload = string.Empty
                    };
                var fallback = await FindAdapter(context).SendAsync(new TraceOutboundMessageData
                {
                    Kind = TraceOutboundKinds.Text,
                    Text = "（照片刚才没有成功发出来：" + detail + "）"
                }, context, cancellationToken);
                fallback.Status = "failed";
                fallback.Summary = "生图失败，已明确告知：" + detail;
                return fallback;
            }
        }

        private async Task<TraceCapabilityResultData> SendDirectUrlAsync(
            string url, TraceTurnContext context, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("URL 发图需要 http/https 图片地址。 ");
            var result = await FindAdapter(context).SendAsync(new TraceOutboundMessageData
            {
                Kind = TraceOutboundKinds.Image,
                File = url
            }, context, cancellationToken);
            context.Services.LogTiming(context.TraceId, "TA的相机 网络图片发送完成", detail:
                "url=" + TruncateForLog(url, 180) + "｜status=" + (result?.Status ?? "null"));
            result.Summary = "已把指定 URL 图片发送到当前 QQ 会话。";
            return result;
        }

        private static TraceCapabilityResultData PreparedFilesResult(IEnumerable<string> files, string mode, string protocol)
        {
            var paths = (files ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return new TraceCapabilityResultData
            {
                Status = paths.Count == 0 ? "failed" : "success",
                Summary = "QQ 相机已生成 " + paths.Count + " 张，待发送（" + mode + " / " + protocol + "）。",
                Payload = string.Join("\n", paths)
            };
        }

        private async Task<TraceCapabilityResultData> SendPreparedFilesAsync(
            BrainCapabilityCallData call,
            TraceTurnContext context,
            CancellationToken cancellationToken)
        {
            var files = SplitCategories(call.GetArgument("files"));
            if (files.Count == 0)
                throw new InvalidOperationException("发图需要 files。");
            var mode = NormalizeMode(call.GetArgument("mode"), call.GetArgument("prompt"), context);
            return await SendFileListAsync(files, mode, context, cancellationToken, Stopwatch.StartNew());
        }

        private async Task<TraceCapabilityResultData> SendFileListAsync(
            IReadOnlyList<string> paths,
            string mode,
            TraceTurnContext context,
            CancellationToken cancellationToken,
            Stopwatch timer)
        {
            var adapter = FindAdapter(context);
            TraceCapabilityResultData last = null;
            foreach (var path in paths)
            {
                last = await adapter.SendAsync(new TraceOutboundMessageData
                {
                    Kind = TraceOutboundKinds.Image,
                    File = path
                }, context, cancellationToken);
                context.Services.LogTiming(context.TraceId, "TA的相机 QQ 图片发送完成", detail:
                    "status=" + (last?.Status ?? "null") + "｜summary=" +
                    TruncateForLog(last?.Summary, 300));
                if (LooksLikeLocalFile(path)) ScheduleDelete(path);
            }
            last = last ?? new TraceCapabilityResultData { Status = "success" };
            last.Status = "success";
            last.Summary = "QQ 相机已发送 " + paths.Count + " 张图片（" + mode + "）。";
            last.Payload = JsonSerializer.Serialize(new { mode, images = paths.Count });
            context.Services.LogTiming(context.TraceId, "QQ 相机发送完成", timer.ElapsedMilliseconds,
                "mode=" + mode + "｜images=" + paths.Count);
            return last;
        }

        private static bool LooksLikeLocalFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            return path.IndexOf('\\') >= 0 || path.IndexOf('/') >= 0 || File.Exists(path);
        }

        private sealed class ScenePlanResult
        {
            public string Mode;
            public string Scene;
            public List<string> ReferenceCategories = new List<string>();
        }

        private async Task<ScenePlanResult> PlanSceneAsync(
            string seed,
            TraceTurnContext context,
            CancellationToken cancellationToken)
        {
            var fallback = new ScenePlanResult { Mode = "selfie", Scene = (seed ?? string.Empty).Trim() };
            var llm = ResolvePlannerLlm(context);
            if (llm == null) return fallback;
            var messages = BuildPlanMessages(fallback.Scene, context, llm);
            var cacheKey = TryPromptCacheKey(context, llm);
            var timer = Stopwatch.StartNew();
            try
            {
                context.Services.LogTiming(context.TraceId, "TA的相机 画面规划请求",
                    detail: "provider=" + (llm.ProviderId ?? string.Empty) +
                            "｜model=" + (llm.Model ?? string.Empty) +
                            "｜messages=" + messages.Count +
                            "｜cache_key=" + (cacheKey ?? "(无)"));
                var planned = await llm.CompleteTextAsync(messages, cancellationToken, cacheKey);
                var parsed = ParseScenePlan(planned, fallback.Scene);
                if (parsed.Scene.Length < 12)
                {
                    context.Services.LogTiming(context.TraceId, "TA的相机 画面规划过短，沿用心智原文",
                        timer.ElapsedMilliseconds, "chars=" + parsed.Scene.Length);
                    parsed.Scene = fallback.Scene.Length > 0 ? fallback.Scene : parsed.Scene;
                }
                context.Services.LogTiming(context.TraceId, "TA的相机 画面规划完成",
                    timer.ElapsedMilliseconds, "mode=" + parsed.Mode + "｜refs=" +
                    (parsed.ReferenceCategories.Count == 0 ? "(无)" : string.Join(",", parsed.ReferenceCategories)) +
                    "｜" + TruncateForLog(parsed.Scene, 400));
                return parsed;
            }
            catch (Exception exception)
            {
                context.Services.LogTiming(context.TraceId, "TA的相机 画面规划失败，沿用心智原文",
                    timer.ElapsedMilliseconds, ExceptionDetail(exception));
                return fallback;
            }
        }

        private ILlmClient ResolvePlannerLlm(TraceTurnContext context)
        {
            var services = context == null ? null : context.Services;
            if (services == null) return null;
            if (services.Providers != null && !string.IsNullOrWhiteSpace(plannerProviderId))
            {
                try
                {
                    var client = services.Providers.CreateClient(
                        plannerProviderId,
                        string.IsNullOrWhiteSpace(plannerModel) ? null : plannerModel,
                        false);
                    if (client != null) return client;
                    services.LogTiming(context.TraceId, "TA的相机 画面描述 LLM 不可用，回退开口模型",
                        detail: "provider=" + plannerProviderId + "｜model=" + plannerModel);
                }
                catch (Exception exception)
                {
                    services.LogTiming(context.TraceId, "TA的相机 画面描述 LLM 创建失败，回退开口模型",
                        detail: SafeMessage(exception));
                }
            }
            return services.Llm;
        }

        private static string TryPromptCacheKey(TraceTurnContext context, ILlmClient llm)
        {
            if (context == null || context.Services == null || llm == null) return null;
            try
            {
                var packer = context.Services.ContextPack;
                return packer == null ? null : packer.BuildPromptCacheKey(llm, context.ConversationId);
            }
            catch (MissingFieldException)
            {
                return null;
            }
            catch (MissingMemberException)
            {
                return null;
            }
        }

        private List<DeepSeekMessageData> BuildPlanMessages(
            string seed, TraceTurnContext context, ILlmClient llm)
        {
            var role = BuildPlanRole(seed, context);
            ILlmContextAssembler packer = null;
            try
            {
                packer = context == null || context.Services == null ? null : context.Services.ContextPack;
            }
            catch (MissingFieldException)
            {
                packer = null;
            }
            catch (MissingMemberException)
            {
                packer = null;
            }
            if (packer != null && context != null)
            {
                var current = context.Moment == null ? string.Empty : (context.Moment.Content ?? string.Empty);
                var memory = string.Empty;
                try
                {
                    memory = context.Workspace == null ? string.Empty : context.Workspace.SharedMemory;
                }
                catch (MissingFieldException) { }
                catch (MissingMemberException) { }
                return packer.Assemble(
                    llm, context, memory, current, QqImageGenPrompts.ScenePlanRoleHeader, role);
            }
            return new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", QqImageGenPrompts.ScenePlanSystem),
                new DeepSeekMessageData("user", role)
            };
        }

        private string BuildPlanRole(string seed, TraceTurnContext context)
        {
            string mood = null;
            string activity = null;
            string inner = null;
            var services = context == null ? null : context.Services;
            if (services != null && services.LifeState != null)
            {
                var life = services.LifeState.Load(context.ConversationId);
                if (life != null) activity = life.FormatDoing();
            }
            var storage = services == null ? null : services.Storage;
            if (storage != null)
            {
                var state = storage.LoadOrCreateInnerRuntime(context.ConversationId);
                if (state != null)
                {
                    mood = state.Mood;
                    if (string.IsNullOrWhiteSpace(activity)) activity = state.OngoingActivity;
                    inner = state.Narrative;
                }
            }
            var catalog = references != null && references.Categories.Count > 0
                ? references.Describe() : string.Empty;
            return QqImageGenPrompts.BuildScenePlanRole(
                characterDetails, catalog, mood, activity, inner, seed);
        }

        private static ScenePlanResult ParseScenePlan(string raw, string seed)
        {
            var text = CleanPlan(raw);
            var kind = ReadLabeledLine(text, "种类");
            var picture = ReadLabeledBlock(text, "画面");
            var referenceText = ReadLabeledLine(text, "参考");
            var mode = kind.Length > 0 ? ClassifyPlanKind(kind) : "selfie";
            var scene = picture.Length > 0 ? picture : StripKindLine(text);
            if (scene.Length < 12) scene = (seed ?? string.Empty).Trim();
            var categories = SplitCategories(referenceText)
                .Where(x => x != "无" && x != "不需要" && x != "none")
                .ToList();
            return new ScenePlanResult { Mode = mode, Scene = scene, ReferenceCategories = categories };
        }

        private static string ClassifyPlanKind(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.IndexOf("画", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("draw", StringComparison.OrdinalIgnoreCase) >= 0)
                return "draw";
            if (text.IndexOf("照片", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("情景", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("photo", StringComparison.OrdinalIgnoreCase) >= 0)
                return "photo";
            return "selfie";
        }

        private static string ReadLabeledLine(string text, string label)
        {
            foreach (var line in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(label, StringComparison.Ordinal)) continue;
                return trimmed.Substring(label.Length).TrimStart('：', ':', ' ', '　');
            }
            return string.Empty;
        }

        private static string ReadLabeledBlock(string text, string label)
        {
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (!trimmed.StartsWith(label, StringComparison.Ordinal)) continue;
                var rest = trimmed.Substring(label.Length).TrimStart('：', ':', ' ', '　');
                var parts = new List<string>();
                if (rest.Length > 0) parts.Add(rest);
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var next = lines[j].Trim();
                    if (next.StartsWith("种类", StringComparison.Ordinal)) break;
                    parts.Add(lines[j]);
                }
                return string.Join("\n", parts).Trim();
            }
            return string.Empty;
        }

        private static string StripKindLine(string text)
        {
            var parts = new List<string>();
            foreach (var line in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Trim().StartsWith("种类", StringComparison.Ordinal)) continue;
                parts.Add(line);
            }
            return string.Join("\n", parts).Trim();
        }

        private static string CleanPlan(string raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var first = text.IndexOf('\n');
                if (first > 0) text = text.Substring(first + 1);
                var close = text.LastIndexOf("```", StringComparison.Ordinal);
                if (close >= 0) text = text.Substring(0, close);
                text = text.Trim();
            }
            if ((text.StartsWith("\"") && text.EndsWith("\"")) ||
                (text.StartsWith("“") && text.EndsWith("”")))
                text = text.Substring(1, Math.Max(0, text.Length - 2)).Trim();
            return Truncate(text.Replace("\r\n", "\n").Trim(), 800);
        }

        private static string Truncate(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private string BuildPrompt(string source, string mode,
            IReadOnlyList<ReferenceImageData> selected, string aspectRatio)
        {
            var hasCharacterReference = selected.Any(x => string.Equals(x.Role, "角色", StringComparison.OrdinalIgnoreCase));
            var prompt = source.Trim();
            var hasPersonIntent = mode == "selfie" || mode == "photo" || hasCharacterReference ||
                                  selected.Any(x => string.Equals(x.Role, "服饰", StringComparison.OrdinalIgnoreCase)) ||
                                  Regex.IsMatch(prompt,
                                      @"人物|角色|人像|女孩|男孩|女人|男人|面孔|脸部|穿着|衣服|自拍|阿循|循循",
                                      RegexOptions.IgnoreCase);
            var locksFace = selfieMode == "lockface" || selfieMode.IndexOf("锁", StringComparison.Ordinal) >= 0;
            if (hasCharacterReference && (strictReferenceMode || locksFace))
                prompt = AppearanceDetails.Replace(prompt, string.Empty);

            var builder = new System.Text.StringBuilder();
            if (mode == "selfie")
            {
                builder.Append(QqImageGenPrompts.Selfie);
                if (locksFace && hasCharacterReference)
                    builder.Append(QqImageGenPrompts.LockFace);
            }
            else if (mode == "photo")
            {
                builder.Append(QqImageGenPrompts.Photo);
            }
            else if (mode == "edit")
            {
                builder.Append(QqImageGenPrompts.Edit);
            }
            else
            {
                builder.Append(QqImageGenPrompts.Draw);
            }
            if (selected.Count > 0)
            {
                builder.Append(QqImageGenPrompts.RefsPrefix);
                builder.Append(string.Join("、", selected.GroupBy(x => x.Category).Select(x =>
                    x.Key + "=" + x.First().Role + "参考")));
                builder.Append(QqImageGenPrompts.RefsHint);
            }
            if (!string.IsNullOrWhiteSpace(stylePrompt))
                builder.Append(QqImageGenPrompts.StylePrefix).Append(stylePrompt.Trim()).Append("。 ");
            builder.Append(QqImageGenPrompts.RequestPrefix).Append(prompt).Append("。 ");
            if (!string.IsNullOrWhiteSpace(aspectRatio))
                builder.Append(QqImageGenPrompts.AspectPrefix).Append(aspectRatio).Append("。");
            if (!string.IsNullOrWhiteSpace(characterDetails) && hasPersonIntent)
                builder.Append(QqImageGenPrompts.CharacterPrefix).Append(characterDetails.Trim()).Append("。 ");
            if (selected.Count > 0)
                builder.Append(BuildReferenceGuide(selected));
            return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        }

        private static string BuildReferenceGuide(IReadOnlyList<ReferenceImageData> selected)
        {
            var indexed = selected.Select((image, index) => new { Image = image, Number = index + 1 });
            var groups = indexed.GroupBy(x => new
            {
                Category = x.Image.Category ?? "未分类",
                Role = x.Image.Role ?? "辅助"
            });
            var mapping = string.Join("；", groups.Select(group =>
                string.Join("、", group.Select(x => "图" + x.Number)) + "=" +
                "分类「" + group.Key.Category + "」/用途「" + group.Key.Role + "」"));
            return QqImageGenPrompts.ReferenceOrderPrefix + mapping + "。" +
                   QqImageGenPrompts.ReferenceFusionRules;
        }

        private string NormalizeMode(string requested, string prompt, TraceTurnContext context)
        {
            var value = string.IsNullOrWhiteSpace(requested) ? defaultMode : requested.Trim().ToLowerInvariant();
            if (value == "selfie" || value == "photo" || value == "draw" || value == "edit" ||
                value == "url" || value == "auto")
                return value;
            prompt = prompt ?? string.Empty;
            if (HasInboundImages(context) && Regex.IsMatch(prompt, "改|修改|换成|去掉|删除|加上|编辑|修图"))
                return "edit";
            return "auto";
        }

        private string NormalizeAspectRatio(string requested, string mode = null)
        {
            if (!string.IsNullOrWhiteSpace(requested) && Regex.IsMatch(requested.Trim(), @"^\d{1,2}:\d{1,2}$"))
                return requested.Trim();
            if (string.Equals(mode, "selfie", StringComparison.OrdinalIgnoreCase))
                return "3:4";
            return aiDecideAspectRatio ? string.Empty : defaultAspectRatio;
        }

        private async Task<List<ReferenceImageData>> LoadInboundImagesAsync(
            TraceTurnContext context, CancellationToken cancellationToken)
        {
            return await LoadLocationsAsync(ReadInboundImageLocations(context), "当前消息图片", "编辑底图",
                context, cancellationToken);
        }

        private async Task<List<ReferenceImageData>> LoadLocationsAsync(
            IEnumerable<string> sourceLocations,
            string category,
            string role,
            TraceTurnContext context,
            CancellationToken cancellationToken)
        {
            var result = new List<ReferenceImageData>();
            var locations = (sourceLocations ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            using (var handler = CreateHandler(proxy))
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Min(300, timeoutSeconds)) })
            {
                foreach (var location in locations.Take(maxReferenceImages))
                {
                    try
                    {
                        byte[] bytes;
                        string mime;
                        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        {
                            using (var response = await client.GetAsync(uri, cancellationToken))
                            {
                                response.EnsureSuccessStatusCode();
                                bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                                mime = response.Content.Headers.ContentType?.MediaType ?? "image/png";
                            }
                        }
                        else
                        {
                            var path = location.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                                ? new Uri(location).LocalPath : location;
                            if (!File.Exists(path)) continue;
                            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                            mime = ReferenceLibrary.MimeOf(path, bytes);
                        }
                        if (bytes.Length > 100) result.Add(new ReferenceImageData
                        {
                            Category = category, Role = role, FileName = "reference.png",
                            Bytes = bytes, MimeType = mime
                        });
                    }
                    catch (Exception exception)
                    {
                        if (debug) services?.LogTiming(context.TraceId, "入站参考图读取失败", detail: SafeMessage(exception));
                    }
                }
            }
            return result;
        }

        private static bool HasInboundImages(TraceTurnContext context) => ReadInboundImageLocations(context).Count > 0;

        private static List<string> ReadInboundImageLocations(TraceTurnContext context)
        {
            var result = new List<string>();
            var payload = context?.Moment?.PayloadJson;
            if (string.IsNullOrWhiteSpace(payload)) return result;
            try
            {
                using (var doc = JsonDocument.Parse(payload))
                    if (doc.RootElement.TryGetProperty("image_urls", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var item in items.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                                result.Add(item.GetString());
            }
            catch { }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task<string> SaveImageAsync(GeneratedImageData image, CancellationToken cancellationToken)
        {
            var directory = Path.Combine(dataDirectory, "generated");
            Directory.CreateDirectory(directory);
            var extension = ExtensionOf(image.MimeType, image.Bytes);
            var path = Path.Combine(directory, "image_" + Guid.NewGuid().ToString("N") + extension);
            await File.WriteAllBytesAsync(path, image.Bytes, cancellationToken);
            lock (filesGate) pendingFiles.Add(path);
            return path;
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
            if (adapter == null) throw new InvalidOperationException("QQ 平台适配器不可用。 ");
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

        private static List<string> SplitCategories(string value)
        {
            return Regex.Split(value ?? string.Empty, "[,，、;；|\\n]")
                .Select(x => x.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ExtensionOf(string mime, byte[] bytes)
        {
            mime = (mime ?? string.Empty).ToLowerInvariant();
            if (mime.Contains("jpeg") || mime.Contains("jpg") ||
                (bytes?.Length > 2 && bytes[0] == 0xff && bytes[1] == 0xd8)) return ".jpg";
            if (mime.Contains("webp")) return ".webp";
            if (mime.Contains("gif")) return ".gif";
            return ".png";
        }

        private static string ReadString(JsonElement root, string key)
        {
            return root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }

        private static List<string> ReadStrings(JsonElement root, string key)
        {
            var result = new List<string>();
            if (!root.TryGetProperty(key, out var value)) return result;
            if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        result.Add(item.GetString());
            else if (value.ValueKind == JsonValueKind.String)
            {
                var raw = (value.GetString() ?? string.Empty).Trim();
                if (raw.StartsWith("[", StringComparison.Ordinal) && raw.EndsWith("]", StringComparison.Ordinal))
                {
                    try
                    {
                        using (var nested = JsonDocument.Parse(raw))
                            if (nested.RootElement.ValueKind == JsonValueKind.Array)
                                foreach (var nestedItem in nested.RootElement.EnumerateArray())
                                    if (nestedItem.ValueKind == JsonValueKind.String &&
                                        !string.IsNullOrWhiteSpace(nestedItem.GetString()))
                                        result.Add(nestedItem.GetString());
                    }
                    catch { /* 退回普通分隔格式 */ }
                }
                else
                    result.AddRange(SplitCategories(raw));
            }
            return result;
        }

        private static void ReplaceList(List<string> target, List<string> source)
        {
            if (source == null || source.Count == 0) return;
            target.Clear();
            target.AddRange(source);
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

        private static string SafeMessage(Exception exception)
        {
            var message = exception?.Message ?? "未知错误";
            return message.Length <= 600 ? message : message.Substring(0, 600);
        }

        private static string ExceptionDetail(Exception exception)
        {
            if (exception == null) return "未知错误";
            var values = new List<string>();
            for (var current = exception; current != null && values.Count < 4; current = current.InnerException)
                values.Add(current.GetType().Name + "：" + SafeMessage(current));
            return TruncateForLog(string.Join(" <- ", values), 1200);
        }

        private static string SafeEndpoint(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "(空)";
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return TruncateForLog(value, 180);
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        private static string TruncateForLog(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }

        private sealed class ImageEffector : ITraceCallableContribution
        {
            private readonly QqImageGenPlugin owner;
            public ImageEffector(QqImageGenPlugin owner) { this.owner = owner; }
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "qq.imagegen.generate", Kind = TraceContributionKindValues.Effector,
                DisplayName = "QQ 相机/生图", Description = QqImageGenPrompts.EffectorDescription,
                Provides = "expression.qq.imagegen",
                Boundary = QqImageGenPrompts.EffectorBoundary,
                BodyId = BodyIds.Qq, BodyTier = BodyTierValues.Chat, Organ = BodyOrganValues.Image,
                ParametersJsonSchema = "{prompt:string,mode?:selfie|photo|draw|edit|url,refs?:string,aspect_ratio?:string,url?:string}",
                HasExternalSideEffect = true
            };
            public bool IsAvailable(TraceTurnContext context) => context != null && owner.IsReady(context.Services);
            public Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,
                TraceTurnContext context, CancellationToken token) => owner.DispatchImageAsync(call, context, token);
        }
    }
}
