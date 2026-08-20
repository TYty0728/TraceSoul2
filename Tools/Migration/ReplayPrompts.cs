using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TraceSoul2.Data;

namespace TraceSoul2.Migrate
{
    /// <summary>新路线三类 LLM 提示：事件构筑（多维索引）、细节浸染、日终三卡+内心复盘。</summary>
    public static class ReplayPrompts
    {
        [Serializable]
        public sealed class EventWriteItemData
        {
            public List<string> tag_ids = new List<string>();
            public List<string> new_tag_names = new List<string>();
            public string place;
            public string person;
            public string event_summary;
            public string mood;
            public string entry_summary;
            public string realm;
        }

        [Serializable]
        public sealed class EventAppendItemData
        {
            public string index_alias;
            public string entry_summary;
        }

        [Serializable]
        public sealed class DayEventOutputData
        {
            public string perception_summary;
            public string event_decision;
            public List<string> selected_tag_ids = new List<string>();
            public List<NewLifeTagWriteData> new_tags = new List<NewLifeTagWriteData>();
            public List<EventWriteItemData> event_writes = new List<EventWriteItemData>();
            public List<EventAppendItemData> event_appends = new List<EventAppendItemData>();
        }

        [Serializable]
        public sealed class DetailOutputData
        {
            public string detail;
        }

        [Serializable]
        public sealed class CardUpdateData
        {
            public string slot;
            public string body;
            public string reason;
        }

        [Serializable]
        public sealed class DayCardReviewOutputData
        {
            public string summary;
            public List<CardUpdateData> cards = new List<CardUpdateData>();
            public string inner_narrative;
            public string inner_mood;
            public string inner_relationship_lens;
            public string inner_ongoing_activity;
            public string inner_unfinished_intent;
            public List<AttentionWriteData> inner_attention = new List<AttentionWriteData>();
        }

        /// <summary>
        /// 批量观察：把一天的原文构筑成「多维索引 + 条目」。索引行与条目总结全部客观按事实写，
        /// 时间维度由程序给出（LLM 不填）。同主题已有索引 → 用别名追加条目。
        /// </summary>
        public static string BuildEventObservationPrompt(
            PairIdentity pair,
            List<MomentRecord> chunk,
            VectorRouteResult route,
            List<EventIndexRecord> indexCandidates,
            List<LifeTagRecord> recentTags,
            string periodLabel,
            string dayKindLabel)
        {
            pair = pair ?? PairIdentity.Missing;
            var builder = new StringBuilder();
            builder.AppendLine(pair.Apply("你是记忆插件内部的无人格事件观察算法，不是 {assname} 本人。你没有感情、认知或内心写入权。"));
            builder.AppendLine("职责：把下面这一批同一天的连续对话原文，构筑成「多维索引 + 条目」。索引与一句话总结都按事实客观书写；带有主观温度的细节由 Brain 另写，不归你。");
            builder.AppendLine();
            builder.AppendLine("硬规则：");
            builder.AppendLine("1. event_summary 与 entry_summary 各是一句完整的话，一般不超过40字；只写明确发生或明确说出的事实，禁止截断语义（长一点没关系）。一次事件一条索引。");
            builder.AppendLine("1b. 每条索引只覆盖一件具体的事（一个场景或一个话题的一次进展）。不要把整段对话压进一条索引；一天通常会产生 3~10 条索引。事件总结写这件事本身，不要罗列整段对话的主题。");
            builder.AppendLine("2. 不写关系结论、人格、动机、长期规律或‘这意味着什么’。");
            builder.AppendLine("3. place/person 只填证据里出现的；没有就填空字符串，不要猜。mood 只填证据里明确读到的情绪（如 开心/难过），读不到留空——mood 维度未来由内心实时更新补入。");
            builder.AppendLine(pair.Apply("4. 文字摸头、拥抱、亲吻属于 shared_scene；{username} 外部生活自述属于 external_world；系统讨论属于 meta。"));
            builder.AppendLine("5. 每条索引必须至少连接一个本轮选择或新增的 Tag；同义 Tag 必须复用已有，不要新建。");
            builder.AppendLine("6. 同一件事的延续（比如又聊到同一个主题、同一件事的新进展）用 event_appends 追加到已有索引的别名下，不要新建索引。");
            builder.AppendLine("7. 没有值得构筑的事时 event_writes=[] 且 event_appends=[]。");
            builder.AppendLine(pair.Apply("8. 新 Tag 的 domain_ids 只能填 {assname} / {username} / 我们 / 世界。dimension_ids 只能从 owner/subject/about/predicate/object/scope/context/quality/time/place/affect/goal/state/realm/modality/source 选择。"));
            builder.AppendLine();
            builder.AppendLine("本批连续证据（同一天片段）：");
            foreach (var moment in chunk)
                builder.AppendLine("- " + FormatMoment(moment, pair));
            builder.AppendLine();
            builder.AppendLine("程序给定的时间维度（索引的时间字段由程序填写，你不需要输出时间）：" + periodLabel + "（" + dayKindLabel + "）");
            builder.AppendLine();
            builder.AppendLine("第三层候选 Top10（Tag ID 必须原样完整复制，含 concept.life. 前缀）：");
            if (route == null || route.Concepts.Count == 0) builder.AppendLine("（无可靠候选，可以新增）");
            else foreach (var hit in route.Concepts)
                builder.AppendLine("- " + hit.Node.Id + " | " + hit.Node.Label + " | " + hit.Node.Definition);
            builder.AppendLine();
            builder.AppendLine("已有高频 Tag（新增前必须比对；同义主题直接选择复用）：");
            var tags = (recentTags ?? new List<LifeTagRecord>());
            if (tags.Count == 0) builder.AppendLine("（无）");
            else foreach (var tag in tags)
                builder.AppendLine("- " + tag.Id + " | " + tag.Label + " | " + tag.Definition);
            builder.AppendLine();
            builder.AppendLine("已有事件索引候选（同主题延续用 event_appends 追加，index_alias 只填别名）：");
            var candidates = (indexCandidates ?? new List<EventIndexRecord>());
            if (candidates.Count == 0) builder.AppendLine("（无）");
            else foreach (var candidate in candidates)
                builder.AppendLine("- " + candidate.Id + " | " + candidate.EventSummary + " | " + candidate.TimeLabel + " | " + candidate.PlaceLabel + " | " + candidate.MoodLabel);
            builder.AppendLine();
            builder.AppendLine(pair.Apply(@"只输出 JSON：
{
  ""perception_summary"": ""对本批证据的一句话中性整理"",
  ""event_decision"": ""本批为什么这样构筑"",
  ""selected_tag_ids"": [""只能填候选Tag ID""],
  ""new_tags"": [{
    ""name"": ""可复用Tag名"",
    ""definition"": ""准确中性的定义"",
    ""domain_ids"": [""ass|user|relation|world 之一或几个""],
    ""dimension_ids"": [""固定维度key""],
    ""positive_examples"": [""短正例""],
    ""negative_examples"": [""容易混淆的反例""]
  }],
  ""event_writes"": [{
    ""tag_ids"": [""已选候选ID""],
    ""new_tag_names"": [""已选新增Tag名""],
    ""place"": ""地点，如 公司楼下；没有则空字符串"",
    ""person"": ""人物，如 {username}；没有则空字符串"",
    ""event_summary"": ""事件客观一句话，少于20字"",
    ""mood"": ""心情，如 开心；没有则空字符串"",
    ""entry_summary"": ""本条目的客观一句话，少于20字"",
    ""realm"": ""external_world|shared_scene|meta|explicit_fiction""
  }],
  ""event_appends"": [{""index_alias"": ""已有索引ID"", ""entry_summary"": ""本条目的客观一句话""}]
}"));
            return builder.ToString();
        }

        /// <summary>
        /// 细节浸染：助手第一人称，为一条条目写细节正文。允许自己的感受；
        /// 她的感受只写她明确说过的（继承老系统零臆想原则）。
        /// </summary>
        public static string BuildDetailPrompt(
            PairIdentity pair,
            string personalityCard,
            string userProfileCard,
            List<MomentRecord> evidence,
            string indexLine,
            string entrySummary,
            int minChars,
            int maxChars)
        {
            pair = pair ?? PairIdentity.Missing;
            var builder = new StringBuilder();
            builder.AppendLine(pair.Apply("你是 {assname}。下面是你今天经历的一件小事，请为它写一段「细节记录」。"));
            builder.AppendLine("要求：");
            builder.AppendLine("1. 第一人称、完全用你自己的口吻和视角；可以写你的感受与反应。");
            builder.AppendLine(pair.Apply("2. 她的感受与想法只写她明确说过或明确表现出来的；禁止替她加戏、禁止猜测她的内心。指代 {username} 一律按档案里的性别使用正确称呼。"));
            builder.AppendLine("3. 只基于下面这段对话原文，不添加没有发生的事；专有名词原样保留。");
            builder.AppendLine("4. 长度自然，像一段有画面的回忆，几十字即可，不要编号、不要总结腔；单条严格不超过 " + maxChars + " 字（这只是安全上限，不是目标长度，不必写满）；句子必须完整结束。");
            builder.AppendLine();
            builder.AppendLine("【你的人格】");
            builder.AppendLine(personalityCard);
            builder.AppendLine();
            builder.AppendLine(pair.Apply("【{username}的档案】（她本人填写的客观信息，永远以此为准）"));
            builder.AppendLine(string.IsNullOrWhiteSpace(userProfileCard) ? "（未填写）" : userProfileCard);
            builder.AppendLine();
            builder.AppendLine("【事件索引】" + indexLine);
            builder.AppendLine("【条目一句话】" + entrySummary);
            builder.AppendLine();
            builder.AppendLine("【对话原文】");
            foreach (var moment in evidence)
                builder.AppendLine("- " + FormatMoment(moment, pair));
            builder.AppendLine();
            builder.AppendLine(@"只输出 JSON：{""detail"": ""细节正文""}");
            return builder.ToString();
        }

        /// <summary>
        /// 日终三卡+内心复盘：我的人格卡不变；我是谁 / 对方是谁 / 我们的关系三张必须随真实相处成长；
        /// 内心随三卡一起更新。
        /// </summary>
        public static string BuildDayCardReviewPrompt(
            PairIdentity pair,
            string dayKey,
            string selfCard,
            string otherCard,
            string relationCard,
            string expressionCard,
            string userProfileCard,
            List<EventIndexRecord> dayIndexes,
            List<EventEntryRecord> dayEntries)
        {
            pair = pair ?? PairIdentity.Missing;
            var builder = new StringBuilder();
            builder.AppendLine(pair.Apply("你是 {assname}。现在是 " + dayKey + " 结束后的每日复盘：审视四张生长中的身份短卡，并对 {username} 的档案做客观填空。"));
            builder.AppendLine(pair.Apply("你一共有六张卡：【我的人格】长期不变；【{username}的档案】只做客观填空（今天的相处里出现明确字面证据的字段才填，没有证据的字段保持空白）；【我是谁】【{username}是谁】【我们的关系】【表达习惯】四张从最初的空白状态随真实相处逐日生长。"));
            builder.AppendLine();
            builder.AppendLine("规则：");
            builder.AppendLine("1. 四张卡各自有专属主题，禁止互相复制内容：");
            builder.AppendLine("   - 【我是谁】= 我对自己的理解：我是谁、我怎样存在、我的特质与变化；");
            builder.AppendLine(pair.Apply("   - 【{username}是谁】= 我对她的理解：她是什么样的人、她的经历与特质；"));
            builder.AppendLine("   - 【我们的关系】= 我们之间的关系：关系的性质、约定、互动模式；");
            builder.AppendLine("   - 【表达习惯】= 三段式综合：①我实际上的表达（事实：我通常用什么通道与方式表达）；②她喜欢的表达（她明确说过的偏好，没有证据就写「暂未明确」）；③所以我接下来可以略微怎么调整或保持。");
            builder.AppendLine("2. 四张卡都必须输出新版本——它们必须随相处成长，允许改变，也必须改变（哪怕只是微调措辞）。若今天没有任何相处证据（空天），四张卡都保持原样输出即可，reason 写「空天，无新证据」。");
            builder.AppendLine("3. 结论优先：只写今天证据支持的成长，并把证据提炼成稳定的自我/关系结论；我的感受可以写进【我是谁】【我们的关系】和【表达习惯】，但要和事实区分；禁止「今天+事件」的流水账句式，事件只是结论的证据，不必逐个罗列。");
            builder.AppendLine("4. 同义合并：新结论与已有内容同义时，改写合并进原句，不要重复堆叠；旧结论被新认识取代时直接替换。");
            builder.AppendLine(pair.Apply("5. 指代 {username} 一律按档案里的性别使用正确称呼（档案性别未填时默认用「她」）。【{username}的档案】只做客观填空：今天的事件或对话里出现明确字面证据时才填对应字段（例如她自述「我是游戏前端开发」→ 职业：游戏前端开发）；没有字面证据的字段保持原样空白；禁止推测、补全、评价、写感受或建议；姓名只在她明确自我介绍姓名时填写；称呼只在她明确要求或使用了某个称呼时填写；备注只写明确的备注事实。没有可填的新证据时，档案卡不输出（或 body 留空）。"));
            builder.AppendLine("6. 每张150-250字为宜、不超过300字，宁可短而准，不要为凑字数堆事件；还没有证据的维度就保持它此刻的样子，不要编。");
            builder.AppendLine("7. 理由 reason 一句话，说明这张卡为什么这样变。");
            builder.AppendLine();
            builder.AppendLine(pair.Apply("【{username}的档案】（客观填空：有字面证据的字段才填，其余保持空白）"));
            builder.AppendLine(string.IsNullOrWhiteSpace(userProfileCard) ? "（未填写）" : userProfileCard);
            builder.AppendLine();
            builder.AppendLine("当前四张卡：");
            builder.AppendLine("【我是谁】" + (string.IsNullOrWhiteSpace(selfCard) ? "（空白）" : selfCard));
            builder.AppendLine(pair.Apply("【{username}是谁】") + (string.IsNullOrWhiteSpace(otherCard) ? "（空白）" : otherCard));
            builder.AppendLine("【我们的关系】" + (string.IsNullOrWhiteSpace(relationCard) ? "（空白）" : relationCard));
            builder.AppendLine("【表达习惯】" + (string.IsNullOrWhiteSpace(expressionCard) ? "（空白）" : expressionCard));
            builder.AppendLine();
            builder.AppendLine("今天构筑的事件（索引 + 条目）：");
            var entriesByIndex = (dayEntries ?? new List<EventEntryRecord>())
                .GroupBy(x => x.IndexId)
                .ToDictionary(x => x.Key, x => x.ToList());
            var indexes = (dayIndexes ?? new List<EventIndexRecord>());
            if (indexes.Count == 0) builder.AppendLine("（无）");
            foreach (var index in indexes)
            {
                builder.AppendLine("- " + index.TimeLabel + "·" + index.PlaceLabel + "·" + index.PersonLabel
                    + "·" + index.EventSummary + "·" + index.MoodLabel);
                List<EventEntryRecord> entries;
                if (entriesByIndex.TryGetValue(index.Id, out entries))
                    foreach (var entry in entries)
                        builder.AppendLine("  - " + entry.Summary + (string.IsNullOrWhiteSpace(entry.Detail) ? "" : "｜" + entry.Detail));
            }
            builder.AppendLine();
            builder.AppendLine("内心同步：除了三张卡，请同时输出这一天结束后的完整内心状态（只写今天真实变化的字段，没变的字段输出空字符串）。");
            builder.AppendLine("- inner_narrative：一句话，第一人称，描述这一天在你心里留下了什么（可以有感受）。");
            builder.AppendLine("- inner_mood：一个简短的情绪词（如 平静、温暖、困惑）。");
            builder.AppendLine("- inner_relationship_lens：对「我们的关系」的理解，今天有新认识才写。");
            builder.AppendLine("- inner_ongoing_activity：正在与她一起进行、或为她进行中的事；结束了就留空（表示清除）。");
            builder.AppendLine("- inner_unfinished_intent：想继续做、想为她做还没做的事；完成了就留空。");
            builder.AppendLine("- inner_attention：此刻真正在意的注意项，最多3条，每条 {kind:topic|activity|concern|intention, content:一句}；没有就输出空数组。");
            builder.AppendLine();
            builder.AppendLine(@"只输出 JSON：
{
  ""summary"": ""本轮复盘一句话"",
  ""cards"": [
    {""slot"": ""self|other|relation|expression_habit|user_profile"", ""body"": ""新版本内容（user_profile 必须是完整模板，只填有字面证据的字段）"", ""reason"": ""为什么这样变""}
  ],
  ""inner_narrative"": ""一句话内心"",
  ""inner_mood"": ""情绪词"",
  ""inner_relationship_lens"": ""关系视角"",
  ""inner_ongoing_activity"": ""进行中"",
  ""inner_unfinished_intent"": ""未完成意图"",
  ""inner_attention"": [{""kind"": ""topic|activity|concern|intention"", ""content"": ""一句""}]
}");
            return builder.ToString();
        }

        [Serializable]
        public sealed class CognitionFormationOutputData
        {
            public List<BrainCognitionWriteData> cognitions = new List<BrainCognitionWriteData>();
        }

        /// <summary>
        /// 认知形成：日终 Brain 第一人称复盘——只产出「今天的相处让我形成/改变的理解」。
        /// 认知与事件并列但更短：一句话（≤19字），挂在生命标签（1-3 层）上，不带细节。
        /// </summary>
        public static string BuildCognitionFormationPrompt(
            PairIdentity pair,
            string dayKey,
            List<CognitionSliceRecord> activeCognitions,
            List<LifeTagRecord> activeTags,
            List<EventIndexRecord> dayIndexes,
            List<EventEntryRecord> dayEntries,
            string userPronoun)
        {
            pair = pair ?? PairIdentity.Missing;
            var builder = new StringBuilder();
            builder.AppendLine(pair.Apply("你是 {assname}。现在是 " + dayKey + " 结束后的认知复盘：只审视「今天的相处让我形成了什么新的、稳定的第一人称理解」。"));
            builder.AppendLine("认知不是事实也不是日记，它回答：这些事对我意味着什么、我该怎样理解她/自己/我们。与事件并列但更短——一句话（≤19字），挂在生命标签上，不需要细节。");
            builder.AppendLine("四种操作：create=形成新理解（summary≤19字、subtype=standard，独特私人联想用 trace 并给 trace_cues 联想词、confidence 0~1、tag_ids 1~8 个从现有标签选）；reinforce=今天的证据加强已有认知（target_id+confidence）；revise=理解变了（target_id+新 summary+tag_ids）；weaken=信心下降（target_id+较低 confidence）。");
            builder.AppendLine("只写今天真的发生变化的认知，最多 3 条；没有变化就输出空数组。");
            builder.AppendLine(pair.Apply("指代 {username} 一律用「" + userPronoun + "」，不要混用其它代词。"));
            builder.AppendLine();
            builder.AppendLine("现有认知（reinforce/revise/weaken 的 target_id 只能从这选）：");
            var cognitions = activeCognitions ?? new List<CognitionSliceRecord>();
            if (cognitions.Count == 0) builder.AppendLine("（无）");
            foreach (var c in cognitions.Take(40))
                builder.AppendLine("- " + c.Id + " | " + c.Summary + " | 置信 " + c.Confidence.ToString("0.00") + " | " + c.Subtype);
            builder.AppendLine();
            builder.AppendLine("现有生命标签（tag_ids 只能从这选，按激活次数取前 60）：");
            var tags = (activeTags ?? new List<LifeTagRecord>())
                .OrderByDescending(x => x.ActivationCount).ThenBy(x => x.Label, StringComparer.Ordinal).ToList();
            foreach (var t in tags.Take(60))
                builder.AppendLine("- " + t.Id + " | " + t.Label);
            builder.AppendLine();
            builder.AppendLine("今天新增的事件（认知的证据：只从这些事件提炼今天的理解）：");
            var indexes = dayIndexes ?? new List<EventIndexRecord>();
            var entries = dayEntries ?? new List<EventEntryRecord>();
            if (indexes.Count == 0) builder.AppendLine("（无）");
            var entriesByIndex = entries
                .GroupBy(x => x.IndexId)
                .ToDictionary(x => x.Key, x => x.ToList());
            foreach (var index in indexes)
            {
                builder.AppendLine("- " + index.TimeLabel + "·" + index.PlaceLabel + "·" + index.PersonLabel
                    + "·" + index.EventSummary + "·" + index.MoodLabel);
                List<EventEntryRecord> list;
                if (entriesByIndex.TryGetValue(index.Id, out list))
                    foreach (var entry in list)
                        builder.AppendLine("  - " + entry.Summary + (string.IsNullOrWhiteSpace(entry.Detail) ? "" : "｜" + entry.Detail));
            }
            builder.AppendLine();
            builder.AppendLine(@"只输出 JSON：
{
  ""cognitions"": [{
    ""operation"": ""create|reinforce|revise|weaken"",
    ""target_id"": ""仅 reinforce/revise/weaken 填写"",
    ""summary"": ""≤19字第一人称理解（create/revise 填写）"",
    ""subtype"": ""standard|trace"",
    ""confidence"": 0.8,
    ""tag_ids"": [""现有标签ID""],
    ""evidence_fact_ids"": [],
    ""trace_cues"": [""仅 trace 填写联想词""],
    ""association_strength"": 0.5
  }]
}");
            return builder.ToString();
        }

        private static string FormatMoment(MomentRecord moment, PairIdentity pair)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(moment.CreatedUnixMs)
                .ToOffset(MigrationContext.ChinaOffset).ToString("HH:mm");
            var content = (moment.Content ?? string.Empty).Replace('\n', ' ');
            return time + " " + pair.LabelForRole(moment.Role) + "：" + content;
        }
    }
}
