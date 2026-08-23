using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Prompts;

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
            builder.AppendLine(pair.Apply(CorePrompts.Migration.EventRole));
            builder.AppendLine(CorePrompts.Migration.EventDuty);
            builder.AppendLine();
            CorePrompts.Write(builder, pair.Apply(CorePrompts.Migration.EventRules));
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.EventEvidenceHeader);
            foreach (var moment in chunk)
                builder.AppendLine("- " + FormatMoment(moment, pair));
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.EventTimePrefix + periodLabel + "（" + dayKindLabel + "）");
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.EventLayer3Header);
            if (route == null || route.Concepts.Count == 0) builder.AppendLine(CorePrompts.Migration.NoReliableTags);
            else foreach (var hit in route.Concepts)
                builder.AppendLine("- " + hit.Node.Id + " | " + hit.Node.Label + " | " + hit.Node.Definition);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.EventFrequentTagsHeader);
            var tags = (recentTags ?? new List<LifeTagRecord>());
            if (tags.Count == 0) builder.AppendLine(CorePrompts.Migration.Empty);
            else foreach (var tag in tags)
                builder.AppendLine("- " + tag.Id + " | " + tag.Label + " | " + tag.Definition);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.EventIndexCandidatesHeader);
            var candidates = (indexCandidates ?? new List<EventIndexRecord>());
            if (candidates.Count == 0) builder.AppendLine(CorePrompts.Migration.Empty);
            else foreach (var candidate in candidates)
                builder.AppendLine("- " + candidate.Id + " | " + candidate.EventSummary + " | " + candidate.TimeLabel + " | " + candidate.PlaceLabel + " | " + candidate.MoodLabel);
            builder.AppendLine();
            CorePrompts.Write(builder, pair.Apply(CorePrompts.Migration.EventJsonSchema));
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
            builder.AppendLine(pair.Apply(CorePrompts.Migration.DetailRole));
            CorePrompts.Write(builder, pair.Apply(CorePrompts.Migration.DetailRules));
            builder.AppendLine(CorePrompts.Migration.DetailLengthRule(maxChars));
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.PersonalityHeader);
            builder.AppendLine(personalityCard);
            builder.AppendLine();
            builder.AppendLine(pair.Apply(CorePrompts.Migration.UserProfileHeader));
            builder.AppendLine(string.IsNullOrWhiteSpace(userProfileCard) ? CorePrompts.Migration.Unfilled : userProfileCard);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.EventIndexPrefix + indexLine);
            builder.AppendLine(CorePrompts.Migration.EntrySummaryPrefix + entrySummary);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.DialogueHeader);
            foreach (var moment in evidence)
                builder.AppendLine("- " + FormatMoment(moment, pair));
            builder.AppendLine();
            CorePrompts.Write(builder, CorePrompts.Migration.DetailJsonSchema);
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
            builder.AppendLine(pair.Apply(CorePrompts.Migration.DayCardRole(dayKey)));
            builder.AppendLine(pair.Apply(CorePrompts.Migration.DayCardIntro));
            builder.AppendLine();
            CorePrompts.Write(builder, pair.Apply(CorePrompts.Migration.DayCardRules));
            builder.AppendLine();
            builder.AppendLine(pair.Apply(CorePrompts.Migration.DayCardProfileHeader));
            builder.AppendLine(string.IsNullOrWhiteSpace(userProfileCard) ? CorePrompts.Migration.Unfilled : userProfileCard);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.DayCardCurrentHeader);
            builder.AppendLine(CorePrompts.Migration.DayCardSelfHeader + (string.IsNullOrWhiteSpace(selfCard) ? CorePrompts.Migration.BlankCard : selfCard));
            builder.AppendLine(pair.Apply(CorePrompts.Migration.DayCardOtherHeader) + (string.IsNullOrWhiteSpace(otherCard) ? CorePrompts.Migration.BlankCard : otherCard));
            builder.AppendLine(CorePrompts.Migration.DayCardRelationHeader + (string.IsNullOrWhiteSpace(relationCard) ? CorePrompts.Migration.BlankCard : relationCard));
            builder.AppendLine(CorePrompts.Migration.DayCardHabitHeader + (string.IsNullOrWhiteSpace(expressionCard) ? CorePrompts.Migration.BlankCard : expressionCard));
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.DayCardEventsHeader);
            var entriesByIndex = (dayEntries ?? new List<EventEntryRecord>())
                .GroupBy(x => x.IndexId)
                .ToDictionary(x => x.Key, x => x.ToList());
            var indexes = (dayIndexes ?? new List<EventIndexRecord>());
            if (indexes.Count == 0) builder.AppendLine(CorePrompts.Migration.Empty);
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
            CorePrompts.Write(builder, CorePrompts.Migration.DayCardInner);
            builder.AppendLine();
            CorePrompts.Write(builder, CorePrompts.Migration.DayCardJsonSchema);
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
            builder.AppendLine(pair.Apply(CorePrompts.Migration.CognitionRole(dayKey)));
            CorePrompts.Write(builder, CorePrompts.Migration.CognitionBody);
            builder.AppendLine(pair.Apply(CorePrompts.Migration.CognitionPronoun(userPronoun)));
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.CognitionExistingHeader);
            var cognitions = activeCognitions ?? new List<CognitionSliceRecord>();
            if (cognitions.Count == 0) builder.AppendLine(CorePrompts.Migration.Empty);
            foreach (var c in cognitions.Take(40))
                builder.AppendLine("- " + c.Id + " | " + c.Summary + " | 置信 " + c.Confidence.ToString("0.00") + " | " + c.Subtype);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.CognitionTagsHeader);
            var tags = (activeTags ?? new List<LifeTagRecord>())
                .OrderByDescending(x => x.ActivationCount).ThenBy(x => x.Label, StringComparer.Ordinal).ToList();
            foreach (var t in tags.Take(60))
                builder.AppendLine("- " + t.Id + " | " + t.Label);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.CognitionEventsHeader);
            var indexes = dayIndexes ?? new List<EventIndexRecord>();
            var entries = dayEntries ?? new List<EventEntryRecord>();
            if (indexes.Count == 0) builder.AppendLine(CorePrompts.Migration.Empty);
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
            CorePrompts.Write(builder, CorePrompts.Migration.CognitionJsonSchema);
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
