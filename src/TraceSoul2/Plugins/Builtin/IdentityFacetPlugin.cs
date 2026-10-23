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
            Description = "每轮挂载我的人格、我是谁、她是谁、我们的关系；日构建及显式 identity.review 共用有检查点的认识演化流程。"
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
                if (EnvironmentLogic.IsPublic(context)) throw new InvalidOperationException("身份重整只能从私密主体入口执行。");
                if (context.Services.Storage is not SqliteMemoryManager store)
                    throw new InvalidOperationException("身份演化需要支持检查点的主体存储。");
                var llm = ResolveLlm(context) ?? throw new InvalidOperationException("复盘没有可用的语言模型。");
                var root = PuzzleViewLogic.Root(context);
                var day = MemoryDayLogic.ClosedDayKey(DateTimeOffset.Now);
                var changed = new List<string>(); var pending = new List<string>();
                foreach (var slot in IdentityReorganizationLogic.Slots)
                {
                    var state = await IdentityEvolutionLogic.EvaluateAsync(store, root, slot, day, llm, cancellationToken);
                    if (state?.pending == true) { pending.Add(slot); continue; }
                    IdentityEvolutionLogic.Commit(store, root, slot, state);
                    if (state?.decision.effect == "changed") changed.Add(slot);
                }
                return new TraceCapabilityResultData {
                    Status = pending.Count > 0 ? "pending" : "success",
                    Summary = pending.Count > 0 ? "认识已保存，部分身份正文待整理。" : changed.Count == 0 ? "当前身份认识保持不变。" : "已更新有实质变化的身份认识。",
                    Payload = IdentityProjectionLogic.Build(context),
                    EvidenceRefs = changed.Select(x => "identity:" + x).ToList()
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
