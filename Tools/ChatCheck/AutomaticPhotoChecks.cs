using System;
using System.Collections.Generic;
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
    private static async Task RunAutomaticPhotoChecksAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("本人", "同伴", "称呼");
        EnvironmentLogic.SaveSettings(store, new EnvironmentSettings { OwnerAccounts = new() {
            new() { PlatformId = "builtin.onebot", UserId = "owner" } } });
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new InnerLifePlugin());
        var probe = new AutomaticPhotoProbe { Active = true };
        manager.RegisterExternal(probe);
        var opportunities = 0;
        services.AutomaticImageProviders.Add((turn, reply, snapshot) => {
            opportunities++;
            Require(reply == "文字已经确定。" && snapshot.Contains("当前状态"), "配图读取最终文字和当前状态快照");
            return new BrainCapabilityCallData { capability_id = "qq.imagegen.generate", arguments = new() {
                new() { name = "prompt", value = reply } } };
        });
        PluginEventData Input(bool background = false) => new() { PluginId = "builtin.onebot",
            Role = background ? "system_event" : "本人", Content = background ? "心跳" : "聊聊今天",
            IsOperational = background, Wake = KernelWakeValues.Mind,
            Environment = new() { PlatformId = "builtin.onebot", SessionType = "private", SessionId = "owner",
                SpeakerId = "owner", ExclusivePair = true, DirectAddress = true } };
        var llm = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"文字已经确定。\"}");
        var kernel = new KernelLogic(store, llm, manager);
        var chat = await kernel.ProcessPluginEventAsync("automatic-photo", Input());
        Require(chat.Reply == "文字已经确定。" && probe.Texts == 1 && probe.Generations == 1 && probe.Images == 0 &&
            opportunities == 1 && llm.Requests.Count == 1, "主模型没有actions也进入后台配图，文字先发且不等待图片");
        var deferred = kernel.TakeDeferredWork();
        Require(deferred != null, "生成与发送交给现有轮后队列");
        probe.Prepared.TrySetResult(new TraceCapabilityResultData { Status = "success", Payload = "mock-image.png" });
        var send = await deferred.AnalyzeAsync(default);
        await send(default);
        Require(probe.Images == 1 && store.GetRecentOperationalEvents("automatic-photo", 10)
            .Count(x => x.Kind == OperationalEventKindValues.OutboundImage) == 1, "后台发送一次并保存真实回执");
        var wait = new KernelLogic(store, new AgentSequenceLlm("{\"step\":\"wait\"}"), manager);
        await wait.ProcessPluginEventAsync("automatic-photo", Input(true));
        Require(opportunities == 1 && wait.TakeDeferredWork() == null, "后台等待不被配图路径改成联系");
        var explicitPhoto = new KernelLogic(store, new AgentSequenceLlm(
            "{\"step\":\"continue\",\"actions\":[{\"capability_id\":\"qq.imagegen.generate\",\"arguments\":[{\"name\":\"prompt\",\"value\":\"自拍\"}]}]}",
            "{\"step\":\"finish\",\"reply\":\"文字已经确定。\"}"), manager);
        await explicitPhoto.ProcessPluginEventAsync("automatic-photo", Input());
        Require(opportunities == 1 && explicitPhoto.TakeDeferredWork() == null, "本轮已调用相机后不再自动补图");
        probe.FailText = true;
        var failed = new KernelLogic(store, new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"文字已经确定。\"}"), manager);
        try { await failed.ProcessPluginEventAsync("automatic-photo", Input()); }
        catch (InvalidOperationException) { }
        Require(opportunities == 1 && failed.TakeDeferredWork() == null, "正文发送失败不产生附加图片");
        Console.WriteLine("Automatic photo checks passed: text-first, deferred delivery, one receipt, wait, explicit photo and send failure.");
    }

    private sealed class AutomaticPhotoProbe : ITracePlugin
    {
        public int Texts, Generations, Images;
        public bool FailText, Active;
        public TaskCompletionSource<TraceCapabilityResultData> Prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TracePluginMetadataData Metadata { get; } = new() { Id = "check.automatic-photo", DisplayName = "离线配图检查", Version = "1",
            Role = PluginRoleValues.Platform, PlatformId = BodyIds.Qq };
        public void Register(TracePluginContext context)
        {
            // 通用插件发现会扫描测试程序集，只有本专项显式启用模拟平台。
            if (!Active) return;
            context.Services.Platforms.Register(new PlatformHandle { Id = BodyIds.Qq, IsConnected = () => true });
            context.AddCallable(new Callable(this, "qq.text.send", BodyOrganValues.Text));
            context.AddCallable(new Callable(this, "qq.imagegen.generate", BodyOrganValues.Image));
        }
        public void Shutdown() { }
        private sealed class Callable : ITraceCallableContribution
        {
            private readonly AutomaticPhotoProbe owner;
            public TraceContributionDescriptorData Descriptor { get; }
            public Callable(AutomaticPhotoProbe owner, string id, string organ)
            {
                this.owner = owner;
                Descriptor = new() { Id = id, Kind = TraceContributionKindValues.Effector, DisplayName = id,
                    BodyId = BodyIds.Qq, BodyTier = BodyTierValues.Chat, Organ = organ, ParametersJsonSchema = "{prompt?:string,text?:string}" };
            }
            public bool IsAvailable(TraceTurnContext context) => true;
            public Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call, TraceTurnContext context, CancellationToken token)
            {
                if (Descriptor.Organ == BodyOrganValues.Text)
                {
                    if (owner.FailText) throw new InvalidOperationException("模拟文字发送失败");
                    owner.Texts++;
                    return Task.FromResult(new TraceCapabilityResultData { Status = "success", ProducedEvent = new() {
                        PluginId = "builtin.onebot", Role = "同伴", Content = call.GetArgument("text") } });
                }
                if (call.GetArgument("dispatch") == "generate") { owner.Generations++; return owner.Prepared.Task; }
                owner.Images++;
                return Task.FromResult(new TraceCapabilityResultData { Status = "success", ProducedEvent = new() {
                    PluginId = "builtin.onebot", Role = "system_event", Content = OneBotPlatformPrompts.SendImageMoment, IsOperational = true } });
            }
        }
    }
}
