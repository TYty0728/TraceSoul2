using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Host;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Logic;

internal static partial class Program
{
    private static void CheckWxxzAttention()
    {
        var now=DateTimeOffset.UtcNow;
        var policy=new WxxzAttention(null,()=>"小汐",()=>now);
        foreach(var sample in new[]{"小溪","小溪你好","你好，小溪。","小溪，帮我看看。"})
            Require(policy.Correct(sample).Changed && policy.Correct(sample).Mentioned,"称呼变体校正："+sample);
        Require(policy.Configuration().pronunciation=="xiao xi" && policy.Configuration().aliases.Length==0,"自动生成读音，不依赖同音枚举");
        foreach(var sample in new[]{"小希你好","小西，帮我看看。","小兮在吗","小熙今天下雨了吗","晓曦","小 西你好","xiao xi","XIAO XI你好"})
            Require(policy.Correct(sample).Text.Contains("小汐") && policy.Correct(sample).MatchMode=="phonetic","未枚举的同音字按读音归一："+sample);
        foreach(var sample in new[]{"小旭你好","小新你好","xxiao xi","xiao xing","xiao xiang","小，希你好"})
            Require(!policy.Correct(sample).Mentioned,"不同读音、词边界或跨标点不匹配："+sample);
        foreach(var sample in new[]{"河边有一条小溪","沿着小溪走","小溪里有鱼","小溪很清澈"})
            Require(!policy.Correct(sample).Changed && !policy.Correct(sample).Mentioned,"普通名词不改："+sample);
        Require(policy.Correct("小溪在吗").WakeOnly && !policy.Correct("小溪帮我看看").WakeOnly,"纯唤醒与实际指令分开");
        policy.Save(new(){name="阿循",aliases=new[]{"阿旭","阿寻"}});
        Require(policy.Correct("阿旭你好").Text=="阿循你好" && policy.Correct("阿寻，听我说").Text=="阿循，听我说","阿循自定义变体");
        policy.Save(new(){name="阿循",aliases=Array.Empty<string>()});
        Require(policy.Correct("阿寻你好").Text=="阿循你好" && policy.Correct("a xun").Text=="阿循" && !policy.Correct("阿旭你好").Mentioned,"a xun不等于a xu，不自动扩大近音范围");
        policy.Save(new(){name="行行",pronunciation="hang hang",aliases=Array.Empty<string>()});
        Require(policy.Correct("杭 航你好").Text=="行行你好","可手动指定多音字，重复音节正确处理空格");
        policy.Save(new(){name="小汐",pronunciation="xiǎo xī",aliases=Array.Empty<string>()});
        Require(policy.Configuration().pronunciation=="xiao xi" && policy.Correct("小希").Text=="小汐","拼音声调归一");
        policy.Save(new(){name="小汐",pronunciation="xiao3 xi1",aliases=Array.Empty<string>()});
        Require(policy.Configuration().pronunciation=="xiao xi" && policy.Correct("晓曦").Text=="小汐","数字声调归一");
        policy.Save(new(){name="吕吕",pronunciation="lǚ lü",aliases=Array.Empty<string>()});
        Require(policy.Configuration().pronunciation=="lv lv" && policy.Correct("旅 侣你好").Text=="吕吕你好","ü保留元音区别，不误并为u");
        policy.Save(new(){name="小汐",phonetic=false,aliases=Array.Empty<string>()});
        Require(!policy.Correct("小溪你好").Changed,"空变体配置不恢复默认");
        policy.Save(new(){name="Ann",aliases=new[]{"An"}});
        Require(policy.Correct("An").Text=="Ann" && policy.Correct("Ann").Text=="Ann" && policy.Correct("Anna").Text=="Anna","只替换一次且英文遵守词边界");
        policy.Start(); Require(!policy.FocusedAt(now),"监听开始处于发呆");
        policy.Complete(); now=now.AddSeconds(29); var onset=now;
        Require(policy.FocusedAt(now),"29秒仍专注"); now=now.AddSeconds(2);
        Require(!policy.FocusedAt(now) && policy.FocusedAt(onset),"30秒过期，截止前开始的完整发言仍可接收");
        policy.Busy(); now=now.AddMinutes(2); Require(policy.FocusedAt(now),"模型回复期间暂停过期");
        policy.Complete(); now=now.AddSeconds(29); Require(policy.FocusedAt(now),"回复结束重新计时");
        policy.Stop(); Require(!policy.FocusedAt(now),"停止即撤销专注");
        var path=Path.Combine(Path.GetTempPath(),"wake-"+Guid.NewGuid().ToString("N"),"wake.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,"{\"enabled\":true,\"name\":\"小汐\",\"aliases\":[],\"reply\":\"我在。\"}");
            Require(new WxxzAttention(path,()=>"小汐").Correct("小希").Text=="小汐","旧配置缺少读音字段时兼容启用自动拼音");
            var persistent=new WxxzAttention(path,()=>"小汐");
            persistent.Save(new(){name="阿循",aliases=new[]{"阿旭","阿寻"},reply="我在。"});
            Require(new WxxzAttention(path,()=>"小汐").Correct("阿寻").Text=="阿循","名字配置可重载");
            var before=File.ReadAllText(path);
            try { persistent.Save(new(){name="错误 名字"}); throw new Exception("invalid settings accepted"); } catch(ArgumentException) { }
            try { persistent.Save(new(){name="阿循",pronunciation="a.* xun"}); throw new Exception("invalid pronunciation accepted"); } catch(ArgumentException) { }
            Require(File.ReadAllText(path)==before,"无效设置不覆盖文件");
            persistent.Save(new(){name="行行",pronunciation="hang hang",phonetic=true});
            Require(new WxxzAttention(path,()=>"小汐").Correct("航航").Text=="行行","手动多音读法可持久保存");
            persistent.Save(new(){name="小汐",phonetic=false,aliases=Array.Empty<string>()});
            Require(!new WxxzAttention(path,()=>"小汐").Correct("小希").Mentioned,"关闭读音匹配可持久保存");
        }
        finally { if(Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path),true); }
    }

    private static async Task CheckWxxzWakeAsync()
    {
        var directory=Path.Combine(Path.GetTempPath(),"wake-call-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(directory,"owner.json");
        object Evidence(int index)=>new {model=WxxzVoiceprint.Model,speech_ms=4000,
            embedding=Enumerable.Range(0,192).Select(i=>i==index?1.0:0.0).ToArray(),
            windows=Enumerable.Range(0,2).Select(_=>new{speech_ms=2000,embedding=Enumerable.Range(0,192).Select(i=>i==index?1.0:0.0).ToArray()}).ToArray()};
        try
        {
            var profile=new WxxzVoiceprint(path);
            for(var i=0;i<3;i++) profile.Add(JsonSerializer.SerializeToElement(Evidence(0)),i==0);
            var now=DateTimeOffset.UtcNow;
            using var store=new SqliteMemoryManager(":memory:");
            var services=new TracePluginServices(store,new HierarchicalVectorRouterLogic(new FakeEncoder()));
            using var manager=new TracePluginManager(store,services);
            var transport=new WxxzMockBridge{UtteranceVoice=JsonSerializer.Serialize(Evidence(0)),UtteranceText="今天怎么样"};
            var syntheses=new List<SpeechTransport>();
            var speech=WxxzSpeech.ForProvider(()=>new LlmProviderRecord{type="qwen_tts",baseUrl="https://voice.example/v1",
                apiKey=new string('K',64),model="qwen3-tts-local",voice="character"},()=>{
                    var handler=new SpeechTransport{CacheResponse="hit"};syntheses.Add(handler);return handler;
                });
            using var board=new WxxzPlatform(transport,path,speech,clock:()=>now); manager.RegisterExternal(board);
            board.ConfigureWake(new(){name="小汐",aliases=new[]{"小溪"}});
            var pathsBefore=transport.Paths.Count;
            var preview=JsonSerializer.SerializeToElement(board.PreviewAttention("小希",false,"short"));
            Require(preview.GetProperty("simulated").GetBoolean() && transport.Paths.Count==pathsBefore,
                "平台沙盘不请求USB桥，不采音或播音");
            await board.StartAsync(false,CancellationToken.None);
            var start=JsonSerializer.SerializeToElement(await board.StartCallAsync(board.SessionId,CancellationToken.None,true));
            var call=start.GetProperty("callId").GetString();
            async Task<JsonElement> Listen()=>JsonSerializer.SerializeToElement(await board.ListenCallAsync(board.SessionId,call,CancellationToken.None));
            var idle=await Listen();
            Require(idle.GetProperty("status").GetString()=="standby"&&!idle.TryGetProperty("captureId",out _),"未叫名字的环境语音不准入");
            try {board.ConfigureWake(new(){name="阿循"});throw new Exception("settings changed during wake");} catch(InvalidOperationException) { }
            transport.UtteranceVoice="null"; transport.UtteranceText="小希";
            var wake=await Listen();
            Require(wake.GetProperty("status").GetString()=="wake_ack"&&!wake.TryGetProperty("captureId",out _) &&
                transport.Paths.Count(x=>x=="/api/speak")==1,"不足声纹长度的短名字仍应答但不取得对话凭据");
            Require(syntheses.Count==1 && syntheses[0].CacheRequest=="v1" &&
                JsonSerializer.SerializeToElement(board.Status()).GetProperty("wakeAudio").GetProperty("lastUse").GetProperty("cache").GetString()=="hit",
                "真实唤醒分支使用缓存协议，且取音回执与设备播音分开记录");
            try {await board.PrepareWakeAudioAsync(CancellationToken.None);throw new Exception("prepared during call");}
            catch(InvalidOperationException) {Require(syntheses.Count==1,"通话期间额外预生成在合成前拒绝");}
            transport.UtteranceText="告诉我一个秘密";
            Require((await Listen()).GetProperty("status").GetString()=="speaker_unconfirmed","唤醒不放宽后续指令声纹");
            transport.UtteranceVoice=JsonSerializer.Serialize(Evidence(0)); transport.UtteranceText="小溪帮我看看";
            var heard=await Listen();
            Require(heard.GetProperty("text").GetString()=="小汐帮我看看","本人指令校正名字");
            var id=heard.GetProperty("captureId").GetString();
            var source=board.Receive(board.SessionId,Guid.NewGuid().ToString(),"小汐帮我看看","测试者","wake-test",id);
            var payload=JsonSerializer.Deserialize<JsonElement>(source.PayloadJson);
            Require(payload.GetProperty("raw_transcript").GetString()=="小溪帮我看看" && payload.GetProperty("name_corrected").GetBoolean(),"Moment载荷保留原听写，正文采用规范名");
            try {board.Receive(board.SessionId,Guid.NewGuid().ToString(),"小汐帮我看看","测试者","wake-test",id);throw new Exception("capture reused");} catch(InvalidOperationException) { }
            now=now.AddMinutes(2); board.CompleteResponse(); now=now.AddSeconds(31);
            transport.UtteranceText="今天怎么样";
            Require((await Listen()).GetProperty("status").GetString()=="standby","回复后30秒无新指令回到发呆");
            transport.UtteranceText="小溪帮我看看";
            var combined=await Listen(); Require(combined.GetProperty("status").GetString()=="verified" && transport.Paths.Count(x=>x=="/api/speak")==2,"名字连指令先应答再准入一次");
            await board.StopCallAsync(call,CancellationToken.None);
            Require(store.GetRecentMoments(source.Environment.SessionId,100).Count==0,"唤醒自身不写入对话Moment");
            var before=transport.Paths.Count(x=>x=="/api/speak");
            start=JsonSerializer.SerializeToElement(await board.StartCallAsync(board.SessionId,CancellationToken.None,true)); call=start.GetProperty("callId").GetString();
            transport.UtteranceText="小溪"; transport.FailVoice=true;
            try {await Listen();throw new Exception("failed ack accepted");} catch(InvalidOperationException) { }
            Require(transport.Paths.Count(x=>x=="/api/speak")==before+1,"唤醒播音失败不自动重试");
            await board.StopCallAsync(call,CancellationToken.None);
            Console.WriteLine("WXXZ name/attention checks passed: correction, short wake, owner gate, 30s lease, raw transcript, no wake Moment/retry.");
            start=JsonSerializer.SerializeToElement(await board.StartCallAsync(board.SessionId,CancellationToken.None,true,background:true));
            call=start.GetProperty("callId").GetString(); transport.FailVoice=false;transport.UtteranceText="小汐帮我看看";
            try {await board.ListenCallAsync(board.SessionId,call,CancellationToken.None);throw new Exception("browser drove managed call");} catch(InvalidOperationException) { }
            var managed=JsonSerializer.SerializeToElement(await board.CompleteCallAsync(
                await board.CaptureCallAsync(board.SessionId,call,CancellationToken.None,background:true),CancellationToken.None));
            var managedCapture=managed.GetProperty("captureId").GetString();
            try {board.Receive(board.SessionId,Guid.NewGuid().ToString(),managed.GetProperty("text").GetString(),"测试者","wake-test",managedCapture);
                throw new Exception("browser admitted managed transcript");} catch(InvalidOperationException) { }
            board.ManagedAdmissionAllowed=_=>false;
            try {board.Receive(board.SessionId,Guid.NewGuid().ToString(),managed.GetProperty("text").GetString(),"测试者","wake-test",managedCapture,call);
                throw new Exception("stopped monitor admission accepted");} catch(OperationCanceledException) { }
            board.ManagedAdmissionAllowed=_=>true;
            var managedSource=board.Receive(board.SessionId,Guid.NewGuid().ToString(),managed.GetProperty("text").GetString(),"测试者","wake-test",managedCapture,call);
            Require(managedSource.Type==TraceSoul2.Data.MomentTypes.Hearing && board.Diagnostics.Snapshot().Any(x=>x.admitted),
                "只有后台凭据和准入状态同时有效时才交给真实听觉主链");
            await board.StopCallAsync(call,CancellationToken.None);
        }
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
