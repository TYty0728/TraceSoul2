using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;

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
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", BuildPrompt(pair, cards, dayMoments, innerNarrative)),
                new DeepSeekMessageData("user", "根据今天的相处，修订需要改的身份短卡。")
            };
            return DeepSeekStructuredOutputLogic.CompleteAsync<IdentityReviewOutputData>(
                llm,
                messages,
                x => x != null && !string.IsNullOrWhiteSpace(x.summary),
                "身份复盘缺少 summary。",
                cancellationToken);
        }

        private static string BuildPrompt(
            PairIdentity pair,
            IReadOnlyList<IdentityCardRecord> cards,
            IEnumerable<MomentRecord> dayMoments,
            string innerNarrative)
        {
            pair = pair ?? PairIdentity.Missing;
            var builder = new StringBuilder();
            builder.AppendLine(pair.Apply("你在帮助 {assname} 维护身份短卡。这不是日记，也不是记忆网。"));
            builder.AppendLine("每天都要审视生长中的五张卡（我是谁/她是谁/我们的关系/表达习惯/她的档案）。没有坐标变化时，对应短卡 changed=false，body 留空。");
            builder.AppendLine("我的人格：长期气质与相处方式。它改得最慢，不要把自我理解写进这里。");
            builder.AppendLine(pair.Apply("我是谁：{assname} 眼中的自己，不是人格。今天的自我理解变清楚了就修订这里。"));
            builder.AppendLine(pair.Apply("{username}是谁：{assname} 眼中的 {username}。称呼习惯写在这里，不写今天发生的事。"));
            builder.AppendLine("我们的关系：已经共同确认的关系定义，不是今晚的心情。");
            builder.AppendLine("表达习惯：三段式综合——①我实际上的表达（事实）；②她喜欢的表达（她明确说过的偏好，没有就写「暂未明确」）；③所以我接下来略微调整或保持什么。");
            builder.AppendLine(pair.Apply("她的档案：只做客观填空。今天的 Moment 里出现明确字面证据时才填对应字段（例如她自述「我是游戏前端开发」→ 职业：游戏前端开发）；没有字面证据的字段保持原样空白；禁止推测、补全、评价、写感受或建议；姓名只在她明确自我介绍姓名时填写；称呼只在她明确要求或使用了某个称呼时填写；备注只写明确的备注事实。body 必须是完整模板行（姓名/性别/生日/职业/居住地/互相的称呼/备注），未填的行保留「字段名：」空白。"));
            builder.AppendLine("写法规则：结论优先（事件只是结论的证据，禁止「今天+事件」流水账句式）；同义合并（与已有内容同义就改写合并进原句，不重复堆叠，被取代的直接替换）；150-250字为宜，宁可短而准，不要为凑字数堆事件。");
            builder.AppendLine("不要把今天吃了什么、说了哪句气话、侧躺抱着睡这类生活细节写进短卡。那些走记忆网点亮。");
            builder.AppendLine();
            builder.AppendLine("当前短卡：");
            builder.AppendLine(IdentityCardLogic.FormatForExpressor(cards, pair));
            builder.AppendLine();
            builder.AppendLine("此刻内心一句：");
            builder.AppendLine(string.IsNullOrWhiteSpace(innerNarrative) ? "（无）" : innerNarrative.Trim());
            builder.AppendLine();
            builder.AppendLine("今天进入生命的 Moment（仅作修订证据）：");
            var moments = (dayMoments ?? Enumerable.Empty<MomentRecord>()).ToList();
            if (moments.Count == 0) builder.AppendLine("（今天几乎没有新的相处）");
            foreach (var moment in moments.Take(60))
            {
                var content = (moment.Content ?? string.Empty).Trim();
                if (content.Length > 80) content = content.Substring(0, 80);
                builder.Append("- ").Append(pair.LabelForRole(moment.Role)).Append("：").AppendLine(content);
            }
            builder.AppendLine();
            builder.AppendLine(@"只输出 JSON：
{
  ""summary"": ""今天身份坐标有无变化的一句话"",
  ""cards"": [{
    ""slot"": ""personality|self|other|relation|expression_habit|user_profile"",
    ""changed"": false,
    ""body"": ""仅 changed=true 时填写完整短卡"",
    ""reason"": ""为何改或不改""
  }]
}");
            return builder.ToString();
        }
    }
}
