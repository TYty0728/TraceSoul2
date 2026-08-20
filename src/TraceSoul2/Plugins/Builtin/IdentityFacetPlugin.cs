using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;

namespace TraceSoul2.Plugins.Builtin
{
    public sealed class IdentityFacetPlugin : ITracePlugin
    {
        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = "builtin.identity",
            DisplayName = "身份短卡",
            Version = "2.1.0",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Kernel,
            Description = "每轮挂载我的人格、我是谁、她是谁、我们的关系；每日复盘时由中枢在开口后调用 identity.review。"
        };

        public void Register(TracePluginContext context)
        {
            context.AddMountedFacet(new IdentityFacet());
            context.AddCallable(new ReviewIdentityNerve());
        }

        public void Shutdown() { }

        private sealed class IdentityFacet : ITraceMountedFacet
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "identity.base",
                Kind = TraceContributionKindValues.MountedFacet,
                DisplayName = "四张身份短卡",
                Description = "每个 Moment 一次进入 BrainFrame 的人格、自我理解、对她的理解和关系定义。",
                Provides = "brain.identity.base",
                RefreshMode = TraceFacetRefreshValues.OncePerTurn,
                Priority = 100,
                MaxContextChars = 3600
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceContextBlockData> BuildContextAsync(
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                var pair = context.Services.Storage.LoadPairIdentity();
                var cards = context.Services.Storage.LoadIdentityCards(context.ConversationId);
                return Task.FromResult(new TraceContextBlockData
                {
                    Title = "身份短卡",
                    Content = IdentityCardLogic.FormatForExpressor(cards, pair)
                });
            }

            public Task<TraceCapabilityResultData> ApplyOutputAsync(
                BrainFacetOutputData output,
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                return Task.FromResult<TraceCapabilityResultData>(null);
            }
        }

        private sealed class ReviewIdentityNerve : ITraceCallableContribution
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "identity.review",
                Kind = TraceContributionKindValues.CallableNerve,
                DisplayName = "复盘身份短卡",
                Description = "根据今天的相处修订我的人格、我是谁、她是谁、我们的关系。只改坐标，不写日记。",
                Provides = "brain.identity.review",
                WhenToUse = "每日复盘到期，或今天的相处明显改变了自我理解、对 {username} 的理解、或关系定义时。",
                WhenNotToUse = "普通对话、寒暄、只是一件生活事实。短卡不是备忘录。",
                ParametersJsonSchema = "{reason:string}",
                HasInternalMutation = true
            };

            public bool IsAvailable(TraceTurnContext context)
            {
                return context != null && context.Services != null && context.Services.Llm != null;
            }

            public async Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call,
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                var pair = context.Services.Storage.LoadPairIdentity();
                var cards = context.Services.Storage.LoadIdentityCards(context.ConversationId);
                var since = DateTimeOffset.UtcNow.AddHours(-26).ToUnixTimeMilliseconds();
                var moments = context.Services.Storage.GetMomentsSince(context.ConversationId, since, 80);
                var inner = context.Services.Storage.LoadOrCreateInnerRuntime(context.ConversationId);
                var review = new IdentityReviewLogic(context.Services.Llm);
                var output = await review.AnalyzeAsync(
                    pair, cards, moments, inner.Narrative, cancellationToken);
                output = IdentityCardLogic.Normalize(output, cards, pair);
                var changed = context.Services.Storage.ApplyIdentityReview(
                    context.ConversationId, context.Moment.Id, output);
                var next = context.Services.Storage.LoadIdentityCards(context.ConversationId);
                return new TraceCapabilityResultData
                {
                    Status = "success",
                    Summary = string.IsNullOrWhiteSpace(output.summary)
                        ? (changed.Count == 0 ? "身份短卡无需修订。" : "已修订 " + changed.Count + " 张身份短卡。")
                        : output.summary,
                    Payload = IdentityCardLogic.FormatForExpressor(next, pair),
                    EvidenceRefs = changed.Select(x => "identity:" + x.Slot).ToList()
                };
            }
        }
    }
}
