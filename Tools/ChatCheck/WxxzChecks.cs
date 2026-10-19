using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Host;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private sealed class WxxzMockBridge : HttpMessageHandler
    {
        public List<string> Paths = new();
        public bool FailText, FailVoice;
        public bool Companion, MissingDisplayReceipt;
        public Action<string> Observe;
        public readonly List<string> Authorizations=new();
        public readonly List<System.Text.Json.JsonElement> Bodies=new();
        public string UtteranceVoice;
        public string UtteranceText = "自动对话样本";
        public bool BlockUtterance;
        public readonly TaskCompletionSource<bool> TextObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<HttpResponseMessage> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HttpResponseMessage Utterance() => new(HttpStatusCode.OK) { Content = new StringContent(
            "{\"ok\":true,\"receipt\":{\"protocol\":1,\"status\":\"done\"},\"status\":\"transcribed\",\"text\":" + System.Text.Json.JsonSerializer.Serialize(UtteranceText) + ",\"voice\":" + UtteranceVoice + "}") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri.AbsolutePath;
            Paths.Add(path);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Observe?.Invoke(path);
            Bodies.Add(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(request.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
            if(path=="/api/text") TextObserved.TrySetResult(true);
            if (path == "/api/voice/sample") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"ok\":true,\"receipt\":{\"protocol\":1,\"status\":\"done\"},\"quality\":{\"recorded_ms\":15000,\"speech_ms\":4000,\"full_text\":\"本段本地听写\"},\"voice\":" + UtteranceVoice + "}") });
            if (path == "/api/utterance") return BlockUtterance ? Pending.Task : Task.FromResult(Utterance());
            if (path == "/api/listen/cancel")
            {
                Pending.TrySetResult(Utterance());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
            }
            var fail = path == "/api/text" ? FailText : path == "/api/speak" && FailVoice;
            var receipt = "{\"protocol\":1,\"firmware\":\"0.3.0\",\"status\":\"" + (path == "/api/status" ? "ready" : "done") + "\"}";
            if (path == "/api/listen")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"receipt\":" + receipt + ",\"text\":\"本地听写样本\"}") });
            return Task.FromResult(new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("{\"ok\":true," + (path == "/api/speak" ? "\"receipts\":[" + receipt + "]" : "\"receipt\":" + receipt) +
                (Companion&&path=="/api/status"?",\"display_version\":1":"")+
                (Companion&&path=="/api/speak"&&!MissingDisplayReceipt?",\"display_complete\":true,\"display_pages\":1,\"display_receipts\":["+receipt+"]":"")+"}", Encoding.UTF8, "application/json") });
        }
    }
    private static async Task RunWxxzChecksAsync(bool hardware = false)
    {
        if (!hardware) { await CheckWxxzDisplayAsync(); await CheckWxxzMonitorAsync(); await CheckWxxzAudioInterlockAsync(); CheckWxxzAttention(); await CheckWxxzWakeAsync(); CheckWxxzVoiceprint(); await CheckWxxzCallLifecycleAsync(); await CheckWxxzSpeechAsync(); }
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("测试者", "测试同伴", "测试者");
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new DialogueTracePlugin());
        var transport = new WxxzMockBridge();
        using var board = new WxxzPlatform(hardware ? null : transport);
        manager.RegisterExternal(board);
        await board.StartAsync(true, CancellationToken.None);
        var source = board.Receive(board.SessionId, Guid.NewGuid().ToString(), "这是一条合成联调发言，请简短回应。", "测试者", "test-wxxz");
        Require(!source.Content.StartsWith(WxxzPlatform.HeardPrefix, StringComparison.Ordinal), "手动发言不冒充实际听见");
        var environment = EnvironmentLogic.Resolve(store, "test-wxxz", source);
        Require(environment.Visibility == "private" && environment.ContextConversationId != "test-wxxz", "WXXZ 私密历史独立于 QQ 根会话");
        store.SaveMoment(new MomentRecord { Id = Guid.NewGuid().ToString("N"), ConversationId = "test-wxxz", Role = "user", Content = "QQ_PRIVATE_SENTINEL", CreatedUnixMs = 1 });
        var llm = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"这是一条合成测试回复，开发板主链已经连接。\"}");
        var kernel = new KernelLogic(store, llm, manager);
        var result = await kernel.ProcessPluginEventAsync("test-wxxz", source, 12);
        Require(result.Reply.Contains("合成测试回复"), "同一 Kernel/Agent 产生 WXXZ 回复");
        Require(llm.Requests.All(x => x.Contains("WXXZ 现场场景") && !x.Contains("QQ_PRIVATE_SENTINEL")), "共用 Prompt 装配并隔离 QQ 原文");
        var moments = store.GetRecentMoments(environment.ContextConversationId, 10);
        Require(moments.Count(x => x.SourcePluginId == "wxxz" && x.Content.Contains("合成测试回复")) == 1, "显示与播音只产生一个语义回复");
        Require(store.GetRecentOperationalEvents(environment.ContextConversationId, 10).Any(x => x.SourcePluginId == "wxxz"), "语音有独立运行回执");
        var status = System.Text.Json.JsonSerializer.Serialize(board.Status());
        if (hardware)
        {
            Console.WriteLine(status);
            Require(status.Contains("\"audio\":\"completed\""), "实机必须收到语音完成回执，不能把部分成功当全部通过");
            Console.WriteLine("WXXZ hardware Kernel/Agent/display/speech receipts passed (synthetic model; not human sight/hearing)."); return;
        }
        Require(transport.Paths.SequenceEqual(new[] { "/api/status", "/api/text", "/api/speak" }), "明确发言直接进入，无 JEV 请求");
        var momentCount = store.GetRecentMoments(environment.ContextConversationId, 100).Count;
        var heard = System.Text.Json.JsonSerializer.SerializeToElement(await board.ListenAsync(board.SessionId, CancellationToken.None));
        Require(store.GetRecentMoments(environment.ContextConversationId, 100).Count == momentCount, "听写预览不自动进入 Moment");
        var captureId = heard.GetProperty("captureId").GetString();
        try { board.Receive(board.SessionId, Guid.NewGuid().ToString(), "伪造转写", "测试者", "test-wxxz", captureId); throw new Exception("accepted altered transcript"); }
        catch (InvalidOperationException) { }
        var spoken = board.Receive(board.SessionId, Guid.NewGuid().ToString(), "本地听写样本", "测试者", "test-wxxz", captureId);
        Require(spoken.Organ == BodyOrganValues.Voice && spoken.PayloadJson.Contains("board_final_transcript"), "已确认板载听写保留真实来源");
        var heardLlm = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"语音标签测试回复\"}");
        await new KernelLogic(store, heardLlm, manager).ProcessPluginEventAsync("test-wxxz", spoken, 12);
        var heardMoment = store.GetRecentMoments(environment.ContextConversationId, 100).Single(x => x.SourceEventId == spoken.ExternalEventId);
        Require(heardMoment.Content == "本地听写样本" && heardMoment.Type == MomentTypes.Hearing &&
            heardMoment.SourcePlatform == "wxxz" && heardMoment.SourcePluginId == "wxxz" &&
            heardLlm.Requests.Any(x => x.Contains("【我听见】") && x.Contains("本地听写样本")),
            "原始听写验证后以听觉类型落库，正文保真，由Prompt生成听觉标签");
        try { board.Receive(board.SessionId, Guid.NewGuid().ToString(), "本地听写样本", "测试者", "test-wxxz", captureId); throw new Exception("accepted reused capture"); }
        catch (InvalidOperationException) { }
        var turn = new TraceTurnContext(environment.ContextConversationId, moments.Last(), moments, 12, true, services, environment);
        foreach (var organ in new[] { BodyOrganValues.Text, BodyOrganValues.Voice, BodyOrganValues.Image, BodyOrganValues.Video, BodyOrganValues.Qzone })
        {
            var qq = new TraceContributionDescriptorData { Id = "qq.check." + organ, Kind = TraceContributionKindValues.Effector, BodyId = "onebot", Organ = organ };
            Require(!EnvironmentLogic.CanUse(turn, qq), "WXXZ 禁止跨 QQ 器官：" + organ);
        }
        var qqSource = new PluginEventData { PluginId = "builtin.onebot", Environment = new EnvironmentObservationData
            { SessionId = "qq", SessionType = "group", SpeakerId = "unknown" }, Role = "user" };
        var qqEnvironment = EnvironmentLogic.Resolve(store, "test-wxxz", qqSource);
        var qqTurn = new TraceTurnContext("qq", new MomentRecord(), new List<MomentRecord>(), 0, true, services, qqEnvironment);
        Require(!EnvironmentLogic.CanUse(qqTurn, manager.GetEnabledCatalog().Single(x => x.Id == "wxxz.send")), "QQ 不可向 WXXZ 输出");
        var oldSession = board.SessionId;
        await board.StartAsync(true, CancellationToken.None);
        Require(!manager.GetAvailableCatalog(turn).Any(x => x.Id == "wxxz.send"), "重连后旧轮不投递到新会话");
        try { board.Receive(oldSession, Guid.NewGuid().ToString(), "旧发言", "测试者", "test-wxxz"); throw new Exception("accepted stale session"); }
        catch (InvalidOperationException) { }
        var requestId = Guid.NewGuid().ToString();
        board.Receive(board.SessionId, requestId, "新发言", "测试者", "test-wxxz");
        try { board.Receive(board.SessionId, requestId, "重复发言", "测试者", "test-wxxz"); throw new Exception("accepted duplicate"); }
        catch (InvalidOperationException) { }
        async Task CheckFailure(bool textFailure)
        {
            transport.FailText = textFailure; transport.FailVoice = !textFailure;
            var input = board.Receive(board.SessionId, Guid.NewGuid().ToString(), "故障合成样本", "测试者", "test-wxxz");
            var scope = EnvironmentLogic.Resolve(store, "test-wxxz", input).ContextConversationId;
            var failureKernel = new KernelLogic(store, new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"故障样本回复\"}"), manager);
            try { await failureKernel.ProcessPluginEventAsync("test-wxxz", input, 12); }
            catch (InvalidOperationException) when (textFailure) { }
            var count = store.GetRecentMoments(scope, 10).Count(x => x.Content == "故障样本回复");
            Require(count == (textFailure ? 0 : 1), "文字失败不记表达；语音失败仍保留实际显示文字");
        }
        await CheckFailure(true);
        await CheckFailure(false);
        Console.WriteLine("WXXZ checks passed: Kernel/Agent, scene/history, source-bound outputs, stale sessions, dedup, partial delivery.");
    }

    private sealed class SpeechTransport : HttpMessageHandler
    {
        public int Count;
        public string Voice, Model, Key, Host, CacheRequest, CacheResponse;
        public System.Net.HttpStatusCode Code = HttpStatusCode.OK;
        public bool InvalidAudio;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++; Host=request.RequestUri.Host; Key=request.Headers.Authorization?.Parameter;
            CacheRequest=request.Headers.TryGetValues("X-TraceSoul-Wake-Cache",out var tags) ? string.Join("",tags) : null;
            if(request.RequestUri.AbsolutePath.EndsWith("/auth/ticket",StringComparison.Ordinal))
                return new HttpResponseMessage(Code){Content=new StringContent("{\"ticket\":\""+new string('A',43)+"\"}")};
            using var body=System.Text.Json.JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            Voice=body.RootElement.GetProperty("voice").GetString(); Model=body.RootElement.GetProperty("model").GetString();
            var bytes=new byte[64];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes,0); Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes,8);
            var response=new HttpResponseMessage(Code) { Content=new ByteArrayContent(InvalidAudio ? new byte[64] : bytes) };
            if(CacheResponse!=null) response.Headers.Add("X-TraceSoul-Wake-Cache",CacheResponse);
            response.Headers.Add("X-Synthesis-Ms",CacheResponse=="hit"?"0":"100");
            response.Headers.Add("X-Audio-Ms","1000");
            return response;
        }
    }

    private static async Task CheckWxxzSpeechAsync()
    {
        var directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"wxxz-speech-"+Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var settingsPath=System.IO.Path.Combine(directory,"speech.json");
        var remotePath=System.IO.Path.Combine(directory,"remote.json");
        var localPath=System.IO.Path.Combine(directory,"local-voice-client.json");
        var localKey=new string('L',64);
        System.IO.File.WriteAllText(localPath,System.Text.Json.JsonSerializer.Serialize(new {
            api_url="http://127.0.0.1:5088/v1/audio/speech",api_key=localKey }));
        System.IO.File.WriteAllText(remotePath,System.Text.Json.JsonSerializer.Serialize(new {
            api_url="https://speech.example.com/v1/audio/speech",api_key="legacy-test-token",model="voice-2-hd",voice_id="test-voice" }));
        SpeechTransport transport=null;
        var client=new WxxzSpeech(settingsPath,remotePath,()=>transport=new SpeechTransport());
        try
        {
            Require(await client.SynthesizeAsync("系统测试",CancellationToken.None)==null && transport==null,"原系统音色不调用远端");
            client.Save(new WxxzSpeech.Settings {engine="remote"});
            var wav=await client.SynthesizeAsync("角色语音测试",CancellationToken.None);
            Require(wav.Length==64 && transport.Count==1 && transport.Host=="speech.example.com" && transport.Key=="legacy-test-token" &&
                transport.Model=="voice-2-hd" && transport.Voice=="test-voice","TTS接口使用成对的地址凭据和既有模型音色");
            Require(!System.Text.Json.JsonSerializer.Serialize(client.Public()).Contains("legacy-test-token") &&
                !System.IO.File.ReadAllText(settingsPath).Contains("legacy-test-token"),"公开状态及WXXZ设置均不复制凭据");
            Require(new WxxzSpeech(settingsPath,remotePath).Description=="远端角色语音","音色选择独立保存并重载");
            var remoteWake=await client.SynthesizeWakeAsync("我在",CancellationToken.None);
            Require(remoteWake.Cache=="unsupported" && transport.CacheRequest==null,"远端固定应答不请求缓存或提前消耗远端额度");
            var failed=new WxxzSpeech(settingsPath,remotePath,()=>transport=new SpeechTransport {Code=HttpStatusCode.Unauthorized});
            try {await failed.SynthesizeAsync("失败测试",CancellationToken.None);throw new Exception("TTS 401 accepted");}
            catch(InvalidOperationException e){Require(e.Message.Contains("401") && transport.Count==1,"失败不重试或偷偷换声音");}
            var malformed=new WxxzSpeech(settingsPath,remotePath,()=>transport=new SpeechTransport {InvalidAudio=true});
            try {await malformed.SynthesizeAsync("格式测试",CancellationToken.None);throw new Exception("non-WAV accepted");}
            catch(InvalidOperationException e){Require(e.Message.Contains("WAV"),"非WAV结果不传给板子");}
            client.Save(new WxxzSpeech.Settings {engine="qwen_local",localVoice="demo"});
            await client.SynthesizeAsync("本地固定测试",CancellationToken.None);
            Require(transport.Host=="127.0.0.1" && transport.Key==localKey && transport.Model=="qwen3-tts-local" && transport.Voice=="demo",
                "本地千问使用私有接口自身密钥和显式音色，不携带远端凭据");
            Require(transport.CacheRequest==null,"普通对话不请求持久语音缓存");
            var legacyWake=await client.SynthesizeWakeAsync("我在",CancellationToken.None);
            Require(transport.CacheRequest=="v1" && legacyWake.Cache=="unsupported","旧服务正常音频可用，但未返回协议时不宣称缓存成功");
            var cachedClient=new WxxzSpeech(settingsPath,remotePath,()=>transport=new SpeechTransport{CacheResponse="hit"});
            var cached=await cachedClient.SynthesizeWakeAsync("我在",CancellationToken.None);
            Require(cached.Cache=="hit" && cached.SynthesisMs==0 && cached.AudioMs==1000 && cached.Audio.Length==64 && transport.Key==localKey,
                "唤醒专用请求携带原密钥，并读回实际缓存与耗时");
            var missClient=new WxxzSpeech(settingsPath,remotePath,()=>transport=new SpeechTransport{CacheResponse="miss"});
            Require((await missClient.SynthesizeWakeAsync("我在",CancellationToken.None)).Cache=="miss","新生成与命中明确区分");
            Require(!System.Text.Json.JsonSerializer.Serialize(client.Public()).Contains(localKey) &&
                !System.IO.File.ReadAllText(settingsPath).Contains(localKey),"本地Key不进入公开状态和普通音色设置");
            var link=await client.WorkbenchAsync(CancellationToken.None);
            Require(transport.Key==localKey && link=="http://127.0.0.1:5088/auth/launch?ticket="+new string('A',43) &&
                !link.Contains(localKey),"工作台只向浏览器交付一次性票据，不交付长期Key");
            var beforeMissing=transport;
            System.IO.File.Move(localPath,localPath+".saved");
            try {await client.SynthesizeAsync("缺少密钥",CancellationToken.None);throw new Exception("missing key accepted");}
            catch(InvalidOperationException){Require(ReferenceEquals(beforeMissing,transport),"本地Key缺失时请求前拒绝，不匿名调用");}
            System.IO.File.Move(localPath+".saved",localPath);
            var private401=new WxxzSpeech(settingsPath,remotePath,()=>transport=new SpeechTransport{Code=HttpStatusCode.Unauthorized});
            try {await private401.SynthesizeAsync("密钥失败",CancellationToken.None);throw new Exception("private 401 accepted");}
            catch(InvalidOperationException e){Require(e.Message.Contains("401") && transport.Count==1 && !e.Message.Contains(localKey),"私有接口401不重试、不泄露Key");}
            foreach(var url in new[]{"http://voice.example/v1/audio/speech","https://voice.example/v1/audio/speech?key=secret"})
            {
                System.IO.File.WriteAllText(localPath,System.Text.Json.JsonSerializer.Serialize(new{api_url=url,api_key=localKey}));
                beforeMissing=transport;
                try {await client.SynthesizeAsync("不安全地址",CancellationToken.None);throw new Exception("insecure URL accepted");}
                catch(InvalidOperationException){Require(ReferenceEquals(beforeMissing,transport),"远端明文HTTP和查询参数在请求前拒绝");}
            }
            System.IO.File.WriteAllText(localPath,System.Text.Json.JsonSerializer.Serialize(new{api_url="http://127.0.0.1:5088/v1/audio/speech",api_key=localKey}));
            var before=transport;
            try {await client.SynthesizeAsync(new string('字',241),CancellationToken.None);throw new Exception("truncated text accepted");}
            catch(InvalidOperationException){Require(ReferenceEquals(before,transport),"超长文字在请求前拒绝，不静默截断");}
            using var store=new SqliteMemoryManager(":memory:");
            using var manager=new TracePluginManager(store,new TracePluginServices(store,new HierarchicalVectorRouterLogic(new FakeEncoder())));
            var mockBoard=new WxxzMockBridge();
            using var board=new WxxzPlatform(mockBoard,speech:cachedClient); manager.RegisterExternal(board);
            var receipt=System.Text.Json.JsonSerializer.SerializeToElement(await board.PrepareWakeAudioAsync(CancellationToken.None));
            Require(receipt.GetProperty("cache").GetString()=="hit" && mockBoard.Paths.Count==0 && board.SessionId==null,
                "未连接板子即可预生成，且不请求任何USB操作");
            Require(!System.Text.Json.JsonSerializer.Serialize(board.Status()).Contains(localKey),"缓存诊断不含密钥或音频正文");
            await board.StartAsync(false,CancellationToken.None);
            await board.TestSpeechAsync(board.SessionId,CancellationToken.None);
            Require(store.GetRecentMoments("test",100).Count==0,"固定音色试听不提交Moment或主模型");
        }
        finally {System.IO.Directory.Delete(directory,true);}
    }

    private static void CheckWxxzVoiceprint()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wxxz-voice-" + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "owner.json");
        System.Text.Json.JsonElement Evidence(params int[] indices)
        {
            var windows = indices.Select(index => new { speech_ms = 2000,
                embedding = Enumerable.Range(0, 192).Select(i => i == index ? 1.0 : 0.0).ToArray() }).ToArray();
            return System.Text.Json.JsonSerializer.SerializeToElement(new { model = WxxzVoiceprint.Model,
                speech_ms = 4000, embedding = windows[0].embedding, windows });
        }
        try
        {
            var voice = new WxxzVoiceprint(path);
            Require(!voice.Match(Evidence(0,0)).matched, "未录入不能匹配");
            voice.Add(Evidence(0,0), true); voice.Add(Evidence(0,0), false);
            Require(!System.IO.File.Exists(path), "三段完成前不保存不完整声纹");
            try { voice.Add(Evidence(1,1), false); throw new Exception("mixed enrollment accepted"); }
            catch (InvalidOperationException) { }
            voice.Add(Evidence(0,0), false);
            Require(voice.Match(Evidence(0,0)).matched, "三段完成后本人匹配");
            Require(voice.Match(Evidence(0,1)).matched, "整句匹配时短窗异常仅诊断，不再逐窗否决");
            var diagnostic = System.Text.Json.JsonSerializer.SerializeToElement(voice.Inspect(Evidence(0,1)));
            Require(diagnostic.GetProperty("wholeScore").GetDouble()==1 && diagnostic.GetProperty("score").GetDouble()==1 &&
                diagnostic.GetProperty("minimumWindowScore").GetDouble()==0 && diagnostic.GetProperty("matched").GetBoolean() &&
                diagnostic.GetProperty("decisionBasis").GetString()=="whole_utterance", "诊断按整句判定，最低短窗仅单列统计");
            var contradictory = System.Text.Json.JsonSerializer.SerializeToElement(new { model=WxxzVoiceprint.Model,
                speech_ms=4000, embedding=Enumerable.Range(0,192).Select(i=>i==1?1.0:0.0).ToArray(),
                windows=Evidence(0,0).GetProperty("windows") });
            Require(!voice.Match(contradictory).matched, "短窗相似不能放行整句不匹配的声音");
            var boundary = System.Text.Json.JsonSerializer.SerializeToElement(new { model=WxxzVoiceprint.Model,
                speech_ms=4000, embedding=Enumerable.Range(0,192).Select(i=>i==0?0.64996:i==1?Math.Sqrt(1-0.64996*0.64996):0).ToArray(),
                windows=Evidence(0,0).GetProperty("windows") });
            Require(!voice.Match(boundary).matched && !System.Text.Json.JsonSerializer.SerializeToElement(voice.Inspect(boundary))
                .GetProperty("matched").GetBoolean(), "整句诊断与通话均在舍入前比较门槛");
            Require(!diagnostic.ToString().Contains("embedding"), "诊断不向页面返回声纹特征");
            Require(new WxxzVoiceprint(path).IsEnrolled, "角色声纹重载保留");
            var reloaded = new WxxzVoiceprint(path);
            reloaded.Add(Evidence(0,0), false, 2, "第二段新的听写");
            var slots = System.Text.Json.JsonSerializer.SerializeToElement(new WxxzVoiceprint(path).Status()).GetProperty("slots");
            Require(slots[1].GetProperty("text").GetString()=="第二段新的听写" &&
                slots[0].GetProperty("text").GetString()=="" && slots[2].GetProperty("accepted").GetBoolean(),
                "重载后可单独替换第二段，其他两段保留，听写持久保存");
            var unchanged = System.IO.File.ReadAllText(path);
            try { reloaded.Add(Evidence(1,1), false, 2, "不应替换的听写"); throw new Exception("bad replacement accepted"); }
            catch (InvalidOperationException) { }
            Require(System.IO.File.ReadAllText(path)==unchanged && reloaded.Match(Evidence(0,0)).matched,
                "单段重录不通过时保留原文件和已生效声纹");
            var safeStatus = System.Text.Json.JsonSerializer.Serialize(reloaded.Status());
            Require(!safeStatus.Contains("embedding") && !safeStatus.Contains("windows"), "分段管理只返回时长听写，不泄露特征");
            try { reloaded.Add(Evidence(0,0), false, 4); throw new Exception("invalid slot accepted"); }
            catch (InvalidOperationException) { }
            // Old centroid-only profiles remain usable while the new per-slot set is collected.
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { model=WxxzVoiceprint.Model,
                sample_count=3, speech_ms=12000, embedding=Enumerable.Range(0,192).Select(i=>i==0?1.0:0.0).ToArray() }));
            var legacy = new WxxzVoiceprint(path);
            legacy.Add(Evidence(0,0), false, 3, "第三段");
            Require(legacy.IsEnrolled && legacy.Match(Evidence(0,0)).matched &&
                System.Text.Json.JsonSerializer.SerializeToElement(legacy.Status()).GetProperty("samples").GetInt32()==1,
                "旧版声纹在新三段完成前继续生效，允许任意段先录");
            legacy.Add(Evidence(0,0), false, 1, "第一段");
            legacy.Add(Evidence(0,0), false, 2, "第二段");
            Require(System.Text.Json.JsonSerializer.SerializeToElement(new WxxzVoiceprint(path).Status()).GetProperty("slots")[2]
                .GetProperty("text").GetString()=="第三段", "任意顺序完成三段后可重载独立样本");
            voice.ClearPending();
            Require(voice.IsEnrolled, "清理未完成录入不删已保存声纹");
            var naturalWindows = new[]
            {
                new { speech_ms = 2000, embedding = Enumerable.Range(0,192).Select(i => i==0 ? 0.62 : i==1 ? Math.Sqrt(1-0.62*0.62) : 0).ToArray() },
                new { speech_ms = 2000, embedding = Enumerable.Range(0,192).Select(i => i==0 ? 0.62 : i==1 ? -Math.Sqrt(1-0.62*0.62) : 0).ToArray() }
            };
            var natural = System.Text.Json.JsonSerializer.SerializeToElement(new { model = WxxzVoiceprint.Model,
                speech_ms = 4000, embedding = Enumerable.Range(0,192).Select(i=>i==0?1.0:0.0).ToArray(), windows = naturalWindows });
            voice.Add(natural, true);
            voice.Add(natural, false); voice.Add(natural, false);
            Require(voice.Match(natural).matched && new WxxzVoiceprint(path).IsEnrolled,
                "整句一致而短窗0.62的三段可完成录入并按整句匹配");
            Require(!voice.Match(Evidence(1,1)).matched, "录入容忍音素变化，陌生人匹配门槛仍不降低");
            var naturalProfile = System.IO.File.ReadAllText(path);
            try { voice.Add(Evidence(1,1), false, 2); throw new Exception("different speaker clip accepted"); }
            catch (InvalidOperationException) { }
            Require(System.IO.File.ReadAllText(path)==naturalProfile, "整句模式仍拒绝不一致的完整录入段且保留原档");
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory);
        }
    }

    private static async Task CheckWxxzCallLifecycleAsync()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wxxz-call-" + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "owner.json");
        string Evidence(int index) => System.Text.Json.JsonSerializer.Serialize(new { model = WxxzVoiceprint.Model,
            speech_ms = 4000, embedding = Enumerable.Range(0,192).Select(i=>i==index?1.0:0.0).ToArray(), windows = Enumerable.Range(0, 2).Select(_ => new { speech_ms = 2000,
                embedding = Enumerable.Range(0, 192).Select(i => i == index ? 1.0 : 0.0).ToArray() }).ToArray() });
        try
        {
            var profile = new WxxzVoiceprint(path);
            var enrollment = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(Evidence(0));
            for (var i = 0; i < 3; i++) profile.Add(enrollment, i == 0);
            using var store = new SqliteMemoryManager(":memory:");
            var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
            using var manager = new TracePluginManager(store, services);
            var transport = new WxxzMockBridge { UtteranceVoice = Evidence(1) };
            using var board = new WxxzPlatform(transport, path); manager.RegisterExternal(board);
            await board.StartAsync(false, CancellationToken.None);
            var profileBefore = System.IO.File.ReadAllText(path);
            var check = System.Text.Json.JsonSerializer.SerializeToElement(await board.CheckVoiceAsync(board.SessionId, Guid.NewGuid().ToString("N"), CancellationToken.None));
            Require(check.GetProperty("diagnostic").GetProperty("score").GetDouble()==0 && !check.TryGetProperty("captureId",out _) &&
                !check.ToString().Contains("embedding"), "独立声纹诊断只返回统计，不提供对话准入凭据");
            Require(System.IO.File.ReadAllText(path)==profileBefore && store.GetRecentMoments("call-test",100).Count==0,
                "诊断不改档案也不创建Moment");
            var idle = System.Text.Json.JsonSerializer.SerializeToElement(await board.CheckVoiceStateAsync(board.SessionId, "wrong-id", CancellationToken.None));
            Require(idle.GetProperty("phase").GetString()=="inactive", "进度只查询当前匹配的诊断编号");
            var rejectedReplacement = System.Text.Json.JsonSerializer.SerializeToElement(await board.EnrollAsync(board.SessionId, false,
                CancellationToken.None, 2, Guid.NewGuid().ToString("N")));
            Require(!rejectedReplacement.GetProperty("accepted").GetBoolean() && System.IO.File.ReadAllText(path)==profileBefore,
                "录入接口单段不一致时保留原声纹");
            transport.UtteranceVoice = Evidence(0);
            var replacement = System.Text.Json.JsonSerializer.SerializeToElement(await board.EnrollAsync(board.SessionId, false,
                CancellationToken.None, 2, Guid.NewGuid().ToString("N")));
            Require(replacement.GetProperty("accepted").GetBoolean() && replacement.GetProperty("voiceprint").GetProperty("slots")[1]
                .GetProperty("text").GetString()=="本段本地听写" && !replacement.TryGetProperty("captureId",out _) &&
                !replacement.ToString().Contains("embedding") && store.GetRecentMoments("call-test",100).Count==0,
                "逐段听写取自本地桥，单段录入不返回特征或对话准入凭据、不创建Moment");
            transport.UtteranceVoice = Evidence(1);
            async Task<string> Start() => System.Text.Json.JsonSerializer.SerializeToElement(
                await board.StartCallAsync(board.SessionId, CancellationToken.None)).GetProperty("callId").GetString();
            var callId = await Start();
            try {board.ConfigureSpeech(new WxxzSpeech.Settings {engine="remote"});throw new Exception("speech changed during call");}
            catch(InvalidOperationException e){Require(e.Message.Contains("结束通话"),"服务端也拒绝在通话中切换音色");}
            var unknown = System.Text.Json.JsonSerializer.SerializeToElement(await board.ListenCallAsync(board.SessionId, callId, CancellationToken.None));
            Require(unknown.GetProperty("text").GetString() == "" && unknown.GetProperty("status").GetString() == "speaker_unconfirmed", "未知声音不取得准入文字或凭据");
            transport.UtteranceVoice = Evidence(0);
            var heard = System.Text.Json.JsonSerializer.SerializeToElement(await board.ListenCallAsync(board.SessionId, callId, CancellationToken.None));
            await board.StopCallAsync(callId, CancellationToken.None);
            try { board.Receive(board.SessionId, Guid.NewGuid().ToString(), heard.GetProperty("text").GetString(), "测试者", "call-test", heard.GetProperty("captureId").GetString()); throw new Exception("stopped capture accepted"); }
            catch (InvalidOperationException) { }
            callId = await Start(); transport.BlockUtterance = true;
            var listening = board.ListenCallAsync(board.SessionId, callId, CancellationToken.None);
            await board.StopCallAsync(callId, CancellationToken.None);
            var cancelled = System.Text.Json.JsonSerializer.SerializeToElement(await listening);
            Require(cancelled.GetProperty("status").GetString() == "cancelled", "采音未返回时结束不等待主体锁，迟到结果作废");
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory);
        }
    }
}
