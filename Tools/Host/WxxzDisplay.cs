using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Plugins;

namespace TraceSoul2.Host;

// Keep the board's presentation defaults aligned with companionStateArtwork in
// the console. Only existing catalog labels are used; this does not infer mood.
public static class WxxzArtwork
{
    public static string Select(ExpressionCatalog catalog,string state)
    {
        if(catalog?.assets==null)return null;
        var weights=state=="thinking"?new Dictionary<string,double>{{"好奇",1},{"困惑",.9},{"洞察",.8},{"平静",.3}}
            :state is "focused" or "listening"?new(){{"在意",1},{"温和",.8},{"好奇",.7},{"温暖",.5},{"开心",.2}}
            :new(){{"平静",1},{"安宁",1},{"温和",.9},{"放松",.8},{"安心",.7},{"满足",.6},{"温暖",.5},{"开心",.15}};
        var purposes=state=="thinking"?new[]{"思考","提问","疑问","困惑"}
            :state is "standby" or "offline"?new[]{"发呆","等待"}
            :state is "focused" or "listening"?new[]{"倾听","在听"}:Array.Empty<string>();
        return catalog.assets.Where(a=>a.id?.Length==64&&a.id.All(c=>c is >= 'a' and <= 'f' or >= '0' and <= '9'))
            .Select(asset=>
            {
                var purpose=asset.semanticTags?.Any(purposes.Contains)==true;
                var labels=new Dictionary<string,double>(StringComparer.Ordinal);
                foreach(var tag in asset.legacyTags??Array.Empty<string>())labels[tag]=1;
                foreach(var tag in asset.tags??new())
                    labels[tag.tag]=Math.Max(labels.GetValueOrDefault(tag.tag),double.IsFinite(tag.score)?tag.score:0);
                var score=(purpose?3:0)+weights.Sum(w=>w.Value*labels.GetValueOrDefault(w.Key));
                if(asset.requiresSemanticMatch&&!purpose)score-=3;
                score-=new[]{"伤心","委屈","压抑","焦虑"}.Sum(t=>.6*labels.GetValueOrDefault(t));
                return (id:asset.id,score);
            }).Where(a=>double.IsFinite(a.score)).OrderByDescending(a=>a.score).ThenBy(a=>a.id,StringComparer.Ordinal)
            .Select(a=>a.id).FirstOrDefault();
    }
}

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
    public void Rest(Ticket ticket,string expressionId)
    {
        lock(sync)
        {
            if(!Current(ticket))return;
            subtitle="";image=expressionId;phase="idle";revision++;
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
                :wake && attention.state=="focused"?"focused":"listening";
            var remaining=session!=null && call!=null && wake?attention.focusRemainingMs:0;
            return new Snapshot(1,revision,session,call,name,state,phase,
                state is "offline" or "paused"?"":subtitle,state is "offline" or "paused"?null:image,
                remaining,state is "standby" or "focused",wake,output);
        }
    }
}
