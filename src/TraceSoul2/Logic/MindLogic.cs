using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
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
            var query = turn.Moment == null ? string.Empty : turn.Moment.Content;
            var embedding = turn.Services == null ? null : turn.Services.Embedding;
            var templates = await MindTemplateLogic.SelectAsync(
                query, embedding, MindTemplateLogic.CandidateCap, cancellationToken);
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", BuildFoundationPrompt(turn)),
                new DeepSeekMessageData("system", BuildTurnPrompt(turn, leaveResult, alreadyLeft, templates)),
                new DeepSeekMessageData("user", query)
            };
            var decided = await DeepSeekStructuredOutputLogic.CompleteAsync<MindDecisionData>(
                llm,
                messages,
                x => x != null && !string.IsNullOrWhiteSpace(Normalize(x).beat),
                "心智决策卡缺少 beat。",
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
            output.mood = Limit((output.mood ?? string.Empty).Trim(), 12);
            output.new_fact = Limit((output.new_fact ?? string.Empty).Trim(), 40);
            output.leave = Limit((output.leave ?? string.Empty).Trim(), 80);
            output.note = Limit((output.note ?? string.Empty).Trim(), 160);
            output.today = Limit((output.today ?? string.Empty).Trim(), 200);
            output.inner = Limit(OneLine(output.inner), 160);
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
            return output;
        }

        private static string BuildFoundationPrompt(TraceTurnContext turn)
        {
            var pair = turn.Services.Storage.LoadPairIdentity();
            var cards = turn.Services.Storage.LoadIdentityCards(turn.ConversationId);
            var builder = new StringBuilder();
            builder.AppendLine(IdentityCardLogic.FormatForMind(cards, pair));
            builder.AppendLine();
            builder.AppendLine("【这一拍怎么想】");
            builder.AppendLine("我先把这一拍想清楚。只写下决定，不写对她说的话，不用动作旁白，不发表情。");
            builder.AppendLine("inner 是这一拍的现在时：一句比较理性的现在进行时，可以带一点温度。换题就写新的；没变就留空，不要把上一句原样抄回。");
            builder.AppendLine("attention 是这一拍还搁在手里的一两件事。换题就换手，不要把上一拍原样抄回；不要把刚结束的事改写成另一件继续捏着。放下后没有了写「无」。");
            builder.AppendLine("cognition 是这一拍真的改了的看法，一句第一人称理解，不超过19字；没改留空。短卡仍不由我改。");
            builder.AppendLine("beat 只填：当下、旧事、出门。出门只填 leave，具体怎么出门由后面按事由去办。");
            builder.AppendLine("一段从手上拿开、坐标变了：review=true 派出复盘，我自己不改短卡。定点复盘不用我派。");
            builder.AppendLine("标签从下面候选里原样勾 0-3 个。候选已按这一句的相近程度排过，越靠前越像。用得上才勾，出门不要勾。有新看法时同时勾相关标签。");
            builder.AppendLine("note 是我给自己开口用的决定，不是台词。today 只有真要往当天轨迹补一句才填，不要填日期。");
            builder.AppendLine();
            builder.AppendLine("只输出一个 JSON 对象：");
            builder.AppendLine("{\"beat\":\"当下|旧事|出门\",\"tags\":\"\",\"query\":\"\",\"mood\":\"\",\"mood_changed\":false,\"archive\":false,\"new_fact\":\"\",\"leave\":\"\",\"note\":\"\",\"today\":\"\",\"inner\":\"\",\"attention\":\"\",\"review\":false,\"cognition\":\"\"}");
            return builder.ToString();
        }

        private static string BuildTurnPrompt(
            TraceTurnContext turn,
            string leaveResult,
            bool alreadyLeft,
            IReadOnlyList<MindTemplate> templates)
        {
            var pair = turn.Services.Storage.LoadPairIdentity();
            var storage = turn.Services.Storage;
            var builder = new StringBuilder();
            builder.AppendLine("现在是 " + TimeLanguageUtil.NaturalNow(DateTimeOffset.Now) + "。");
            builder.AppendLine();
            var runtime = storage.LoadOrCreateInnerRuntime(turn.ConversationId);
            builder.AppendLine(InnerLifeLogic.FormatForMind(runtime));
            builder.AppendLine("这一拍变了才写进 inner；还搁着的才写进 attention。换题就换，不要照抄。");
            var todayItems = storage.GetTodayNewItems(
                turn.ConversationId, TodayBoundary(DateTimeOffset.Now).ToUnixTimeMilliseconds(), 10);
            if (todayItems != null && todayItems.Count > 0)
            {
                builder.AppendLine("今天刚知道的：");
                foreach (var item in todayItems)
                    builder.AppendLine("- " + item.Content);
            }
            var trajectory = storage.LoadDayTrajectory(MemoryDayKey(DateTimeOffset.Now));
            if (trajectory != null && !string.IsNullOrWhiteSpace(trajectory.Text))
                builder.AppendLine("今天我们的轨迹：" + trajectory.Text.Trim());
            builder.AppendLine();
            builder.AppendLine("【可选生命标签】");
            var tags = MemoryRecallLogic.ListTagCandidates(turn, TagCandidateCap);
            if (tags.Count == 0)
                builder.AppendLine("（这一句没有足够接近的标签。）");
            else
            {
                foreach (var tag in tags)
                    builder.AppendLine("- " + tag.Label + "：" + Limit(tag.Definition, 40));
            }
            if (!string.IsNullOrWhiteSpace(leaveResult))
            {
                builder.AppendLine();
                builder.AppendLine("【外出结果】");
                builder.AppendLine(leaveResult.Trim());
            }
            if (alreadyLeft)
                builder.AppendLine("我已经出门过了，beat 只能是 当下 或 旧事，不要再出门。");
            var organized = MindTemplateLogic.Format(templates);
            if (!string.IsNullOrWhiteSpace(organized))
            {
                builder.AppendLine();
                builder.AppendLine(organized);
            }
            builder.AppendLine();
            builder.AppendLine("【此刻】");
            if (turn.Wake == KernelWakeValues.Mind)
            {
                builder.AppendLine("时间把我叫醒。先看上一拍手上还在不在，再看当前时；没有要对她说的就静默，不要硬找话说。");
            }
            else
            {
                builder.AppendLine(turn.RequiresExpression
                    ? pair.Apply("这是 {username} 正在对我说话。我想好这一拍，不要写台词。")
                    : "这是后台感知。可以静默；没有要说的就 beat=当下，note 写静默。");
            }
            return builder.ToString();
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
