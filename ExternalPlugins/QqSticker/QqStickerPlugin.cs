using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins;

public sealed class QqStickerPlugin : ITracePlugin
{
    public TracePluginMetadataData Metadata {get;}=new(){Id="qq.sticker",DisplayName="QQ 表情（共用情绪图库）",Version="3.0.0",
        Author="TraceSoul2",Role=PluginRoleValues.Organ,PlatformId=BodyIds.Qq,Description="复用本轮共享标签评分的最高分图片，跟随QQ正文发送；不向其他平台代发。"};
    public void Register(TracePluginContext context)=>context.AddCallable(new Sticker());
    public void Shutdown() { }
    private sealed class Sticker : ITraceCallableContribution
    {
        public TraceContributionDescriptorData Descriptor {get;}=new(){Id="qq.sticker.send",PluginId="qq.sticker",
            Kind=TraceContributionKindValues.Effector,DisplayName="QQ 自动表情",Provides="sticker",Organ=BodyOrganValues.Sticker,
            BodyId=BodyIds.Qq,BodyTier=BodyTierValues.Chat,ParametersJsonSchema="{emotion:string}",HasExternalSideEffect=true,SupportsPublicEnvironment=true,
            Description="自动使用本轮表达的共享情绪标签匹配；无合格图片则跳过。"};
        public bool IsAvailable(TraceTurnContext turn)=>turn?.Services.Expressions!=null &&
            turn.Environment?.PlatformId=="builtin.onebot" && turn.Services.PlatformAdapters.Any(p=>p.PlatformId=="builtin.onebot");
        public Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,TraceTurnContext turn,CancellationToken token)
        {
            if(!IsAvailable(turn))return Task.FromResult(new TraceCapabilityResultData{Status="skipped",Summary="QQ表情目标不可用，未改发其他平台。"});
            var match=turn.Workspace.ExpressionMatch;
            if(match?.status!="matched"||string.IsNullOrEmpty(match.selectedId))
                return Task.FromResult(new TraceCapabilityResultData{Status="skipped",Summary="共享表情库没有足够相关的图片。"});
            var image=turn.Services.Expressions.ReadImage(match.selectedId);
            var adapter=turn.Services.PlatformAdapters.First(p=>p.PlatformId=="builtin.onebot");
            return adapter.SendAsync(new TraceOutboundMessageData{Kind=TraceOutboundKinds.Image,File=image.path,
                SessionId=turn.Environment.SessionId,SessionType=turn.Environment.SessionType},turn,token);
        }
    }
}
