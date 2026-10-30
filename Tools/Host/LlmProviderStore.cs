using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Manager;

namespace TraceSoul2.Host
{
    /// <summary>本机保存 LLM 提供商。密钥不进 SQLite，也不出现在公开 API 正文里。</summary>
    public sealed class LlmProviderStore : ILlmProviderDirectory
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private readonly string path;
        private readonly object gate = new object();
        private FileData data;
        public FailureProtection Protection { get; }

        public LlmProviderStore(string path)
        {
            this.path = path ?? throw new ArgumentNullException("path");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            Protection = new FailureProtection(Path.GetDirectoryName(path) ?? ".");
            var created = !File.Exists(path);
            data = LoadUnsafe(out var unified);
            if (unified && !File.Exists(path + ".before-main-unification.bak"))
                File.Copy(path, path + ".before-main-unification.bak");
            if (created || unified) SaveUnsafe();
        }

        public string CurrentId
        {
            get
            {
                lock (gate) return data.currentId;
            }
        }

        public IReadOnlyList<LlmProviderRecord> List()
        {
            lock (gate) return data.providers.Select(Clone).ToList();
        }

        public MainModelFallbackSettings MainFallbacks()
        {
            lock (gate) return new MainModelFallbackSettings {
                secondary = CloneSlot(data.mainFallbacks?.secondary), tertiary = CloneSlot(data.mainFallbacks?.tertiary) };
        }

        public MainModelFallbackSettings SetMainFallbacks(MainModelFallbackSettings settings)
        {
            if (settings == null) throw new ArgumentException("备用模型配置不能为空。");
            lock (gate)
            {
                var next = new MainModelFallbackSettings {
                    secondary = ValidateMainFallback(settings.secondary), tertiary = ValidateMainFallback(settings.tertiary) };
                ValidateMainRoute(FindChat(), null, next);
                data.mainFallbacks = next;
                SaveUnsafe();
                return MainFallbacks();
            }
        }

        private LlmSlotRef ValidateMainFallback(LlmSlotRef slot)
        {
            if (string.IsNullOrWhiteSpace(slot?.providerId)) return new LlmSlotRef();
            var provider = Find(slot.providerId);
            var model = string.IsNullOrWhiteSpace(slot.model) ? provider?.model : slot.model.Trim();
            if (!CanUseMainModel(provider, model))
                throw new ArgumentException("备用通道必须选择已配置密钥、已启用的聊天模型。");
            return new LlmSlotRef { providerId = provider.id, model = model };
        }

        private static bool CanUseMainModel(LlmProviderRecord provider, string model)
            => provider != null && LlmProviderCatalog.IsChat(provider.type) && !string.IsNullOrWhiteSpace(provider.apiKey) &&
               !string.IsNullOrWhiteSpace(model) && provider.models.Any(m => m.enabled && m.id == model &&
                   (m.roles == null || m.roles.Count == 0 || m.roles.Contains("chat") ||
                    !m.roles.Any(r => r == "speech" || r == "image")));

        private static string MainRouteKey(LlmProviderRecord provider, string model)
            => (provider.baseUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant() + "\n" + model;

        private void ValidateMainRoute(LlmProviderRecord primary, string model, MainModelFallbackSettings settings)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (primary != null) seen.Add(MainRouteKey(primary, string.IsNullOrWhiteSpace(model) ? primary.model : model.Trim()));
            foreach (var slot in new[] { settings?.secondary, settings?.tertiary })
            {
                if (string.IsNullOrWhiteSpace(slot?.providerId)) continue;
                var provider = Find(slot.providerId);
                if (provider != null && !seen.Add(MainRouteKey(provider, slot.model)))
                    throw new ArgumentException("主模型、二级和三级备用不能重复使用同一接口的同一模型，请先调整备用顺序。");
            }
        }

        public LlmProviderRecord Get(string id)
        {
            lock (gate)
            {
                var item = Find(id);
                return item == null ? null : Clone(item);
            }
        }

        /// <summary>One-time migration of the already paired local voice service; never overwrites user edits.</summary>
        public void ImportLocalVoice(string configPath)
        {
            lock (gate)
            {
                if (data.localVoiceImported || !File.Exists(configPath)) return;
                var existing = data.providers.FirstOrDefault(x => LlmProviderCatalog.IsTts(x.type));
                if (existing == null)
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(configPath));
                    var url = json.RootElement.GetProperty("api_url").GetString() ?? "";
                    const string suffix = "/audio/speech";
                    if (!url.EndsWith(suffix, StringComparison.Ordinal))
                        throw new InvalidOperationException("原本地千问地址无效，未迁移供应商。");
                    existing = new LlmProviderRecord { id = UniqueId("qwen-local-tts"), type = LlmProviderCatalog.QwenTts,
                        displayName = "本地千问 TTS", baseUrl = url[..^suffix.Length], model = "qwen3-tts-local",
                        apiKey = json.RootElement.GetProperty("api_key").GetString(), voice = "character", timeout = 120 };
                    QwenTtsProvider.Validate(existing, true);
                    EnsureModel(existing, existing.model, LlmSlotNames.Speech);
                    NormalizeProvider(existing);
                    data.providers.Add(existing);
                }
                if (string.IsNullOrWhiteSpace(data.speech?.providerId))
                    data.speech = new LlmSlotRef { providerId = existing.id, model = existing.model };
                data.localVoiceImported = true;
                SaveUnsafe();
            }
        }

        public LlmProviderRecord SpeechProvider(string id = null)
        {
            lock (gate)
            {
                var slot = data.speech;
                var item = Find(id ?? slot?.providerId);
                if (item == null || !LlmProviderCatalog.IsTts(item.type))
                    throw new InvalidOperationException("请在大脑 · LLM的语音槽选择本地千问TTS供应商。");
                var result = Clone(item);
                if (id == null && !string.IsNullOrWhiteSpace(slot?.model)) result.model = slot.model;
                if (!item.models.Any(m => m.enabled && m.id == result.model))
                    throw new InvalidOperationException("当前千问语音模型已停用或移除，请重新选择。");
                QwenTtsProvider.Validate(result, true);
                return result;
            }
        }

        public Dictionary<string, LlmSlotRef> Slots()
        {
            lock (gate)
            {
                var chat = FindChat();
                var map = new Dictionary<string, LlmSlotRef>(StringComparer.OrdinalIgnoreCase)
                {
                    [LlmSlotNames.Chat] = chat == null
                        ? new LlmSlotRef()
                        : new LlmSlotRef { providerId = chat.id, model = chat.model ?? string.Empty },
                    [LlmSlotNames.Jev] = CloneSlot(data.jev),
                    [LlmSlotNames.Review] = CloneSlot(data.review),
                    [LlmSlotNames.Multimodal] = CloneSlot(data.multimodal),
                    [LlmSlotNames.Image] = CloneSlot(data.image),
                    [LlmSlotNames.Speech] = CloneSlot(data.speech)
                };
                return map;
            }
        }

        public LlmProviderRecord Upsert(LlmProviderRecord incoming)
        {
            if (incoming == null) throw new ArgumentNullException("incoming");
            var id = string.IsNullOrWhiteSpace(incoming.id) ? "default" : incoming.id.Trim();
            lock (gate)
            {
                var previous = Find(id);
                var item = previous == null ? new LlmProviderRecord { id = id } : Clone(previous);
                item.type = LlmProviderCatalog.NormalizeType(
                    string.IsNullOrWhiteSpace(incoming.type) ? item.type : incoming.type);
                item.displayName = string.IsNullOrWhiteSpace(incoming.displayName)
                    ? (string.IsNullOrWhiteSpace(item.displayName) ? id : item.displayName)
                    : incoming.displayName.Trim();
                if (!string.IsNullOrWhiteSpace(incoming.baseUrl)) item.baseUrl = incoming.baseUrl.Trim();
                if (!string.IsNullOrWhiteSpace(incoming.model))
                {
                    item.model = incoming.model.Trim();
                    EnsureModel(item, item.model, LlmSlotNames.Chat);
                }
                if (!string.IsNullOrWhiteSpace(incoming.apiKey)) item.apiKey = incoming.apiKey.Trim();
                if (incoming.voice != null) item.voice = incoming.voice.Trim();
                if (incoming.temperature > 0) item.temperature = incoming.temperature;
                if (incoming.topP > 0) item.topP = incoming.topP;
                if (incoming.maxTokens > 0) item.maxTokens = incoming.maxTokens;
                if (incoming.timeout > 0) item.timeout = incoming.timeout;
                if (incoming.transientRetries >= 0)
                    item.transientRetries = Math.Max(0, Math.Min(2, incoming.transientRetries));
                if (incoming.proxy != null) item.proxy = incoming.proxy.Trim();
                if (incoming.decisionMode != null) item.decisionMode = incoming.decisionMode.Trim();
                if (incoming.clearRelayKey) item.relayKey = null;
                else if (!string.IsNullOrWhiteSpace(incoming.relayKey)) item.relayKey = incoming.relayKey.Trim();
                item.thinkingEnabled = incoming.thinkingEnabled;
                if (!string.IsNullOrWhiteSpace(incoming.reasoningEffort))
                    item.reasoningEffort = incoming.reasoningEffort.Trim();
                if (LlmProviderCatalog.IsDecisions(item.type))
                {
                    if (incoming.baseUrl != null) item.baseUrl = incoming.baseUrl.Trim();
                    DecisionsClient.ValidateConfiguration(item);
                    if (string.Equals(data.currentId, id, StringComparison.OrdinalIgnoreCase) ||
                        new[] { data.review, data.multimodal, data.image, data.speech, data.mainFallbacks?.secondary, data.mainFallbacks?.tertiary }.Any(s =>
                            string.Equals(s?.providerId, id, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("请先将该供应商从主模型及非 JEV 槽移除，再改为 Decisions 协议。");
                }
                if (LlmProviderCatalog.IsTts(item.type))
                {
                    if (string.Equals(data.currentId, id, StringComparison.OrdinalIgnoreCase) ||
                        new[] { data.jev, data.review, data.multimodal, data.image, data.mainFallbacks?.secondary, data.mainFallbacks?.tertiary }.Any(s =>
                            string.Equals(s?.providerId, id, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("请先从非语音用途槽移除此供应商，再改为TTS协议。");
                    if (string.IsNullOrWhiteSpace(item.voice)) item.voice = "character";
                    QwenTtsProvider.Validate(item);
                }
                if (string.IsNullOrWhiteSpace(data.currentId) && LlmProviderCatalog.IsChat(item.type)) data.currentId = id;
                NormalizeProvider(item);
                if (previous == null) data.providers.Add(item);
                else data.providers[data.providers.IndexOf(previous)] = item;
                SaveUnsafe();
                return Clone(item);
            }
        }

        public LlmProviderRecord AddFromTemplate(string templateKey, string id)
        {
            var template = LlmProviderCatalog.Find(templateKey);
            if (template == null)
                throw new InvalidOperationException("没有这个供应商模板：" + templateKey);
            lock (gate)
            {
                var newId = string.IsNullOrWhiteSpace(id) ? template.id : id.Trim();
                if (Find(newId) != null)
                    newId = UniqueId(template.id);
                var item = new LlmProviderRecord
                {
                    id = newId,
                    type = template.type,
                    displayName = template.displayName,
                    baseUrl = template.baseUrl,
                    model = template.model,
                    temperature = template.temperature,
                    topP = template.topP,
                    maxTokens = template.maxTokens,
                    timeout = 120,
                    transientRetries = 2
                };
                if (LlmProviderCatalog.IsTts(item.type)) item.voice = "character";
                if (string.Equals(template.id, "moonshot", StringComparison.OrdinalIgnoreCase))
                {
                    item.thinkingEnabled = true;
                    // K3 关不掉思考；日常陪伴默认 low，不要用官网缺省的 max。
                    item.reasoningEffort = "low";
                    item.timeout = 300;
                }
                // Ollama 的 OpenAI 兼容接口不校验 Key；给通用客户端一个本地占位值，
                // 避免把“无须密钥”误判成“尚未配置”。这个值只发往 loopback。
                if (IsLocalOllama(item)) item.apiKey = "ollama";
                if (!string.IsNullOrWhiteSpace(item.model))
                    EnsureModel(item, item.model, LlmSlotNames.Chat);
                data.providers.Add(item);
                NormalizeProvider(item);
                if (string.IsNullOrWhiteSpace(data.currentId) && LlmProviderCatalog.IsChat(item.type)) data.currentId = item.id;
                SaveUnsafe();
                return Clone(item);
            }
        }

        public LlmProviderRecord Delete(string id)
        {
            lock (gate)
            {
                var item = Find(id);
                if (item == null) throw new InvalidOperationException("没有这个供应商：" + id);
                if (data.providers.Count <= 1)
                    throw new InvalidOperationException("至少保留一个供应商。");
                data.providers.Remove(item);
                if (string.Equals(data.currentId, item.id, StringComparison.OrdinalIgnoreCase))
                    data.currentId = data.providers.FirstOrDefault(x => LlmProviderCatalog.IsChat(x.type))?.id ?? "";
                ClearSlotIf(data.jev, item.id);
                ClearSlotIf(data.review, item.id);
                ClearSlotIf(data.multimodal, item.id);
                ClearSlotIf(data.image, item.id);
                ClearSlotIf(data.speech, item.id);
                ClearSlotIf(data.mainFallbacks?.secondary, item.id);
                ClearSlotIf(data.mainFallbacks?.tertiary, item.id);
                SaveUnsafe();
                return Clone(data.providers[0]);
            }
        }

        public LlmProviderRecord Select(string id, string model)
        {
            lock (gate)
            {
                var item = Find(id);
                if (item == null) throw new InvalidOperationException("没有这个语言模型提供商：" + id);
                if (LlmProviderCatalog.IsDecisions(item.type)) throw new InvalidOperationException("Decisions 模型只能用于 JEV，不能设为聊天主模型。");
                if (LlmProviderCatalog.IsTts(item.type)) throw new InvalidOperationException("千问TTS只能用于语音，不能设为聊天主模型。");
                ValidateMainRoute(item, model, data.mainFallbacks);
                data.currentId = item.id;
                if (!string.IsNullOrWhiteSpace(model))
                {
                    item.model = model.Trim();
                    EnsureModel(item, item.model, LlmSlotNames.Chat);
                }
                SaveUnsafe();
                return Clone(item);
            }
        }

        public Dictionary<string, LlmSlotRef> SetSlot(string slot, string providerId, string model)
        {
            slot = NormalizeSlot(slot);
            if (string.Equals(slot, LlmSlotNames.Chat, StringComparison.OrdinalIgnoreCase))
            {
                Select(providerId, model);
                return Slots();
            }
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(providerId))
                {
                    AssignSlot(slot, null);
                }
                else
                {
                    var item = Find(providerId);
                    if (item == null) throw new InvalidOperationException("没有这个供应商：" + providerId);
                    if (LlmProviderCatalog.IsDecisions(item.type) && slot != LlmSlotNames.Jev)
                        throw new InvalidOperationException("Decisions 模型只能用于 JEV 槽。");
                    if (LlmProviderCatalog.IsTts(item.type) && slot != LlmSlotNames.Speech)
                        throw new InvalidOperationException("千问TTS只能用于语音槽。");
                    var chosen = string.IsNullOrWhiteSpace(model) ? item.model : model.Trim();
                    if (!string.IsNullOrWhiteSpace(chosen))
                        EnsureModel(item, chosen, slot);
                    AssignSlot(slot, new LlmSlotRef { providerId = item.id, model = chosen ?? string.Empty });
                }
                SaveUnsafe();
            }
            return Slots();
        }

        public LlmProviderRecord MergeFetched(string id, IReadOnlyList<string> fetched)
        {
            lock (gate)
            {
                var item = Find(id);
                if (item == null) throw new InvalidOperationException("没有这个供应商：" + id);
                NormalizeProvider(item);
                if (fetched != null)
                {
                    foreach (var raw in fetched)
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        EnsureModel(item, raw.Trim(), null);
                    }
                }
                if (string.IsNullOrWhiteSpace(item.model))
                {
                    var firstChat = item.models.FirstOrDefault(x =>
                        x.enabled && LlmProviderCatalog.HasRole(x, LlmSlotNames.Chat));
                    if (firstChat == null) firstChat = item.models.FirstOrDefault(x => x.enabled);
                    if (firstChat != null) item.model = firstChat.id;
                }
                SaveUnsafe();
                return Clone(item);
            }
        }

        public LlmProviderRecord UpsertModel(string id, string modelId, bool? enabled, List<string> roles)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new InvalidOperationException("模型 id 不能为空。");
            lock (gate)
            {
                var item = Find(id);
                if (item == null) throw new InvalidOperationException("没有这个供应商：" + id);
                NormalizeProvider(item);
                var model = EnsureModel(item, modelId.Trim(), null);
                if (enabled.HasValue) model.enabled = enabled.Value;
                if (roles != null)
                {
                    model.roles = roles
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => NormalizeSlot(x.Trim()))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                if (LlmProviderCatalog.IsDecisions(item.type)) model.roles = new List<string> { LlmSlotNames.Jev };
                if (LlmProviderCatalog.IsTts(item.type)) model.roles = new List<string> { LlmSlotNames.Speech };
                if (string.IsNullOrWhiteSpace(item.model) && model.enabled)
                    item.model = model.id;
                SaveUnsafe();
                return Clone(item);
            }
        }

        public LlmProviderRecord DeleteModel(string id, string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new InvalidOperationException("模型 id 不能为空。");
            lock (gate)
            {
                var item = Find(id);
                if (item == null) throw new InvalidOperationException("没有这个供应商：" + id);
                NormalizeProvider(item);
                item.models.RemoveAll(x => string.Equals(x.id, modelId, StringComparison.OrdinalIgnoreCase));
                if (string.Equals(item.model, modelId, StringComparison.OrdinalIgnoreCase))
                {
                    var next = item.models.FirstOrDefault(x => x.enabled);
                    item.model = next == null ? string.Empty : next.id;
                }
                DropSlotModel(data.jev, id, modelId);
                DropSlotModel(data.review, id, modelId);
                DropSlotModel(data.multimodal, id, modelId);
                DropSlotModel(data.image, id, modelId);
                DropSlotModel(data.speech, id, modelId);
                DropSlotModel(data.mainFallbacks?.secondary, id, modelId);
                DropSlotModel(data.mainFallbacks?.tertiary, id, modelId);
                SaveUnsafe();
                return Clone(item);
            }
        }

        public ILlmClient CreateClient(string id)
        {
            return CreateClient(id, null, null);
        }

        public ILlmClient CreateClient(string id, string model, bool? thinkingOverride)
            => CreateConfiguredClient(id, model, thinkingOverride, false);

        private ILlmClient CreateConfiguredClient(string id, string model, bool? thinkingOverride, bool review)
        {
            lock (gate)
            {
                var item = Find(id);
                if (item != null && LlmProviderCatalog.IsTts(item.type))
                    throw new InvalidOperationException("千问TTS使用语音合成协议，不能创建聊天客户端。");
                if (item == null || string.IsNullOrWhiteSpace(item.apiKey)) return null;
                if (LlmProviderCatalog.IsDecisions(item.type))
                    throw new InvalidOperationException("此供应商使用原生 Decisions 协议，请使用决策客户端。");
                var config = ToConfig(item, model, thinkingOverride);
                if (review)
                {
                    config.Temperature = data.reviewTemperature ?? 0.2f;
                    config.TimeoutSeconds = 600;
                }
                // Review owns one retry allowance; disable provider-level nested retries.
                ILlmClient client = LlmProviderCatalog.IsGeminiNative(item.type)
                    ? new GeminiClientManager(config, singleAttempt: review)
                    : new DeepSeekClientManager(config, singleAttempt: review);
                if (review) client = new ReviewTransportClient(client);
                return new ProtectedLlmClient(client, Protection, review);
            }
        }

        public ILlmClient CreateCurrentClient(Action<string> log = null)
        {
            lock (gate)
            {
                var backups = new[] { data.mainFallbacks?.secondary, data.mainFallbacks?.tertiary };
                if (!backups.Any(s => !string.IsNullOrWhiteSpace(s?.providerId)))
                    return CreateClient(data.currentId, null, null);
                var primary = FindChat();
                var slots = new[] { new LlmSlotRef { providerId = primary?.id, model = primary?.model } }.Concat(backups);
                var clients = new List<ILlmClient>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var slot in slots)
                {
                    if (string.IsNullOrWhiteSpace(slot?.providerId)) continue;
                    var item = Find(slot.providerId);
                    if (!CanUseMainModel(item, slot.model))
                    {
                        log?.Invoke("主模型路由跳过未配置或已停用的通道：" + slot.providerId);
                        continue;
                    }
                    if (!seen.Add(MainRouteKey(item, slot.model))) continue;
                    var config = ToConfig(item, slot.model, null);
                    clients.Add(LlmProviderCatalog.IsGeminiNative(item.type)
                        ? new GeminiClientManager(config, singleAttempt: true)
                        : new DeepSeekClientManager(config, singleAttempt: true));
                }
                // Only an exhausted route becomes a persistent error; a recovered request is successful.
                return clients.Count == 0 ? null : new ProtectedLlmClient(new MainModelFallbackClient(clients, log), Protection);
            }
        }

        /// <summary>独立决策路由。只用明确配置的JEV模型；未配置或不可用时不回退到主模型或复盘。</summary>
        public ILlmClient CreateJevClient()
        {
            var endpoint = ResolveExplicitSlot(LlmSlotNames.Jev);
            return endpoint == null ? null : CreateClient(endpoint.ProviderId, endpoint.Model, null);
        }

        public DecisionsClient CreateDecisionClient(string providerId)
        {
            var item = Get(providerId);
            if (item == null || !LlmProviderCatalog.IsDecisions(item.type))
                throw new InvalidOperationException("请选择 OpenRouter Decisions 供应商。");
            return new DecisionsClient(item);
        }

        public DecisionsClient CreateJevDecisionClient()
        {
            var slot = Slots()[LlmSlotNames.Jev];
            if (string.IsNullOrWhiteSpace(slot.providerId)) return null;
            var item = Get(slot.providerId);
            if (item == null || !LlmProviderCatalog.IsDecisions(item.type))
                throw new InvalidOperationException("当前 JEV 槽是旧聊天配置，请选择 OpenRouter Decisions。");
            var model = string.IsNullOrWhiteSpace(slot.model) ? item.model : slot.model;
            if (!item.models.Any(x => x.id == model && x.enabled))
                throw new InvalidOperationException("当前 JEV 模型已停用或移除。");
            item.model = model;
            return new DecisionsClient(item);
        }

        /// <summary>复盘：指定槽则用该模型并关思考；未指定则用主模型关思考，避免推理模型把额度耗在 reasoning。</summary>
        public float ReviewTemperature { get { lock (gate) return data.reviewTemperature ?? 0.2f; } }

        public void SetReviewTemperature(float temperature)
        {
            if (!float.IsFinite(temperature) || temperature < 0 || temperature > 1)
                throw new ArgumentException("复盘温度必须在 0～1 之间。");
            lock (gate) { data.reviewTemperature = temperature; SaveUnsafe(); }
        }

        public ILlmClient CreateReviewClient() => CreateReviewClient(null, null);

        public ILlmClient CreateReviewClient(string providerOverride, string modelOverride)
        {
            string providerId;
            string model;
            lock (gate)
            {
                var refer = data.review;
                if (!string.IsNullOrWhiteSpace(providerOverride))
                {
                    providerId = providerOverride;
                    model = modelOverride;
                }
                else if (refer != null && !string.IsNullOrWhiteSpace(refer.providerId))
                {
                    providerId = refer.providerId;
                    model = string.IsNullOrWhiteSpace(refer.model) ? null : refer.model;
                }
                else
                {
                    providerId = data.currentId;
                    model = null;
                }
            }
            return CreateConfiguredClient(providerId, model, false, true);
        }

        public DeepSeekConfigData CurrentConfig()
        {
            lock (gate)
            {
                var item = FindChat();
                return item == null ? null : ToConfig(item, null, null);
            }
        }

        public LlmEndpointData Resolve(string providerId, string model = null)
        {
            lock (gate)
            {
                var item = Find(providerId);
                if (item == null || string.IsNullOrWhiteSpace(item.apiKey)) return null;
                return ToEndpoint(item, model);
            }
        }

        public LlmEndpointData ResolveSlot(string slot)
        {
            slot = NormalizeSlot(slot);
            // 决策模型必须由用户明确选择，清空槽后不能又按历史用途标记自动启用。
            if (slot == LlmSlotNames.Jev) return ResolveExplicitSlot(slot);
            lock (gate)
            {
                if (string.Equals(slot, LlmSlotNames.Chat, StringComparison.OrdinalIgnoreCase))
                {
                    var chat = FindChat();
                    return chat == null || string.IsNullOrWhiteSpace(chat.apiKey)
                        ? null
                        : ToEndpoint(chat, chat.model);
                }
                var refer = SlotOf(slot);
                if (refer != null && !string.IsNullOrWhiteSpace(refer.providerId))
                {
                    var item = Find(refer.providerId);
                    if (item != null && LlmProviderCatalog.IsTts(item.type) && slot != LlmSlotNames.Speech)
                        throw new InvalidOperationException("千问TTS只能用于语音槽。");
                    if (item != null && !string.IsNullOrWhiteSpace(item.apiKey))
                        return ToEndpoint(item, string.IsNullOrWhiteSpace(refer.model) ? item.model : refer.model);
                }
                foreach (var provider in data.providers)
                {
                    if (provider == null || LlmProviderCatalog.IsDecisions(provider.type) ||
                        LlmProviderCatalog.IsTts(provider.type) && slot != LlmSlotNames.Speech || string.IsNullOrWhiteSpace(provider.apiKey)) continue;
                    NormalizeProvider(provider);
                    var match = provider.models.FirstOrDefault(x =>
                        x.enabled && LlmProviderCatalog.HasRole(x, slot));
                    if (match != null) return ToEndpoint(provider, match.id);
                }
                return null;
            }
        }

        public LlmEndpointData ResolveExplicitSlot(string slot)
        {
            slot = NormalizeSlot(slot);
            lock (gate)
            {
                if (string.Equals(slot, LlmSlotNames.Chat, StringComparison.OrdinalIgnoreCase))
                {
                    var chat = FindChat();
                    return chat == null || string.IsNullOrWhiteSpace(chat.apiKey)
                        ? null
                        : ToEndpoint(chat, chat.model);
                }
                var refer = SlotOf(slot);
                if (refer == null || string.IsNullOrWhiteSpace(refer.providerId)) return null;
                var item = Find(refer.providerId);
                if (item != null && LlmProviderCatalog.IsTts(item.type) && slot != LlmSlotNames.Speech)
                    throw new InvalidOperationException("千问TTS只能用于语音槽。");
                if (item == null || string.IsNullOrWhiteSpace(item.apiKey)) return null;
                return ToEndpoint(item, string.IsNullOrWhiteSpace(refer.model) ? item.model : refer.model);
            }
        }

        public IReadOnlyList<LlmProviderBriefData> ListBrief()
        {
            lock (gate)
            {
                return data.providers.Select(ToBrief).ToList();
            }
        }

        private LlmProviderRecord FindChat()
        {
            var current = Find(data.currentId);
            return current != null && LlmProviderCatalog.IsChat(current.type) ? current :
                data.providers.FirstOrDefault(x => LlmProviderCatalog.IsChat(x.type));
        }

        private LlmProviderRecord Find(string id)
        {
            return data.providers.FirstOrDefault(x =>
                string.Equals(x.id, id, StringComparison.OrdinalIgnoreCase));
        }

        private string UniqueId(string seed)
        {
            seed = string.IsNullOrWhiteSpace(seed) ? "provider" : seed.Trim();
            if (Find(seed) == null) return seed;
            for (var i = 2; i < 100; i++)
            {
                var candidate = seed + "_" + i;
                if (Find(candidate) == null) return candidate;
            }
            return seed + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private FileData LoadUnsafe(out bool unified)
        {
            unified = false;
            if (!File.Exists(path))
            {
                var created = new FileData();
                created.providers.Add(DefaultProvider());
                created.currentId = "default";
                return created;
            }
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<FileData>(json, JsonOptions);
            if (loaded == null) loaded = new FileData();
            if (loaded.providers == null) loaded.providers = new List<LlmProviderRecord>();
            if (loaded.reviewTemperature.HasValue && (!float.IsFinite(loaded.reviewTemperature.Value) ||
                loaded.reviewTemperature < 0 || loaded.reviewTemperature > 1)) loaded.reviewTemperature = null;
            if (loaded.providers.Count == 0) loaded.providers.Add(DefaultProvider());
            foreach (var item in loaded.providers)
            {
                if (item == null) continue;
                item.type = LlmProviderCatalog.NormalizeType(item.type);
                NormalizeProvider(item);
            }
            if (string.IsNullOrWhiteSpace(loaded.currentId))
                loaded.currentId = loaded.providers.FirstOrDefault(x => LlmProviderCatalog.IsChat(x.type))?.id ?? "";
            if (loaded.thinking != null)
            {
                // Preserve the model actually used by the old conditional route on upgrade.
                var chat = loaded.providers.FirstOrDefault(x =>
                    string.Equals(x.id, loaded.currentId, StringComparison.OrdinalIgnoreCase));
                var legacy = loaded.providers.FirstOrDefault(x =>
                    string.Equals(x.id, loaded.thinking.providerId, StringComparison.OrdinalIgnoreCase));
                if (chat?.thinkingEnabled == true && legacy != null && !string.IsNullOrWhiteSpace(legacy.apiKey))
                {
                    loaded.currentId = legacy.id;
                    if (!string.IsNullOrWhiteSpace(loaded.thinking.model))
                        legacy.model = loaded.thinking.model.Trim();
                    legacy.thinkingEnabled = true;
                    EnsureModel(legacy, legacy.model, LlmSlotNames.Chat);
                }
                loaded.thinking = null;
                unified = true;
            }
            return loaded;
        }

        private void SaveUnsafe()
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (File.Create(temporary)) { }
                if (OperatingSystem.IsWindows())
                {
                    var security = new System.Security.AccessControl.FileSecurity();
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        System.Security.Principal.WindowsIdentity.GetCurrent().User,
                        System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                        System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                    new FileInfo(temporary).SetAccessControl(security);
                }
                else File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static LlmProviderRecord DefaultProvider()
        {
            var item = new LlmProviderRecord
            {
                id = "default",
                type = "openai_chat_completion",
                displayName = "DeepSeek",
                baseUrl = "https://api.deepseek.com/v1",
                model = "deepseek-v4-flash",
                timeout = 120
            };
            EnsureModel(item, item.model, LlmSlotNames.Chat);
            return item;
        }

        private static void NormalizeProvider(LlmProviderRecord item)
        {
            if (item.models == null) item.models = new List<LlmModelEntry>();
            if (IsLocalOllama(item) && string.IsNullOrWhiteSpace(item.apiKey))
                item.apiKey = "ollama";
            item.models.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.id));
            foreach (var model in item.models)
            {
                model.id = model.id.Trim();
                if (model.roles == null) model.roles = new List<string>();
                model.roles = model.roles
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => NormalizeSlot(x.Trim()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (model.roles.Count == 0)
                    model.roles = LlmProviderCatalog.GuessRoles(model.id);
                if (LlmProviderCatalog.IsDecisions(item.type)) model.roles = new List<string> { LlmSlotNames.Jev };
                if (LlmProviderCatalog.IsTts(item.type)) model.roles = new List<string> { LlmSlotNames.Speech };
            }
            if (item.models.Count == 0 && !string.IsNullOrWhiteSpace(item.model))
                EnsureModel(item, item.model, LlmSlotNames.Chat);
            if (item.timeout <= 0) item.timeout = 120;
            item.transientRetries = Math.Max(0, Math.Min(2, item.transientRetries));
            if (item.proxy == null) item.proxy = string.Empty;
            if (LlmProviderCatalog.IsDecisions(item.type))
            {
                if (string.IsNullOrWhiteSpace(item.decisionMode)) item.decisionMode = "direct";
                item.transientRetries = 0;
            }
            if (LlmProviderCatalog.IsTts(item.type))
            {
                item.voice = string.IsNullOrWhiteSpace(item.voice) ? "character" : item.voice;
                item.transientRetries = 0;
                item.thinkingEnabled = false;
            }
        }

        private static bool IsLocalOllama(LlmProviderRecord item)
        {
            if (item == null) return false;
            var url = (item.baseUrl ?? string.Empty).Trim().ToLowerInvariant();
            var name = ((item.id ?? string.Empty) + " " + (item.displayName ?? string.Empty)).ToLowerInvariant();
            return name.Contains("ollama") &&
                   (url.StartsWith("http://127.0.0.1:11434") ||
                    url.StartsWith("http://localhost:11434") ||
                    url.StartsWith("http://[::1]:11434"));
        }

        private static LlmModelEntry EnsureModel(LlmProviderRecord item, string modelId, string extraRole)
        {
            if (item.models == null) item.models = new List<LlmModelEntry>();
            var existing = item.models.FirstOrDefault(x =>
                string.Equals(x.id, modelId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new LlmModelEntry
                {
                    id = modelId,
                    enabled = true,
                    roles = LlmProviderCatalog.GuessRoles(modelId)
                };
                item.models.Add(existing);
            }
            if (!string.IsNullOrWhiteSpace(extraRole) && !LlmProviderCatalog.HasRole(existing, extraRole))
                existing.roles.Add(NormalizeSlot(extraRole));
            if (LlmProviderCatalog.IsDecisions(item.type)) existing.roles = new List<string> { LlmSlotNames.Jev };
            if (LlmProviderCatalog.IsTts(item.type)) existing.roles = new List<string> { LlmSlotNames.Speech };
            return existing;
        }

        private LlmSlotRef SlotOf(string slot)
        {
            if (string.Equals(slot, LlmSlotNames.Jev, StringComparison.OrdinalIgnoreCase)) return data.jev;
            if (string.Equals(slot, LlmSlotNames.Review, StringComparison.OrdinalIgnoreCase)) return data.review;
            if (string.Equals(slot, LlmSlotNames.Multimodal, StringComparison.OrdinalIgnoreCase)) return data.multimodal;
            if (string.Equals(slot, LlmSlotNames.Image, StringComparison.OrdinalIgnoreCase)) return data.image;
            if (string.Equals(slot, LlmSlotNames.Speech, StringComparison.OrdinalIgnoreCase)) return data.speech;
            return null;
        }

        private void AssignSlot(string slot, LlmSlotRef value)
        {
            if (string.Equals(slot, LlmSlotNames.Jev, StringComparison.OrdinalIgnoreCase)) data.jev = value;
            else if (string.Equals(slot, LlmSlotNames.Review, StringComparison.OrdinalIgnoreCase)) data.review = value;
            else if (string.Equals(slot, LlmSlotNames.Multimodal, StringComparison.OrdinalIgnoreCase)) data.multimodal = value;
            else if (string.Equals(slot, LlmSlotNames.Image, StringComparison.OrdinalIgnoreCase)) data.image = value;
            else if (string.Equals(slot, LlmSlotNames.Speech, StringComparison.OrdinalIgnoreCase)) data.speech = value;
            else throw new InvalidOperationException("没有这个用途槽：" + slot);
        }

        private static void ClearSlotIf(LlmSlotRef slot, string providerId)
        {
            if (slot == null) return;
            if (string.Equals(slot.providerId, providerId, StringComparison.OrdinalIgnoreCase))
            {
                slot.providerId = string.Empty;
                slot.model = string.Empty;
            }
        }

        private static void DropSlotModel(LlmSlotRef slot, string providerId, string modelId)
        {
            if (slot == null) return;
            if (string.Equals(slot.providerId, providerId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(slot.model, modelId, StringComparison.OrdinalIgnoreCase))
            {
                slot.providerId = string.Empty;
                slot.model = string.Empty;
            }
        }

        private static string NormalizeSlot(string slot)
        {
            slot = (slot ?? string.Empty).Trim().ToLowerInvariant();
            if (slot == "thinking") return LlmSlotNames.Chat; // Legacy plugin/role alias.
            if (slot == "chat" || slot == "jev" || slot == "review" || slot == "multimodal" ||
                slot == "image" || slot == "speech")
                return slot;
            if (slot == "vision") return LlmSlotNames.Multimodal;
            if (slot == "tts" || slot == "voice") return LlmSlotNames.Speech;
            if (slot == "img" || slot == "picture") return LlmSlotNames.Image;
            return slot;
        }

        private static LlmSlotRef CloneSlot(LlmSlotRef value)
        {
            if (value == null) return new LlmSlotRef();
            return new LlmSlotRef
            {
                providerId = value.providerId ?? string.Empty,
                model = value.model ?? string.Empty
            };
        }

        private static LlmProviderRecord Clone(LlmProviderRecord value)
        {
            return new LlmProviderRecord
            {
                id = value.id,
                type = value.type,
                displayName = value.displayName,
                baseUrl = value.baseUrl,
                model = value.model,
                apiKey = value.apiKey,
                voice = value.voice,
                decisionMode = value.decisionMode,
                relayKey = value.relayKey,
                temperature = value.temperature,
                topP = value.topP,
                maxTokens = value.maxTokens,
                timeout = value.timeout,
                transientRetries = value.transientRetries,
                proxy = value.proxy,
                thinkingEnabled = value.thinkingEnabled,
                reasoningEffort = value.reasoningEffort,
                models = (value.models ?? new List<LlmModelEntry>()).Select(m => new LlmModelEntry
                {
                    id = m.id,
                    enabled = m.enabled,
                    roles = m.roles == null ? new List<string>() : new List<string>(m.roles)
                }).ToList()
            };
        }

        private static LlmProviderBriefData ToBrief(LlmProviderRecord item)
        {
            NormalizeProvider(item);
            return new LlmProviderBriefData
            {
                Id = item.id,
                DisplayName = item.displayName,
                Type = item.type,
                BaseUrl = item.baseUrl,
                HasApiKey = !string.IsNullOrWhiteSpace(item.apiKey),
                Models = item.models.Select(m => new LlmModelBriefData
                {
                    Id = m.id,
                    Enabled = m.enabled,
                    Roles = m.roles == null ? new List<string>() : new List<string>(m.roles)
                }).ToList()
            };
        }

        private static LlmEndpointData ToEndpoint(LlmProviderRecord item, string model)
        {
            return new LlmEndpointData
            {
                ProviderId = item.id,
                Type = item.type,
                DisplayName = item.displayName,
                BaseUrl = item.baseUrl,
                ApiKey = item.apiKey,
                Model = string.IsNullOrWhiteSpace(model) ? item.model : model.Trim(),
                TimeoutSeconds = item.timeout <= 0 ? 120 : item.timeout,
                Proxy = item.proxy ?? string.Empty
            };
        }

        private static DeepSeekConfigData ToConfig(LlmProviderRecord item, string model, bool? thinkingOverride)
        {
            return new DeepSeekConfigData
            {
                ProviderId = item.id,
                Type = LlmProviderCatalog.NormalizeType(item.type),
                ApiKey = item.apiKey,
                BaseUrl = item.baseUrl,
                Model = string.IsNullOrWhiteSpace(model) ? item.model : model.Trim(),
                Temperature = item.temperature <= 0
                    ? (LlmProviderCatalog.IsGeminiNative(item.type) ? 0.7f : 0.3f)
                    : item.temperature,
                TopP = item.topP <= 0 ? 1f : item.topP,
                MaxTokens = item.maxTokens <= 0 ? 8192 : item.maxTokens,
                TimeoutSeconds = item.timeout <= 0 ? 120 : item.timeout,
                TransientErrorRetries = item.transientRetries,
                ThinkingEnabled = thinkingOverride ?? item.thinkingEnabled,
                ReasoningEffort = item.reasoningEffort,
                EmptyContentRetries = 1
            };
        }

        private sealed class FileData
        {
            public string currentId { get; set; }
            public MainModelFallbackSettings mainFallbacks { get; set; }
            public bool localVoiceImported { get; set; }
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public LlmSlotRef thinking { get; set; }
            public LlmSlotRef jev { get; set; }
            public LlmSlotRef review { get; set; }
            public float? reviewTemperature { get; set; }
            public LlmSlotRef multimodal { get; set; }
            public LlmSlotRef image { get; set; }
            public LlmSlotRef speech { get; set; }
            public List<LlmProviderRecord> providers { get; set; } = new List<LlmProviderRecord>();
        }
    }

    public sealed class LlmSlotRef
    {
        public string providerId { get; set; }
        public string model { get; set; }
    }

    public sealed class MainModelFallbackSettings
    {
        public LlmSlotRef secondary { get; set; }
        public LlmSlotRef tertiary { get; set; }
    }

    public sealed class LlmModelEntry
    {
        public string id { get; set; }
        public bool enabled { get; set; } = true;
        public List<string> roles { get; set; } = new List<string>();
    }

    public sealed class LlmProviderRecord
    {
        public string id { get; set; }
        public string type { get; set; }
        public string displayName { get; set; }
        public string baseUrl { get; set; }
        public string model { get; set; }
        public string apiKey { get; set; }
        public string voice { get; set; }
        public string decisionMode { get; set; }
        public string relayKey { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool clearRelayKey { get; set; }
        public float temperature { get; set; }
        public float topP { get; set; }
        public int maxTokens { get; set; }
        public int timeout { get; set; }
        public int transientRetries { get; set; } = 2;
        public string proxy { get; set; }
        public bool thinkingEnabled { get; set; }
        public string reasoningEffort { get; set; }
        public List<LlmModelEntry> models { get; set; } = new List<LlmModelEntry>();
    }
}
