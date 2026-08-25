using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Prompts;

namespace TraceSoul2.Logic
{
    /// <summary>复盘（潜意识）：我长成什么样。修订身份短卡坐标，不写日记，不抢嘴。</summary>
    public sealed class IdentityReviewLogic
    {
        private readonly ILlmClient llm;

        public IdentityReviewLogic(ILlmClient llm)
        {
            this.llm = llm ?? throw new ArgumentNullException("llm");
        }

        public Task<IdentityReviewOutputData> AnalyzeAsync(
            PairIdentity pair,
            IReadOnlyList<IdentityCardRecord> cards,
            IEnumerable<MomentRecord> dayMoments,
            string innerNarrative,
            CancellationToken cancellationToken)
        {
            return AnalyzeAsync(pair, cards, dayMoments, innerNarrative, null, cancellationToken);
        }

        public Task<IdentityReviewOutputData> AnalyzeAsync(
            PairIdentity pair,
            IReadOnlyList<IdentityCardRecord> cards,
            IEnumerable<MomentRecord> dayMoments,
            string innerNarrative,
            string conversationId,
            CancellationToken cancellationToken)
        {
            var shared = IdentityCardLogic.FormatForExpressor(cards, pair);
            var role = BuildReviewRole(pair, dayMoments, innerNarrative);
            var messages = LlmContextPackLogic.AssembleReview(
                llm, shared, role, CorePrompts.IdentityReview.UserAsk);
            var promptCacheKey = LlmContextPackLogic.BuildPromptCacheKey(llm, conversationId);
            return DeepSeekStructuredOutputLogic.CompleteAsync<IdentityReviewOutputData>(
                llm,
                messages,
                x => x != null && !string.IsNullOrWhiteSpace(x.summary),
                CorePrompts.IdentityReview.MissingSummary,
                cancellationToken,
                promptCacheKey);
        }

        private static string BuildReviewRole(
            PairIdentity pair,
            IEnumerable<MomentRecord> dayMoments,
            string innerNarrative)
        {
            pair = pair ?? PairIdentity.Missing;
            var builder = new StringBuilder();
            builder.AppendLine(pair.Apply(CorePrompts.IdentityReview.Role));
            CorePrompts.Write(builder, pair.Apply(CorePrompts.IdentityReview.Rules));
            builder.AppendLine();
            AppendReviewEvidence(builder, pair, dayMoments, innerNarrative);
            return builder.ToString();
        }

        private static string BuildPrompt(
            PairIdentity pair,
            IReadOnlyList<IdentityCardRecord> cards,
            IEnumerable<MomentRecord> dayMoments,
            string innerNarrative)
        {
            pair = pair ?? PairIdentity.Missing;
            var builder = new StringBuilder();
            builder.AppendLine(pair.Apply(CorePrompts.IdentityReview.Role));
            CorePrompts.Write(builder, pair.Apply(CorePrompts.IdentityReview.Rules));
            builder.AppendLine();
            builder.AppendLine(CorePrompts.IdentityReview.CurrentCardsHeader);
            builder.AppendLine(IdentityCardLogic.FormatForExpressor(cards, pair));
            builder.AppendLine();
            AppendReviewEvidence(builder, pair, dayMoments, innerNarrative);
            return builder.ToString();
        }

        private static void AppendReviewEvidence(
            StringBuilder builder,
            PairIdentity pair,
            IEnumerable<MomentRecord> dayMoments,
            string innerNarrative)
        {
            builder.AppendLine(CorePrompts.IdentityReview.InnerHeader);
            builder.AppendLine(string.IsNullOrWhiteSpace(innerNarrative)
                ? CorePrompts.IdentityReview.EmptyInner
                : innerNarrative.Trim());
            builder.AppendLine();
            builder.AppendLine(CorePrompts.IdentityReview.MomentsHeader);
            var moments = (dayMoments ?? Enumerable.Empty<MomentRecord>()).ToList();
            if (moments.Count == 0) builder.AppendLine(CorePrompts.IdentityReview.EmptyMoments);
            foreach (var moment in moments.Take(60))
            {
                var content = (moment.Content ?? string.Empty).Trim();
                if (content.Length > 80) content = content.Substring(0, 80);
                builder.Append("- ").Append(pair.LabelForRole(moment.Role)).Append("：").AppendLine(content);
            }
            builder.AppendLine();
            CorePrompts.Write(builder, CorePrompts.IdentityReview.JsonSchema);
        }
    }
}
