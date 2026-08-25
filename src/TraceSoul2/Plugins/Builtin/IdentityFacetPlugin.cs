using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;

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
                Description = IdentityFacetPrompts.FacetDescription,
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
                Description = IdentityFacetPrompts.ReviewDescription,
                Provides = "brain.identity.review",
                WhenToUse = IdentityFacetPrompts.ReviewWhenToUse,
                WhenNotToUse = IdentityFacetPrompts.ReviewWhenNotToUse,
                ParametersJsonSchema = "{reason:string}",
                HasInternalMutation = true
            };

            public bool IsAvailable(TraceTurnContext context)
            {
                if (context == null || context.Services == null) return false;
                if (context.Services.ReviewLlm != null || context.Services.Llm != null) return true;
                var directory = context.Services.Providers;
                if (directory == null) return false;
                return directory.ResolveSlot(LlmSlotNames.Review) != null
                    || directory.ResolveSlot(LlmSlotNames.Chat) != null;
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
                var llm = ResolveLlm(context);
                if (llm == null)
                    throw new InvalidOperationException("复盘没有可用的语言模型。请在「大脑 · LLM」里指定复盘槽或对话开口。");
                var review = new IdentityReviewLogic(llm);
                var output = await review.AnalyzeAsync(
                    pair, cards, moments, inner.Narrative, context.ConversationId, cancellationToken);
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

            private static ILlmClient ResolveLlm(TraceTurnContext context)
            {
                if (context == null || context.Services == null) return null;
                if (context.Services.Providers != null)
                {
                    var fromSlot = context.Services.Providers.CreateReviewClient();
                    if (fromSlot != null) return fromSlot;
                }
                if (context.Services.ReviewLlm != null) return context.Services.ReviewLlm;
                return context.Services.Llm;
            }
        }
    }
}
