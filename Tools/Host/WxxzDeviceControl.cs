using System;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.Host;

/// <summary>A dashboard-authorized, volatile device lease. Reconnects never replay a start.</summary>
public sealed class WxxzDeviceControl : IDisposable
{
    public sealed record Request(string controller, long ack, bool active, string phase, bool allowed, string error);
    public sealed record Receipt(string controller, string boot, long sequence, string action, bool localMicPaused);
    public sealed record Snapshot(bool enabled, bool online, string state, string error, long handled);
    private readonly object sync=new();
    private readonly SemaphoreSlim pump=new(1,1), configuration=new(1,1);
    private readonly Func<Request,CancellationToken,Task<Receipt>> exchange;
    private readonly Func<string,CancellationToken,Task> action;
    private readonly Func<(bool active,string phase,bool available)> current;
    private CancellationTokenSource arm=new();
    private bool enabled,online,disposed,starting;
    private string controller=Guid.NewGuid().ToString("N"),boot,error;
    private long handled;
    public WxxzDeviceControl(Func<Request,CancellationToken,Task<Receipt>> exchange,
        Func<string,CancellationToken,Task> action,Func<(bool active,string phase,bool available)> current)
    {this.exchange=exchange;this.action=action;this.current=current;}
    public Snapshot Status(){lock(sync)return new(enabled,online,!enabled?"disabled":online?"ready":"waiting",error,handled);}
    public bool Starting {get{lock(sync)return starting;}}
    public async Task ConfirmStartAsync(CancellationToken token)
    {
        string identity,expectedBoot;long sequence;
        lock(sync)
        {
            if(!enabled||!starting)throw new InvalidOperationException("设备开始请求已失效。");
            identity=controller;expectedBoot=boot;sequence=handled;
        }
        // Confirm the created Host call before its monitor can capture/play. A later local stop wins.
        var receipt=await exchange(new(identity,sequence,true,"focused",true,null),token);
        lock(sync)
            if(!enabled||controller!=identity||receipt.controller!=identity||receipt.boot!=expectedBoot||
                receipt.sequence!=sequence||receipt.action!=""||receipt.localMicPaused)
                throw new InvalidOperationException("设备已停止或更换会话，未开始采音。");
    }
    public async Task<Snapshot> ConfigureAsync(bool value,CancellationToken token)
    {
        await configuration.WaitAsync(token);
        try
        {
            lock(sync){if(disposed)throw new ObjectDisposedException(nameof(WxxzDeviceControl));if(enabled==value)return Status();}
            if(value&&current().active)throw new InvalidOperationException("请先结束现有通话，再授权触屏控制。");
            if(!value)
            {
                lock(sync){enabled=online=false;arm.Cancel();}
                await action("stop",token);
            }
            // Re-arm only once the old pump has stopped; a late start cannot stop a newer arm.
            await pump.WaitAsync(token);
            try
            {
                if(!value)
                {
                    try{using var release=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await exchange(new(controller,handled,false,"paused",false,null),release.Token);}catch{ }
                }
                lock(sync){arm.Dispose();arm=new();enabled=value;online=false;controller=Guid.NewGuid().ToString("N");boot=null;handled=0;error=null;}
            }
            finally{pump.Release();}
            return Status();
        }
        finally{configuration.Release();}
    }
    public async Task TickAsync(CancellationToken token)
    {
        if(!await pump.WaitAsync(0,token))return;
        string identity=null;bool received=false;
        try
        {
            CancellationToken armed;long ack;string previousError;
            lock(sync){if(!enabled||disposed)return;identity=controller;armed=arm.Token;ack=handled;previousError=error;}
            using var bounded=CancellationTokenSource.CreateLinkedTokenSource(token,armed);
            bounded.CancelAfter(TimeSpan.FromSeconds(45));
            var state=current();
            var response=await exchange(new(identity,ack,state.active,state.phase,state.available,previousError),bounded.Token);
            if(response.controller!=identity||!Guid.TryParseExact(response.boot,"N",out _)||response.sequence<0||response.sequence>uint.MaxValue||
                response.action is not ("" or "start" or "stop"))throw new InvalidOperationException("invalid_device_receipt");
            received=true;
            bool changedBoot;
            lock(sync)
            {
                if(!enabled||controller!=identity)return;
                online=state.available;changedBoot=boot!=response.boot;
                if(changedBoot){boot=response.boot;handled=response.sequence;}
                else if(response.sequence<handled)throw new InvalidOperationException("invalid_device_sequence");
            }
            // Bootstrap establishes a baseline, never acts on events from before this lease.
            if(changedBoot){if(state.active)await action("stop",bounded.Token);return;}
            lock(sync)
            {
                if(response.sequence<=handled)return;
                handled=response.sequence; // An ambiguous/failed action is acknowledged, never retried.
            }
            if(response.action=="start")
            {
                if(response.localMicPaused||!state.available)throw new InvalidOperationException("start_unavailable");
                if(!state.active)
                {
                    lock(sync){starting=true;}
                    try{await action("start",bounded.Token);}
                    finally{lock(sync){starting=false;}}
                }
            }
            else if(response.action=="stop")await action("stop",bounded.Token);
            lock(sync){error=null;}
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){ }
        catch(Exception)
        {
            lock(sync){if(controller==identity){online=false;error="control_or_call_failed";
                if(!received){controller=Guid.NewGuid().ToString("N");boot=null;handled=0;}}}
            // Fail closed. Polling may reconnect, but a fresh physical tap is required to start.
            try{await action("stop",CancellationToken.None);}catch{ }
        }
        finally{pump.Release();}
    }
    public void Dispose(){lock(sync){if(disposed)return;disposed=true;enabled=online=false;arm.Cancel();}}
}
