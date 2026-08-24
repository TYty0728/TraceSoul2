using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Plugins;

namespace TraceSoul2.Manager
{
    /// <summary>只负责插件生命周期与贡献调度；它不编排插件之间的关系。</summary>
    public sealed class TracePluginManager : IDisposable, ITracePluginRegistrar
    {
        private sealed class LoadedPlugin
        {
            public ITracePlugin Instance;
            public TracePluginMetadataData Metadata;
            public string PackageDirectory;
            public string PluginDataDirectory;
        }

        private readonly IMemoryStore storage;
        private readonly TracePluginServices services;
        private readonly Dictionary<string, LoadedPlugin> plugins =
            new Dictionary<string, LoadedPlugin>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ITraceCallableContribution> callables =
            new Dictionary<string, ITraceCallableContribution>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ITraceMomentSource> momentSources =
            new Dictionary<string, ITraceMomentSource>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ITraceMountedFacet> facets =
            new Dictionary<string, ITraceMountedFacet>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ITraceBackgroundService> backgroundServices =
            new Dictionary<string, ITraceBackgroundService>(StringComparer.OrdinalIgnoreCase);

        public TracePluginServices Services { get { return services; } }

        public TracePluginManager(IMemoryStore storage, TracePluginServices services)
        {
            this.storage = storage ?? throw new ArgumentNullException("storage");
            this.services = services ?? throw new ArgumentNullException("services");
            services.EnabledCatalogProvider = () => GetEnabledCatalog();
            services.AvailableCatalogProvider = GetAvailableCatalog;
        }

        public void Discover(params Assembly[] assemblies)
        {
            var sources = assemblies == null || assemblies.Length == 0
                ? AppDomain.CurrentDomain.GetAssemblies()
                : assemblies;
            foreach (var type in sources.SelectMany(SafeTypes)
                         .Where(x => x != null && !x.IsAbstract && !x.IsInterface &&
                                     typeof(ITracePlugin).IsAssignableFrom(x)))
            {
                ITracePlugin instance;
                try { instance = (ITracePlugin)Activator.CreateInstance(type); }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("TraceSoul2 插件实例化失败：" + type.FullName + " / " + exception.Message);
                    continue;
                }
                var metadata = instance.Metadata;
                ValidateMetadata(metadata, type);
                if (plugins.ContainsKey(metadata.Id))
                    throw new InvalidOperationException("插件 ID 重复：" + metadata.Id);
                ApplyRole(metadata);
                metadata.Enabled = ResolveEnabled(metadata);
                plugins.Add(metadata.Id, new LoadedPlugin { Instance = instance, Metadata = metadata });
                if (!metadata.Enabled) continue;
                try { Activate(metadata.Id); }
                catch (Exception exception)
                {
                    Deactivate(metadata.Id);
                    metadata.Enabled = false;
                    metadata.LoadError = exception.Message;
                    Console.Error.WriteLine("TraceSoul2 插件加载失败：" + metadata.Id + " / " + exception.Message);
                }
            }
        }

        /// <summary>
        /// 注册一个外部插件实例（由宿主的外部插件加载器从独立程序集实例化后交进来）。
        /// 启用状态沿用持久化的插件开关；默认启用。
        /// </summary>
        public void RegisterExternal(
            ITracePlugin instance,
            string packageDirectory = null,
            string pluginDataDirectory = null)
        {
            if (instance == null) throw new ArgumentNullException("instance");
            var metadata = instance.Metadata;
            ValidateMetadata(metadata, instance.GetType());
            if (plugins.ContainsKey(metadata.Id))
                throw new InvalidOperationException("插件 ID 重复：" + metadata.Id);
            ApplyRole(metadata);
            metadata.Enabled = ResolveEnabled(metadata);
            plugins.Add(metadata.Id, new LoadedPlugin
            {
                Instance = instance,
                Metadata = metadata,
                PackageDirectory = packageDirectory,
                PluginDataDirectory = pluginDataDirectory
            });
            if (!metadata.Enabled) return;
            try { Activate(metadata.Id); }
            catch (Exception exception)
            {
                Deactivate(metadata.Id);
                metadata.Enabled = false;
                metadata.LoadError = exception.Message;
            }
        }

        /// <summary>卸载并移除一个插件（停用贡献、Shutdown 实例）；由外部加载器在重扫/卸载时调用。</summary>
        public bool Unregister(string pluginId)
        {
            LoadedPlugin loaded;
            if (!plugins.TryGetValue(pluginId ?? string.Empty, out loaded)) return false;
            Deactivate(pluginId);
            plugins.Remove(pluginId);
            return true;
        }

        public List<TracePluginMetadataData> GetPlugins()
        {
            return plugins.Values.Select(x => x.Metadata).OrderBy(x => x.Id).ToList();
        }

        public List<TraceContributionDescriptorData> GetAvailableCatalog(TraceTurnContext turn)
        {
            return MouthLogic.Apply(
                callables.Values.Where(x => x.IsAvailable(turn)).Select(x => Bind(x.Descriptor))
                .Concat(facets.Values.Where(x => x.IsAvailable(turn)).Select(x => Bind(x.Descriptor)))
                .Concat(momentSources.Values.Where(x => x.IsAvailable).Select(x => Bind(x.Descriptor)))
                .Concat(backgroundServices.Values.Where(x => x.IsAvailable).Select(x => Bind(x.Descriptor))),
                turn);
        }

        public List<TraceContributionDescriptorData> GetRegisteredCatalog()
        {
            return callables.Values.Select(x => Bind(x.Descriptor))
                .Concat(facets.Values.Select(x => Bind(x.Descriptor)))
                .Concat(momentSources.Values.Select(x => Bind(x.Descriptor)))
                .Concat(backgroundServices.Values.Select(x => Bind(x.Descriptor)))
                .OrderBy(x => x.Kind).ThenBy(x => x.Id).ToList();
        }

        /// <summary>仅启用插件的贡献目录（感官目录等基础设施用）。</summary>
        public List<TraceContributionDescriptorData> GetEnabledCatalog()
        {
            return GetRegisteredCatalog()
                .Where(x => plugins.Values.Any(p => p.Metadata.Enabled && p.Metadata.Id == x.PluginId))
                .ToList();
        }

        private TraceContributionDescriptorData Bind(TraceContributionDescriptorData source)
        {
            var pair = storage.LoadPairIdentity();
            if (source == null) return null;
            return new TraceContributionDescriptorData
            {
                Id = source.Id,
                PluginId = source.PluginId,
                Kind = source.Kind,
                DisplayName = pair.Apply(source.DisplayName),
                Description = pair.Apply(source.Description),
                Provides = source.Provides,
                WhenToUse = pair.Apply(source.WhenToUse),
                WhenNotToUse = pair.Apply(source.WhenNotToUse),
                ParametersJsonSchema = pair.Apply(source.ParametersJsonSchema),
                OutputJsonSchema = pair.Apply(source.OutputJsonSchema),
                Boundary = pair.Apply(source.Boundary),
                BodyId = source.BodyId,
                BodyTier = source.BodyTier,
                BodyScale = source.BodyScale,
                Organ = source.Organ,
                RefreshMode = source.RefreshMode,
                Priority = source.Priority,
                MaxContextChars = source.MaxContextChars,
                HasInternalMutation = source.HasInternalMutation,
                HasExternalSideEffect = source.HasExternalSideEffect
            };
        }

        public void SetEnabled(string pluginId, bool enabled)
        {
            LoadedPlugin plugin;
            if (!plugins.TryGetValue(pluginId ?? string.Empty, out plugin))
                throw new KeyNotFoundException("没有插件：" + pluginId);
            if (!enabled && PluginRoleValues.IsKernel(plugin.Metadata.Role))
                throw new InvalidOperationException("内核不能关闭：" + pluginId);
            if (plugin.Metadata.Enabled == enabled) return;
            if (enabled)
            {
                try
                {
                    Activate(pluginId);
                    plugin.Metadata.LoadError = string.Empty;
                }
                catch
                {
                    Deactivate(pluginId);
                    throw;
                }
            }
            else Deactivate(pluginId);
            plugin.Metadata.Enabled = enabled;
            storage.SavePluginEnabled(pluginId, enabled);
        }

        public PluginEventData ReceiveMoment(
            string sourceId,
            string role,
            string content,
            string payloadJson = null)
        {
            ITraceMomentSource source;
            if (!momentSources.TryGetValue(sourceId ?? string.Empty, out source))
                throw new InvalidOperationException("Moment 来源当前不可用：" + sourceId);
            if (!source.IsAvailable)
                throw new InvalidOperationException("Moment 来源当前离线：" + sourceId);
            return source.Receive(role, content, payloadJson);
        }

        public List<PluginEventData> PollBackgroundServices(long nowUnixMs)
        {
            var result = new List<PluginEventData>();
            foreach (var service in backgroundServices.Values.Where(x => x.IsAvailable).ToList())
            {
                try
                {
                    foreach (var item in service.Poll(nowUnixMs) ?? Enumerable.Empty<PluginEventData>())
                        if (item != null) result.Add(item);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("后台服务轮询失败：" + service.Descriptor.Id + " / " + exception.Message);
                }
            }
            return result;
        }

        public async Task<List<TraceContextBlockData>> BuildContextBlocksAsync(
            TraceTurnContext turn,
            CancellationToken cancellationToken)
        {
            var blocks = new List<TraceContextBlockData>();
            foreach (var facet in facets.Values.Where(x => x.IsAvailable(turn) &&
                                                           !MouthLogic.IsProtocolFacet(x.Descriptor.Id))
                         .OrderByDescending(x => x.Descriptor.Priority))
            {
                var timer = Stopwatch.StartNew();
                services.LogTiming(turn == null ? null : turn.TraceId,
                    "上下文面开始 " + facet.Descriptor.Id);
                TraceContextBlockData block;
                var cached = false;
                try
                {
                    var once = facet.Descriptor.RefreshMode != TraceFacetRefreshValues.EveryBrainStep;
                    if (once && turn.Workspace.TryGetFacetCache(facet.Descriptor.Id, out block))
                    {
                        cached = true;
                        if (block != null) blocks.Add(block);
                        continue;
                    }
                    block = await facet.BuildContextAsync(turn, cancellationToken);
                    if (block != null)
                    {
                        block.FacetId = facet.Descriptor.Id;
                        block.Priority = facet.Descriptor.Priority;
                        var cap = facet.Descriptor.MaxContextChars <= 0 ? 2000 : facet.Descriptor.MaxContextChars;
                        block.Content = TrimToSentence(block.Content, cap);
                        blocks.Add(block);
                    }
                    if (once) turn.Workspace.SetFacetCache(facet.Descriptor.Id, block);
                }
                finally
                {
                    services.LogTiming(turn == null ? null : turn.TraceId,
                        "上下文面完成 " + facet.Descriptor.Id,
                        timer.ElapsedMilliseconds, cached ? "cache" : null);
                }
            }
            turn.Workspace.ContextBlocks.Clear();
            turn.Workspace.ContextBlocks.AddRange(blocks);
            return blocks;
        }

        public async Task ApplyFacetOutputsAsync(
            IEnumerable<BrainFacetOutputData> outputs,
            TraceTurnContext turn,
            CancellationToken cancellationToken)
        {
            var byId = (outputs ?? Enumerable.Empty<BrainFacetOutputData>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.facet_id))
                .GroupBy(x => x.facet_id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            turn.Workspace.FacetOutputs.Clear();
            turn.Workspace.FacetOutputs.AddRange(byId.Values);
            foreach (var facet in facets.Values.Where(x => x.IsAvailable(turn)))
            {
                var timer = Stopwatch.StartNew();
                services.LogTiming(turn == null ? null : turn.TraceId,
                    "面输出应用开始 " + facet.Descriptor.Id);
                BrainFacetOutputData output;
                byId.TryGetValue(facet.Descriptor.Id, out output);
                try
                {
                    var result = await facet.ApplyOutputAsync(output, turn, cancellationToken);
                    if (result == null) continue;
                    result.CallId = "facet:" + facet.Descriptor.Id;
                    result.CapabilityId = facet.Descriptor.Id;
                    result.Status = string.IsNullOrWhiteSpace(result.Status) ? "success" : result.Status;
                    result.Summary = result.Summary ?? string.Empty;
                    result.Payload = result.Payload ?? string.Empty;
                    turn.Workspace.Results.Add(result);
                }
                finally
                {
                    services.LogTiming(turn == null ? null : turn.TraceId,
                        "面输出应用完成 " + facet.Descriptor.Id,
                        timer.ElapsedMilliseconds);
                }
            }
        }

        public async Task<TraceCapabilityResultData> ExecuteAsync(
            BrainCapabilityCallData call,
            TraceTurnContext turn,
            CancellationToken cancellationToken)
        {
            if (call == null) throw new ArgumentNullException("call");
            if (string.IsNullOrWhiteSpace(call.call_id)) call.call_id = Guid.NewGuid().ToString("N");
            ITraceCallableContribution contribution;
            if (!callables.TryGetValue(call.capability_id ?? string.Empty, out contribution) ||
                !contribution.IsAvailable(turn))
                return Failed(call, "能力当前不可用。");
            var timer = Stopwatch.StartNew();
            services.LogTiming(turn == null ? null : turn.TraceId,
                "能力执行开始 " + contribution.Descriptor.Id);
            try
            {
                var result = await contribution.ExecuteAsync(call, turn, cancellationToken);
                if (result == null) return Failed(call, "插件返回了空结果。");
                result.CallId = call.call_id;
                result.CapabilityId = contribution.Descriptor.Id;
                result.Status = string.IsNullOrWhiteSpace(result.Status) ? "success" : result.Status;
                result.Summary = result.Summary ?? string.Empty;
                result.Payload = result.Payload ?? string.Empty;
                result.EvidenceRefs = result.EvidenceRefs ?? new List<string>();
                services.LogTiming(turn == null ? null : turn.TraceId,
                    "能力执行完成 " + contribution.Descriptor.Id,
                    timer.ElapsedMilliseconds, "status=" + result.Status);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                services.LogTiming(turn == null ? null : turn.TraceId,
                    "能力执行失败 " + contribution.Descriptor.Id,
                    timer.ElapsedMilliseconds, exception.Message);
                return Failed(call, exception.Message);
            }
        }

        public void RegisterCallable(string pluginId, ITraceCallableContribution contribution)
        {
            ValidateDescriptor(pluginId, contribution == null ? null : contribution.Descriptor);
            if (!TraceContributionKindValues.IsBrainCallable(contribution.Descriptor.Kind))
                throw new InvalidOperationException("可调用贡献类型无效：" + contribution.Descriptor.Kind);
            callables.Add(contribution.Descriptor.Id, contribution);
        }

        public void RegisterMountedFacet(string pluginId, ITraceMountedFacet facet)
        {
            ValidateDescriptor(pluginId, facet == null ? null : facet.Descriptor);
            facet.Descriptor.Kind = TraceContributionKindValues.MountedFacet;
            facets.Add(facet.Descriptor.Id, facet);
        }

        public void RegisterMomentSource(string pluginId, ITraceMomentSource source)
        {
            ValidateDescriptor(pluginId, source == null ? null : source.Descriptor);
            source.Descriptor.Kind = TraceContributionKindValues.MomentSource;
            momentSources.Add(source.Descriptor.Id, source);
        }

        public void RegisterBackgroundService(string pluginId, ITraceBackgroundService service)
        {
            ValidateDescriptor(pluginId, service == null ? null : service.Descriptor);
            service.Descriptor.Kind = TraceContributionKindValues.BackgroundService;
            backgroundServices.Add(service.Descriptor.Id, service);
        }

        public void Dispose()
        {
            foreach (var id in plugins.Keys.ToList()) Deactivate(id);
            plugins.Clear();
        }

        private void Activate(string id)
        {
            var loaded = plugins[id];
            loaded.Instance.Register(new TracePluginContext(
                this, services, loaded.Metadata, loaded.PackageDirectory, loaded.PluginDataDirectory));
        }

        private void Deactivate(string id)
        {
            LoadedPlugin loaded;
            if (!plugins.TryGetValue(id, out loaded)) return;
            foreach (var service in backgroundServices.Values
                         .Where(x => x.Descriptor.PluginId == id).ToList())
                service.Shutdown();
            RemoveOwned(callables, id, x => x.Descriptor.PluginId);
            RemoveOwned(momentSources, id, x => x.Descriptor.PluginId);
            RemoveOwned(facets, id, x => x.Descriptor.PluginId);
            RemoveOwned(backgroundServices, id, x => x.Descriptor.PluginId);
            try { loaded.Instance.Shutdown(); }
            catch (Exception exception)
            {
                Console.Error.WriteLine("TraceSoul2 插件卸载失败：" + id + " / " + exception.Message);
            }
        }

        private static void RemoveOwned<T>(Dictionary<string, T> values, string pluginId, Func<T, string> owner)
        {
            foreach (var key in values.Where(x => owner(x.Value) == pluginId).Select(x => x.Key).ToList())
                values.Remove(key);
        }

        private static void ValidateDescriptor(string pluginId, TraceContributionDescriptorData descriptor)
        {
            if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.Id))
                throw new ArgumentException("插件贡献必须拥有稳定 ID。", "descriptor");
            descriptor.PluginId = pluginId;
            descriptor.Priority = Math.Max(-100, Math.Min(100, descriptor.Priority));
        }

        private static TraceCapabilityResultData Failed(BrainCapabilityCallData call, string message)
        {
            return new TraceCapabilityResultData
            {
                CallId = call.call_id,
                CapabilityId = call.capability_id,
                Status = "failed",
                Summary = message ?? "未知插件错误",
                Payload = string.Empty
            };
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { return exception.Types.Where(x => x != null); }
            catch { return new Type[0]; }
        }

        private static void ValidateMetadata(TracePluginMetadataData metadata, Type type)
        {
            if (metadata == null || string.IsNullOrWhiteSpace(metadata.Id) ||
                string.IsNullOrWhiteSpace(metadata.DisplayName) || string.IsNullOrWhiteSpace(metadata.Version))
                throw new InvalidOperationException("插件元数据不完整：" + type.FullName);
        }

        private static void ApplyRole(TracePluginMetadataData metadata)
        {
            if (metadata == null) return;
            metadata.Role = PluginRoleValues.Normalize(metadata.Role, metadata.Id);
            metadata.PlatformId = PluginRoleValues.IsKernel(metadata.Role)
                ? string.Empty
                : PluginRoleValues.PlatformOf(metadata.Id, metadata.PlatformId);
        }

        private bool ResolveEnabled(TracePluginMetadataData metadata)
        {
            if (PluginRoleValues.IsKernel(metadata.Role))
            {
                if (!storage.LoadPluginEnabled(metadata.Id, true))
                    storage.SavePluginEnabled(metadata.Id, true);
                return true;
            }
            return storage.LoadPluginEnabled(metadata.Id, true);
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        /// <summary>按句界收尾的截断：找最后一个句号/问号/叹号/省略号收口，绝不切半句。</summary>
        private static string TrimToSentence(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length < max) return value;
            var window = value.Substring(0, Math.Min(value.Length, max));
            var lastEnd = -1;
            foreach (var marker in new[] { '。', '！', '？', '…', '\n' })
            {
                var index = window.LastIndexOf(marker);
                if (index > lastEnd) lastEnd = index;
            }
            if (lastEnd >= 40) return value.Substring(0, lastEnd + 1).Trim();
            return window.Trim();
        }
    }
}
