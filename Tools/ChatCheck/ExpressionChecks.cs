using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.ExternalPlugins;
using TraceSoul2.Host;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Tools.Memory;

internal static partial class Program
{
    private sealed class EmotionEncoder : IEmbeddingService
    {
        public string ModelId {get;set;}="emotion-test";
        public int Calls,CancelAt;
        public Task<float[]> EmbedAsync(string text,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();
            if(++Calls==CancelAt)throw new OperationCanceledException();
            var vector=new float[3];vector[text.Contains("开心")?0:text.Contains("不安")?1:2]=1;
            return Task.FromResult(vector);
        }
    }
    private sealed class CountingExpressions : IExpressionLibrary
    {
        public IExpressionLibrary Inner;public int Calls;public ExpressionQuery Last;public bool Fail;
        public ExpressionMatch Result;
        public readonly HashSet<string> Unreadable=new();
        public ExpressionCatalog Catalog()=>Inner.Catalog();
        public ExpressionImage ReadImage(string id)=>Unreadable.Contains(id)?throw new IOException("image unavailable"):Inner.ReadImage(id);
        public Task<ExpressionMatch> MatchAsync(ExpressionQuery query,CancellationToken token)
        {Calls++;Last=query;if(Fail)throw new InvalidOperationException("test failure");return Result!=null?Task.FromResult(Result):Inner.MatchAsync(query,token);}
    }
    private sealed class ExpressionAdapter : ITracePlatformAdapter
    {
        public string PlatformId=>"builtin.onebot";public readonly List<TraceOutboundMessageData> Sent=new();
        public bool FailSend;
        public PluginEventData ConvertInbound(string text)=>null;
        public Task<TraceCapabilityResultData> SendAsync(TraceOutboundMessageData message,TraceTurnContext turn,CancellationToken token)
        {Sent.Add(message);if(FailSend)throw new HttpRequestException("send outcome unknown");return Task.FromResult(new TraceCapabilityResultData{Status="success"});}
        public Task<string> CallActionAsync(string action,Dictionary<string,object> parameters,CancellationToken token=default)=>throw new Exception("No network in expression checks");
    }
    private sealed class ExpressionQqPlatform : ITracePlugin
    {
        public ExpressionQqPlatform(bool fixture){}
        public TracePluginMetadataData Metadata {get;}=new(){Id="builtin.onebot",DisplayName="test QQ",Version="1",Role=PluginRoleValues.Platform,PlatformId=BodyIds.Qq};
        public void Register(TracePluginContext context){}
        public void Shutdown(){}
    }
    private static async Task RunExpressionChecksAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"expression-check-"+Guid.NewGuid().ToString("N"));
        var data=Path.Combine(root,"data","qq-sticker");
        var folder=Path.Combine(data,"emojis","default");Directory.CreateDirectory(folder);
        var palette=Path.Combine(AppContext.BaseDirectory,"emotion_tags.json");
        var emptySemantics=Path.Combine(root,"empty-semantics.json");File.WriteAllText(emptySemantics,"{\"schema_version\":1,\"tags\":[]}");
        var encoder=new EmotionEncoder();
        File.WriteAllBytes(Path.Combine(folder,"happy.png"),new byte[]{1,2,3});
        File.WriteAllBytes(Path.Combine(folder,"worried.png"),new byte[]{4,5,6});
        var labels=JsonDocument.Parse(File.ReadAllText(palette)).RootElement.GetProperty("categories").EnumerateArray()
            .SelectMany(c=>c.GetProperty("tags").EnumerateArray().Select(t=>t.GetString())).ToArray();
        // Explicit scores remove intentional fake-encoder collisions from the fixture.
        Dictionary<string,double> Weights(string tag)=>labels.ToDictionary(t=>t,t=>t==tag?1d:0d);
        var index=Path.Combine(folder,"emoji_index.json");
        File.WriteAllText(index,JsonSerializer.Serialize(new Dictionary<string,object>{
            ["happy.png"]=new{emotions=new[]{"旧开心词"},description="开心",tag_scores=Weights("开心")},
            ["worried.png"]=new{emotions=new[]{"旧疑惑词"},description="不安",tag_scores=Weights("不安")}}));
        File.WriteAllText(Path.Combine(data,"config.json"),"{\"personas\":[\"default\"],\"threshold\":0.55,\"top_tag_count\":1}");
        ExpressionLibrary Make()=>new(palette,data,Path.Combine(root,"package"),()=>encoder,emptySemantics);
        try
        {
            var library=Make();var catalog=library.Catalog();
            Require(catalog.tags.Count==42&&catalog.tags.Select(t=>t.tag).Distinct().Count()==42&&catalog.assets.Count==2,"恢复六类42词，原索引图片可载入");
            Require(!JsonSerializer.Serialize(catalog).Contains(root)&&encoder.Calls==0,"目录不暴露绝对路径、不提前计算用户表达");
            Require((await library.MatchAsync(new(),default)).reason=="empty_expression","空表达不随机配图");
            var match=await library.MatchAsync(new(){text="开心",topTags=1},default);
            Require(match.status=="matched"&&match.tags[0].tag=="开心"&&match.candidates[0].name=="happy.png"&&match.candidates[0].score>0.99,"统一标签最高分选图");
            Require(match.selectedId==match.candidates[0].id,"选择最高分而非随机候选");
            var zero=await library.MatchAsync(new(){text="普通技术说明"},default);
            Require(zero.status=="matched"&&zero.candidates.Count==2&&zero.candidates.All(c=>c.score==0)&&zero.selectedId==zero.candidates[0].id,
                "弱信号和全零分也选择稳定最高分，不按最低分门槛跳过");
            File.WriteAllText(Path.Combine(data,"config.json"),"{\"personas\":[\"default\"],\"threshold\":1,\"semantic_threshold\":1,\"top_tag_count\":1}");
            Require((await Make().MatchAsync(new(){text="普通技术说明"},default)).selectedId==zero.selectedId,
                "旧配置高阈值不能阻止最高分图片入选");
            var calls=encoder.Calls;await library.MatchAsync(new(){text="开心"},default);
            Require(encoder.Calls==calls+1,"图片和标签档案缓存，普通表达只编码正文");
            encoder.ModelId="emotion-v2";await library.MatchAsync(new(){text="不安"},default);
            Require(encoder.Calls>calls+42,"模型版本改变时重新建立图片档案");
            foreach(var n in new[]{0,43})
            {try {await library.MatchAsync(new(){text="开心",topTags=n},default);throw new Exception("invalid n accepted");}catch(ArgumentException){}}
            try {library.ReadImage("../private");throw new Exception("path accepted");}catch(ArgumentException){}
            var canceled=Make();encoder.CancelAt=encoder.Calls+4;
            try {await canceled.MatchAsync(new(){text="开心"},default);throw new Exception("cancellation accepted");}catch(OperationCanceledException){}
            Require(!canceled.Catalog().profilesReady,"取消建档不发布半成品");
            encoder.CancelAt=0;Require((await canceled.MatchAsync(new(){text="开心"},default)).status=="matched","取消后下一次显式匹配可完成");
            var missing=new ExpressionLibrary(palette,data,"unused",()=>null,emptySemantics);
            Require((await missing.MatchAsync(new(){text="开心"},default)).reason=="embedding_unavailable","模型不可用不偷偷调用LLM或随机发图");
            var mutable=library.Catalog();mutable.assets[0].tags.Clear();
            Require(library.Catalog().assets[0].tags.Count==42,"预览DTO不能改变共享评分");
            await CheckExpressionBodies(library);
            await CheckExpressionRankedDelivery(library);
            await CheckExpressionQqMerge(library,missing);
            await CheckExpressionSemantics(root,palette);
            File.WriteAllBytes(Path.Combine(folder,"happy.png"),new byte[]{9});
            try {library.ReadImage(match.selectedId);throw new Exception("changed image accepted");}catch(InvalidOperationException){}
            var next=await library.MatchAsync(new(){text="开心"},default);
            Require(next.status=="matched"&&next.selectedId!=match.selectedId&&!next.candidates[0].eligible&&next.candidates[0].reason=="image_unavailable",
                "最高分原图失效时选下一个可用分数，不让整个评分失败");
            File.WriteAllText(index,"{\"../private.png\":{}}");
            try {Make();throw new Exception("unsafe index accepted");}catch(InvalidOperationException){}
            Console.WriteLine("Expression checks passed: 42 labels, scores/ranking/cache/cancel, bound assets, Kernel/WXXZ/QQ isolation and incomplete delivery.");
        }
        finally {Directory.Delete(root,true);}
    }
    private sealed class SemanticEncoder : IEmbeddingService
    {
        public string ModelId=>"semantic-fixture";
        public Task<float[]> EmbedAsync(string text,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();var vector=new float[6];
            vector[text.Contains("[sleep]")?1:text.Contains("[sweat]")?2:text.Contains("[bye]")?3:text.Contains("[custom]")?4:text.Contains("开心")?0:5]=1;
            return Task.FromResult(vector);
        }
    }
    private static async Task CheckExpressionSemantics(string root,string palette)
    {
        var data=Path.Combine(root,"semantic-data");var folder=Path.Combine(data,"emojis","default");Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(data,"config.json"),"{\"personas\":[\"default\"]}");
        var schema=Path.Combine(root,"semantics.json");
        File.WriteAllText(schema,"{\"schema_version\":1,\"tags\":[{\"tag\":\"睡觉\",\"description\":\"[sleep]\",\"requires_intent\":true,\"exclusions\":[\"[not-sleep]\"]},{\"tag\":\"流汗\",\"description\":\"[sweat]\",\"requires_intent\":true},{\"tag\":\"告别\",\"description\":\"[bye]\",\"requires_intent\":true}]}");
        var index=Path.Combine(folder,"emoji_index.json");
        File.WriteAllText(index,"{\"sleep.png\":{\"emotions\":[\"平静\"],\"description\":\"[sleep]\",\"semantic_tags\":[\"睡觉\"]},\"sweat.png\":{\"description\":\"[sweat]\",\"semantic_tags\":[\"流汗\"]},\"happy.png\":{\"emotions\":[\"开心\"],\"description\":\"开心\",\"future_field\":{\"keep\":true}}}");
        foreach(var name in new[]{"sleep.png","sweat.png","happy.png"})File.WriteAllBytes(Path.Combine(folder,name),new byte[]{1,2,3});
        var library=new ExpressionLibrary(palette,data,"unused",()=>new SemanticEncoder(),schema);
        var sleep=await library.MatchAsync(new(){text="[sleep]",mood="开心"},default);
        Require(sleep.status=="matched"&&sleep.semanticMode=="combined"&&sleep.candidates[0].name=="sleep.png","用途与心情综合评分，睡觉用途高分图胜出");
        Require(sleep.candidates.All(c=>c.eligible)&&sleep.candidates.Count==3,"整库参与评分，不因用途不一致预先排除");
        var sweat=await library.MatchAsync(new(){text="[sweat]"},default);
        Require(sweat.status=="matched"&&sweat.candidates[0].name=="sweat.png","流汗用途选流汗图");
        var happy=await library.MatchAsync(new(){text="开心"},default);
        Require(happy.status=="matched"&&happy.candidates[0].name=="happy.png"&&happy.candidates.All(c=>c.eligible),"开心图按综合分胜出，其他图保留各自评分");
        var bye=await library.MatchAsync(new(){text="[bye]",mood="开心"},default);
        Require(bye.status=="matched"&&bye.selectedId==bye.candidates[0].id&&bye.candidates.Count==3,"用途缺图时仍从全库选最高分，不落空");
        Require((await library.MatchAsync(new(){mood="[sleep]"},default)).semanticMode=="emotion","仅感受摘要不参与用途评分");
        var denied=await library.MatchAsync(new(){text="[not-sleep]"},default);
        Require(denied.status=="matched"&&denied.selectedId!=sleep.selectedId&&denied.semanticTags.Single(t=>t.tag=="睡觉").excluded&&
            denied.candidates.Single(c=>c.name=="sleep.png").semanticScore<0,"排除语义降低用途分，仍选本轮最高分图片");
        var original=File.ReadAllText(index);var sleepAsset=library.Catalog().assets.Single(a=>a.name=="sleep.png");
        var changed=library.WithSemanticTags(sleepAsset.id,new[]{"[custom]"});
        Require(changed.Catalog().revision!=library.Catalog().revision&&changed.Catalog().tags.Count==42&&changed.Catalog().semanticTags.Contains("[custom]"),"语义标签可扩展，42心情词不改变");
        var custom=await changed.MatchAsync(new(){text="[custom]"},default);
        Require(custom.status=="matched"&&custom.selectedId==sleepAsset.id,"保存新语义后，同一资产和两个平台选择器立即可用");
        using(var before=JsonDocument.Parse(original))using(var after=JsonDocument.Parse(File.ReadAllText(index)))
        {
            Require(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(before.RootElement.GetProperty("happy.png").GetRawText()),System.Text.Json.Nodes.JsonNode.Parse(after.RootElement.GetProperty("happy.png").GetRawText())),"单图编辑保留其他图与未知扩展字段");
            Require(after.RootElement.GetProperty("sleep.png").GetProperty("description").GetString()=="[sleep]"&&Directory.GetFiles(folder,"*.bak").Length==1,"原描述保留且索引有原子备份");
        }
        try {library.WithSemanticTags(sleepAsset.id,new[]{"睡觉"});throw new Exception("stale edit accepted");}catch(InvalidOperationException){}
        var saved=File.ReadAllText(index);
        foreach(var invalid in new[]{new[]{"重复","重复"},new[]{"换\n行"},Enumerable.Repeat("a",17).ToArray()})
        {try {changed.WithSemanticTags(sleepAsset.id,invalid);throw new Exception("invalid semantic edit accepted");}catch(ArgumentException){}}
        Require(File.ReadAllText(index)==saved,"非法标签不写文件");
        var legacy=Path.Combine(root,"legacy-data");var legacyFolder=Path.Combine(legacy,"emojis","default");Directory.CreateDirectory(legacyFolder);
        var legacyIndex=System.Text.Json.Nodes.JsonNode.Parse(original).AsObject();
        foreach(var item in legacyIndex){item.Value.AsObject().Remove("semantic_tags");File.Copy(Path.Combine(folder,item.Key),Path.Combine(legacyFolder,item.Key));}
        File.WriteAllText(Path.Combine(legacyFolder,"emoji_index.json"),legacyIndex.ToJsonString());
        File.Copy(Path.Combine(data,"config.json"),Path.Combine(legacy,"config.json"));
        var legacyLibrary=new ExpressionLibrary(palette,legacy,"unused",()=>new SemanticEncoder(),schema);
        var legacyMatch=await legacyLibrary.MatchAsync(new(){text="[sleep]",mood="开心"},default);
        Require(legacyMatch.status=="matched"&&legacyMatch.semanticMode=="emotion"&&legacyMatch.candidates[0].name=="happy.png","完全未标注的旧图库保持原心情匹配行为");
        Console.WriteLine("Expression semantics checks passed: continuous purpose/mood scores, whole-library ranking, no minimum cutoff, exclusions, editable tags and atomic backup.");
    }
    private static async Task CheckExpressionBodies(IExpressionLibrary library)
    {
        using var store=new SqliteMemoryManager(":memory:");store.SavePairIdentity("测试者","测试同伴","测试者");
        var matcher=new CountingExpressions{Inner=library};
        var services=new TracePluginServices(store,new HierarchicalVectorRouterLogic(new FakeEncoder())){Expressions=matcher};
        using var manager=new TracePluginManager(store,services);manager.RegisterExternal(new DialogueTracePlugin());
        var adapter=new ExpressionAdapter();services.PlatformAdapters.Add(adapter);manager.RegisterExternal(new ExpressionQqPlatform(true));manager.RegisterExternal(new QqStickerPlugin());
        services.Platforms.Register(new PlatformHandle{Id=BodyIds.Qq,DisplayName="test QQ",IsConnected=()=>true});
        var bridge=new WxxzMockBridge{Companion=true};using var board=new WxxzPlatform(bridge);manager.RegisterExternal(board);
        await board.StartAsync(true,default);
        async Task Reply(string reply="开心")
        {
            var source=board.Receive(board.SessionId,Guid.NewGuid().ToString(),"合成输入","测试者","expression-body");
            await new KernelLogic(store,new AgentSequenceLlm(JsonSerializer.Serialize(new{step="finish",reply})),manager).ProcessPluginEventAsync("expression-body",source,12);
        }
        await Reply();
        Require(matcher.Calls==1&&matcher.Last.text=="开心","最终表达只匹配一次，传入Soul回复");
        var sent=bridge.Bodies[bridge.Paths.IndexOf("/api/speak")].GetProperty("display");
        var selected=await library.MatchAsync(new(){text="开心"},default);
        Require(board.DisplayState().expressionId==selected.selectedId&&board.DisplayState().subtitle=="开心","实时屏幕投影复用本轮共用表情匹配，不额外匹配或读取QQ历史");
        Require(sent.GetProperty("subtitle").GetString()=="开心"&&Convert.FromBase64String(sent.GetProperty("image").GetString()).SequenceEqual(library.ReadImage(selected.selectedId).bytes),"WXXZ使用共享最高分原图并保留字幕");
        var prepared=bridge.Bodies[bridge.Paths.IndexOf("/api/display")];
        Require(prepared.GetProperty("image").GetString()==sent.GetProperty("image").GetString(),"开发板准备和说话画面使用同一最高分图片，不改选状态图");
        Require(!bridge.Paths.Contains("/api/text")&&adapter.Sent.Count==0,"新版分页显示不走旧裁切路径，WXXZ不误发QQ");
        var qqSource=new PluginEventData{PluginId="builtin.onebot",Role="user",Environment=new(){PlatformId="builtin.onebot",SessionId="fixture-target",SessionType="private",SpeakerId="fixture-user",ExclusivePair=true}};
        var qqEnvironment=EnvironmentLogic.Resolve(store,"expression-qq",qqSource);
        var turn=new TraceTurnContext(qqEnvironment.ContextConversationId,new MomentRecord(),new List<MomentRecord>(),0,true,services,qqEnvironment);
        turn.Workspace.ExpressionMatch=selected;
        var qqResult=await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,default);
        Require(qqResult.Status=="success","QQ fixture delivery: "+qqResult.Summary+"; "+JsonSerializer.Serialize(qqEnvironment));
        Require(adapter.Sent.Count==1&&adapter.Sent[0].File==library.ReadImage(selected.selectedId).path&&adapter.Sent[0].SessionId=="fixture-target","QQ发送同一最高分图片到原会话");
        var zero=await library.MatchAsync(new(){text="普通技术说明"},default);
        await Reply("普通技术说明");
        var zeroDisplay=bridge.Bodies.Last(b=>b.TryGetProperty("display",out _)).GetProperty("display");
        turn.Workspace.ExpressionMatch=zero;
        await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,default);
        Require(zero.candidates.All(c=>c.score==0)&&board.DisplayState().expressionId==zero.selectedId&&
            Convert.FromBase64String(zeroDisplay.GetProperty("image").GetString()).SequenceEqual(library.ReadImage(zero.selectedId).bytes)&&
            adapter.Sent.Last().File==library.ReadImage(zero.selectedId).path,"零分时开发板与QQ也都直接使用同一最高分图片");
        matcher.Result=selected;matcher.Unreadable.Add(selected.selectedId);
        await Reply();
        turn.Workspace.ExpressionMatch=selected;
        await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,default);
        var nextId=selected.candidates[1].id;
        var nextImage=library.ReadImage(nextId);
        var nextDisplay=bridge.Bodies.Last(b=>b.TryGetProperty("display",out _)).GetProperty("display");
        Require(board.DisplayState().expressionId==nextId&&Convert.FromBase64String(nextDisplay.GetProperty("image").GetString()).SequenceEqual(nextImage.bytes)&&
            adapter.Sent.Last().File==nextImage.path,"首图失效时两边顺位选择同一第二高分，实体与网页投影一致");
        var nextPrepared=bridge.Bodies.Last(b=>b.TryGetProperty("state",out var state)&&state.GetString()=="thinking");
        Require(nextPrepared.GetProperty("image").GetString()==nextDisplay.GetProperty("image").GetString(),"失效顺位后开发板准备和说话也共用同一已验证图片");
        matcher.Result=null;matcher.Unreadable.Clear();
        var qqCount=adapter.Sent.Count;
        turn.Workspace.ExpressionMatch=null;await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,default);
        Require(adapter.Sent.Count==qqCount&&turn.Workspace.ExpressionMatch==null,"评分失败不伪造本轮评分或另发默认图");
        var matchCalls=matcher.Calls;
        matcher.Fail=true;await Reply();
        var lastDisplay=bridge.Bodies.Last(b=>b.TryGetProperty("display",out _)).GetProperty("display");
        Require(!lastDisplay.TryGetProperty("image",out _)&&board.DisplayState().expressionId==null&&adapter.Sent.Count==qqCount&&matcher.Calls==matchCalls+1,
            "真正评分异常时保留回复，但开发板也不伪造本轮选图或改选状态图");
        Require(board.Diagnostics.Snapshot().Any(e=>e.code=="reply_artwork_unavailable"),"真实评分故障明确报告，与低分仍有选图区分");
        var count=store.GetRecentMoments(board.ContextConversationId,100).Count(m=>m.Role=="测试同伴"&&m.Content=="开心");
        bridge.MissingDisplayReceipt=true;
        try {await Reply();throw new Exception("missing display receipt accepted");}catch(InvalidOperationException){}
        Require(store.GetRecentMoments(board.ContextConversationId,100).Count(m=>m.Role=="测试同伴"&&m.Content=="开心")==count,"字幕页回执缺失不记完整表达成功");
    }
    private static async Task CheckExpressionRankedDelivery(IExpressionLibrary library)
    {
        using var store=new SqliteMemoryManager(":memory:");store.SavePairIdentity("测试者","测试同伴","测试者");
        var matcher=new CountingExpressions{Inner=library};
        var services=new TracePluginServices(store,new HierarchicalVectorRouterLogic(new FakeEncoder())){Expressions=matcher};
        using var manager=new TracePluginManager(store,services);
        var adapter=new ExpressionAdapter();services.PlatformAdapters.Add(adapter);
        manager.RegisterExternal(new ExpressionQqPlatform(true));manager.RegisterExternal(new QqStickerPlugin());
        services.Platforms.Register(new PlatformHandle{Id=BodyIds.Qq,IsConnected=()=>true});
        TraceTurnContext Turn(string type="private",string platform="builtin.onebot")
        {
            var environment=EnvironmentLogic.Resolve(store,"ranked-"+type+platform,new PluginEventData{PluginId=platform,
                Environment=new(){PlatformId=platform,SessionId="fixture-"+type,SessionType=type,SpeakerId="fixture-user",ExclusivePair=type=="private"}});
            return new(environment.ContextConversationId,new MomentRecord(),new(),0,true,services,environment);
        }
        var turn=Turn();
        async Task<TraceOutboundMessageData> Send()
        {
            var before=adapter.Sent.Count;
            var result=await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,default);
            Require(result.Status=="success"&&adapter.Sent.Count==before+1,"按评分每次恰好交付一张图片");
            return adapter.Sent.Last();
        }
        var match=await library.MatchAsync(new(){text="开心"},default);
        var ranked=match.candidates.Select(c=>c.id).ToArray();turn.Workspace.ExpressionMatch=match;
        Require((await Send()).File==library.ReadImage(ranked[0]).path,"按本轮最高分选择，不走通用状态图评分");
        // Deliberately scramble both the supplied list and selected id: delivery
        // must still honor scores rather than arrival order or an old choice.
        match.candidates.Reverse();match.selectedId=ranked[1];
        Require((await Send()).File==library.ReadImage(ranked[0]).path,"评分顺序决定选图，旧选择和列表顺序不覆盖最高分");
        matcher.Unreadable.Add(ranked[0]);
        Require((await Send()).File==library.ReadImage(ranked[1]).path,"首图失效时顺位选择本轮第二高分图片");
        matcher.Unreadable.Add(ranked[1]);
        var count=adapter.Sent.Count;
        Require((await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,default)).Status=="failed"&&adapter.Sent.Count==count,
            "图库全部失效明确失败，不改发默认QQ表情");
        matcher.Unreadable.Clear();
        foreach(var reason in new[]{"embedding_unavailable","empty_library","images_unavailable"})
        {
            turn.Workspace.ExpressionMatch=new(){status="skipped",reason=reason};
            Require((await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,default)).Status=="failed"&&adapter.Sent.Count==count,
                "基础服务或资产失败如实报告，不构造默认图补位");
        }
        turn=Turn("group");turn.Workspace.ExpressionMatch=match;
        var group=await Send();Require(group.Kind==TraceOutboundKinds.Image&&group.File==library.ReadImage(ranked[0]).path&&group.SessionId=="fixture-group"&&group.SessionType=="group",
            "最高分图片保留原群聊目标");
        count=adapter.Sent.Count;
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();
            try {await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,canceled.Token);throw new Exception("cancel ignored");}
            catch(OperationCanceledException){}
        }
        Require(adapter.Sent.Count==count,"取消后不能发送");
        turn=Turn(platform:"wxxz.local");
        Require((await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,default)).Status!="success"&&adapter.Sent.Count==count,
            "开发板目标不能误发QQ");
        turn=Turn();turn.Workspace.ExpressionMatch=match;adapter.FailSend=true;
        Require((await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,default)).Status=="failed"&&adapter.Sent.Count==count+1,
            "发送结果未知保留失败，不再选下一张重发造成重复");
        Require(matcher.Calls==0,"发送只读取一次评分的排名，不重复调用匹配器或模型");
    }
    private sealed class ExpressionTransport : HttpMessageHandler
    {
        public readonly List<JsonElement> Sent=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            using var json=JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            if(json.RootElement.GetProperty("action").GetString()=="send_private_msg")Sent.Add(json.RootElement.GetProperty("params").Clone());
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"status\":\"ok\",\"retcode\":0}")};
        }
    }
    private static async Task CheckExpressionQqMerge(IExpressionLibrary library,IExpressionLibrary unavailable)
    {
        using var store=new SqliteMemoryManager(":memory:");store.SavePairIdentity("测试者","测试同伴","测试者");
        EnvironmentLogic.SaveSettings(store,new EnvironmentSettings{OwnerAccounts=new(){new(){PlatformId="builtin.onebot",UserId="123"}}});
        var services=new TracePluginServices(store,new HierarchicalVectorRouterLogic(new FakeEncoder()));
        using var manager=new TracePluginManager(store,services);manager.RegisterExternal(new DialogueTracePlugin());
        var qq=new OneBotPlatformPlugin();manager.RegisterExternal(qq);manager.RegisterExternal(new QqStickerPlugin());
        using var handler=new ExpressionTransport();using var http=new HttpClient(handler);
        void Field(string name,object value)=>typeof(OneBotPlatformPlugin).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(qq,value);
        Field("http",http);Field("connected",true);qq.Config.enabled=true;qq.Config.reply_enabled=true;
        qq.Config.mode="forward";qq.Config.http_url="https://qq.invalid";
        var zero=await library.MatchAsync(new(){text="普通技术说明"},default);
        var matcher=new CountingExpressions{Inner=library,Result=zero};
        var sequence=0;
        async Task Reply(IExpressionLibrary expressions,string suffix)
        {
            services.Expressions=expressions;
            qq.HandleInbound(JsonSerializer.Serialize(new{post_type="message",self_id=999,user_id=123,message_type="private",message_id=++sequence,
                message=new[]{new{type="text",data=new{text="公开合成输入"}}}}),null);
            var llm=new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"测试回复。\"}");
            await new KernelLogic(store,llm,manager).ProcessPluginEventAsync("expression-merge",qq.TakeInbound().Single());
            var sent=handler.Sent.Last();
            Require(handler.Sent.Count==sequence&&sent.GetProperty("user_id").GetInt64()==123&&sent.GetProperty("message").GetString()=="测试回复。"+suffix,
                "真实Kernel/QQ适配器把正文与最高分表情合并成一条消息，保留原收件人");
            Require(llm.Requests.Count==1,"按评分选图不增加主模型轮次");
        }
        var image="[CQ:image,file="+library.ReadImage(zero.selectedId).path.Replace(",","%2C")+"]";
        await Reply(matcher,image);matcher.Fail=true;await Reply(matcher,"");
        await Reply(unavailable,"");await Reply(null,"");
        Require(matcher.Calls==2,"低分与异常每轮只尝试一次共享评分");
    }
    private static async Task RunExpressionProbeAsync(string data,string package,string models)
    {
        using var encoder=new OnnxBgeEncoder(Path.Combine(models,"bge-small-zh-v1.5.onnx"),Path.Combine(models,"vocab.txt"));
        var service=new BgeEmbeddingService(encoder);
        var library=new ExpressionLibrary(Path.Combine(AppContext.BaseDirectory,"emotion_tags.json"),data,package,()=>service);
        foreach(var text in new[]{"晚安，早点睡，做个好梦。","我困得睁不开眼了，先去睡一觉。","我已经困得打哈欠了，想躺下休息。","呃，这也太尴尬了，我都冒汗了。","这下真不知道该怎么接话了，好尴尬。","你做得真棒，给你点赞！","说得对，就按你的想法来。","再见，我先走了，下次聊。","哇，居然准备了这样的惊喜，太开心了！","我没太明白你的意思，可以再解释一下吗？","这个参数需要改成三十秒，然后保存配置。","我暂时还不想睡觉，我们继续聊聊吧。"})
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();var match=await library.MatchAsync(new(){text=text},default);
            Console.WriteLine(JsonSerializer.Serialize(new{text,elapsedMs=watch.ElapsedMilliseconds,match.status,match.reason,match.semanticMode,semantics=match.semanticTags.Take(3).Select(t=>new{t.tag,score=Math.Round(t.score,3)}),candidates=match.candidates.Take(3).Select(c=>new{c.name,score=Math.Round(c.score,3),semantic=Math.Round(c.semanticScore,3),emotion=Math.Round(c.emotionScore,3),c.eligible})},new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        }
    }
}
