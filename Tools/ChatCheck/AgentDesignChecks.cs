using System;
using System.Collections.Generic;
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
    private static async Task RunAgentDesignChecksAsync()
    {
        var path=Path.Combine(Path.GetTempPath(),"agent-design-"+Guid.NewGuid().ToString("N")+".db");
        var full="读取内容标记：牛肉面加小葱。"+new string('甲',7200)+"尾部数学定理";
        string sourceId;
        const string read="{\"step\":\"continue\",\"actions\":[{\"capability_id\":\"check.observation.read\"}]}";
        const string finish="{\"step\":\"finish\",\"reply\":\"看完了。\"}";
        try
        {
            using (var store=new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("小雨","小光","雨雨");
                var services=new TracePluginServices(store,new HierarchicalVectorRouterLogic(new BagOfCharsVectorEncoder()));
                using var plugins=new TracePluginManager(store,services);
                plugins.RegisterExternal(new DialogueTracePlugin()); plugins.RegisterExternal(new InnerLifePlugin());
                plugins.RegisterExternal(new MemoryNervePlugin());
                var probe=new ObservationProbe { Payload=full }; plugins.RegisterExternal(probe);
                var llm=new AgentSequenceLlm(read,read,finish);
                await new KernelLogic(store,llm,plugins).ChatAsync("design","帮我看看资料",historyWindowMax:8);
                var source=store.GetToolObservations("design",20).Single(); sourceId=source.Id;
                Require(probe.Calls==1 && source.Content.EndsWith(full) && source.ParentMomentId!=null &&
                    source.MemoryVisibility=="private" && source.EvidenceType==EvidenceTypeValues.PluginObserved,
                    "工具观察原件完整保存并绑定触发来源/受众，同轮重复调用不重复经历");
                var next=new AgentSequenceLlm(finish);
                await new KernelLogic(store,next,plugins).ChatAsync("design","刚才读了什么",historyWindowMax:8);
                Require(next.Requests[0].Contains("读取内容标记") && next.Requests[0].Contains(source.Id) &&
                    next.Requests[0].Contains("不是对方发言") && store.GetTodayNewItems("design",0,100).Count==0,
                    "省略new_fact仍能在下一轮读到有来源的材料，不伪造今日事实");
                var story=new AgentSequenceLlm(finish);
                await new KernelLogic(store,story,plugins).ChatAsync("design","小光，你给我讲个故事嘛",historyWindowMax:8);
                Require(story.Requests[0].Contains("此刻可参考的回应方式") && story.Requests[0].Contains("把内容做出来") &&
                    !story.Requests[0].Contains("archive=true"),"实际Agent请求收到语义选出的情境提示，使用当前行动契约");
                foreach(var status in new[]{"empty","failed","running","accepted"})
                {
                    probe.Status=status;
                    await new KernelLogic(store,new AgentSequenceLlm(read,finish),plugins).ChatAsync("design","再看一次",historyWindowMax:8);
                }
                Require(store.GetToolObservations("design",20).Count==1,"空、失败和进行中的工具结果不生成已发生观察");
                probe.Status="success"; probe.Descriptor.RetainResultAsObservation=false;
                await new KernelLogic(store,new AgentSequenceLlm(read,finish),plugins).ChatAsync("design","普通工具",historyWindowMax:8);
                Require(store.GetToolObservations("design",20).Count==1,"未声明观察用途的内部工具结果不冒充外部材料");
                probe.Authored=true;
                await new KernelLogic(store,new AgentSequenceLlm(read,finish),plugins).ChatAsync("design","插件提供原件",historyWindowMax:8);
                Require(store.GetToolObservations("design",20).Count==2,"已有ProducedEvent沿用，仅保存一份并补来源关联");
            }
            using (var store=new SqliteMemoryManager(path))
            {
                var services=new TracePluginServices(store,new HierarchicalVectorRouterLogic(new BagOfCharsVectorEncoder()));
                using var plugins=new TracePluginManager(store,services);
                var kernel=new KernelLogic(store,new AgentSequenceLlm(),plugins);
                var turn=new TraceTurnContext("design",Moment("design","尾部数学定理"),new(),0,true,services);
                var execute=typeof(KernelLogic).GetMethod("ExecuteAgentActionAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                async Task<TraceCapabilityResultData> Call(string capability,params (string,string)[] args) =>
                    await (Task<TraceCapabilityResultData>)execute.Invoke(kernel,new object[]{new BrainCapabilityCallData {
                        capability_id=capability,arguments=args.Select(a=>new BrainCallArgumentData{name=a.Item1,value=a.Item2}).ToList()},turn,CancellationToken.None});
                var recalled=await Call("memory.recall",("query","尾部数学定理"));
                Require(recalled.Status=="success" && recalled.Payload.Contains("尾部数学定理") && recalled.Payload.Contains(sourceId),
                    "重开数据库后可从原件尾部召回，未依赖审查快照截取内容");
                var first=await Call("memory.source",("source_id",sourceId));
                var second=await Call("memory.source",("source_id",sourceId),("offset","4000"));
                using var a=System.Text.Json.JsonDocument.Parse(first.Payload);
                using var b=System.Text.Json.JsonDocument.Parse(second.Payload);
                Require((a.RootElement.GetProperty("content").GetString()+b.RootElement.GetProperty("content").GetString()).EndsWith(full),
                    "memory.source分段读回完整原件");
                Require((await Call("memory.source",("source_id",sourceId),("offset","-1"))).Status=="failed","拒绝非法分页");
                turn=new TraceTurnContext("design:environment:room",Moment("design:environment:room","资料"),new(),0,true,services,
                    new EnvironmentSnapshotData{RootConversationId="design",Visibility="public"});
                Require(!ToolExperienceLogic.RecentContext(turn).Contains("读取内容标记") &&
                    (await Call("memory.source",("source_id",sourceId))).Status=="empty", "公开环境不能读取私密工具原件");
                turn=new TraceTurnContext("other",Moment("other","资料"),new(),0,true,services);
                Require((await Call("memory.source",("source_id",sourceId))).Status=="empty","不同主体不能借ID读原件");
            }
        }
        finally { foreach(var suffix in new[]{"","-wal","-shm"}) if(File.Exists(path+suffix)) File.Delete(path+suffix); }
        Console.WriteLine("Agent design checks passed: actual situational prompt, tool evidence, next-turn reading, restart recall, full source, dedupe, failures and audience isolation.");
    }

    private sealed class ObservationProbe : ITracePlugin, ITraceCallableContribution
    {
        public string Payload,Status="success"; public int Calls; public bool Authored;
        public TracePluginMetadataData Metadata {get;}=new(){Id="check.observation",DisplayName="模拟读取",Version="1.0.0"};
        public TraceContributionDescriptorData Descriptor {get;}=new(){Id="check.observation.read",Kind=TraceContributionKindValues.CallableNerve,
            Description="读取测试材料",RetainResultAsObservation=true,SupportsPublicEnvironment=true};
        public void Register(TracePluginContext context)=>context.AddCallable(this);
        public void Shutdown(){}
        public bool IsAvailable(TraceTurnContext context)=>true;
        public Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,TraceTurnContext context,CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new TraceCapabilityResultData{Status=Status,Payload=Payload,Summary="模拟结果",ProducedEvent=Authored ? new(){
                PluginId=Metadata.Id,Role="system_event",Content=Payload,Realm=TraceRealmValues.ExternalWorld,
                EvidenceType=EvidenceTypeValues.PluginObserved,OccurredUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} : null});
        }
    }
}
