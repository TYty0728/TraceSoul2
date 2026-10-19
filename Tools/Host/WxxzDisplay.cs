using System;

namespace TraceSoul2.Host;

/// <summary>Read-only presentation of this body's lifecycle. Never drives admission or device I/O.</summary>
public sealed class WxxzDisplay
{
    public sealed record Ticket(string Session, string Call, long Sequence);
    public sealed record Snapshot(int version, long revision, string sessionId, string callId, string name,
        string state, string phase, string subtitle, string expressionId, long focusRemainingMs, bool animate, bool wake,long outputSequence);
    private readonly object sync = new();
    private string session, call, capture, phase = "idle", subtitle = "", image;
    private bool thinking, failed;
    private Ticket turn;
    private long revision, output;

    public void Reset(string sessionId)
    {
        lock(sync)
        {
            session=sessionId;call=capture=null;phase="idle";subtitle="";image=null;
            thinking=failed=false;turn=null;output=++revision;
        }
    }
    public void StartCall(string sessionId, string callId)
    {
        lock(sync)
        {
            if(session!=sessionId)return;
            call=callId;capture=null;phase="idle";subtitle="";image=null;
            thinking=failed=false;turn=null;output=++revision;
        }
    }
    public void StopCall(string callId)
    {
        lock(sync)
        {
            if(call!=callId)return;
            call=capture=null;phase="idle";subtitle="";image=null;thinking=false;turn=null;output=++revision;
        }
    }
    public void Capture(string sessionId,string callId,string captureId,bool recording)
    {
        lock(sync)
        {
            if(session!=sessionId || call!=callId)return;
            if(recording) {capture=captureId;phase="capturing";}
            else if(capture==captureId) {capture=null;if(phase=="capturing")phase="idle";}
            revision++;
        }
    }
    public Ticket BeginTurn(string sessionId)
    {
        lock(sync)
        {
            if(session!=sessionId)return null;
            thinking=true;failed=false;phase="thinking";subtitle="";image=null;output=++revision;
            return turn=new Ticket(session,call,revision);
        }
    }
    public void CompleteTurn(Ticket ticket,bool success)
    {
        lock(sync)
        {
            if(ticket==null || ticket!=turn || ticket.Session!=session || ticket.Call!=call)return;
            turn=null;thinking=false;failed|=!success;phase=failed?"error":"idle";revision++;
        }
    }
    public Ticket Prepare(string sessionId,string callId,string text,string expressionId=null)
    {
        lock(sync)
        {
            if(session==null || session!=sessionId || call!=callId)return null;
            subtitle=text;image=expressionId;phase="synthesizing";failed=false;
            output=++revision;return new Ticket(session,call,output);
        }
    }
    private bool Current(Ticket ticket) => ticket!=null && session==ticket.Session && call==ticket.Call && output==ticket.Sequence;
    public void Playing(Ticket ticket)
    { lock(sync) {if(!Current(ticket))return;phase="speaking";revision++;} }
    public void Finished(Ticket ticket,bool success)
    {
        lock(sync)
        {
            if(!Current(ticket))return;
            failed=!success;phase=failed?"error":thinking?"thinking":"idle";revision++;
        }
    }
    public Snapshot Read(string name,WxxzAttention.Snapshot attention,WxxzVoiceMonitor.Snapshot monitor,bool wake)
    {
        lock(sync)
        {
            if(monitor?.sessionId!=session)monitor=null;
            var state=session==null?"offline":monitor?.state=="faulted"?"error":monitor?.state=="stopping"?"paused"
                :failed?"error":phase=="speaking"?"speaking":thinking || phase=="synthesizing"?"thinking"
                :call==null?"paused":wake && attention.state=="standby"?"standby"
                :capture!=null?"listening":wake && attention.state=="focused"?"focused":"listening";
            var remaining=session!=null && call!=null && wake?attention.focusRemainingMs:0;
            return new Snapshot(1,revision,session,call,name,state,phase,
                state is "offline" or "paused"?"":subtitle,state is "offline" or "paused"?null:image,
                remaining,state is "standby" or "focused",wake,output);
        }
    }
}
