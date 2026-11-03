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
using TraceSoul2.Util;

internal static partial class Program
{
    private static BrainCapabilityCallData ScopeCall(params (string Name,string Value)[] args) => new() {
        capability_id="memory.recall",arguments=args.Select(x=>new BrainCallArgumentData{name=x.Name,value=x.Value}).ToList()
    };
    private static async Task RunMemoryScopeChecksAsync()
    {
        var reference=DateTimeOffset.Parse("2026-11-03T10:00:00+08:00");
        var early=DateTimeOffset.Parse("2026-11-03T01:30:00+08:00");
        var calendar=MemoryReadScope.Parse(ScopeCall(("period","yesterday")),early);
        var memory=MemoryReadScope.Parse(ScopeCall(("period","yesterday"),("day_basis","memory")),early);
        Require(calendar.Start==DateTimeOffset.Parse("2026-11-02T00:00:00+08:00") &&
            calendar.End==DateTimeOffset.Parse("2026-11-03T00:00:00+08:00") &&
            memory.Start==DateTimeOffset.Parse("2026-11-01T04:00:00+08:00") &&
            memory.End==DateTimeOffset.Parse("2026-11-02T04:00:00+08:00"),"自然日与04点记忆日明确分离");
        Require(calendar.Contains(calendar.StartMs) && calendar.Contains(calendar.EndMs-1) &&
            !calendar.Contains(calendar.StartMs-1) && !calendar.Contains(calendar.EndMs),"时间区间左闭右开");
        var leap=MemoryReadScope.Parse(ScopeCall(("date_start","2024-02-29"),("date_end","2024-02-29")),reference);
        Require(leap.End-leap.Start==TimeSpan.FromDays(1),"闰日单日查询可用");
        Require(MemoryReadScope.Parse(ScopeCall(("period","last_week")),reference).Start.Value.Day==26 &&
            MemoryReadScope.Parse(ScopeCall(("period","last_month")),reference).Start.Value.Month==10,
            "周一开始的周范围与自然月范围");
        foreach(var bad in new[]{ScopeCall(("period","yestarday")),ScopeCall(("date_start","2026-11-02")),
            ScopeCall(("date_start","2026-11-03"),("date_end","2026-11-02")),
            ScopeCall(("date_start","2026-02-30"),("date_end","2026-03-01")),
            ScopeCall(("period","yesterday"),("date_start","2026-11-02"),("date_end","2026-11-02")),
            ScopeCall(("period","yesterday"),("kind","cognitions")),ScopeCall(("day_basis","memory")),ScopeCall(("kind","unknown"))})
        {
            var rejected=false;try { MemoryReadScope.Parse(bad,reference); } catch(ArgumentException) { rejected=true; }
            Require(rejected,"错误范围不能静默变为全库查询");
        }

        using var store=new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("小雨","小光","雨雨");
        var nodes=new[]{("hygiene","日常清洁"),("food","口味偏好"),("empty","未有经历的主题")}.Select(x=>
            new VectorIndexNode("concept."+x.Item1,VectorNodeLevel.Concept,x.Item2,x.Item2,"scope",new[]{"user"},
                new[]{"dimension.scope"},new[]{x.Item2},Array.Empty<string>())).ToList();
        store.SeedLifeTags(nodes);
        var router=new HierarchicalVectorRouterLogic(new FakeEncoder());router.Build(nodes);
        var probe=new RoutingScopeProbe();
        var services=new TracePluginServices(store,router){Recall=probe};
        using var plugins=new TracePluginManager(store,services);
        plugins.RegisterExternal(new DialogueTracePlugin());plugins.RegisterExternal(new InnerLifePlugin());plugins.RegisterExternal(new MemoryNervePlugin());
        const string root="scope-check";
        MomentRecord Seed(string id,string time,string text,string tag=null,string conversation=root,string visibility="private",bool observed=false)
        {
            var stamp=DateTimeOffset.Parse(time).ToUnixTimeMilliseconds();
            var source=new MomentRecord {Id="source-"+id,ConversationId=conversation,Role=observed ? "system_event" : "user",
                Type=observed ? MomentTypes.Event : MomentTypes.Message,Content=text,CreatedUnixMs=stamp,MemoryVisibility=visibility,
                EvidenceType=observed ? EvidenceTypeValues.PluginObserved : EvidenceTypeValues.UserReported,
                Realm=TraceRealmValues.ExternalWorld,SourcePluginId="scope.fixture",ParentMomentId=observed ? "trigger" : "",MemoryStatus="built"};
            store.SaveMoment(source);
            if(tag!=null)
            {
                store.SaveEventIndex(new(){Id=id,Status="active",TimeUnixMs=stamp,FirstMomentId=source.Id,TagIds="concept."+tag,EventSummary=text});
                store.AppendEventEntry(new(){Id="entry-"+id,IndexId=id,SourceMomentId=source.Id,Summary=text,Detail=text,
                    Realm=TraceRealmValues.ExternalWorld,CreatedUnixMs=reference.ToUnixTimeMilliseconds()});
            }
            return source;
        }
        Seed("old","2026-10-26T12:00:00+08:00","洗头旧日标记","hygiene");
        Seed("before","2026-11-01T23:59:59.999+08:00","洗头边界外标记","hygiene");
        Seed("start","2026-11-02T00:00:00+08:00","洗头零点标记","hygiene");
        Seed("late","2026-11-02T23:59:59.999+08:00","洗头昨日末尾标记","hygiene");
        Seed("today","2026-11-03T00:00:00+08:00","洗头今天标记","hygiene");
        var food=Seed("food","2026-11-02T13:00:00+08:00","喜欢吃牛肉面口味标记","food");
        Seed("raw","2026-11-02T14:00:00+08:00","洗头没有切片的原文标记");
        Seed("observed","2026-11-02T15:00:00+08:00","洗头工具观察标记",observed:true);
        Seed("other","2026-11-02T16:00:00+08:00","洗头另一主体标记","hygiene",conversation:"other");
        store.CommitCognitions(food.Id,new[]{new BrainCognitionWriteData{operation="create",summary="喜欢吃牛肉面的长期口味认知标记",
            domains=new(){"user"},tag_ids=new(){"concept.food"},evidence_moment_ids=new(){food.Id}}});
        var trigger=Moment(root,"昨天我洗头了吗");trigger.CreatedUnixMs=reference.ToUnixTimeMilliseconds();
        var turn=new TraceTurnContext(root,trigger,new(),0,true,services);
        TraceCapabilityResultData Read(params (string,string)[] args)=>ScopedMemoryReadLogic.Read(turn,ScopeCall(args));
        var result=Read(("query","洗头"),("period","yesterday"));
        Require(probe.Calls.Last().Ids.ToHashSet().SetEquals(new[]{"entry-start","entry-late","entry-food"}),
            "日期与受众在语义检索之前收窄；按索引锚点而非晚一天复盘写入时间筛选");
        Require(result.Payload.Contains("没有切片的原文标记") && result.Payload.Contains("工具观察标记") &&
            !result.Payload.Contains("旧日标记") && !result.Payload.Contains("今天标记") && !result.Payload.Contains("另一主体标记") &&
            !result.Payload.Contains("长期口味认知标记"),"日期查询覆盖原件与工具观察，不混入范围外资料或当前认知");
        result=Read(("query","洗头"),("period","yesterday"),("tags","[\"日常清洁\"]"));
        Require(probe.Calls.Last().Ids.ToHashSet().SetEquals(new[]{"entry-start","entry-late"}) &&
            !result.Payload.Contains("口味标记") && !result.Payload.Contains("没有切片的原文标记"),"日期和主题取交集，未归类原文不能冒充已选主题");
        result=Read(("query","喜欢吃什么"),("kind","cognitions"),("tags","[\"口味偏好\"]"));
        Require(result.Payload.Contains("长期口味认知标记") && !result.Payload.Contains("事件切片候选") && !result.Payload.Contains("洗头"),"偏好路径只读所选主题的认知及依据");
        var beforeCalls=probe.Calls.Count;
        result=Read(("query","洗头"),("kind","sources"),("period","yesterday"));
        Require(result.Payload.Contains("没有切片的原文标记") && probe.Calls.Count==beforeCalls,"查原文不调用事件向量排序");
        result=Read(("query","洗头"),("date_start","2020-01-01"),("date_end","2020-01-01"));
        Require(result.Status=="empty" && !result.Payload.Contains("洗头旧日标记") && result.Summary.Contains("不能据此断言"),"空范围不跨日兜底，也不说没发生");
        result=Read(("query","洗头"),("period","yesterday"),("tags","[\"未有经历的主题\"]"));
        Require(result.Status=="empty","空主题不能回退日期内全部事件");
        Require(Read(("query","洗头"),("period","昨天")).Status=="failed","非法日期枚举返回失败而非全库");

        const string room=root+":environment:room";
        Seed("public","2026-11-02T12:00:00+08:00","洗头公开原文标记",conversation:room,visibility:"public");
        Seed("private-room","2026-11-02T12:00:00+08:00","洗头私密原文标记",conversation:room);
        turn=new TraceTurnContext(room,trigger,new(),0,true,services,new EnvironmentSnapshotData{RootConversationId=root,Visibility="public"});
        result=Read(("query","洗头"),("period","yesterday"));
        Require(result.Payload.Contains("公开原文标记") && !result.Payload.Contains("私密原文标记") &&
            !result.Payload.Contains("没有切片的原文标记"),"公开范围读取只含当前环境公开原件");

        // 真正Agent入口：只在它明确选择日期后才向量检索，首步不做全库预激活。
        probe.Calls.Clear();
        var llm=new AgentSequenceLlm("{\"step\":\"continue\",\"actions\":[{\"capability_id\":\"memory.recall\",\"arguments\":[{\"name\":\"query\",\"value\":\"洗头\"},{\"name\":\"period\",\"value\":\"yesterday\"},{\"name\":\"tags\",\"value\":\"[\\\"日常清洁\\\"]\"}]}]}",
            "{\"step\":\"finish\",\"reply\":\"我核对了昨天的记录。\"}");
        await new KernelLogic(store,llm,plugins).ProcessPluginEventAsync(root,new PluginEventData{PluginId="builtin.dialogue",Role="小雨",
            Type=MomentTypes.Message,Content="昨天我洗头了吗",OccurredUnixMs=reference.ToUnixTimeMilliseconds()},0);
        Require(probe.Calls.Count==1 && probe.Calls[0].Ids.ToHashSet().SetEquals(new[]{"entry-start","entry-late"}) &&
            llm.Requests[0].Contains("记忆读取范围") && llm.Requests[1].Contains("2026-11-02 00:00 +08:00"),
            "实际Kernel没有先全库语义检索，Agent收到范围说明并取得限定结果");

        // 候选窗口不能挤掉较早日期；超过上限时不能宣称已查尽。
        for(var i=0;i<2001;i++) Seed("bulk-"+i,"2026-11-03T12:00:00+08:00","今日无关条目"+i,conversation:"scope-large");
        Seed("historic","2026-11-02T12:00:00+08:00","洗头旧日期仍可读标记",conversation:"scope-large");
        turn=new TraceTurnContext("scope-large",trigger,new(),0,true,services);
        Require(Read(("query","洗头"),("kind","sources"),("period","yesterday")).Payload.Contains("旧日期仍可读标记"),
            "日期必须在原件LIMIT之前筛选，旧日不会被最近2000条挤掉");
        result=Read(("query","不存在"),("kind","sources"),("period","today"));
        Require(result.Status=="empty" && result.Summary.Contains("不完整"),"截断候选窗口明确报告覆盖不完整");
        for(var i=0;i<300;i++) Seed("batch-"+i,"2026-11-02T18:00:00+08:00","洗头批量依据标记"+i,"hygiene");
        turn=new TraceTurnContext(root,trigger,new(),0,true,services);
        Read(("query","洗头"),("period","yesterday"),("tags","[\"日常清洁\"]"));
        Require(probe.Calls.Last().Ids.Count==302,"分批加载依据，不因256条接口上限静默丢失候选");
        Console.WriteLine("Memory scope checks passed: calendar/memory day, ranges, actual Kernel routing, topic intersection, cognition/source paths, empty/invalid scope, audience and bounded coverage.");
    }
}
