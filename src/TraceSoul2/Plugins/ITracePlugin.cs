using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;

namespace TraceSoul2.Plugins
{
    public interface ITracePlugin
    {
        TracePluginMetadataData Metadata { get; }
        void Register(TracePluginContext context);
        void Shutdown();
    }

    /// <summary>Brain 主动选择的内部神经或外部执行器。</summary>
    public interface ITraceCallableContribution
    {
        TraceContributionDescriptorData Descriptor { get; }
        bool IsAvailable(TraceTurnContext context);
        Task<TraceCapabilityResultData> ExecuteAsync(
            BrainCapabilityCallData call,
            TraceTurnContext context,
            CancellationToken cancellationToken);
    }

    /// <summary>启用后按生命周期固定挂入 BrainFrame，不消耗一次 Brain 工具调用。</summary>
    public interface ITraceMountedFacet
    {
        TraceContributionDescriptorData Descriptor { get; }
        bool IsAvailable(TraceTurnContext context);
        Task<TraceContextBlockData> BuildContextAsync(
            TraceTurnContext context,
            CancellationToken cancellationToken);
        Task<TraceCapabilityResultData> ApplyOutputAsync(
            BrainFacetOutputData output,
            TraceTurnContext context,
            CancellationToken cancellationToken);
    }

    /// <summary>外部平台、身体或环境进入 TraceSoul 的 Moment 入口。</summary>
    public interface ITraceMomentSource
    {
        TraceContributionDescriptorData Descriptor { get; }
        bool IsAvailable { get; }
        PluginEventData Receive(string role, string content, string payloadJson = null);
    }

    /// <summary>启用期间由宿主轮询，只能产生 Moment，不能调用任何其他插件。</summary>
    public interface ITraceBackgroundService
    {
        TraceContributionDescriptorData Descriptor { get; }
        bool IsAvailable { get; }
        IEnumerable<PluginEventData> Poll(long nowUnixMs);
        void Shutdown();
    }
}
