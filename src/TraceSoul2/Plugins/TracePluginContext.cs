using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;

namespace TraceSoul2.Plugins
{
    /// <summary>宿主提供给插件注册贡献的入口（由插件管理器实现）。</summary>
    public interface ITracePluginRegistrar
    {
        void RegisterCallable(string pluginId, ITraceCallableContribution contribution);
        void RegisterMountedFacet(string pluginId, ITraceMountedFacet facet);
        void RegisterMomentSource(string pluginId, ITraceMomentSource source);
        void RegisterBackgroundService(string pluginId, ITraceBackgroundService service);
        void RegisterWebSocketEndpoint(string pluginId, ITraceWebSocketEndpoint endpoint);
    }
    /// <summary>
    /// 插件声明的 WebSocket 入口：平台插件（如 OneBot 反向 WS）把自己的端点挂到宿主 HTTP 服务器上。
    /// 客户端（NapCat 等）主动连进来，宿主只负责完成握手并把连接交给插件。
    /// </summary>
    public interface ITraceWebSocketEndpoint
    {
        /// <summary>端点在宿主 HTTP 服务器上的路径，如 "/ws"。</summary>
        string Path { get; }

        /// <summary>握手前的鉴权：返回 false 时宿主回 401 并拒绝连接。</summary>
        bool Accept(string authorizationHeader, string queryString);

        /// <summary>连接已建立；持有该连接直到关闭或取消。</summary>
        Task OnConnectedAsync(WebSocket socket, CancellationToken token);
    }

    /// <summary>宿主提供的基础设施。它不提供“调用另一个插件”的入口。</summary>
    public sealed class TracePluginServices
    {
        public IMemoryStore Storage { get; private set; }
        /// <summary>统一的当前生活状态读写面；插件更新时必须带 source/source_id。</summary>
        public ILifeStateStore LifeState { get; set; }
        public IHierarchicalVectorRouter Router { get; private set; }
        public ILlmClient Llm { get; set; }

        /// <summary>模型供应商目录（宿主注入；插件按 id 或用途槽解析密钥，为 null 时只能用自己的 plugin.json）。</summary>
        public ILlmProviderDirectory Providers { get; set; }

        /// <summary>文本语义向量编码服务（宿主注入；为 null 时插件自行兜底）。</summary>
        public IEmbeddingService Embedding { get; set; }

        /// <summary>记忆神经子代理专用小模型（宿主注入；为 null 时退回字符路由）。</summary>
        public ILlmClient NerveLlm { get; set; }

        /// <summary>复盘专用（日构建 / identity.review）。未指定槽时由宿主回落到开口关思考。</summary>
        public ILlmClient ReviewLlm { get; set; }

        /// <summary>
        /// 公共上下文装配器（宿主注入）。插件的专用对话模型应走这里，
        /// 与心智/开口共享身份卡和历史前缀，不要自己截断拼接。
        /// </summary>
        public ILlmContextAssembler ContextPack { get; set; }

        /// <summary>宿主注入的链路时序日志出口；插件只记阶段、耗时和状态，不记密钥。</summary>
        public Action<string> TimingLog { get; set; }

        /// <summary>语义向量拼装引擎（宿主注入；为 null 时退回 n-gram 打分）。</summary>
        public IMemoryRecallEngine Recall { get; set; }

        /// <summary>平台注册表：平台插件注册自己，感官目录与控制台读取。</summary>
        public PlatformRegistry Platforms { get; set; }

        /// <summary>插件声明的 WebSocket 入口（宿主启动 HTTP 服务器后逐个挂载）。</summary>
        public List<ITraceWebSocketEndpoint> WebSocketEndpoints { get; } = new List<ITraceWebSocketEndpoint>();

        /// <summary>平台适配器注册表：平台插件把适配器挂进来，感官插件据此把表达发到对应平台。</summary>
        public List<ITracePlatformAdapter> PlatformAdapters { get; } = new List<ITracePlatformAdapter>();

        /// <summary>启用插件的贡献目录提供者（由插件管理器注入，供感官目录等基础设施读取）。</summary>
        public Func<List<TraceContributionDescriptorData>> EnabledCatalogProvider { get; set; }

        /// <summary>按当前轮可用性过滤的贡献目录提供者（与 Brain 可调用的目录一致）。</summary>
        public Func<TraceTurnContext, List<TraceContributionDescriptorData>> AvailableCatalogProvider { get; set; }

        /// <summary>整轮表达结束后的收尾钩子（平台插件用来把暂存文字与表情合并成一条消息发送等）。</summary>
        public List<Func<TraceTurnContext, Task>> TurnCompleteHooks { get; } =
            new List<Func<TraceTurnContext, Task>>();

        /// <summary>
        /// 对话入口开始思考回应，或自主轮次已决定对外表达时的钩子。
        /// 平台可用它展示「正在输入」；钩子不得影响表达主链。
        /// </summary>
        public List<Func<TraceTurnContext, Task>> ExpressionStartingHooks { get; } =
            new List<Func<TraceTurnContext, Task>>();

        /// <summary>
        /// 本轮对外内容（包括轮后生图等延迟内容）全部发送完毕后的钩子。
        /// </summary>
        public List<Func<TraceTurnContext, Task>> ExpressionCompletedHooks { get; } =
            new List<Func<TraceTurnContext, Task>>();

        /// <summary>
        /// 器官插件可把心智补充说明挂到这里。未加载或未就绪时不要挂；
        /// 心智核心提示词里不应出现相机/出图字段。
        /// </summary>
        public List<Func<TraceTurnContext, string>> MindPromptAppends { get; } =
            new List<Func<TraceTurnContext, string>>();

        /// <summary>
        /// 器官插件可把心智 JSON 额外字段挂到这里，例如 "image":"有|无"。
        /// 未就绪时返回空；核心 JSON 样例本身不含出图字段。
        /// </summary>
        public List<Func<TraceTurnContext, string>> MindJsonFields { get; } =
            new List<Func<TraceTurnContext, string>>();

        /// <summary>数据目录（宿主注入；平台插件读自己的配置文件用）。</summary>
        public string DataDirectory { get; set; }

        /// <summary>心跳最短分钟。与最长都为 0 表示关闭。</summary>
        public int HeartbeatMinMinutes { get; set; }

        /// <summary>心跳最长分钟。每次入站后的第一次心跳在此范围内随机。</summary>
        public int HeartbeatMaxMinutes { get; set; }

        public TracePluginServices(IMemoryStore storage, IHierarchicalVectorRouter router)
        {
            Storage = storage ?? throw new ArgumentNullException("storage");
            Router = router ?? throw new ArgumentNullException("router");
            Platforms = new PlatformRegistry();
        }

        public void SetRouter(IHierarchicalVectorRouter router)
        {
            Router = router ?? throw new ArgumentNullException("router");
        }

        public void LogTiming(string traceId, string stage, long? elapsedMs = null, string detail = null)
        {
            var id = string.IsNullOrWhiteSpace(traceId) ? "--------" : traceId.Trim();
            var message = "[链路 " + id + "] " + (stage ?? string.Empty);
            if (elapsedMs.HasValue) message += "｜耗时 " + elapsedMs.Value + " ms";
            if (!string.IsNullOrWhiteSpace(detail)) message += "｜" + detail.Trim();
            TimingLog?.Invoke(message);
        }
    }

    /// <summary>每轮通用工作区；领域插件只能在自己的 State 槽中保存私有运行态。</summary>
    public sealed class TraceTurnWorkspace
    {
        private readonly Dictionary<string, object> pluginStates =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TraceContextBlockData> facetCache =
            new Dictionary<string, TraceContextBlockData>(StringComparer.OrdinalIgnoreCase);

        public List<TraceCapabilityResultData> Results { get; private set; } =
            new List<TraceCapabilityResultData>();
        public List<TraceContextBlockData> ContextBlocks { get; private set; } =
            new List<TraceContextBlockData>();
        public List<BrainFacetOutputData> FacetOutputs { get; private set; } =
            new List<BrainFacetOutputData>();

        /// <summary>本轮心智/开口共用的预激活记忆原文；插件装配上下文时应原样传入。</summary>
        public string SharedMemory { get; set; }

        /// <summary>本轮已读到的 QQ 说说摘要；心智和开口共用，避免假装看过。</summary>
        public string QzoneSeen { get; set; }

        /// <summary>本轮向量检索入选的长尾工具；心智动态段注入，tool_call 白名单也用它。</summary>
        public List<ToolCandidateData> ToolCandidates { get; set; }

        /// <summary>心智选定工具的执行摘要；开口据此自然说出「我先看看 / 发好了」。</summary>
        public string ToolReport { get; set; }

        public T GetOrCreateState<T>(string pluginId, Func<T> factory) where T : class
        {
            object existing;
            if (pluginStates.TryGetValue(pluginId, out existing)) return existing as T;
            var created = factory == null ? null : factory();
            pluginStates[pluginId] = created;
            return created;
        }

        public bool TryGetFacetCache(string id, out TraceContextBlockData value)
        {
            return facetCache.TryGetValue(id, out value);
        }

        public void SetFacetCache(string id, TraceContextBlockData value)
        {
            facetCache[id] = value;
        }
    }

    public sealed class TraceTurnContext
    {
        public string ConversationId { get; private set; }
        public MomentRecord Moment { get; private set; }
        public IReadOnlyList<MomentRecord> RecentMoments { get; private set; }
        public int RawHistoryLimit { get; private set; }
        /// <summary>历史窗口滑动粒度；≤0 时装配器按默认 4 条处理。</summary>
        public int HistoryWindowAlign { get; private set; }
        public bool RequiresExpression { get; private set; }
        /// <summary>本轮中枢轨道：dialogue / mind / subconscious。</summary>
        public string Wake { get; private set; }
        public TracePluginServices Services { get; private set; }
        public TraceTurnWorkspace Workspace { get; private set; }
        public string TraceId { get; private set; }

        public TraceTurnContext(
            string conversationId,
            MomentRecord moment,
            List<MomentRecord> recentMoments,
            int rawHistoryLimit,
            bool requiresExpression,
            TracePluginServices services,
            string wake = null,
            string traceId = null,
            int historyWindowAlign = 0)
        {
            ConversationId = conversationId;
            Moment = moment;
            RecentMoments = recentMoments ?? new List<MomentRecord>();
            RawHistoryLimit = Math.Max(0, rawHistoryLimit);
            HistoryWindowAlign = Math.Max(0, historyWindowAlign);
            RequiresExpression = requiresExpression;
            Wake = KernelWakeValues.Normalize(wake);
            if (string.IsNullOrEmpty(Wake))
                Wake = requiresExpression ? KernelWakeValues.Dialogue : KernelWakeValues.Mind;
            Services = services;
            TraceId = traceId ?? string.Empty;
            Workspace = new TraceTurnWorkspace();
        }
    }

    public sealed class TracePluginContext
    {
        private readonly ITracePluginRegistrar registrar;

        public TracePluginServices Services { get; private set; }
        public TracePluginMetadataData Plugin { get; private set; }

        /// <summary>外部插件包的所在文件夹（内置插件为 null）；只用于读取代码包与静态资源。</summary>
        public string PackageDirectory { get; private set; }

        /// <summary>外部插件独立的持久目录（plugins_data/&lt;包目录名&gt;）；配置与运行数据应写在这里。</summary>
        public string PluginDataDirectory { get; private set; }

        public TracePluginContext(
            ITracePluginRegistrar registrar,
            TracePluginServices services,
            TracePluginMetadataData plugin,
            string packageDirectory = null,
            string pluginDataDirectory = null)
        {
            this.registrar = registrar;
            Services = services;
            Plugin = plugin;
            PackageDirectory = packageDirectory;
            PluginDataDirectory = pluginDataDirectory;
        }

        public void AddCallable(ITraceCallableContribution contribution)
        {
            registrar.RegisterCallable(Plugin.Id, contribution);
        }

        public void AddMountedFacet(ITraceMountedFacet facet)
        {
            registrar.RegisterMountedFacet(Plugin.Id, facet);
        }

        public void AddMomentSource(ITraceMomentSource source)
        {
            registrar.RegisterMomentSource(Plugin.Id, source);
        }

        public void AddBackgroundService(ITraceBackgroundService service)
        {
            registrar.RegisterBackgroundService(Plugin.Id, service);
        }

        /// <summary>
        /// 注册插件自己的 WebSocket 入口。宿主按插件生命周期移除旧端点，
        /// 因此外部插件热重载后不会继续命中已经卸载的实例。
        /// </summary>
        public void AddWebSocketEndpoint(ITraceWebSocketEndpoint endpoint)
        {
            registrar.RegisterWebSocketEndpoint(Plugin.Id, endpoint);
        }
    }
}
