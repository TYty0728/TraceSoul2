using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Prompts;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>心智：安静、理性，只组织这一拍怎么想。不演戏、不对她说话。</summary>
    public sealed class MindLogic
    {
        public const int TagCandidateCap = 12;
        private readonly ILlmClient llm;

        public MindLogic(ILlmClient llm)
        {
            this.llm = llm ?? throw new ArgumentNullException("llm");
        }

        public async Task<MindDecisionData> DecideAsync(
            TraceTurnContext turn,
            string leaveResult,
            bool alreadyLeft,
            CancellationToken cancellationToken)
        {
            return await DecideAsync(turn, leaveResult, alreadyLeft, string.Empty, cancellationToken);
        }

        public async Task<MindDecisionData> DecideAsync(
            TraceTurnContext turn,
            string leaveResult,
            bool alreadyLeft,
            string naturallyAwakenedPast,
            CancellationToken cancellationToken)
        {
            var query = turn.Moment == null ? string.Empty : turn.Moment.Content;
            var embedding = turn.Services == null ? null : turn.Services.Embedding;
            var templates = await MindTemplateLogic.SelectAsync(
                query, embedding, MindTemplateLogic.CandidateCap, cancellationToken);
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", BuildFoundationPrompt(turn)),
                new DeepSeekMessageData("system", BuildTurnPrompt(
                    turn, leaveResult, alreadyLeft, templates, naturallyAwakenedPast)),
                new DeepSeekMessageData("user", query)
            };
            var decided = await DeepSeekStructuredOutputLogic.CompleteAsync<MindDecisionData>(
                llm,
                messages,
                x => x != null && !string.IsNullOrWhiteSpace(Normalize(x).beat),
                CorePrompts.Mind.MissingBeat,
                cancellationToken);
            return Normalize(decided, alreadyLeft);
        }

        public static MindDecisionData Normalize(MindDecisionData output, bool alreadyLeft = false)
        {
            output = output ?? new MindDecisionData();
            output.beat = output.BeatValue();
            if (alreadyLeft && output.BeatValue() == MindBeatValues.Leave)
            {
                output.beat = MindBeatValues.Now;
                output.leave = string.Empty;
            }
            output.tags = string.Join("、", output.ParseTags());
            output.query = Limit((output.query ?? string.Empty).Trim(), 80);
            output.mood = OneLine(output.mood);
            output.new_fact = Limit((output.new_fact ?? string.Empty).Trim(), 40);
            output.leave = Limit((output.leave ?? string.Empty).Trim(), 80);
            output.note = (output.note ?? string.Empty).Trim();
            output.today = Limit((output.today ?? string.Empty).Trim(), 200);
            output.inner = OneLine(output.inner);
            output.cognition = Limit(OneLine(output.cognition), 19);
            if (output.ClearsAttention())
                output.attention = "无";
            else
            {
                var held = output.ParseAttention().Select(x => Limit(x, 80)).Where(x => x.Length > 0).ToList();
                output.attention = held.Count == 0 ? string.Empty : string.Join("、", held);
            }
            if (LooksLikeBareDate(output.today)) output.today = string.Empty;
            if (output.BeatValue() != MindBeatValues.Leave) output.leave = string.Empty;
            if (output.BeatValue() == MindBeatValues.Leave)
            {
                output.tags = string.Empty;
                output.query = string.Empty;
            }
            output.next_heartbeat_minutes = HeartbeatLogic.ClampMinutes(output.next_heartbeat_minutes);
            if (output.sleep) output.next_heartbeat_minutes = 0;
            output.sticker = output.StickerValue();
            output.image = output.ImageValue();
            if (output.sleep || output.BeatValue() == MindBeatValues.Leave)
            {
                output.image = MindAtmosphereValues.None;
                output.sticker = MindAtmosphereValues.None;
            }
            if (output.WantsImage()) output.sticker = MindAtmosphereValues.None;
            return output;
        }

        private static string BuildFoundationPrompt(TraceTurnContext turn)
        {
            var pair = turn.Services.Storage.LoadPairIdentity();
            var cards = turn.Services.Storage.LoadIdentityCards(turn.ConversationId);
            var builder = new StringBuilder();
            builder.AppendLine(IdentityCardLogic.FormatForMind(cards, pair));
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Mind.HowToThinkHeader);
            CorePrompts.Write(builder, CorePrompts.Mind.Foundation);
            return builder.ToString();
        }

        private static string BuildTurnPrompt(
            TraceTurnContext turn,
            string leaveResult,
            bool alreadyLeft,
            IReadOnlyList<MindTemplate> templates,
            string naturallyAwakenedPast)
        {
            var pair = turn.Services.Storage.LoadPairIdentity();
            var storage = turn.Services.Storage;
            var builder = new StringBuilder();
            builder.AppendLine(CorePrompts.Mind.NowPrefix + TimeLanguageUtil.NaturalNow(DateTimeOffset.Now) + "。");
            builder.AppendLine();
            var recentDialogue = FormatRecentDialogue(turn);
            if (!string.IsNullOrWhiteSpace(recentDialogue))
            {
                builder.AppendLine(recentDialogue);
                builder.AppendLine();
            }
            var runtime = storage.LoadOrCreateInnerRuntime(turn.ConversationId);
            builder.AppendLine(InnerLifeLogic.FormatForMind(runtime));
            builder.AppendLine(CorePrompts.Mind.InnerAttentionRule);
            var todayItems = storage.GetTodayNewItems(
                turn.ConversationId, TodayBoundary(DateTimeOffset.Now).ToUnixTimeMilliseconds(), 10);
            if (todayItems != null && todayItems.Count > 0)
            {
                builder.AppendLine(CorePrompts.Mind.TodayNewHeader);
                foreach (var item in todayItems)
                    builder.AppendLine("- " + item.Content);
            }
            var trajectory = storage.LoadDayTrajectory(MemoryDayKey(DateTimeOffset.Now));
            if (trajectory != null && !string.IsNullOrWhiteSpace(trajectory.Text))
                builder.AppendLine(CorePrompts.Mind.TrajectoryPrefix + trajectory.Text.Trim());
            if (!string.IsNullOrWhiteSpace(naturallyAwakenedPast))
            {
                builder.AppendLine();
                builder.AppendLine(naturallyAwakenedPast.Trim());
            }
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Mind.TagCandidatesHeader);
            var tags = MemoryRecallLogic.ListTagCandidates(turn, TagCandidateCap);
            if (tags.Count == 0)
                builder.AppendLine(CorePrompts.Mind.NoCloseTags);
            else
            {
                foreach (var tag in tags)
                    builder.AppendLine("- " + tag.Label + "：" + Limit(tag.Definition, 40));
            }
            if (!string.IsNullOrWhiteSpace(leaveResult))
            {
                builder.AppendLine();
                builder.AppendLine(CorePrompts.Mind.LeaveResultHeader);
                builder.AppendLine(leaveResult.Trim());
            }
            if (alreadyLeft)
                builder.AppendLine(CorePrompts.Mind.AlreadyLeft);
            var organized = MindTemplateLogic.Format(templates);
            if (!string.IsNullOrWhiteSpace(organized))
            {
                builder.AppendLine();
                builder.AppendLine(organized);
            }
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Mind.NowHeader);
            var momentContent = turn.Moment == null ? string.Empty : turn.Moment.Content;
            if (HeartbeatLogic.IsHeartbeatContent(momentContent))
                CorePrompts.Write(builder, CorePrompts.Mind.Heartbeat);
            else if (turn.Wake == KernelWakeValues.Mind)
                builder.AppendLine(CorePrompts.Mind.MindWake);
            else
            {
                builder.AppendLine(turn.RequiresExpression
                    ? pair.Apply(CorePrompts.Mind.HumanSpeak)
                    : CorePrompts.Mind.Background);
            }
            return builder.ToString();
        }

        /// <summary>WebUI 控制的原文拼接；只拼两个人的真实 Moment，不把时间事件混成对话。</summary>
        internal static string FormatRecentDialogue(TraceTurnContext turn)
        {
            if (turn == null || turn.RawHistoryLimit <= 0 || turn.RecentMoments == null ||
                turn.RecentMoments.Count == 0 || turn.Services == null || turn.Services.Storage == null)
                return string.Empty;
            var pair = turn.Services.Storage.LoadPairIdentity();
            var lines = turn.RecentMoments
                .Where(x => x != null &&
                            (pair.IsHumanMoment(x.Role) || pair.IsCompanionMoment(x.Role)) &&
                            !string.IsNullOrWhiteSpace(x.Content) &&
                            !IsOutboundProtocolMoment(x.Content))
                .TakeLast(turn.RawHistoryLimit)
                .ToList();
            if (lines.Count == 0) return string.Empty;
            var builder = new StringBuilder();
            builder.AppendLine(CorePrompts.Mind.RecentDialogueHeader);
            builder.AppendLine(CorePrompts.Mind.RecentDialogueHint);
            foreach (var item in lines)
                builder.AppendLine(pair.LabelForRole(item.Role) + "：" + item.Content.Trim());
            return builder.ToString().TrimEnd();
        }

        /// <summary>出站入库的系统占位，不是对她说的话，不能进最近对话原文。</summary>
        internal static bool IsOutboundProtocolMoment(string content)
        {
            var text = (content ?? string.Empty).Trim();
            if (text.Length < 4 || text[0] != '[') return false;
            return text.StartsWith("[QQ ", StringComparison.Ordinal) ||
                   text.StartsWith("[CQ:", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTimeOffset TodayBoundary(DateTimeOffset now)
        {
            var local = now.ToOffset(TimeSpan.FromHours(8));
            var boundary = local.Date.AddHours(4);
            if (local < boundary) boundary = boundary.AddDays(-1);
            return boundary;
        }

        private static string MemoryDayKey(DateTimeOffset now)
        {
            return TodayBoundary(now).ToString("yyyy-MM-dd");
        }

        private static string OneLine(string value)
        {
            return string.Join(" ", (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static bool LooksLikeBareDate(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return true;
            if (value.IndexOf('。') >= 0 || value.IndexOf('，') >= 0 || value.IndexOf(',') >= 0)
                return false;
            return value.IndexOf("年", StringComparison.Ordinal) >= 0 &&
                   value.IndexOf("月", StringComparison.Ordinal) >= 0 &&
                   value.IndexOf("日", StringComparison.Ordinal) >= 0 &&
                   value.Length <= 14;
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
