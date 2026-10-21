using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Net.Http;
using TraceSoul2.Host;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Logic;

internal static partial class Program
{
    private static async Task CheckWxxzAudioInterlockAsync()
    {
        var directory=Path.Combine(Path.GetTempPath(),"wxxz-audio-lock-"+Guid.NewGuid().ToString("N"));
        try
        {
            var path=Path.Combine(directory,"owner.json");
            var embedding=Enumerable.Range(0,192).Select(i=>i==0?1.0:0.0).ToArray();
            var evidence=JsonSerializer.SerializeToElement(new {model=WxxzVoiceprint.Model,speech_ms=4000,embedding,
                windows=new[]{new{speech_ms=2000,embedding},new{speech_ms=2000,embedding}}});
            var profile=new WxxzVoiceprint(path);for(var i=0;i<3;i++)profile.Add(evidence,i==0);
            using var store=new SqliteMemoryManager(":memory:");
            store.SavePairIdentity("测试者","测试同伴","测试者");
            var services=new TracePluginServices(store,new HierarchicalVectorRouterLogic(new FakeEncoder()));
            using var plugins=new TracePluginManager(store,services);
            plugins.RegisterExternal(new DialogueTracePlugin());
            var bridge=new WxxzMockBridge{BlockUtterance=true};
            using var board=new WxxzPlatform(bridge,path);plugins.RegisterExternal(board);
            await board.StartAsync(false,CancellationToken.None);
            var source=board.Receive(board.SessionId,Guid.NewGuid().ToString(),"模拟已接收的现场发言","测试者","audio-lock");
            var started=JsonSerializer.SerializeToElement(await board.StartCallAsync(board.SessionId,CancellationToken.None,background:true));
            var call=started.GetProperty("callId").GetString();
            var read=board.CaptureCallAsync(board.SessionId,call,CancellationToken.None,background:true);
            var kernel=new KernelLogic(store,new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"交错音频测试回复\"}"),plugins);
            var reply=kernel.ProcessPluginEventAsync("audio-lock",source,12);
            await bridge.TextObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!bridge.Paths.Contains("/api/speak"),"后台释放主锁后，其他WXXZ输出仍不能在采音时播音");
            bridge.Pending.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(
                "{\"ok\":true,\"receipt\":{\"protocol\":1,\"status\":\"done\"},\"status\":\"no_speech\",\"text\":\"\",\"voice\":null}")});
            await read.WaitAsync(TimeSpan.FromSeconds(3));
            await reply.WaitAsync(TimeSpan.FromSeconds(3));
            Require(bridge.Paths.Count(x=>x=="/api/speak")==1,"采音结束才播音，且只播放一次");
            await board.StopCallAsync(call,CancellationToken.None);
            using var shutdownStore=new SqliteMemoryManager(":memory:");
            var shutdownServices=new TracePluginServices(shutdownStore,new HierarchicalVectorRouterLogic(new FakeEncoder()));
            using var shutdownPlugins=new TracePluginManager(shutdownStore,shutdownServices);
            var shutdownBridge=new WxxzMockBridge{BlockUtterance=true,UtteranceVoice="null"};
            using var shutdownBoard=new WxxzPlatform(shutdownBridge,path);shutdownPlugins.RegisterExternal(shutdownBoard);
            await shutdownBoard.StartAsync(false,CancellationToken.None);
            var last=JsonSerializer.SerializeToElement(await shutdownBoard.StartCallAsync(shutdownBoard.SessionId,CancellationToken.None,background:true));
            var closingCapture=shutdownBoard.CaptureCallAsync(shutdownBoard.SessionId,last.GetProperty("callId").GetString(),CancellationToken.None,background:true);
            shutdownBoard.Shutdown();
            var closingResult=JsonSerializer.SerializeToElement(await shutdownBoard.CompleteCallAsync(
                await closingCapture.WaitAsync(TimeSpan.FromSeconds(3)),CancellationToken.None));
            Require(closingResult.GetProperty("status").GetString()=="cancelled" && shutdownBridge.Paths.Contains("/api/listen/cancel"),
                "插件关闭会取消进行中的采音，迟到结果不准入");
            Console.WriteLine("WXXZ audio interlock checks passed: host capture does not overlap Kernel/Agent board speech.");
        }
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    private static JsonElement MonitorHeard(string status) => JsonSerializer.SerializeToElement(new {
        status,text=status=="verified" ? "模拟本人发言" : "",captureId=status=="verified" ? "verified-test" : null});
    private static async Task CheckWxxzMonitorAsync()
    {
        var input=Channel.CreateUnbounded<JsonElement>();
        var listening=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replied=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts=0;var replies=0;
        using(var monitor=new WxxzVoiceMonitor(
            (s,w,t)=>{starts++;return Task.FromResult("call");},
            async(s,c,t,life)=>{listening.TrySetResult(true);return await input.Reader.ReadAsync(life);},
            (s,c,text,capture,life)=>{replies++;replied.TrySetResult(true);return Task.FromResult(true);},
            c=>{input.Writer.TryWrite(MonitorHeard("verified"));return Task.CompletedTask;},
            (phase,code,reason)=>{}))
        {
            Require(!monitor.IsActive && starts==0,"构造后台服务不会自行采音");
            using var browser=new CancellationTokenSource();
            var started=await monitor.StartAsync("session",true,browser.Token);
            await listening.Task.WaitAsync(TimeSpan.FromSeconds(3));
            browser.Cancel();
            Require(monitor.IsActive,"网页请求结束不撤销后台监听");
            try {await monitor.StartAsync("session",true,CancellationToken.None);throw new Exception("duplicate monitor accepted");} catch(InvalidOperationException) { }
            await monitor.StopAsync("stale-id");Require(monitor.IsActive,"旧页面的过期停止编号不影响新会话");
            foreach(var outcome in new[]{"standby","wake_ack","speaker_unconfirmed","verified"}) input.Writer.TryWrite(MonitorHeard(outcome));
            await replied.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(replies==1 && starts==1,"只有verified由后台提交，发呆/唤醒/陌生声音均不提交");
            await monitor.StopAsync(started.id);
            await monitor.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Require(!monitor.IsActive && replies==1 && monitor.Status().state=="stopped","停止后迟到的verified也不再提交");
        }
        var physicalStops=0;var physicalReplies=0;
        using(var monitor=new WxxzVoiceMonitor((s,w,t)=>Task.FromResult("physical-stop-call"),
            (s,c,t,life)=>Task.FromResult(MonitorHeard("cancelled")),
            (s,c,text,capture,life)=>{physicalReplies++;return Task.FromResult(true);},
            c=>{physicalStops++;return Task.CompletedTask;},(p,c,r)=>{}))
        {
            await monitor.StartAsync("session",false,CancellationToken.None);
            await monitor.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Require(monitor.Status().state=="stopped"&&monitor.Status().errorCode==null&&
                physicalReplies==0&&physicalStops==1,"物理停止先于Host取消到达，正常结束且不提交残留语音");
        }
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var monitor=new WxxzVoiceMonitor((s,w,t)=>Task.FromResult("call"),
            (s,c,t,life)=>Task.FromResult(MonitorHeard("verified")),
            async(s,c,text,capture,life)=>{entered.TrySetResult(true);return await release.Task.WaitAsync(life);},
            c=>Task.CompletedTask,(p,c,r)=>{}))
        {
            await monitor.StartAsync("session",true,CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await monitor.StopAsync();
            Require(monitor.IsActive && monitor.Status().state=="stopping","已经开始的回复收尾前仍显示忙碌");
            try {await monitor.StartAsync("session",true,CancellationToken.None);throw new Exception("restart before drain");} catch(InvalidOperationException) { }
            release.TrySetResult(true);
            await monitor.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Require(monitor.Status().turns==1,"停止时已开始的回复正常收尾一次");
        }
        var captures=0;replies=0;
        using(var monitor=new WxxzVoiceMonitor((s,w,t)=>Task.FromResult("call"),
            (s,c,t,life)=>{captures++;return Task.FromResult(MonitorHeard("verified"));},
            (s,c,text,capture,life)=>{replies++;return Task.FromResult(false);},
            c=>Task.CompletedTask,(p,c,r)=>{}))
        {
            await monitor.StartAsync("session",true,CancellationToken.None);
            await monitor.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Require(monitor.Status().state=="faulted" && monitor.Status().errorCode=="response_or_delivery_failed" &&
                captures==1 && replies==1,"交付失败终止，不重复采音、提交或播音");
        }
        var starting=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startRelease=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        captures=0;var stops=0;
        using(var monitor=new WxxzVoiceMonitor(async(s,w,t)=>{starting.SetResult(true);return await startRelease.Task;},
            (s,c,t,life)=>{captures++;return Task.FromResult(MonitorHeard("verified"));},
            (s,c,text,capture,life)=>Task.FromResult(true),
            c=>{stops++;return Task.CompletedTask;},(p,c,r)=>{}))
        {
            var begin=monitor.StartAsync("session",true,CancellationToken.None);
            await starting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await monitor.StopAsync();startRelease.SetResult("late-started-call");
            try {await begin;throw new Exception("late start reported ready");} catch(OperationCanceledException) { }
            await monitor.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Require(captures==0 && stops==1,"启动途中暂停，即使设备迟到返回也不开始采音");
        }
        var captureEntered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownMonitor=new WxxzVoiceMonitor((s,w,t)=>Task.FromResult("call"),
            async(s,c,t,life)=>{captureEntered.TrySetResult(true);await Task.Delay(Timeout.Infinite,life);return default;},
            (s,c,text,capture,life)=>Task.FromResult(true),c=>Task.CompletedTask,(p,c,r)=>{});
        await shutdownMonitor.StartAsync("session",true,CancellationToken.None);
        await captureEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Run(shutdownMonitor.Dispose).WaitAsync(TimeSpan.FromSeconds(3));
        Require(!shutdownMonitor.IsActive,"Host关闭时可取消采音等待并回收后台任务");
        var journal=new WxxzDiagnostics();
        var words=new WxxzAttention(null,()=>"小汐").Correct(new string('啊',1000));
        for(var i=0;i<60;i++) journal.Add("decision","standby","未准入",words);
        Require(journal.Snapshot().Length==40 && journal.Snapshot()[0].sequence==21 &&
            journal.Snapshot().All(e=>e.rawText.Length<=501),"诊断条数和文字长度有界");
        journal.Clear();Require(journal.Snapshot().Length==0,"清空仅影响内存诊断");
        var policy=new WxxzAttention(null,()=>"小汐");
        foreach(var example in new[]{
            (text:"今天天气怎么样",focused:false,speaker:"owner",wake:false,admit:false),
            (text:"小希",focused:false,speaker:"short",wake:true,admit:false),
            (text:"小希帮我看看",focused:false,speaker:"owner",wake:true,admit:true),
            (text:"小希帮我看看",focused:false,speaker:"unknown",wake:true,admit:false),
            (text:"今天天气怎么样",focused:true,speaker:"owner",wake:false,admit:true),
            (text:"今天天气怎么样",focused:true,speaker:"unknown",wake:false,admit:false)})
        {
            var preview=JsonSerializer.SerializeToElement(policy.Preview(example.text,example.focused,example.speaker));
            Require(preview.GetProperty("simulated").GetBoolean() && preview.GetProperty("wouldCreateMoment").GetBoolean()==example.admit &&
                preview.GetProperty("wouldAcknowledge").GetBoolean()==example.wake,"沙盘共用实际准入规则："+example.text+example.speaker);
        }
        Require(JsonSerializer.SerializeToElement(policy.State()).GetProperty("state").GetString()=="stopped","沙盘不启动实际注意力或采音");
        Console.WriteLine("WXXZ monitor checks passed: host-owned lifetime, duplicate/stale stops, late results, drain, no retry, shutdown, bounded diagnostics and pure sandbox.");
    }
}
