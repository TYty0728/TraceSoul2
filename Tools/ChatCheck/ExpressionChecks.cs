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
        public ExpressionCatalog Catalog()=>Inner.Catalog();
        public ExpressionImage ReadImage(string id)=>Inner.ReadImage(id);
        public Task<ExpressionMatch> MatchAsync(ExpressionQuery query,CancellationToken token)
        {Calls++;Last=query;if(Fail)throw new InvalidOperationException("test failure");return Inner.MatchAsync(query,token);}
    }
    private sealed class ExpressionAdapter : ITracePlatformAdapter
    {
        public string PlatformId=>"builtin.onebot";public readonly List<TraceOutboundMessageData> Sent=new();
        public PluginEventData ConvertInbound(string text)=>null;
        public Task<TraceCapabilityResultData> SendAsync(TraceOutboundMessageData message,TraceTurnContext turn,CancellationToken token)
        {Sent.Add(message);return Task.FromResult(new TraceCapabilityResultData{Status="success"});}
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
        var folder=Path.Combine(root,"data","emojis","default");Directory.CreateDirectory(folder);
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
        File.WriteAllText(Path.Combine(root,"data","config.json"),"{\"personas\":[\"default\"],\"threshold\":0.55,\"top_tag_count\":1}");
        ExpressionLibrary Make()=>new(palette,Path.Combine(root,"data"),Path.Combine(root,"package"),()=>encoder,emptySemantics);
        try
        {
            var library=Make();var catalog=library.Catalog();
            Require(catalog.tags.Count==42&&catalog.tags.Select(t=>t.tag).Distinct().Count()==42&&catalog.assets.Count==2,"恢复六类42词，原索引图片可载入");
            Require(!JsonSerializer.Serialize(catalog).Contains(root)&&encoder.Calls==0,"目录不暴露绝对路径、不提前计算用户表达");
            Require((await library.MatchAsync(new(),default)).reason=="empty_expression","空表达不随机配图");
            var match=await library.MatchAsync(new(){text="开心",topTags=1},default);
            Require(match.status=="matched"&&match.tags[0].tag=="开心"&&match.candidates[0].name=="happy.png"&&match.candidates[0].score>0.99,"统一标签最高分选图");
            Require(match.selectedId==match.candidates[0].id,"选择最高分而非随机候选");
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
            var missing=new ExpressionLibrary(palette,Path.Combine(root,"data"),"unused",()=>null,emptySemantics);
            Require((await missing.MatchAsync(new(){text="开心"},default)).reason=="embedding_unavailable","模型不可用不偷偷调用LLM或随机发图");
            var mutable=library.Catalog();mutable.assets[0].tags.Clear();
            Require(library.Catalog().assets[0].tags.Count==42,"预览DTO不能改变共享评分");
            await CheckExpressionBodies(library);
            await CheckExpressionSemantics(root,palette);
            File.WriteAllBytes(Path.Combine(folder,"happy.png"),new byte[]{9});
            try {library.ReadImage(match.selectedId);throw new Exception("changed image accepted");}catch(InvalidOperationException){}
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
        Require(sleep.status=="matched"&&sleep.semanticMode=="intent"&&sleep.candidates[0].name=="sleep.png","明确睡觉用途先于开心标签，心情弱也可使用用途图");
        Require(sleep.candidates.Single(c=>c.name=="happy.png").eligible==false,"不相关开心图被用途筛选排除");
        var sweat=await library.MatchAsync(new(){text="[sweat]"},default);
        Require(sweat.status=="matched"&&sweat.candidates[0].name=="sweat.png","流汗用途选流汗图");
        var happy=await library.MatchAsync(new(){text="开心"},default);
        Require(happy.status=="matched"&&happy.candidates[0].name=="happy.png"&&happy.candidates.Where(c=>c.name!="happy.png").All(c=>!c.eligible),"用途不明确回到心情，睡觉流汗等专用图不参与");
        var bye=await library.MatchAsync(new(){text="[bye]",mood="开心"},default);
        Require(bye.reason=="semantic_no_asset"&&bye.selectedId==null,"明确告别但无图，不改发开心图");
        Require((await library.MatchAsync(new(){mood="[sleep]"},default)).semanticMode!="intent","仅感受摘要不能制造用途意图");
        var denied=await library.MatchAsync(new(){text="[not-sleep]"},default);
        Require(denied.selectedId==null&&denied.semanticTags.Single(t=>t.tag=="睡觉").excluded,"更接近排除语义时，不选择睡觉图");
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
        Console.WriteLine("Expression semantics checks passed: intent before mood, narrow-purpose guards, absent-purpose skip, editable/extensible tags, atomic backup and stale edit rejection.");
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
        async Task Reply()
        {
            var source=board.Receive(board.SessionId,Guid.NewGuid().ToString(),"合成输入","测试者","expression-body");
            await new KernelLogic(store,new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"开心\"}"),manager).ProcessPluginEventAsync("expression-body",source,12);
        }
        await Reply();
        Require(matcher.Calls==1&&matcher.Last.text=="开心","最终表达只匹配一次，传入Soul回复");
        var sent=bridge.Bodies[bridge.Paths.IndexOf("/api/speak")].GetProperty("display");
        var selected=await library.MatchAsync(new(){text="开心"},default);
        Require(board.DisplayState().expressionId==selected.selectedId&&board.DisplayState().subtitle=="开心","实时屏幕投影复用本轮共用表情匹配，不额外匹配或读取QQ历史");
        Require(sent.GetProperty("subtitle").GetString()=="开心"&&Convert.FromBase64String(sent.GetProperty("image").GetString()).SequenceEqual(library.ReadImage(selected.selectedId).bytes),"WXXZ使用共享最高分原图并保留字幕");
        Require(!bridge.Paths.Contains("/api/text")&&adapter.Sent.Count==0,"新版分页显示不走旧裁切路径，WXXZ不误发QQ");
        var qqSource=new PluginEventData{PluginId="builtin.onebot",Role="user",Environment=new(){PlatformId="builtin.onebot",SessionId="fixture-target",SessionType="private",SpeakerId="fixture-user",ExclusivePair=true}};
        var qqEnvironment=EnvironmentLogic.Resolve(store,"expression-qq",qqSource);
        var turn=new TraceTurnContext(qqEnvironment.ContextConversationId,new MomentRecord(),new List<MomentRecord>(),0,true,services,qqEnvironment);
        turn.Workspace.ExpressionMatch=selected;
        var qqResult=await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,default);
        Require(qqResult.Status=="success","QQ fixture delivery: "+qqResult.Summary+"; "+JsonSerializer.Serialize(qqEnvironment));
        Require(adapter.Sent.Count==1&&adapter.Sent[0].File==library.ReadImage(selected.selectedId).path&&adapter.Sent[0].SessionId=="fixture-target","QQ发送同一最高分图片到原会话");
        turn.Workspace.ExpressionMatch=null;await manager.ExecuteAsync(new(){capability_id="qq.sticker.send"},turn,default);
        Require(adapter.Sent.Count==1,"无本轮匹配不复用上轮图片");
        matcher.Fail=true;await Reply();
        var lastDisplay=bridge.Bodies.Last(b=>b.TryGetProperty("display",out _)).GetProperty("display");
        Require(!lastDisplay.TryGetProperty("image",out _)&&adapter.Sent.Count==1,"匹配异常仍回复，且不复用旧情绪或误发");
        var count=store.GetRecentMoments(board.ContextConversationId,100).Count(m=>m.Role=="测试同伴"&&m.Content=="开心");
        bridge.MissingDisplayReceipt=true;
        try {await Reply();throw new Exception("missing display receipt accepted");}catch(InvalidOperationException){}
        Require(store.GetRecentMoments(board.ContextConversationId,100).Count(m=>m.Role=="测试同伴"&&m.Content=="开心")==count,"字幕页回执缺失不记完整表达成功");
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
