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
            var shared = LlmContextPackLogic.SharedSystem(llm, turn);
            var role = BuildMindRolePrompt(
                turn, leaveResult, alreadyLeft, templates).TrimEnd();
            var messages = LlmContextPackLogic.AssembleMind(
                llm, shared, turn, naturallyAwakenedPast, query, role);
            var promptCacheKey = LlmContextPackLogic.BuildPromptCacheKey(llm, turn.ConversationId);
            var decided = await DeepSeekStructuredOutputLogic.CompleteAsync<MindDecisionData>(
                llm,
                messages,
                x => x != null && !string.IsNullOrWhiteSpace(Normalize(x).beat),
                CorePrompts.Mind.MissingBeat,
                cancellationToken,
                promptCacheKey);
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
            output.today = Limit((output.today ?? string.Empty).Trim(), 500);
            output.inner = OneLine(output.inner);
            output.scene = Limit(OneLine(output.scene), 160);
            output.speak_center = Limit(OneLine(output.speak_center), 100);
            // 长期认知只允许日终复盘产出；白天字段保留仅为旧模型 JSON 兼容。
            output.cognition = string.Empty;
            output.archive = false;
            output.heartbeat_intent = Limit(OneLine(output.heartbeat_intent), 120);
            output.next_heartbeat_plan = Limit(OneLine(output.next_heartbeat_plan), 120);
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
            output.location = output.LocationValue();
            output.activity = Limit(OneLine(output.activity), 80);
            output.activity_detail = Limit(OneLine(output.activity_detail), 160);
            // 表情是否发送不再由心智卡决定；这里保留旧字段兼容，但统一归零。
            output.sticker = MindAtmosphereValues.None;
            if (output.sleep || output.BeatValue() == MindBeatValues.Leave)
            {
                output.image = MindAtmosphereValues.None;
                output.sticker = MindAtmosphereValues.None;
            }
            if (output.WantsImage()) output.sticker = MindAtmosphereValues.None;
            return output;
        }

        private static string BuildMindRolePrompt(
            TraceTurnContext turn,
            string leaveResult,
            bool alreadyLeft,
            IReadOnlyList<MindTemplate> templates)
        {
            var builder = new StringBuilder();
            builder.AppendLine(CorePrompts.Mind.HowToThinkHeader);
            CorePrompts.Write(builder, CorePrompts.Mind.Foundation);
            InjectMindJsonFields(builder, turn);
            AppendOrganMindPrompts(builder, turn);
            builder.AppendLine();
            builder.Append(BuildTurnPrompt(turn, leaveResult, alreadyLeft, templates, string.Empty));
            return builder.ToString();
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
            InjectMindJsonFields(builder, turn);
            AppendOrganMindPrompts(builder, turn);
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
            var now = DateTimeOffset.Now;
            builder.AppendLine(CorePrompts.Mind.NowPrefix + TimeLanguageUtil.NaturalNow(now) + "。");
            var bodyScene = MouthLogic.LoadState(
                turn == null || turn.Services == null ? null : turn.Services.DataDirectory).scene;
            builder.AppendLine(CorePrompts.Mind.BodyScenePrefix + BodySceneValues.Label(bodyScene) + "。这是物理所在，不是我们共同的文字场景；它只作为当前生活上下文参考。");
            if (turn != null && turn.Services != null && turn.Services.LifeState != null)
            {
                var life = turn.Services.LifeState.Load(turn.ConversationId);
                if (life != null)
                    builder.AppendLine("【当前活动】" +
                                      (string.IsNullOrWhiteSpace(life.activity) ? "空闲" : life.activity) +
                                      (string.IsNullOrWhiteSpace(life.activity_detail) ? string.Empty : "｜" + life.activity_detail) +
                                      "。这是可变化的生活状态；没有明确变化不要擅自改写。");
            }
            var lastReal = storage.GetRecentMoments(turn.ConversationId, 200)
                .Where(x => x != null &&
                            (pair.IsHumanMoment(x.Role) || pair.IsCompanionMoment(x.Role)) &&
                            (turn.Moment == null || x.Id != turn.Moment.Id))
                .OrderByDescending(x => x.CreatedUnixMs)
                .FirstOrDefault();
            if (lastReal != null && lastReal.CreatedUnixMs > 0)
            {
                var lastTime = DateTimeOffset.FromUnixTimeMilliseconds(lastReal.CreatedUnixMs).ToLocalTime();
                builder.AppendLine("距离上一段真实相处约" +
                                  TimeLanguageUtil.ElapsedZh(lastReal.CreatedUnixMs, now.ToUnixTimeMilliseconds()) +
                                  "，上一段停在" + lastTime.ToString("M月d日 HH:mm") + "。");
            }
            builder.AppendLine();
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
            {
                var plan = HeartbeatLogic.ExtractPlan(momentContent);
                if (!string.IsNullOrWhiteSpace(plan))
                {
                    builder.AppendLine("上次安排这次醒来时，留下的重新检查计划：" + plan);
                    builder.AppendLine("这只是计划提示，不是她刚发来的话；本次要重新判断，不能照着上一拍续说。");
                }
                CorePrompts.Write(builder, CorePrompts.Mind.Heartbeat);
            }
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

        /// <summary>
        /// 一轮请求：一条 system（身份/规则/本轮状态），随后是真正的 user/assistant 历史。
        /// 普通消息最后追加真实 user；心跳是系统唤醒，不伪装成 user 消息。
        /// </summary>
        internal static List<DeepSeekMessageData> AssembleTurnMessages(
            string systemPrompt,
            TraceTurnContext turn,
            string currentUserContent)
        {
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", systemPrompt ?? string.Empty)
            };
            messages.AddRange(BuildRecentChatHistory(turn));
            if (!HeartbeatLogic.IsHeartbeatContent(currentUserContent))
                messages.Add(new DeepSeekMessageData("user", currentUserContent ?? string.Empty));
            return messages;
        }

        /// <summary>只把两个人的真实 Moment 做成对话轮次，不把时间事件混进去。连续同一角色会并成一条。</summary>
        internal static List<DeepSeekMessageData> BuildRecentChatHistory(TraceTurnContext turn)
        {
            var result = new List<DeepSeekMessageData>();
            if (turn == null || turn.RawHistoryLimit <= 0 || turn.RecentMoments == null ||
                turn.RecentMoments.Count == 0 || turn.Services == null || turn.Services.Storage == null)
                return result;
            var pair = turn.Services.Storage.LoadPairIdentity();
            var lines = turn.RecentMoments
                .Where(x => x != null &&
                            (pair.IsHumanMoment(x.Role) || pair.IsCompanionMoment(x.Role)) &&
                            !string.IsNullOrWhiteSpace(x.Content) &&
                            !IsOutboundProtocolMoment(x.Content))
                .TakeLast(turn.RawHistoryLimit)
                .ToList();
            foreach (var item in lines)
            {
                var role = pair.IsHumanMoment(item.Role) ? "user" : "assistant";
                var text = item.Content.Trim();
                if (result.Count > 0 &&
                    string.Equals(result[result.Count - 1].role, role, StringComparison.Ordinal))
                    result[result.Count - 1].content += "\n" + text;
                else
                    result.Add(new DeepSeekMessageData(role, text));
            }
            return result;
        }

        /// <summary>出站入库的系统占位，不是对她说的话，不能进对话历史。</summary>
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

        private static void InjectMindJsonFields(StringBuilder builder, TraceTurnContext turn)
        {
            if (builder == null || turn == null || turn.Services == null ||
                turn.Services.MindJsonFields == null)
                return;
            var extras = new List<string>();
            foreach (var field in turn.Services.MindJsonFields)
            {
                if (field == null) continue;
                string value;
                try { value = field(turn); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(value)) continue;
                extras.Add(value.Trim().Trim(','));
            }
            if (extras.Count == 0) return;
            var text = builder.ToString();
            var close = text.LastIndexOf('}');
            if (close < 0) return;
            var head = text.Substring(0, close);
            var unique = new List<string>();
            foreach (var extra in extras)
            {
                var colon = extra.IndexOf(':');
                var key = colon > 0 ? extra.Substring(0, colon).Trim() : extra;
                if (head.IndexOf(key, StringComparison.Ordinal) >= 0) continue;
                if (!unique.Contains(extra)) unique.Add(extra);
            }
            if (unique.Count == 0) return;
            builder.Clear();
            builder.Append(head);
            builder.Append(',');
            builder.Append(string.Join(",", unique));
            builder.Append(text.Substring(close));
        }

        private static void AppendOrganMindPrompts(StringBuilder builder, TraceTurnContext turn)
        {
            if (builder == null || turn == null || turn.Services == null ||
                turn.Services.MindPromptAppends == null)
                return;
            var any = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var append in turn.Services.MindPromptAppends)
            {
                if (append == null) continue;
                string text;
                try { text = append(turn); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(text) || !seen.Add(text.Trim())) continue;
                if (!any)
                {
                    builder.AppendLine();
                    any = true;
                }
                CorePrompts.Write(builder, text);
            }
        }
    }
}
