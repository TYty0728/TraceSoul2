using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>
    /// 旧「可用感官目录」。嘴由身体路由收口，不再往 Prompt 灌通道清单。
    /// 仍注册以便发现，但 facet 不可用。
    /// </summary>
    public sealed class SensesCatalogPlugin : ITracePlugin
    {
        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = "builtin.senses",
            DisplayName = "可用感官目录",
            Version = "1.0.0",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Kernel,
            Description = "已停用注入：通道由身体路由收口，不再把嘴清单写进 Prompt。"
        };

        public void Register(TracePluginContext context)
        {
            context.AddMountedFacet(new SenseCatalogFacet());
        }

        public void Shutdown() { }

        private sealed class SenseCatalogFacet : ITraceMountedFacet
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "senses.catalog",
                Kind = TraceContributionKindValues.MountedFacet,
                DisplayName = "可用感官",
                Description = "已停用。",
                Provides = "senses.current_catalog",
                RefreshMode = TraceFacetRefreshValues.OncePerTurn,
                Priority = 88,
                MaxContextChars = 800
            };

            public bool IsAvailable(TraceTurnContext context) { return false; }

            public Task<TraceContextBlockData> BuildContextAsync(
                TraceTurnContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult<TraceContextBlockData>(null);
            }

            public Task<TraceCapabilityResultData> ApplyOutputAsync(
                BrainFacetOutputData output, TraceTurnContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult<TraceCapabilityResultData>(null);
            }
        }
    }
}
