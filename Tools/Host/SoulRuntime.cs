using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Tools.Memory;
using TraceSoul2.Util;

namespace TraceSoul2.Host
{
    /// <summary>C# 常驻宿主：Moment → Brain → Reply。Unity 不在这条链上。</summary>
    public sealed class SoulRuntime : IDisposable
    {
        public const string DefaultConversationId = "tracesoul2";

        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly Channel<KernelLogic.DeferredTurnWork> deferredTurns =
            Channel.CreateUnbounded<KernelLogic.DeferredTurnWork>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        private readonly CancellationTokenSource deferredCts = new CancellationTokenSource();
        private readonly Task deferredWorker;
        private readonly object eventGate = new object();
        private readonly List<Channel<string>> eventSubscribers = new List<Channel<string>>();
        private readonly SqliteVectorManager vectorStore;
        private readonly SoulRuntimeSettings runtimeSettings;
        private HierarchicalVectorRouterLogic router;
        private MemoryRecallEngine recallEngine;
        private OnnxBgeEncoder encoder;
        private string nerveProviderId;
        private bool disposed;

        public string ConversationId { get { return DefaultConversationId; } }
        public string DataDirectory { get; private set; }
        public string PluginsDirectory { get; private set; }
        public string PluginsDataDirectory { get; private set; }
        public SqliteMemoryManager Store { get; private set; }
        public TracePluginManager Plugins { get; private set; }
        public LlmProviderStore Providers { get; private set; }
        public int ContextInjectionCount { get; private set; }
        public int HeartbeatMinMinutes { get; private set; }
        public int HeartbeatMaxMinutes { get; private set; }
        public ChatTurnResultData LastTurn { get; private set; }
        public MemoryNerveSettings NerveSettings { get; private set; }

        public SoulRuntime(
            string dataDirectory,
            string pluginsDirectory = null,
            string pluginsDataDirectory = null)
        {
            DataDirectory = dataDirectory ?? throw new ArgumentNullException("dataDirectory");
            Directory.CreateDirectory(DataDirectory);
            runtimeSettings = SoulRuntimeSettings.Load(Path.Combine(DataDirectory, "runtime-settings.json"));
            ContextInjectionCount = runtimeSettings.ContextInjectionCount;
            HeartbeatMinMinutes = runtimeSettings.HeartbeatMinMinutes;
            HeartbeatMaxMinutes = runtimeSettings.HeartbeatMaxMinutes;
            Store = new SqliteMemoryManager(Path.Combine(DataDirectory, "tracesoul2-brainframe.sqlite3"));
            vectorStore = new SqliteVectorManager(Path.Combine(DataDirectory, "tracesoul2-vectors.sqlite3"));
            router = new HierarchicalVectorRouterLogic(new BagOfCharsVectorEncoder(), vectorStore);
            var services = new TracePluginServices(Store, router);
            services.DataDirectory = DataDirectory;
            services.HeartbeatMinMinutes = HeartbeatMinMinutes;
            services.HeartbeatMaxMinutes = HeartbeatMaxMinutes;
            services.TimingLog = Emit;
            liveServices = services;
            Providers = new LlmProviderStore(Path.Combine(DataDirectory, "llm-providers.json"));
            services.Providers = Providers;
            services.ReviewLlm = Providers.CreateReviewClient();
            Plugins = new TracePluginManager(Store, services);
            Plugins.Discover(typeof(DialogueTracePlugin).Assembly);
            IdentityCardLogic.PreferDataDirectory(DataDirectory);
            // 外部插件包：家目录 plugins/，可被 TRACESOUL2_PLUGINS 覆盖。不再写死盘符。
            var pluginsDir = string.IsNullOrWhiteSpace(pluginsDirectory)
                ? Environment.GetEnvironmentVariable("TRACESOUL2_PLUGINS")
                : pluginsDirectory;
            if (string.IsNullOrWhiteSpace(pluginsDir) && TraceHome.Current != null)
                pluginsDir = TraceHome.Current.PluginsDirectory;
            if (string.IsNullOrWhiteSpace(pluginsDir))
                pluginsDir = Path.Combine(TraceHome.DefaultRoot(), "plugins");
            PluginsDirectory = Path.GetFullPath(pluginsDir);
            if (string.IsNullOrWhiteSpace(pluginsDataDirectory))
                pluginsDataDirectory = Environment.GetEnvironmentVariable(TraceHome.EnvPluginsData);
            if (string.IsNullOrWhiteSpace(pluginsDataDirectory) && TraceHome.Current != null)
                pluginsDataDirectory = TraceHome.Current.PluginsDataDirectory;
            if (string.IsNullOrWhiteSpace(pluginsDataDirectory))
                pluginsDataDirectory = Path.Combine(
                    Path.GetDirectoryName(PluginsDirectory) ?? PluginsDirectory,
                    "plugins_data");
            PluginsDataDirectory = Path.GetFullPath(pluginsDataDirectory);
            Directory.CreateDirectory(PluginsDirectory);
            Directory.CreateDirectory(PluginsDataDirectory);
            externalPlugins = new ExternalPluginLoader(PluginsDirectory, PluginsDataDirectory);
            externalPlugins.ScanAndLoad(Plugins, Emit);
            // 记忆神经已改造成新事件结构（event_indexes/event_entries），只读召回，强制启用
            // （历史版本曾因引用已删除的旧表而把它持久化为禁用）。构筑与浸染仍由每日管线完成。
            Plugins.SetEnabled("builtin.memory", true);
            NerveSettings = MemoryNerveSettings.Load(Path.Combine(DataDirectory, "memory-nerve.json"));
            TryWireMemoryEngine(services);
            RebuildOntology();
            deferredWorker = Task.Run(() => RunDeferredTurnLoopAsync(deferredCts.Token));
            Emit("host 已启动");
        }

        public int SetContextInjectionCount(int value)
        {
            ContextInjectionCount = Math.Max(0, Math.Min(100, value));
            runtimeSettings.ContextInjectionCount = ContextInjectionCount;
            runtimeSettings.Save(Path.Combine(DataDirectory, "runtime-settings.json"));
            Emit("上下文拼接已更新：最近 " + ContextInjectionCount + " 条对话原文");
            return ContextInjectionCount;
        }

        public object SetHeartbeatRange(int minMinutes, int maxMinutes)
        {
            HeartbeatLogic.NormalizeRange(ref minMinutes, ref maxMinutes);
            HeartbeatMinMinutes = minMinutes;
            HeartbeatMaxMinutes = maxMinutes;
            runtimeSettings.HeartbeatMinMinutes = minMinutes;
            runtimeSettings.HeartbeatMaxMinutes = maxMinutes;
            runtimeSettings.HeartbeatRangeSpecified = true;
            runtimeSettings.Save(Path.Combine(DataDirectory, "runtime-settings.json"));
            if (liveServices != null)
            {
                liveServices.HeartbeatMinMinutes = minMinutes;
                liveServices.HeartbeatMaxMinutes = maxMinutes;
            }
            Emit(HeartbeatLogic.IsEnabled(minMinutes, maxMinutes)
                ? "心跳范围已更新：" + minMinutes + "–" + maxMinutes + " 分钟"
                : "心跳已关闭");
            return new { minMinutes, maxMinutes };
        }

        /// <summary>注入记忆神经的子代理小模型与 BGE 语义向量引擎；失败则退回字符路由。</summary>
        private void TryWireMemoryEngine(TracePluginServices services)
        {
            try
            {
                var modelDir = Path.Combine(AppContext.BaseDirectory, "Models", "BgeSmallZh");
                encoder = new OnnxBgeEncoder(
                    Path.Combine(modelDir, "bge-small-zh-v1.5.onnx"),
                    Path.Combine(modelDir, "vocab.txt"));
                recallEngine = new MemoryRecallEngine(encoder, vectorStore)
                {
                    DefaultTopK = Math.Max(1, Math.Min(10, NerveSettings.TopK))
                };
                router = new HierarchicalVectorRouterLogic(encoder, vectorStore);
                services.SetRouter(router);
                services.Recall = recallEngine;
                services.Embedding = new BgeEmbeddingService(encoder);
                services.NerveLlm = BuildNerveClient();
                servicesNerveReady = services.NerveLlm != null;
                Emit("记忆神经：子代理小模型 + BGE 语义向量已就绪（标签路由与拼装共用，top_k=" + recallEngine.DefaultTopK + "）");
            }
            catch (Exception exception)
            {
                Emit("记忆神经引擎未就绪（退回字符路由）：" + exception.Message);
            }
        }

        /// <summary>
        /// 子代理小模型：优先用设置里指定的提供商槽（provider_id），否则用当前提供商
        /// 并压低温（0.1）与关闭思考。找不到可用 Key 时返回 null（插件走字符路由兜底）。
        /// </summary>
        private ILlmClient BuildNerveClient()
        {
            var wanted = string.IsNullOrWhiteSpace(NerveSettings.ProviderId)
                ? Providers.CurrentId
                : NerveSettings.ProviderId;
            var record = Providers.Get(wanted) ?? Providers.Get(Providers.CurrentId);
            if (record == null || string.IsNullOrWhiteSpace(record.apiKey)) return null;
            nerveProviderId = record.id;
            var config = new DeepSeekConfigData
            {
                ProviderId = record.id,
                ApiKey = record.apiKey,
                BaseUrl = record.baseUrl,
                Model = record.model,
                Temperature = 0.1f,
                TopP = record.topP <= 0 ? 1f : record.topP,
                MaxTokens = 4096,
                TimeoutSeconds = record.timeout <= 0 ? 120 : record.timeout,
                ThinkingEnabled = false,
                ReasoningEffort = string.Empty,
                EmptyContentRetries = 1
            };
            return new DeepSeekClientManager(config);
        }

        /// <summary>记忆神经状态（控制台设置区展示）。</summary>
        public object NerveStatus()
        {
            return new
            {
                top_k = recallEngine == null ? NerveSettings.TopK : recallEngine.DefaultTopK,
                provider_id = NerveSettings.ProviderId ?? string.Empty,
                effective_provider_id = nerveProviderId ?? Providers.CurrentId,
                effective_model = Providers.Get(nerveProviderId ?? Providers.CurrentId)?.model ?? string.Empty,
                vector_model = encoder == null ? string.Empty : encoder.ModelId,
                vectors_count = vectorStore.CountEntryEmbeddings(),
                ready = recallEngine != null && servicesNerveReady
            };
        }

        private bool servicesNerveReady;
        private TracePluginServices liveServices;
        private ExternalPluginLoader externalPlugins;

        /// <summary>外部插件包状态（目录 + 各包加载/启用/错误）。</summary>
        public object ExternalPluginStatus()
        {
            return externalPlugins == null
                ? new { directory = string.Empty, dataDirectory = string.Empty, packages = new List<object>() }
                : externalPlugins.Status();
        }

        /// <summary>把插件目录读取、生命周期变更与对话/后台执行放在同一互斥边界内。</summary>
        public async Task<T> ExclusiveAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            if (action == null) throw new ArgumentNullException("action");
            await gate.WaitAsync(cancellationToken);
            try { return action(); }
            finally { gate.Release(); }
        }

        /// <summary>重新扫描插件目录：卸载全部外部包再加载（安装/卸载插件包后调用）。</summary>
        public object RescanExternalPlugins()
        {
            externalPlugins.ScanAndLoad(Plugins, Emit);
            return ExternalPluginStatus();
        }

        /// <summary>卸载单个外部插件包，并移到扫描目录外的可恢复区。</summary>
        public object UninstallExternalPlugin(string folderOrId)
        {
            var package = externalPlugins.Find(folderOrId);
            var dataPath = package == null ? string.Empty : package.DataPath ?? string.Empty;
            var backupPath = externalPlugins.Uninstall(folderOrId, Plugins);
            if (backupPath == null) throw new KeyNotFoundException("没有外部插件包：" + folderOrId);
            Emit("外部插件已卸载：" + folderOrId +
                 (string.IsNullOrWhiteSpace(backupPath) ? string.Empty : "（可恢复于 " + backupPath + "）"));
            return new
            {
                uninstalled = true,
                id = folderOrId,
                backupPath = backupPath ?? string.Empty,
                dataPath,
                dataPreserved = !string.IsNullOrWhiteSpace(dataPath),
                status = ExternalPluginStatus()
            };
        }

        public object SetPluginEnabled(string pluginId, bool enabled)
        {
            Plugins.SetEnabled(pluginId, enabled);
            externalPlugins?.SetEnabled(pluginId, enabled);
            Emit((enabled ? "开启 " : "关闭 ") + pluginId);
            return new { id = pluginId, enabled };
        }

        public ExternalPluginPackage FindExternalPackage(string folderOrId)
        {
            return externalPlugins == null ? null : externalPlugins.Find(folderOrId);
        }

        /// <summary>控制台表单：标签 + 说明 + 当前值。密钥不写日志。</summary>
        public object ReadPluginConfig(string pluginId)
        {
            if (string.Equals(pluginId, "builtin.onebot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pluginId, "onebot", StringComparison.OrdinalIgnoreCase))
            {
                var form = PluginConfigStore.ReadOneBot(DataDirectory);
                return WrapConfig(pluginId, "QQ 平台（OneBot v11 / NapCat）", form);
            }
            var package = FindExternalPackage(pluginId);
            if (package == null || string.IsNullOrWhiteSpace(package.Path))
                throw new KeyNotFoundException("没有可配置的插件：" + pluginId);
            var organ = PluginConfigStore.ReadPackage(
                package.Path, package.DataPath, package.AssemblyFile);
            return WrapConfig(
                string.IsNullOrWhiteSpace(package.Id) ? pluginId : package.Id,
                string.IsNullOrWhiteSpace(package.DisplayName) ? pluginId : package.DisplayName,
                organ);
        }

        /// <summary>把器官表单写到 plugins_data，OneBot 写到角色目录。器官保存后立即重扫。</summary>
        public PluginConfigStore.SaveResult WritePluginConfig(string pluginId, Dictionary<string, JsonElement> values)
        {
            if (string.Equals(pluginId, "builtin.onebot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pluginId, "onebot", StringComparison.OrdinalIgnoreCase))
            {
                PluginConfigStore.WriteOneBot(DataDirectory, values);
                Emit("QQ 平台配置已保存，宿主即将重启应用…");
                return new PluginConfigStore.SaveResult
                {
                    saved = true,
                    restart = true,
                    message = "配置已保存，宿主正在重启（约 2 秒）。"
                };
            }
            var package = FindExternalPackage(pluginId);
            if (package == null || string.IsNullOrWhiteSpace(package.Path))
                throw new KeyNotFoundException("没有可配置的插件：" + pluginId);
            PluginConfigStore.WritePackage(
                package.Path, package.DataPath, package.AssemblyFile, values);
            ScanAndLoadQuiet();
            Emit("已保存并重载器官包：" + (package.Id ?? pluginId));
            return new PluginConfigStore.SaveResult
            {
                saved = true,
                restart = false,
                message = "已保存并重载。"
            };
        }

        private void ScanAndLoadQuiet()
        {
            externalPlugins.ScanAndLoad(Plugins, Emit);
        }

        private static object WrapConfig(string id, string displayName, object form)
        {
            return new { id, displayName, form };
        }

        /// <summary>更新记忆神经设置：top_k 与子代理提供商槽，立即生效并落盘。</summary>
        public object UpdateNerve(int topK, string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) providerId = string.Empty;
            if (!string.IsNullOrWhiteSpace(providerId) && Providers.Get(providerId) == null)
                throw new InvalidOperationException("没有这个语言模型提供商：" + providerId);
            NerveSettings.TopK = Math.Max(1, Math.Min(10, topK <= 0 ? NerveSettings.TopK : topK));
            NerveSettings.ProviderId = providerId;
            NerveSettings.Save(Path.Combine(DataDirectory, "memory-nerve.json"));
            if (recallEngine != null) recallEngine.DefaultTopK = NerveSettings.TopK;
            if (liveServices != null)
            {
                liveServices.NerveLlm = BuildNerveClient();
                servicesNerveReady = liveServices.NerveLlm != null;
            }
            Emit("记忆神经设置已更新：top_k=" + NerveSettings.TopK + "，子代理提供商=" +
                 (string.IsNullOrEmpty(NerveSettings.ProviderId) ? "当前提供商" : NerveSettings.ProviderId));
            return NerveStatus();
        }

        public void RefreshReviewClient()
        {
            if (liveServices == null) return;
            liveServices.ReviewLlm = Providers.CreateReviewClient();
        }

        public object Status()
        {
            var pair = Store.LoadPairIdentity();
            var current = Providers.Get(Providers.CurrentId);
            var home = TraceHome.Current;
            return new
            {
                alive = true,
                version = TraceHome.HostVersion(),
                home = home == null ? null : home.Root,
                soulId = home == null ? null : home.SoulId,
                dataDirectory = DataDirectory,
                pluginsDirectory = PluginsDirectory,
                pluginsDataDirectory = PluginsDataDirectory,
                updatesDirectory = home == null ? null : home.UpdatesDirectory,
                conversationId = ConversationId,
                username = pair.Username,
                assname = pair.Assname,
                callName = pair.CallName,
                hostTime = DateTimeOffset.Now.ToString("O"),
                llm = current == null ? null : new
                {
                    current.id,
                    current.displayName,
                    current.baseUrl,
                    current.model,
                    hasApiKey = !string.IsNullOrWhiteSpace(current.apiKey)
                },
                slots = PublicSlots(),
                contextInjectionCount = ContextInjectionCount,
                heartbeatMinMinutes = HeartbeatMinMinutes,
                heartbeatMaxMinutes = HeartbeatMaxMinutes,
                asleep = Store.LoadOrCreateInnerRuntime(ConversationId).Asleep,
                nextHeartbeatUnixMs = HeartbeatLogic.NextDueUnixMs(Store, ConversationId)
            };
        }

        public async Task<ChatTurnResultData> PostMomentAsync(string text, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Moment 内容不能为空。");
            var rawClient = Providers.CreateCurrentClient();
            if (rawClient == null)
                throw new InvalidOperationException(
                    "供应商「" + Providers.CurrentId + "」还没有 API Key，先在「大脑 · LLM」里填上再开口。");
            var traceId = NewTraceId();
            var client = new TimedLlmClient(rawClient, traceId, Emit);
            var queueTimer = Stopwatch.StartNew();
            EmitTiming(traceId, "本地对话接收，等待运行锁");
            ChatTurnResultData completed = null;
            KernelLogic.DeferredTurnWork deferred = null;
            await gate.WaitAsync(cancellationToken);
            try
            {
                EmitTiming(traceId, "本地对话取得运行锁", queueTimer.ElapsedMilliseconds);
                var chat = new KernelLogic(Store, client, Plugins);
                var result = await chat.ChatAsync(
                    ConversationId, text.Trim(), "dialogue.receive",
                    ContextInjectionCount, cancellationToken, traceId);
                deferred = chat.TakeDeferredWork();
                LastTurn = result;
                foreach (var memoryResult in result.ContributionResults
                             .Where(x => x != null && x.CapabilityId == "memory.activate"))
                    Emit("记忆神经：" + memoryResult.Status + " | " + memoryResult.Summary);
                RebuildOntology();
                Emit("moment：" + Truncate(text, 40));
                completed = result;
            }
            finally { gate.Release(); }
            QueueDeferredTurn(deferred);
            return completed;
        }

        public async Task PollBackgroundAsync(CancellationToken cancellationToken)
        {
            if (!Store.LoadPairIdentity().IsComplete) return;
            var rawClient = Providers.CreateCurrentClient();
            if (rawClient == null) return;
            var lockTimer = Stopwatch.StartNew();
            await gate.WaitAsync(cancellationToken);
            try
            {
                var due = Plugins.PollBackgroundServices(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (due.Count == 0) return;
                foreach (var source in due)
                {
                    if (string.IsNullOrWhiteSpace(source.TraceId)) source.TraceId = NewTraceId();
                    var queueMs = source.OccurredUnixMs <= 0
                        ? 0
                        : Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - source.OccurredUnixMs);
                    EmitTiming(source.TraceId, "后台收件箱取出",
                        detail: "queue=" + queueMs + " ms｜lock=" + lockTimer.ElapsedMilliseconds + " ms");
                    var client = new TimedLlmClient(rawClient, source.TraceId, Emit);
                    var chat = new KernelLogic(Store, client, Plugins);
                    var result = await chat.ProcessPluginEventAsync(
                        string.IsNullOrWhiteSpace(source.ConversationId)
                            ? ConversationId : source.ConversationId,
                        source, ContextInjectionCount, cancellationToken);
                    QueueDeferredTurn(chat.TakeDeferredWork());
                    LastTurn = result;
                    RebuildOntology();
                    Emit("后台 Moment：" + Truncate(source.Content, 40));
                }
            }
            finally { gate.Release(); }
        }

        private void QueueDeferredTurn(KernelLogic.DeferredTurnWork work)
        {
            if (work == null || disposed) return;
            if (deferredTurns.Writer.TryWrite(work))
                EmitTiming(work.TraceId, "轮后任务入队", 0);
            else
                EmitTiming(work.TraceId, "轮后任务入队失败", 0);
        }

        /// <summary>
        /// 轮后慢任务（生图等）完全不持有对话 gate；只有发图入库时重新取锁。
        /// 单读者按入队顺序提交，避免和下一轮对话抢 SQLite。
        /// </summary>
        private async Task RunDeferredTurnLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await deferredTurns.Reader.WaitToReadAsync(cancellationToken))
                {
                    KernelLogic.DeferredTurnWork work;
                    while (deferredTurns.Reader.TryRead(out work))
                    {
                        Func<CancellationToken, Task> commit;
                        try
                        {
                            commit = await work.AnalyzeAsync(cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception exception)
                        {
                            EmitTiming(work.TraceId, "轮后任务失败", 0,
                                exception.GetType().Name + ": " + exception.Message);
                            continue;
                        }
                        if (commit == null) continue;

                        await gate.WaitAsync(cancellationToken);
                        try
                        {
                            await commit(cancellationToken);
                            RebuildOntology();
                        }
                        catch (Exception exception)
                        {
                            EmitTiming(work.TraceId, "轮后任务提交失败", 0,
                                exception.GetType().Name + ": " + exception.Message);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 宿主关闭。
            }
        }

        public sealed class EventSubscription : IDisposable
        {
            private readonly SoulRuntime owner;
            private readonly Channel<string> channel;
            private bool disposed;

            internal EventSubscription(SoulRuntime owner, Channel<string> channel)
            {
                this.owner = owner;
                this.channel = channel;
            }

            public ChannelReader<string> Reader { get { return channel.Reader; } }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.RemoveEventSubscriber(channel);
            }
        }

        public EventSubscription SubscribeEvents()
        {
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(500)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            lock (eventGate) eventSubscribers.Add(channel);
            return new EventSubscription(this, channel);
        }

        public void Emit(string message)
        {
            var line = DateTimeOffset.Now.ToString("HH:mm:ss.fff") + " " + (message ?? string.Empty);
            lock (eventGate)
            {
                foreach (var subscriber in eventSubscribers.ToList())
                    subscriber.Writer.TryWrite(line);
            }
        }

        private void RemoveEventSubscriber(Channel<string> channel)
        {
            lock (eventGate)
            {
                eventSubscribers.Remove(channel);
                channel.Writer.TryComplete();
            }
        }

        private void EmitTiming(string traceId, string stage, long? elapsedMs = null, string detail = null)
        {
            liveServices?.LogTiming(traceId, stage, elapsedMs, detail);
        }

        private static string NewTraceId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public void RebuildOntology()
        {
            var ontology = LifeTagVectorLogic.BuildOntology(
                Store, CoreVectorOntologyFactory.Create(Store.LoadPairIdentity()));
            vectorStore.UpsertOntology(ontology);
            router.Build(ontology);
        }

        public object LastTurnPayload()
        {
            var turn = LastTurn;
            if (turn == null)
            {
                var reviews = Store.GetRecentTurnReviews(ConversationId, 1);
                if (reviews.Count == 0) return null;
                var review = reviews[reviews.Count - 1];
                TurnPayloadSnapshotData snapshot = null;
                if (!string.IsNullOrWhiteSpace(review.PayloadJson))
                {
                    try { snapshot = TraceJson.FromJson<TurnPayloadSnapshotData>(review.PayloadJson); }
                    catch { snapshot = null; }
                }
                return new
                {
                    reply = "",
                    brainMode = review.BrainMode,
                    brainIntent = review.BrainIntent,
                    decisionSummary = review.DecisionSummary,
                    capabilitySummary = review.CapabilitySummary,
                    facetSummary = review.FacetSummary,
                    blocks = snapshot == null
                        ? new List<object>()
                        : snapshot.blocks.Select(x => new { x.facet_id, x.title, x.content })
                            .Cast<object>().ToList(),
                    facetOutputs = new List<object>(),
                    results = snapshot == null
                        ? new List<object>()
                        : snapshot.results.Select(x => new
                        {
                            CapabilityId = x.capability_id,
                            Status = x.status,
                            Summary = x.summary,
                            Payload = x.payload
                        }).Cast<object>().ToList()
                };
            }
            return new
            {
                reply = turn.Reply,
                brainMode = turn.BrainMode,
                brainIntent = turn.BrainIntent,
                decisionSummary = turn.DecisionSummary,
                blocks = turn.ContextBlocks.Select(x => new { x.FacetId, x.Title, x.Content }).ToList(),
                facetOutputs = turn.FacetOutputs.Select(x => new { x.facet_id, x.changed, x.summary }).ToList(),
                results = turn.ContributionResults.Select(x => new { x.CapabilityId, x.Status, x.Summary, x.Payload }).ToList()
            };
        }

        public object PublicProvider(LlmProviderRecord record)
        {
            if (record == null) return null;
            return new
            {
                record.id,
                record.type,
                record.displayName,
                record.baseUrl,
                record.model,
                hasApiKey = !string.IsNullOrWhiteSpace(record.apiKey),
                record.temperature,
                record.topP,
                record.maxTokens,
                timeout = record.timeout <= 0 ? 120 : record.timeout,
                proxy = record.proxy ?? string.Empty,
                record.thinkingEnabled,
                record.reasoningEffort,
                current = string.Equals(record.id, Providers.CurrentId, StringComparison.OrdinalIgnoreCase),
                models = (record.models ?? new List<LlmModelEntry>()).Select(m => new
                {
                    m.id,
                    m.enabled,
                    roles = m.roles ?? new List<string>()
                }).ToList()
            };
        }

        public object PublicSlots()
        {
            var slots = Providers.Slots();
            return new
            {
                chat = PublicSlot(slots, LlmSlotNames.Chat),
                thinking = PublicSlot(slots, LlmSlotNames.Thinking),
                review = PublicSlot(slots, LlmSlotNames.Review),
                multimodal = PublicSlot(slots, LlmSlotNames.Multimodal),
                image = PublicSlot(slots, LlmSlotNames.Image),
                speech = PublicSlot(slots, LlmSlotNames.Speech)
            };
        }

        private static object PublicSlot(Dictionary<string, LlmSlotRef> slots, string name)
        {
            LlmSlotRef value;
            if (slots == null || !slots.TryGetValue(name, out value) || value == null)
                return new { providerId = string.Empty, model = string.Empty };
            return new
            {
                providerId = value.providerId ?? string.Empty,
                model = value.model ?? string.Empty
            };
        }

        /// <summary>时间阶梯榜单（日/周/月/年/永久，各取最新周期）——控制台展示。</summary>
        public object LadderStatus()
        {
            var items = Store.GetAllLadderItems();
            var tiers = new[] { "day", "week", "month", "year", "forever" };
            var names = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "day", "日榜" }, { "week", "周榜" }, { "month", "月榜" },
                { "year", "年榜" }, { "forever", "永久榜" }
            };
            var result = new List<object>();
            foreach (var tier in tiers)
            {
                var latest = items.Where(x => x.Tier == tier)
                    .GroupBy(x => x.PeriodKey)
                    .OrderByDescending(x => x.Key)
                    .FirstOrDefault();
                result.Add(new
                {
                    tier,
                    name = names[tier],
                    period = latest == null ? string.Empty : latest.Key,
                    items = latest == null
                        ? new List<object>()
                        : latest.OrderBy(x => x.Rank).Select(x => new { x.Rank, x.Label, x.Reason })
                            .Cast<object>().ToList()
                });
            }
            return result;
        }

        /// <summary>今天的轨迹（滚动摘要，04:00 边界自动清空）。</summary>
        public object DayTrajectoryStatus()
        {
            var dayKey = DateTimeOffset.Now.AddHours(-4).ToString("yyyy-MM-dd");
            var record = Store.LoadDayTrajectory(dayKey);
            return new
            {
                day = dayKey,
                text = record == null ? string.Empty : record.Text ?? string.Empty
            };
        }

        /// <summary>身体路由（层、同层分数、当前激活）。</summary>
        public object MouthStatus()
        {
            return MouthLogic.Describe(DataDirectory, liveServices);
        }

        public object SaveMouths(string scene, string activeBody, IEnumerable<MouthRankEntry> items)
        {
            MouthLogic.SaveRouting(DataDirectory, scene, activeBody, items);
            Emit("身体路由已保存");
            return MouthStatus();
        }

        /// <summary>平台列表（连接状态与运行详情，控制台展示）。</summary>
        public object PlatformStatus()
        {
            return liveServices == null
                ? new List<object>()
                : liveServices.Platforms.List().Select(x => new
                {
                    x.Id,
                    x.DisplayName,
                    connected = SafeConnected(x),
                    details = SafeDetails(x)
                }).ToList();
        }

        /// <summary>插件声明的 WebSocket 入口（宿主 HTTP 服务器逐个挂载）。</summary>
        public List<ITraceWebSocketEndpoint> WebSocketEndpoints()
        {
            return liveServices == null
                ? new List<ITraceWebSocketEndpoint>()
                : liveServices.WebSocketEndpoints.ToList();
        }

        private static bool SafeConnected(PlatformHandle handle)
        {
            try { return handle != null && handle.IsConnected != null && handle.IsConnected(); }
            catch { return false; }
        }

        private static object SafeDetails(PlatformHandle handle)
        {
            try { return handle != null && handle.Details != null ? handle.Details() : null; }
            catch { return null; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { deferredTurns.Writer.TryComplete(); } catch { /* ignored */ }
            try { deferredCts.Cancel(); } catch { /* ignored */ }
            try { deferredWorker?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignored */ }
            deferredCts.Dispose();
            try { externalPlugins?.Dispose(); } catch { /* ignored */ }
            Plugins.Dispose();
            Store.Dispose();
            vectorStore.Dispose();
            if (encoder != null) encoder.Dispose();
            lock (eventGate)
            {
                foreach (var subscriber in eventSubscribers.ToList())
                    subscriber.Writer.TryComplete();
                eventSubscribers.Clear();
            }
        }

        private static string Truncate(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }

    /// <summary>角色运行设置；与调试分支目录一起隔离，不写进循的主数据库。</summary>
    public sealed class SoulRuntimeSettings
    {
        public int ContextInjectionCount { get; set; }
        public int HeartbeatMinMinutes { get; set; } = HeartbeatLogic.DefaultMinMinutes;
        public int HeartbeatMaxMinutes { get; set; } = HeartbeatLogic.DefaultMaxMinutes;
        public bool HeartbeatRangeSpecified { get; set; }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public static SoulRuntimeSettings Load(string path)
        {
            var settings = new SoulRuntimeSettings();
            if (!File.Exists(path)) return settings;
            try
            {
                var loaded = JsonSerializer.Deserialize<SoulRuntimeSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded != null)
                {
                    settings.ContextInjectionCount = Math.Max(0, Math.Min(100, loaded.ContextInjectionCount));
                    if (loaded.HeartbeatRangeSpecified)
                    {
                        var min = loaded.HeartbeatMinMinutes;
                        var max = loaded.HeartbeatMaxMinutes;
                        HeartbeatLogic.NormalizeRange(ref min, ref max);
                        settings.HeartbeatMinMinutes = min;
                        settings.HeartbeatMaxMinutes = max;
                        settings.HeartbeatRangeSpecified = true;
                    }
                }
            }
            catch
            {
                /* 损坏按默认值处理 */
            }
            return settings;
        }

        public void Save(string path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
    }

    /// <summary>记忆神经设置：向量拼装条数 + 子代理提供商槽。保存在数据目录 memory-nerve.json。</summary>
    public sealed class MemoryNerveSettings
    {
        public int TopK { get; set; } = 3;
        public string ProviderId { get; set; } = string.Empty;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public static MemoryNerveSettings Load(string path)
        {
            var settings = new MemoryNerveSettings();
            if (File.Exists(path))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<MemoryNerveSettings>(
                        File.ReadAllText(path), JsonOptions);
                    if (loaded != null)
                    {
                        settings.TopK = Math.Max(1, Math.Min(10, loaded.TopK));
                        settings.ProviderId = loaded.ProviderId ?? string.Empty;
                    }
                }
                catch
                {
                    /* 损坏按默认值处理 */
                }
            }
            return settings;
        }

        public void Save(string path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
    }
}
