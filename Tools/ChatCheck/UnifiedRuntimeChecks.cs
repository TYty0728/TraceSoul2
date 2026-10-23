using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private static async Task CheckUnifiedRuntimeAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "unified-runtime-" + Guid.NewGuid().ToString("N") + ".db");
        const string root = "one-subject", secret = "PRIVATE_RUNTIME_ORIGIN_567";
        string publicContext;
        long revision;
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("本人", "同伴", "称呼");
                EnvironmentLogic.SaveSettings(store, new EnvironmentSettings { OwnerAccounts = new() {
                    new() { PlatformId = "future.alpha", UserId = "owner" },
                    new() { PlatformId = "future.beta", UserId = "owner" },
                    new() { PlatformId = "future.gamma", UserId = "owner" } } });
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                store.SavePluginDocument(SubjectRuntimeLogic.StoreId, root, TraceSoul2.Util.TraceJson.ToJson(new SubjectState {
                    Revision = 7, Narrative = new() { Text = "旧版主体的最新感受", Context = root, Visibility = "private" },
                    Mood = new() { Text = "旧版主体心情", Context = root, Visibility = "private" },
                    Attention = new() { Text = "[]", Context = root, Visibility = "private" } }));
                var upgraded = SubjectRuntimeLogic.Read(store, root);
                Require(upgraded.RootRuntime && upgraded.Revision == 8 && store.LoadOrCreateInnerRuntime(root).Narrative == "旧版主体的最新感受",
                    "旧版主体正文原子接回根runtime，来源片段之后只保留可见性职责");
                var canonical = store.LoadOrCreateInnerRuntime(root);
                canonical.Narrative = "根runtime之后的修订"; canonical.Revision++;
                store.SaveInnerRuntime(canonical);
                Require(SubjectRuntimeLogic.View(store, root, root, false).Narrative == "根runtime之后的修订",
                    "兼容接回只执行一次，来源副本不能覆盖根runtime的后续更新");
                using var manager = new TracePluginManager(store, services);
                manager.RegisterExternal(new DialogueTracePlugin());
                manager.RegisterExternal(new InnerLifePlugin());
                foreach (var platform in new[] { "future.alpha", "future.beta", "future.gamma" })
                    manager.RegisterExternal(new UnifiedPlatformProbe(platform));
                PluginEventData Input(string platform, string text, bool owner = true) => new() {
                    PluginId = platform, Role = "本人", Content = text, ExternalEventId = Guid.NewGuid().ToString("N"),
                    Environment = new() { PlatformId = platform, SessionType = "private", SessionId = owner ? "owner" : "guest",
                        SpeakerId = owner ? "owner" : "guest", ExclusivePair = true, DirectAddress = true } };
                TraceTurnContext Turn(EnvironmentSnapshotData e) => new(e.ContextConversationId,
                    Moment(e.ContextConversationId, "此刻"), new(), 0, true, services, e);
                var alpha = EnvironmentLogic.Resolve(store, root, Input("future.alpha", "先记下共同计划"));
                var beta = EnvironmentLogic.Resolve(store, root, Input("future.beta", "继续"));
                var gamma = EnvironmentLogic.Resolve(store, root, Input("future.gamma", "继续"));
                Require(new[] { alpha, beta, gamma }.All(e => e.ContextConversationId == root),
                    "任意新增平台的本人私密入口直接共用根会话，无需平台白名单");
                var first = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"已记录\",\"inner\":\"" + secret +
                    "\",\"mood\":\"轻松\",\"attention\":\"共同关注\",\"scene\":\"窗边看书\",\"affect\":\"joyful\"," +
                    "\"goal_updates\":[{\"operation\":\"create\",\"kind\":\"goal\",\"content\":\"共同计划\",\"applies_when\":\"之后交流\"," +
                    "\"horizon\":\"ongoing\",\"source\":\"user\",\"evidence_quote\":\"共同计划\"}]}");
                await new KernelLogic(store, first, manager).ProcessPluginEventAsync(root, Input("future.alpha", "先记下共同计划"));
                var originalRevision = store.LoadOrCreateInnerRuntime(root).Revision;
                var second = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"继续\",\"inner\":\"换入口继续同一刻\"}");
                await new KernelLogic(store, second, manager).ProcessPluginEventAsync(root, Input("future.beta", "继续"));
                Require(second.Requests.Single().Contains(secret) && second.Requests.Single().Contains("共同关注") &&
                    second.Requests.Single().Contains("窗边看书") && second.Requests.Single().Contains("共同计划"),
                    "新平台读取同一内心、关注、场景和目标；不是额外摘录另一实例");
                Require(!second.Requests.Single().Contains("先记下共同计划"), "关闭原文注入只改变材料选择，主体runtime和有效目标仍连续");
                var goal = GoalMemoryLogic.Read(store, root).Single(x => x.Status == "active");
                var third = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"已撤销\",\"mood\":\"期待\",\"goal_updates\":[{\"operation\":\"cancel\",\"id\":\"" +
                    goal.Id + "\",\"source\":\"user\",\"evidence_quote\":\"撤销共同计划\"}]}");
                await new KernelLogic(store, third, manager).ProcessPluginEventAsync(root, Input("future.gamma", "撤销共同计划"));
                Require(!GoalMemoryLogic.BuildContext(Turn(alpha)).Contains("共同计划") &&
                    SubjectRuntimeLogic.View(Turn(alpha)).Mood == "期待" && store.LoadOrCreateInnerRuntime(root).Revision > originalRevision,
                    "第三平台修改同一目标和情绪，原入口立即看到同一版本");
                Require(store.GetRecentDialogueMoments(root, 20).Where(x => x.Role == "同伴").Select(x => x.SourcePlatform)
                    .SequenceEqual(new[] { "future.alpha", "future.beta", "future.gamma" }),
                    "未来平台同样沿原入口输出，三个实际模拟回执共同进入根记忆");

                var guestInput = Input("future.beta", "公开打招呼", false);
                var guest = EnvironmentLogic.Resolve(store, root, guestInput);
                publicContext = guest.ContextConversationId;
                var legacy = InnerLifeLogic.CreateInitial(publicContext, 1);
                legacy.Narrative = "STALE_ENVIRONMENT_RUNTIME";
                legacy.OngoingActivity = "STALE_ENVIRONMENT_SCENE";
                store.SaveInnerRuntime(legacy);
                var publicLlm = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"你好\",\"refine\":true,\"scene\":\"大厅看窗外\",\"affect\":\"curious\"}", "你好。");
                await new KernelLogic(store, publicLlm, manager).ProcessPluginEventAsync(root, guestInput);
                Require(publicLlm.Requests.Count == 2 && publicLlm.Requests.All(x => !x.Contains("共同关注") && !x.Contains("窗边看书") &&
                    !x.Contains("STALE_ENVIRONMENT")), "公开决策与润色都只过滤同一runtime，不复活旧环境状态或泄露保留字段");
                Require(SubjectRuntimeLogic.View(Turn(alpha)).Narrative == "换入口继续同一刻" &&
                    SubjectRuntimeLogic.View(Turn(alpha)).OngoingActivity == "大厅看窗外" &&
                    store.LoadOrCreateInnerRuntime(publicContext).ConversationId == root,
                    "公开经历也更新唯一runtime，切回原平台不会恢复另一份场景");
                var view = SubjectRuntimeLogic.View(Turn(guest));
                view.Narrative = "改动临时视图";
                Require(SubjectRuntimeLogic.View(Turn(alpha)).Narrative == "换入口继续同一刻", "注入视图不是另一份可变主体");
                var fork = InnerLifeLogic.CreateInitial(publicContext, 2);
                try { store.SaveInnerRuntime(fork); throw new Exception("environment runtime fork accepted"); }
                catch (InvalidOperationException) { }
                using (var inspect = new SQLite.SQLiteConnection(path))
                {
                    Require(inspect.ExecuteScalar<int>("SELECT COUNT(*) FROM inner_runtime") == 2 &&
                        inspect.ExecuteScalar<string>("SELECT Narrative FROM inner_runtime WHERE ConversationId=?", publicContext) == "STALE_ENVIRONMENT_RUNTIME",
                        "仅根runtime继续写入；已存在的旧环境快照保留原件，不继续发展");
                }
                revision = SubjectRuntimeLogic.Read(store, root).Revision;
            }
            using (var reopened = new SqliteMemoryManager(path))
            {
                Require(reopened.LoadOrCreateInnerRuntime(publicContext).ConversationId == root &&
                    SubjectRuntimeLogic.Read(reopened, root).Revision == revision &&
                    SubjectRuntimeLogic.View(reopened, root, root, false).OngoingActivity == "大厅看窗外",
                    "重启仍解析到同一主体和同一版本，不从旧平台快照产生分支");
            }
        }
        finally { File.Delete(path); }
        Console.WriteLine("Unified runtime checks passed: arbitrary platforms, one state/goal timeline, public projection, stale snapshots and restart.");
    }

    private sealed class UnifiedPlatformProbe : ITracePlugin
    {
        public TracePluginMetadataData Metadata { get; }
        public UnifiedPlatformProbe(string platform) => Metadata = new() { Id = platform, DisplayName = platform, Version = "1.0.0" };
        public void Register(TracePluginContext context)
        {
            context.Services.Platforms.Register(new PlatformHandle { Id = Metadata.Id, IsConnected = () => true });
            context.AddCallable(new Text(Metadata.Id));
        }
        public void Shutdown() { }
        private sealed class Text : ITraceCallableContribution
        {
            public TraceContributionDescriptorData Descriptor { get; }
            public Text(string platform) => Descriptor = new() { Id = platform + ".text", BodyId = platform,
                Kind = TraceContributionKindValues.Effector, BodyTier = BodyTierValues.Chat, Organ = BodyOrganValues.Text,
                SupportsPublicEnvironment = true, ParametersJsonSchema = "{text:string}" };
            public bool IsAvailable(TraceTurnContext context) => true;
            public Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call, TraceTurnContext context, CancellationToken token) =>
                Task.FromResult(new TraceCapabilityResultData { Status = "success", ProducedEvent = new() {
                    PluginId = Descriptor.BodyId, Role = "同伴", Content = call.GetArgument("text"), OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } });
        }
    }
}
