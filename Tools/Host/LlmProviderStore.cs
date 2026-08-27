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

        public LlmProviderStore(string path)
        {
            this.path = path ?? throw new ArgumentNullException("path");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var created = !File.Exists(path);
            data = LoadUnsafe();
            if (created) SaveUnsafe();
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

        public LlmProviderRecord Get(string id)
        {
            lock (gate)
            {
                var item = Find(id);
                return item == null ? null : Clone(item);
            }
        }

        public Dictionary<string, LlmSlotRef> Slots()
        {
            lock (gate)
            {
                var chat = Find(data.currentId) ?? data.providers.FirstOrDefault();
                var map = new Dictionary<string, LlmSlotRef>(StringComparer.OrdinalIgnoreCase)
                {
                    [LlmSlotNames.Chat] = chat == null
                        ? new LlmSlotRef()
                        : new LlmSlotRef { providerId = chat.id, model = chat.model ?? string.Empty },
                    [LlmSlotNames.Thinking] = CloneSlot(data.thinking),
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
                var item = Find(id);
                if (item == null)
                {
                    item = new LlmProviderRecord { id = id };
                    data.providers.Add(item);
                }
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
                if (incoming.temperature > 0) item.temperature = incoming.temperature;
                if (incoming.topP > 0) item.topP = incoming.topP;
                if (incoming.maxTokens > 0) item.maxTokens = incoming.maxTokens;
                if (incoming.timeout > 0) item.timeout = incoming.timeout;
                if (incoming.transientRetries >= 0)
                    item.transientRetries = Math.Max(0, Math.Min(6, incoming.transientRetries));
                if (incoming.proxy != null) item.proxy = incoming.proxy.Trim();
                item.thinkingEnabled = incoming.thinkingEnabled;
                if (!string.IsNullOrWhiteSpace(incoming.reasoningEffort))
                    item.reasoningEffort = incoming.reasoningEffort.Trim();
                if (string.IsNullOrWhiteSpace(data.currentId)) data.currentId = id;
                NormalizeProvider(item);
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
                    transientRetries = 3
                };
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
                if (string.IsNullOrWhiteSpace(data.currentId)) data.currentId = item.id;
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
                    data.currentId = data.providers[0].id;
                ClearSlotIf(data.thinking, item.id);
                ClearSlotIf(data.review, item.id);
                ClearSlotIf(data.multimodal, item.id);
                ClearSlotIf(data.image, item.id);
                ClearSlotIf(data.speech, item.id);
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
                DropSlotModel(data.thinking, id, modelId);
                DropSlotModel(data.review, id, modelId);
                DropSlotModel(data.multimodal, id, modelId);
                DropSlotModel(data.image, id, modelId);
                DropSlotModel(data.speech, id, modelId);
                SaveUnsafe();
                return Clone(item);
            }
        }

        public ILlmClient CreateClient(string id)
        {
            return CreateClient(id, null, null);
        }

        public ILlmClient CreateClient(string id, string model, bool? thinkingOverride)
        {
            lock (gate)
            {
                var item = Find(id);
                if (item == null || string.IsNullOrWhiteSpace(item.apiKey)) return null;
                var config = ToConfig(item, model, thinkingOverride);
                if (LlmProviderCatalog.IsGeminiNative(item.type))
                    return new GeminiClientManager(config);
                return new DeepSeekClientManager(config);
            }
        }

        public ILlmClient CreateCurrentClient()
        {
            string chatId;
            bool thinkingOn;
            LlmSlotRef thinking;
            lock (gate)
            {
                chatId = data.currentId;
                var chat = Find(chatId);
                thinkingOn = chat != null && chat.thinkingEnabled;
                thinking = CloneSlot(data.thinking);
            }
            if (thinkingOn && thinking != null && !string.IsNullOrWhiteSpace(thinking.providerId))
            {
                var client = CreateClient(thinking.providerId, thinking.model, true);
                if (client != null) return client;
            }
            return CreateClient(chatId, null, null);
        }

        /// <summary>复盘：指定槽则用该模型并关思考；未指定则用对话开口关思考，避免推理模型把额度耗在 reasoning。</summary>
        public ILlmClient CreateReviewClient()
        {
            string providerId;
            string model;
            lock (gate)
            {
                var refer = data.review;
                if (refer != null && !string.IsNullOrWhiteSpace(refer.providerId))
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
            return CreateClient(providerId, model, false);
        }

        public DeepSeekConfigData CurrentConfig()
        {
            lock (gate)
            {
                var item = Find(data.currentId) ?? data.providers.FirstOrDefault();
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
            lock (gate)
            {
                if (string.Equals(slot, LlmSlotNames.Chat, StringComparison.OrdinalIgnoreCase))
                {
                    var chat = Find(data.currentId) ?? data.providers.FirstOrDefault();
                    return chat == null || string.IsNullOrWhiteSpace(chat.apiKey)
                        ? null
                        : ToEndpoint(chat, chat.model);
                }
                var refer = SlotOf(slot);
                if (refer != null && !string.IsNullOrWhiteSpace(refer.providerId))
                {
                    var item = Find(refer.providerId);
                    if (item != null && !string.IsNullOrWhiteSpace(item.apiKey))
                        return ToEndpoint(item, string.IsNullOrWhiteSpace(refer.model) ? item.model : refer.model);
                }
                foreach (var provider in data.providers)
                {
                    if (provider == null || string.IsNullOrWhiteSpace(provider.apiKey)) continue;
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
                    var chat = Find(data.currentId) ?? data.providers.FirstOrDefault();
                    return chat == null || string.IsNullOrWhiteSpace(chat.apiKey)
                        ? null
                        : ToEndpoint(chat, chat.model);
                }
                var refer = SlotOf(slot);
                if (refer == null || string.IsNullOrWhiteSpace(refer.providerId)) return null;
                var item = Find(refer.providerId);
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

        private FileData LoadUnsafe()
        {
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
            if (loaded.providers.Count == 0) loaded.providers.Add(DefaultProvider());
            foreach (var item in loaded.providers)
            {
                if (item == null) continue;
                item.type = LlmProviderCatalog.NormalizeType(item.type);
                NormalizeProvider(item);
            }
            if (string.IsNullOrWhiteSpace(loaded.currentId))
                loaded.currentId = loaded.providers[0].id;
            return loaded;
        }

        private void SaveUnsafe()
        {
            File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
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
            }
            if (item.models.Count == 0 && !string.IsNullOrWhiteSpace(item.model))
                EnsureModel(item, item.model, LlmSlotNames.Chat);
            if (item.timeout <= 0) item.timeout = 120;
            item.transientRetries = Math.Max(0, Math.Min(6, item.transientRetries));
            if (item.proxy == null) item.proxy = string.Empty;
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
            return existing;
        }

        private LlmSlotRef SlotOf(string slot)
        {
            if (string.Equals(slot, LlmSlotNames.Thinking, StringComparison.OrdinalIgnoreCase)) return data.thinking;
            if (string.Equals(slot, LlmSlotNames.Review, StringComparison.OrdinalIgnoreCase)) return data.review;
            if (string.Equals(slot, LlmSlotNames.Multimodal, StringComparison.OrdinalIgnoreCase)) return data.multimodal;
            if (string.Equals(slot, LlmSlotNames.Image, StringComparison.OrdinalIgnoreCase)) return data.image;
            if (string.Equals(slot, LlmSlotNames.Speech, StringComparison.OrdinalIgnoreCase)) return data.speech;
            return null;
        }

        private void AssignSlot(string slot, LlmSlotRef value)
        {
            if (string.Equals(slot, LlmSlotNames.Thinking, StringComparison.OrdinalIgnoreCase)) data.thinking = value;
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
            if (slot == "chat" || slot == "thinking" || slot == "review" || slot == "multimodal" ||
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
            public LlmSlotRef thinking { get; set; }
            public LlmSlotRef review { get; set; }
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
        public float temperature { get; set; }
        public float topP { get; set; }
        public int maxTokens { get; set; }
        public int timeout { get; set; }
        public int transientRetries { get; set; } = 3;
        public string proxy { get; set; }
        public bool thinkingEnabled { get; set; }
        public string reasoningEffort { get; set; }
        public List<LlmModelEntry> models { get; set; } = new List<LlmModelEntry>();
    }
}
