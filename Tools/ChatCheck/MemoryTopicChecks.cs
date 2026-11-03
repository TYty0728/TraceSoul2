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
using TraceSoul2.Util;

internal static partial class Program
{
    // 只读链路探针：报告实际送进检索引擎的范围，不把当前缺口断言为应保留行为。
    private static async Task RunMemoryRoutingAuditAsync()
    {
        using var store=new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("小雨","小光","雨雨");
        var nodes=new[]{("hygiene","日常清洁"),("food","口味偏好")}.Select(x=>
            new VectorIndexNode("concept."+x.Item1,VectorNodeLevel.Concept,x.Item2,x.Item2,"scope",
                new[]{"user"},new[]{"dimension.scope"},new[]{x.Item2},Array.Empty<string>())).ToList();
        store.SeedLifeTags(nodes);
        var router=new HierarchicalVectorRouterLogic(new FakeEncoder()); router.Build(nodes);
        var recall=new RoutingScopeProbe();
        var services=new TracePluginServices(store,router){Recall=recall};
        using var plugins=new TracePluginManager(store,services);
        plugins.RegisterExternal(new TraceSoul2.Plugins.Builtin.DialogueTracePlugin());
        plugins.RegisterExternal(new TraceSoul2.Plugins.Builtin.InnerLifePlugin());
        plugins.RegisterExternal(new TraceSoul2.Plugins.Builtin.MemoryNervePlugin());
        foreach(var item in new[]{("today",0,"hygiene","今天洗头"),("yesterday",-1,"hygiene","昨天洗头"),
            ("older",-8,"hygiene","八天前洗头"),("food",-2,"food","喜欢吃牛肉面")})
        {
            var time=DateTimeOffset.UtcNow.ToOffset(MemoryDayLogic.ChinaOffset).Date.AddDays(item.Item2).AddHours(12);
            var stamp=new DateTimeOffset(time,MemoryDayLogic.ChinaOffset).ToUnixTimeMilliseconds();
            store.SaveEventIndex(new EventIndexRecord{Id=item.Item1,Status="active",TagIds="concept."+item.Item3,
                TimeUnixMs=stamp,EventSummary=item.Item4});
            store.AppendEventEntry(new EventEntryRecord{Id="entry-"+item.Item1,IndexId=item.Item1,
                Summary=item.Item4,Detail=item.Item4,CreatedUnixMs=stamp});
        }
        const string question="昨天我洗头了吗";
        var llm=new AgentSequenceLlm("{\"step\":\"continue\",\"actions\":[{\"capability_id\":\"memory.recall\",\"arguments\":[{\"name\":\"query\",\"value\":\""+question+"\"},{\"name\":\"period\",\"value\":\"yesterday\"}]}]}",
            "{\"step\":\"finish\",\"reply\":\"已查看资料。\"}");
        await new KernelLogic(store,llm,plugins).ChatAsync("routing-audit",question,historyWindowMax:0);
        Console.WriteLine("Yesterday query, candidate entries sent to search: "+string.Join(",",recall.Calls.Last(x=>x.Query==question).Ids));
        var turn=new TraceTurnContext("routing-audit",Moment("routing-audit","我喜欢吃什么"),new(),0,true,services);
        var kernel=new KernelLogic(store,new AgentSequenceLlm(),plugins);
        var execute=typeof(KernelLogic).GetMethod("ExecuteAgentActionAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
        var result=await (Task<TraceCapabilityResultData>)execute.Invoke(kernel,new object[]{new BrainCapabilityCallData{
            capability_id="memory.recall",arguments=new(){new(){name="query",value="我喜欢吃什么"},new(){name="tags",value="[\"口味偏好\"]"}}},turn,CancellationToken.None});
        Console.WriteLine("Preference tag, candidate entries sent to search: "+string.Join(",",recall.Calls.Last().Ids));
        Require(result.Payload.Contains("牛肉面") && !result.Payload.Contains("洗头"),"喜好主题选中后确实收窄实际召回");
        await (Task<TraceCapabilityResultData>)execute.Invoke(kernel,new object[]{new BrainCapabilityCallData{
            capability_id="memory.recall",arguments=new(){new(){name="query",value=question},new(){name="period",value="yesterday"},new(){name="tags",value="[\"日常清洁\"]"}}},turn,CancellationToken.None});
        Console.WriteLine("Yesterday query with hygiene tag, candidate entries sent to search: "+string.Join(",",recall.Calls.Last().Ids));
        var catalog=AgentLoopLogic.BuildCatalog(plugins.GetAvailableActionCatalog(turn));
        Console.WriteLine("Agent recall schema: "+catalog.Single(x=>x.Id=="memory.recall").ParametersJsonSchema);
        Console.WriteLine("Legacy memory.activate exposed to Agent: "+catalog.Any(x=>x.Id=="memory.activate"));
    }

    private sealed class RoutingScopeProbe : IMemoryRecallEngine
    {
        public bool IsAvailable=>true;
        public string ModelId=>"scope-audit";
        public int DefaultTopK {get;set;}=6;
        public List<(string Query,List<string> Ids)> Calls=new();
        public List<MemoryRecallHit> Search(string query,IReadOnlyList<string> candidateEntryIds,int topK)
        {
            Calls.Add((query,candidateEntryIds.ToList()));
            return candidateEntryIds.Take(topK).Select(x=>new MemoryRecallHit{EntryId=x,Score=1}).ToList();
        }
        public void PutEntryVector(string entryId,string summaryText) { }
    }

    private static async Task RunMemoryTopicChecksAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "topic-recall-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new SqliteMemoryManager(path);
            store.SavePairIdentity("小雨", "小光", "雨雨");
            var nodes = new[] { ("food", "口味偏好"), ("math", "数学联想"), ("empty", "饮食、习惯") }
                .Select(x => new VectorIndexNode("concept." + x.Item1, VectorNodeLevel.Concept, x.Item2,
                    "牛肉面", "scope", new[] { "user" }, new[] { "dimension.scope" },
                    new[] { "牛肉面" }, Array.Empty<string>())).ToList();
            store.SeedLifeTags(nodes);
            var router = new HierarchicalVectorRouterLogic(new FakeEncoder()); router.Build(nodes);
            var services = new TracePluginServices(store, router);
            using var manager = new TracePluginManager(store, services);
            var turn = new TraceTurnContext("topic-check", Moment("topic-check", "牛肉面"), new(), 0, true, services);
            foreach (var name in new[] { "food", "math" })
            {
                var source = Moment("topic-check", "牛肉面" + name); source.Id = "topic-source-" + name;
                store.SaveMoment(source);
                store.CommitCognitions(source.Id, new[] { new BrainCognitionWriteData { operation="create",
                    summary = name == "food" ? "口味认识标记：牛肉面喜欢小葱" : "数学认识标记：吃面时想到定理",
                    domains=new(){"user"}, tag_ids=new(){"concept."+name}, evidence_moment_ids=new(){source.Id} } });
                using var db = new SQLite.SQLiteConnection(path);
                db.Insert(new EventIndexRecord { Id="topic-index-"+name, Status="active", TagIds="concept."+name,
                    EventSummary=name == "food" ? "口味事件标记" : "数学事件标记", TimeUnixMs=source.CreatedUnixMs });
                db.Insert(new EventEntryRecord { Id="topic-entry-"+name, IndexId="topic-index-"+name,
                    Summary="牛肉面"+name, Detail=name == "food" ? "口味细节标记" : "数学细节标记", SourceMomentId=source.Id });
            }
            var context = AgentPromptContextLogic.Context(turn);
            Require(context.Contains("可选记忆主题") && context.Contains("口味偏好") && context.Contains("数学联想"),
                "Agent动态提示实际列出语义候选，旧选词入口恢复");
            var kernel = new KernelLogic(store, new AgentSequenceLlm(), manager);
            var execute = typeof(KernelLogic).GetMethod("ExecuteAgentActionAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            async Task<TraceCapabilityResultData> Recall(string tags, string capability = "memory.recall") =>
                await (Task<TraceCapabilityResultData>)execute.Invoke(kernel, new object[] { new BrainCapabilityCallData {
                    capability_id=capability, arguments=new(){new(){name="query",value="牛肉面"},new(){name="tags",value=tags}} }, turn, CancellationToken.None });
            var food = await Recall("[\"口味偏好\"]");
            Require(food.Status == "success" && food.Payload.Contains("口味细节标记") && food.Payload.Contains("口味认识标记") &&
                !food.Payload.Contains("数学细节标记") && !food.Payload.Contains("数学认识标记"), "真实Agent动作把选词传给事件与认知召回，同词不同主题隔离");
            var both = await Recall("[\"口味偏好\",\"数学联想\"]");
            Require(both.Payload.Contains("口味细节标记") && both.Payload.Contains("数学细节标记"), "同一句可选择多个语义分支");
            var empty = await Recall("[\"饮食、习惯\"]");
            Require(empty.Status == "empty" && !empty.Payload.Contains("口味细节标记"), "所选主题无内容时不退回全库");
            Require((await Recall("[\"不存在的主题\"]")).Status == "failed" && (await Recall("不是JSON")).Status == "failed",
                "不存在或非法主题返回可修正的失败，不执行无约束查询");
            Require((await Recall("", "memory.topics")).Payload.Contains("数学联想"), "可换查询取得主题候选");
            var foodNode = store.GetCognitionNodes(20).Single(n => n.Summary.Contains("口味认识标记"));
            var confirm = Moment("topic-check", "新经历进一步印证口味认识"); store.SaveMoment(confirm);
            var reinforcement = new BrainCognitionWriteData { operation="reinforce", target_id=foodNode.Id,
                domains=new(){"user"}, tag_ids=new(){"concept.empty"}, evidence_moment_ids=new(){confirm.Id} };
            store.CommitCognitions(confirm.Id, new[] { reinforcement });
            store.CommitCognitions(confirm.Id, new[] { reinforcement });
            Require(store.GetCognitionTagIds(new[] { foodNode.Id })[foodNode.Id].Count == 2 &&
                (await Recall("[\"饮食、习惯\"]")).Payload.Contains("口味认识标记"),
                "认知印证可增加主题关联，重放不会重复挂接，新主题能召回旧节点");
            turn = new TraceTurnContext("topic-check", turn.Moment, new(), 0, true, services,
                new EnvironmentSnapshotData { Visibility="public" });
            Require(MemoryRecallLogic.TopicCandidates(turn) == "" && (await Recall("[\"口味偏好\"]")).Status == "failed",
                "公开环境不展示或使用私密主题");
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path+suffix)) File.Delete(path+suffix); }
        Console.WriteLine("Memory topic checks passed: prompt candidates, actual Agent selection, multi-topic events/cognitions, empty results and public isolation.");
    }
}
