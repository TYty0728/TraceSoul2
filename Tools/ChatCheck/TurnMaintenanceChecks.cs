using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.ExternalPlugins;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private static async Task RunTurnMaintenanceChecksAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("本人", "同伴", "称呼");
        var llm = new MaintenanceProbe();
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder())) { ReviewLlm = llm };
        const string root = "maintenance";
        TraceTurnContext Turn(string id, string platform = "builtin.dialogue", bool publicInput = false) => new(root,
            new MomentRecord { Id = id, ConversationId = root, Content = "已经谈完了，回去吧。", Role = "本人", CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            new(), 8, true, services, environment: new EnvironmentSnapshotData {
                RootConversationId = root, ContextConversationId = root, Visibility = publicInput ? "public" : "private",
                PlatformId = platform, SpeakerId = "local-owner", AudienceConfirmed = true });
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var initial = InnerLifeLogic.Reduce(store.LoadOrCreateInnerRuntime(root), new InnerRuntimeWriteData {
            ongoing_activity = "文字场景中散步", attention = new() { new() { kind = "topic", content = "相机接口的问题" } }
        }, "old", now - 600000);
        store.SaveInnerRuntime(initial);
        var turn = Turn("one");
        var work = TurnMaintenanceLogic.Prepare(turn, new AgentStepData { step = "finish", reply = "好，回去吧。" });
        Require(work != null && work.Maintenance && llm.Calls == 0, "快照准备不调用模型、不阻塞前台回复");
        llm.Response = "{\"attention\":[],\"scene\":\"\"}";
        var commit = await work.AnalyzeAsync(default);
        Require(store.LoadOrCreateInnerRuntime(root).Attention.Count == 1, "后台分析不访问或修改权威状态");
        await commit(default);
        var cleared = store.LoadOrCreateInnerRuntime(root);
        Require(cleared.Attention.Count == 0 && cleared.OngoingActivity == "" && cleared.Mood == initial.Mood,
            "独立整理清除结束关注和场景，不修改情绪/身份");
        await commit(default);
        Require(store.LoadOrCreateInnerRuntime(root).Revision == cleared.Revision, "同一整理结果不能重复提交");
        Require(llm.Messages.Count == 2 && !llm.Messages[0].content.Contains("【Agent 当下】") &&
            llm.Messages[1].content.Contains("proposed_reply"), "整理仅用有界证据，不复制完整Agent前缀或伪造送达");

        // 模拟上一轮请求尚未完成，另一平台已提交新的一拍。
        var stale = TurnMaintenanceLogic.Prepare(Turn("old-running"), new AgentStepData { step = "wait" });
        llm.Pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analysis = stale.AnalyzeAsync(default);
        var newer = Turn("newer", "wxxz");
        SubjectRuntimeLogic.Begin(newer);
        SubjectRuntimeLogic.Commit(newer, new AgentStepData { step = "finish", affect = "joyful" });
        var newRevision = SubjectRuntimeLogic.Read(store, root).Revision;
        llm.Pending.SetResult("{\"attention\":[{\"kind\":\"topic\",\"content\":\"旧话题\"}],\"scene\":\"旧场景\"}");
        var lateCommit = await analysis;
        await lateCommit(default);
        Require(SubjectRuntimeLogic.Read(store, root).Revision == newRevision && store.LoadOrCreateInnerRuntime(root).Attention.Count == 0,
            "另一平台新对话先提交后，晚完成的旧整理不能倒写");
        llm.Pending = null;

        var unchanged = InnerLifeLogic.Reduce(initial, new InnerRuntimeWriteData {
            attention = new() { new() { kind = "topic", content = "相机接口的问题" } }
        }, "repeat", now);
        Require(unchanged.Attention.Single().UpdatedUnixMs == initial.Attention.Single().UpdatedUnixMs,
            "逐字重复关注不刷新年龄");
        var expired = InnerLifeLogic.Reduce(initial, new InnerRuntimeWriteData {
            attention = new() { new() { kind = "topic", content = "相机接口的问题" } }
        }, "expired", now + 7 * 3600000L);
        Require(expired.Attention.Count == 0, "已经自然过期的关注不能靠重复输出复活");
        Require(TurnMaintenanceLogic.Prepare(Turn("public", publicInput: true), new AgentStepData()) == null,
            "公开输入不读取私人关注进入后台整理");
        var beforeInvalid = SubjectRuntimeLogic.Read(store, root).Revision;
        foreach (var invalid in new[] { "{}", "not json", "{\"attention\":[{\"kind\":\"topic\",\"content\":\"" + new string('字', 161) + "\"}]}" })
        {
            llm.Response = invalid;
            var calls = llm.Calls;
            var invalidWork = TurnMaintenanceLogic.Prepare(Turn("invalid"), new AgentStepData());
            Require(await invalidWork.AnalyzeAsync(default) == null && llm.Calls == calls + 1 &&
                SubjectRuntimeLogic.Read(store, root).Revision == beforeInvalid, "格式失败只做一次整理请求，保留状态");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledWork = TurnMaintenanceLogic.Prepare(Turn("cancelled"), new AgentStepData());
        try { await cancelledWork.AnalyzeAsync(cancelled.Token); throw new Exception("取消必须传播"); }
        catch (OperationCanceledException) { }

        // 从真实Kernel入口取走新的工作，与既有图片/概览任务分开。
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new InnerLifePlugin());
        manager.RegisterExternal(new DialogueTracePlugin());
        var main = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"听见了。\"}");
        var kernel = new KernelLogic(store, main, manager);
        var callsBefore = llm.Calls;
        await kernel.ChatAsync(root, "这件事已经解决啦。", "dialogue.receive", 8);
        Require(main.Requests.Count == 1 && llm.Calls == callsBefore && kernel.TakeStateMaintenanceWork() != null &&
            kernel.TakeStateMaintenanceWork() == null, "真实主对话一次回复后独立移交状态整理，不进入行动循环");
        Console.WriteLine("Turn maintenance checks passed: independent snapshot, closure, repeated age, stale cross-platform result, failure and cancellation.");
        await RunCameraPlatformBoundaryChecksAsync();
    }

    private sealed class MaintenanceProbe : ILlmClient
    {
        public string ProviderId => "maintenance-check";
        public string Model => "maintenance-check";
        public int Calls;
        public string Response;
        public TaskCompletionSource<string> Pending;
        public IReadOnlyList<DeepSeekMessageData> Messages;
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
        {
            token.ThrowIfCancellationRequested();
            Calls++; Messages = messages;
            return Pending?.Task.WaitAsync(token) ?? Task.FromResult(Response);
        }
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
            => throw new InvalidOperationException("整理不能调用表达模型");
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { Model });
    }

    private static async Task RunCameraPlatformBoundaryChecksAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "camera-platform-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), "{\"api_key\":\"fixture\",\"base_url\":\"https://camera.invalid/v1\",\"model\":\"gpt-image-2\"}");
            using var store = new SqliteMemoryManager(":memory:");
            store.SavePairIdentity("本人", "同伴", "称呼");
            var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
            using var manager = new TracePluginManager(store, services);
            var camera = new QqImageGenPlugin();
            manager.RegisterExternal(camera, dir, dir);
            var qq = new CameraPlatformProbe("builtin.onebot");
            var other = new CameraPlatformProbe("fixture-platform");
            services.PlatformAdapters.AddRange(new[] { qq, other });
            foreach (var id in new[] { "onebot", "fixture-platform" })
                services.Platforms.Register(new PlatformHandle { Id = id, IsConnected = () => true });
            EnvironmentLogic.SaveSettings(store, new EnvironmentSettings { OwnerAccounts = new() {
                new() { PlatformId = "builtin.onebot", UserId = "owner" }, new() { PlatformId = "fixture-platform", UserId = "owner" }
            } });
            TraceTurnContext Turn(string platform) => new("camera", new MomentRecord { Id = "camera", SourcePluginId = platform }, new(), 0, true, services,
                environment: new EnvironmentSnapshotData { RootConversationId = "camera", ContextConversationId = "camera", Visibility = "private",
                    PlatformId = platform, SessionType = "private", SessionId = "owner", SpeakerId = "owner", AudienceConfirmed = true });
            Require(string.IsNullOrEmpty(camera.Metadata.PlatformId) && !manager.IsOrganDormant(camera.Metadata), "通用相机不依赖QQ插件启用状态");
            foreach (var adapter in new[] { qq, other })
            {
                var turn = Turn(adapter.PlatformId);
                var descriptor = manager.GetAvailableActionCatalog(turn).Single(x => x.Id == "qq.imagegen.generate");
                Require(descriptor.BodyId == MouthLogic.BodyOfPlugin(adapter.PlatformId), "目录绑定当前平台，不改写共享描述对象");
                var sent = await manager.ExecuteAsync(new BrainCapabilityCallData { capability_id = descriptor.Id, arguments = new() {
                    new() { name = "mode", value = "url" }, new() { name = "url", value = "https://camera.invalid/test.png" }
                } }, turn, default);
                Require(sent.Status == "success" && adapter.Sends == 1, "相机输出由原目标平台发送，无真实网络调用");
            }
            store.SaveMoment(new MomentRecord { Id = "qq-reply", ConversationId = "camera", Role = "同伴", Content = "一句话",
                SourcePluginId = "builtin.onebot", CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PayloadJson = "{\"session_type\":\"private\",\"session_id\":\"owner\"}" });
            var reserve = typeof(QqImageGenPlugin).Assembly.GetType("TraceSoul2.ExternalPlugins.CameraSharingContext")
                .GetMethod("TryReserve", BindingFlags.Static | BindingFlags.NonPublic);
            Require((bool)reserve.Invoke(null, new object[] { Turn(qq.PlatformId), 1, 1 }) &&
                !(bool)reserve.Invoke(null, new object[] { Turn(other.PlatformId), 1, 1 }),
                "两平台相同会话编号也不共享QQ配图机会和最近会话");
            other.Supported = false;
            Require(manager.GetAvailableActionCatalog(Turn(other.PlatformId)).All(x => x.Id != "qq.imagegen.generate") && qq.Sends == 1,
                "当前平台不支持图片就隐藏相机，不借QQ发送");
            other.Supported = true;
            other.Fail = true;
            var failure = await manager.ExecuteAsync(new BrainCapabilityCallData { capability_id = "qq.imagegen.generate", arguments = new() {
                new() { name = "mode", value = "url" }, new() { name = "url", value = "https://camera.invalid/test.png" }
            } }, Turn(other.PlatformId), default);
            Require(failure.Status == "failed" && failure.Summary == "fixture failed" && qq.Sends == 1,
                "失败回执保留，不宣称已发送，也不跨平台补发");
            Console.WriteLine("Camera platform boundary checks passed: two adapters, target binding, unavailable media and failure receipts.");
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class CameraPlatformProbe : ITracePlatformAdapter, ITraceOutboundCapabilities
    {
        public string PlatformId { get; }
        public bool Supported = true, Fail;
        public int Sends;
        public CameraPlatformProbe(string id) { PlatformId = id; }
        public bool SupportsOutbound(string kind) => Supported && kind == TraceOutboundKinds.Image;
        public PluginEventData ConvertInbound(string payload) => null;
        public Task<string> CallActionAsync(string action, Dictionary<string, object> parameters, CancellationToken token = default)
            => throw new InvalidOperationException("平台动作不应由相机直接调用");
        public Task<TraceCapabilityResultData> SendAsync(TraceOutboundMessageData message, TraceTurnContext turn, CancellationToken token)
        {
            Require(turn.Environment.PlatformId == PlatformId && message.Kind == TraceOutboundKinds.Image, "原轮平台与图片类型绑定");
            Sends++;
            return Task.FromResult(new TraceCapabilityResultData { Status = Fail ? "failed" : "success", Summary = Fail ? "fixture failed" : "sent" });
        }
    }
}
