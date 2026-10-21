using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins;

public sealed class QqStickerPlugin : ITracePlugin
{
    public TracePluginMetadataData Metadata {get;}=new(){Id="qq.sticker",DisplayName="QQ 表情（共用情绪图库）",Version="3.1.2",
        Author="TraceSoul2",Role=PluginRoleValues.Organ,PlatformId=BodyIds.Qq,Description="直接发送本轮共用评分最高的可用图片，不设最低分门槛。"};
    public void Register(TracePluginContext context)=>context.AddCallable(new Sticker());
    public void Shutdown() { }
    private sealed class Sticker : ITraceCallableContribution
    {
        public TraceContributionDescriptorData Descriptor {get;}=new(){Id="qq.sticker.send",PluginId="qq.sticker",
            Kind=TraceContributionKindValues.Effector,DisplayName="QQ 自动表情",Provides="sticker",Organ=BodyOrganValues.Sticker,
            BodyId=BodyIds.Qq,BodyTier=BodyTierValues.Chat,ParametersJsonSchema="{emotion:string}",HasExternalSideEffect=true,SupportsPublicEnvironment=true,
            Description="自动发送本轮评分最高的可用图片；图片失效时按本轮分数顺位选择。"};
        public bool IsAvailable(TraceTurnContext turn)=>turn?.Services!=null &&
            turn.Environment?.PlatformId=="builtin.onebot" && turn.Services.PlatformAdapters.Any(p=>p.PlatformId=="builtin.onebot");
        public async Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,TraceTurnContext turn,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(!IsAvailable(turn))return new(){Status="skipped",Summary="QQ表情目标不可用，未改发其他平台。"};
            var library=turn.Services.Expressions;
            var match=turn.Workspace.ExpressionMatch;
            if(library==null||match?.status!="matched")
                return new(){Status="failed",Summary="本轮共用表情评分不可用："+(match?.reason??"scoring_unavailable")};
            var selected=ExpressionSelection.ReadHighest(library,match,token);
            if(!selected.HasValue)return new(){Status="failed",Summary="本轮评分图片均已失效，请重新加载图库。"};
            turn.Services.LogTiming(turn.TraceId,"QQ表情选择",0,"highest_available_score");
            var adapter=turn.Services.PlatformAdapters.First(p=>p.PlatformId=="builtin.onebot");
            // Resolve unreadable assets before sending. Never retry a send whose
            // external outcome may already have happened.
            return await adapter.SendAsync(new TraceOutboundMessageData{
                Kind=TraceOutboundKinds.Image,File=selected.Value.image.path,
                SessionId=turn.Environment.SessionId,SessionType=turn.Environment.SessionType},turn,token);
        }
    }
}
