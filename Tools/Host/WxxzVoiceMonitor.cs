using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.Host;

/// <summary>One host-owned loop. HTTP clients observe or explicitly stop it; they never pump audio or submit transcripts.</summary>
public sealed class WxxzVoiceMonitor : IDisposable
{
    public sealed record Snapshot(string id, bool active, string state, string phase, string sessionId, string callId,
        bool wake, string lastOutcome, string reason, string errorCode, int turns, long revision);
    private sealed class Run
    {
        public string Id = Guid.NewGuid().ToString("N"), Session, Call, Phase="starting", Outcome, Reason, Error;
        public bool Wake;
        public volatile bool StopRequested;
        public string State="starting";
        public int Turns;
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Worker;
    }
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<string,bool,CancellationToken,Task<string>> start;
    private readonly Func<string,string,CancellationToken,CancellationToken,Task<JsonElement>> listen;
    private readonly Func<string,string,string,string,CancellationToken,Task<bool>> reply;
    private readonly Func<string,Task> stop;
    private readonly Action<string,string,string> trace;
    private Run current;
    private long revision;
    private bool disposed;
    public WxxzVoiceMonitor(Func<string,bool,CancellationToken,Task<string>> start,
        Func<string,string,CancellationToken,CancellationToken,Task<JsonElement>> listen,
        Func<string,string,string,string,CancellationToken,Task<bool>> reply, Func<string,Task> stop,
        Action<string,string,string> trace)
    { this.start=start; this.listen=listen; this.reply=reply; this.stop=stop; this.trace=trace; }
    public bool IsActive { get { lock(sync) return current?.Worker is {IsCompleted:false}; } }
    public bool AdmissionAllowed(string call)
    {
        var r=Volatile.Read(ref current);
        return r!=null && r.Call==call && !r.StopRequested;
    }
    public Snapshot Status()
    {
        lock(sync)
        {
            var r=current;
            return new Snapshot(r?.Id, r?.Worker is {IsCompleted:false}, r?.State ?? "stopped", r?.Phase ?? "stopped",
                r?.Session,r?.Call,r?.Wake ?? false,r?.Outcome,r?.Reason,r?.Error,r?.Turns ?? 0,revision);
        }
    }
    public async Task<Snapshot> StartAsync(string session, bool wake, CancellationToken requestToken)
    {
        Run r;
        lock(sync)
        {
            if(disposed) throw new ObjectDisposedException(nameof(WxxzVoiceMonitor));
            if(current?.Worker is {IsCompleted:false}) throw new InvalidOperationException("后台监听已启动或正在收尾，请勿重复开始。");
            current?.Stop.Dispose();
            r=new Run{Session=session,Wake=wake}; current=r; revision++;
            r.Worker=Task.Run(()=>RunAsync(r));
        }
        // Aborting the dashboard request does not revoke an explicit start request.
        await r.Ready.Task.WaitAsync(requestToken);
        return Status();
    }
    private void Phase(Run r,string phase,string reason)
    {
        lock(sync) { if(r.StopRequested) return; r.Phase=phase;r.Reason=reason;revision++; }
    }
    private async Task RunAsync(Run r)
    {
        var failurePhase="starting";
        try
        {
            r.Call=await start(r.Session,r.Wake,r.Stop.Token);
            r.Stop.Token.ThrowIfCancellationRequested();
            lock(sync) {r.State="running";revision++;}
            r.Ready.TrySetResult(true);
            trace("monitor","started","后台监听已开始；关闭网页不会结束监听。");
            while(!r.Stop.IsCancellationRequested)
            {
                failurePhase="listening"; Phase(r,"listening","等待语音；采音等待不占用主逻辑锁。");
                var heard=await listen(r.Session,r.Call,r.Stop.Token,lifetime.Token);
                r.Stop.Token.ThrowIfCancellationRequested();
                var outcome=heard.GetProperty("status").GetString();
                lock(sync) {r.Outcome=outcome;revision++;}
                if(outcome=="cancelled") break;
                if(outcome is "speech_too_long" or "transfer_too_slow")
                    throw new InvalidOperationException("capture_rejected");
                if(outcome!="verified")
                {
                    await Task.Delay(100,r.Stop.Token);
                    continue;
                }
                failurePhase="replying"; Phase(r,"replying","本人语音已核验，正在交给主逻辑处理；暂不采音。");
                var completed=await reply(r.Session,r.Call,heard.GetProperty("text").GetString(),
                    heard.GetProperty("captureId").GetString(),lifetime.Token);
                if(!completed) throw new InvalidOperationException("delivery_failed");
                lock(sync) {r.Turns++;revision++;}
                trace("monitor","turn_completed","本轮主逻辑处理及所需交付已完成；继续等待下一句。");
            }
        }
        catch(OperationCanceledException) when(r.Stop.IsCancellationRequested || lifetime.IsCancellationRequested) { }
        catch(Exception)
        {
            // Do not copy provider errors/keys or arbitrary exception bodies into a diagnostic feed.
            lock(sync)
            {
                r.Error=failurePhase=="starting" ? "start_failed" : failurePhase=="replying" ? "response_or_delivery_failed" : "capture_or_wake_failed";
                r.Reason="后台监听发生错误，已停止；请检查设备、模型与交付状态，不会自动重试。";revision++;
            }
            trace("monitor",r.Error,r.Reason);
            r.Ready.TrySetException(new InvalidOperationException("后台监听未能启动，请检查设备连接、固件和本人声纹。"));
        }
        finally
        {
            if(r.Call!=null)
                try {await stop(r.Call);} catch {lock(sync) {r.Error ??= "stop_unconfirmed";}}
            lock(sync)
            {
                r.State=r.Error==null ? "stopped" : "faulted"; r.Phase=r.State;
                if(r.Error==null) r.Reason="后台监听已停止，需手动开始才会再次采音。";
                revision++;
            }
            r.Ready.TrySetCanceled(new CancellationToken(true));
            trace("monitor",r.State,r.Reason);
        }
    }
    public async Task<Snapshot> StopAsync(string expectedId=null)
    {
        Run r;
        lock(sync)
        {
            r=current;
            if(r==null || r.Worker.IsCompleted || expectedId!=null && expectedId!=r.Id) return Status();
            r.StopRequested=true;r.State="stopping";r.Phase="stopping";
            r.Reason="已请求停止；迟到语音不再准入，已经开始的回复会收尾。";
            r.Stop.Cancel(); revision++;
        }
        if(r.Call!=null) {try {await stop(r.Call);} catch { trace("monitor","stop_unconfirmed","取消采音的设备回执未知，等待后台收尾。"); }}
        return Status();
    }
    public Task WaitAsync(CancellationToken token)
    { lock(sync) return current?.Worker?.WaitAsync(token) ?? Task.CompletedTask; }
    public void Dispose()
    {
        lock(sync) {if(disposed)return;disposed=true;}
        try {StopAsync().GetAwaiter().GetResult();} catch { }
        lifetime.Cancel();
        try {WaitAsync(CancellationToken.None).GetAwaiter().GetResult();} catch { }
        current?.Stop.Dispose(); lifetime.Dispose();
    }
}
