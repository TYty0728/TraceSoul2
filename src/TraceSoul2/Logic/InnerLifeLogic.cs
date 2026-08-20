using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TraceSoul2.Data;

namespace TraceSoul2.Logic
{
    public static class InnerLifeLogic
    {
        public const string ContinuationPrefix = "续上：";

        private static readonly HashSet<string> AllowedAttentionKinds =
            new HashSet<string>(new[] { "topic", "activity", "concern", "intention" });

        public static InnerRuntimeData CreateInitial(string conversationId, long nowUnixMs)
        {
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("conversationId 不能为空。", "conversationId");
            return new InnerRuntimeData
            {
                ConversationId = conversationId.Trim(),
                SnapshotId = Guid.NewGuid().ToString("N"),
                Revision = 0,
                Narrative = "我刚刚恢复运行，正在重新感受此刻。",
                RelationshipLens = "我会根据真实相处逐渐形成对我们的理解。",
                Mood = "平静",
                OngoingActivity = string.Empty,
                UnfinishedIntent = string.Empty,
                Attention = new List<AttentionItemData>(),
                SourceMomentId = string.Empty,
                UpdatedUnixMs = nowUnixMs
            };
        }

        public static InnerRuntimeData Reduce(
            InnerRuntimeData current,
            InnerRuntimeWriteData proposed,
            string sourceMomentId,
            long nowUnixMs)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (string.IsNullOrWhiteSpace(sourceMomentId))
                throw new ArgumentException("sourceMomentId 不能为空。", "sourceMomentId");

            return new InnerRuntimeData
            {
                ConversationId = current.ConversationId,
                SnapshotId = Guid.NewGuid().ToString("N"),
                Revision = checked(current.Revision + 1),
                Narrative = KeepOrLimit(proposed == null ? null : proposed.narrative, current.Narrative, 300),
                RelationshipLens = KeepOrLimit(proposed == null ? null : proposed.relationship_update, current.RelationshipLens, 200),
                Mood = KeepOrLimit(proposed == null ? null : proposed.mood, current.Mood, 80),
                OngoingActivity = KeepOrLimit(proposed == null ? null : proposed.ongoing_activity, current.OngoingActivity, 120, true),
                UnfinishedIntent = KeepOrLimit(proposed == null ? null : proposed.unfinished_intent, current.UnfinishedIntent, 160, true),
                Attention = proposed == null || proposed.attention == null
                    ? CloneAttention(current.Attention)
                    : NormalizeAttention(proposed.attention, current.Attention, sourceMomentId),
                SourceMomentId = sourceMomentId,
                UpdatedUnixMs = nowUnixMs
            };
        }

        /// <summary>
        /// 决策卡 → 内心写集。空字段表示不改；attention 空列表表示放下；未给 attention 表示手上保持原样。
        /// </summary>
        public static InnerRuntimeWriteData ProposeFromMind(MindDecisionData mind, InnerRuntimeData current)
        {
            mind = MindLogic.Normalize(mind);
            current = current ?? new InnerRuntimeData();
            var proposed = new InnerRuntimeWriteData { attention = null };
            var inner = OneLine(mind.inner);
            var currentNarrative = (current.Narrative ?? string.Empty).Trim();
            if (inner.Length > 0 && !string.Equals(inner, currentNarrative, StringComparison.Ordinal))
            {
                proposed.narrative = inner;
                proposed.ongoing_activity = inner;
            }
            if (mind.mood_changed && !string.IsNullOrWhiteSpace(mind.mood))
                proposed.mood = Limit(mind.mood.Trim(), 80);
            if (mind.ClearsAttention())
            {
                proposed.attention = new List<AttentionWriteData>();
                proposed.unfinished_intent = string.Empty;
            }
            else
            {
                var held = mind.ParseAttention();
                var currentHold = FormatHold(current);
                if (held.Count > 0 &&
                    !string.Equals(string.Join("、", held), currentHold, StringComparison.Ordinal))
                {
                    proposed.attention = held.Select(x => new AttentionWriteData
                    {
                        kind = ClassifyAttention(x),
                        content = x
                    }).ToList();
                    proposed.unfinished_intent = string.Join("、", held);
                }
            }
            return proposed;
        }

        public static bool HasProposedWrite(InnerRuntimeWriteData proposed)
        {
            if (proposed == null) return false;
            return proposed.narrative != null ||
                   proposed.mood != null ||
                   proposed.relationship_update != null ||
                   proposed.ongoing_activity != null ||
                   proposed.unfinished_intent != null ||
                   proposed.attention != null;
        }

        public static string Format(InnerRuntimeData runtime)
        {
            if (runtime == null) return "（内心 Runtime 尚未建立）";
            var builder = new StringBuilder();
            builder.Append("此刻：").AppendLine(runtime.Narrative);
            builder.Append("情绪：").AppendLine(Blank(runtime.Mood));
            builder.Append("关系视角：").AppendLine(Blank(runtime.RelationshipLens));
            builder.Append("进行中：").AppendLine(Blank(runtime.OngoingActivity));
            builder.Append("未完成意图：").AppendLine(Blank(runtime.UnfinishedIntent));
            builder.Append("注意：");
            if (runtime.Attention == null || runtime.Attention.Count == 0)
                builder.Append("（无）");
            else
                foreach (var item in runtime.Attention.Take(3))
                    builder.AppendLine().Append("- [").Append(item.kind).Append("] ").Append(item.content);
            return builder.ToString().TrimEnd();
        }

        /// <summary>心智动态段：上一拍刚写下的当前时、未完成、手上。空不改，所以要看见上一版。</summary>
        public static string FormatForMind(InnerRuntimeData runtime)
        {
            var builder = new StringBuilder();
            var narrative = runtime == null ? string.Empty : OneLine(runtime.Narrative);
            var mood = runtime == null ? string.Empty : (runtime.Mood ?? string.Empty).Trim();
            var ongoing = runtime == null ? string.Empty : OneLine(runtime.OngoingActivity);
            var unfinished = runtime == null ? string.Empty : OneLine(runtime.UnfinishedIntent);
            builder.Append("上一拍当前时：").Append(narrative.Length == 0 ? "（空）" : narrative);
            if (mood.Length > 0) builder.Append("（情绪：").Append(mood).Append("）");
            builder.AppendLine();
            if (ongoing.Length > 0 && !string.Equals(ongoing, narrative, StringComparison.Ordinal))
                builder.Append("上一拍进行中：").AppendLine(ongoing);
            builder.Append("上一拍未完成：").AppendLine(unfinished.Length == 0 ? "（空）" : unfinished);
            builder.Append("上一拍手上：").Append(FormatHold(runtime).Length == 0 ? "（空）" : FormatHold(runtime));
            return builder.ToString().TrimEnd();
        }

        public static string FormatHold(InnerRuntimeData runtime)
        {
            if (runtime == null || runtime.Attention == null) return string.Empty;
            return string.Join("、", runtime.Attention
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.content))
                .Select(x => OneLine(x.content))
                .Where(x => x.Length > 0)
                .Take(2));
        }

        public static bool HasUnfinished(InnerRuntimeData runtime)
        {
            if (runtime == null) return false;
            if (!string.IsNullOrWhiteSpace(runtime.UnfinishedIntent)) return true;
            return FormatHold(runtime).Length > 0;
        }

        public static string FormatContinuation(InnerRuntimeData runtime)
        {
            if (!HasUnfinished(runtime)) return string.Empty;
            var unfinished = runtime == null ? string.Empty : OneLine(runtime.UnfinishedIntent);
            var hold = FormatHold(runtime);
            var body = unfinished.Length > 0 ? unfinished : hold;
            return ContinuationPrefix + Limit(body, 80);
        }

        public static bool IsContinuationContent(string content)
        {
            var value = (content ?? string.Empty).Trim();
            if (value.StartsWith(ContinuationPrefix, StringComparison.Ordinal)) return true;
            const string due = "时间任务到期：";
            return value.StartsWith(due, StringComparison.Ordinal) &&
                   value.IndexOf(ContinuationPrefix, StringComparison.Ordinal) >= due.Length;
        }

        public static long InferContinuationDueUnixMs(string text, DateTimeOffset now)
        {
            var value = text ?? string.Empty;
            if (value.IndexOf("明天", StringComparison.Ordinal) >= 0)
            {
                var next = new DateTimeOffset(now.Year, now.Month, now.Day, 10, 0, 0, now.Offset).AddDays(1);
                return next.ToUnixTimeMilliseconds();
            }
            if (value.IndexOf("今晚", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("夜里", StringComparison.Ordinal) >= 0)
            {
                var tonight = new DateTimeOffset(now.Year, now.Month, now.Day, 20, 0, 0, now.Offset);
                if (tonight <= now) tonight = tonight.AddDays(1);
                return tonight.ToUnixTimeMilliseconds();
            }
            return now.AddMinutes(20).ToUnixTimeMilliseconds();
        }

        public static string ClassifyAttention(string content)
        {
            var value = content ?? string.Empty;
            if (ContainsAny(value, "答应", "记得", "明天", "还没", "回头", "等我", "别忘", "未完成", "待会", "稍后"))
                return "intention";
            if (ContainsAny(value, "正在", "讲到", "唱到", "做到", "写到", "看到一半"))
                return "activity";
            if (ContainsAny(value, "担心", "怕", "惦记"))
                return "concern";
            return "topic";
        }

        public static string AttentionKindFromField(string fieldName)
        {
            var name = (fieldName ?? string.Empty).Trim();
            const string prefix = "attention_";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "attention_clear", StringComparison.OrdinalIgnoreCase))
                return "topic";
            var kind = name.Substring(prefix.Length).Trim().ToLowerInvariant();
            return AllowedAttentionKinds.Contains(kind) ? kind : "topic";
        }

        private static List<AttentionItemData> NormalizeAttention(
            IEnumerable<AttentionWriteData> proposed,
            IEnumerable<AttentionItemData> current,
            string sourceMomentId)
        {
            var existing = (current ?? Enumerable.Empty<AttentionItemData>()).ToList();
            var result = new List<AttentionItemData>();
            foreach (var item in proposed ?? Enumerable.Empty<AttentionWriteData>())
            {
                if (item == null) continue;
                var kind = (item.kind ?? string.Empty).Trim().ToLowerInvariant();
                var content = (item.content ?? string.Empty).Trim();
                if (content.Length == 0) continue;
                if (!AllowedAttentionKinds.Contains(kind)) kind = "topic";
                content = Limit(content, 160);
                if (result.Any(x => x.kind == kind && x.content == content)) continue;
                var old = existing.FirstOrDefault(x => x != null && x.kind == kind && x.content == content);
                result.Add(new AttentionItemData
                {
                    kind = kind,
                    content = content,
                    source_refs = old == null
                        ? new List<string> { "moment:" + sourceMomentId }
                        : new List<string>(old.source_refs ?? new List<string>())
                });
                if (result.Count == 3) break;
            }
            return result;
        }

        private static List<AttentionItemData> CloneAttention(IEnumerable<AttentionItemData> source)
        {
            return (source ?? Enumerable.Empty<AttentionItemData>())
                .Where(x => x != null)
                .Take(3)
                .Select(x => new AttentionItemData
                {
                    kind = x.kind,
                    content = x.content,
                    source_refs = new List<string>(x.source_refs ?? new List<string>())
                }).ToList();
        }

        private static string KeepOrLimit(string proposed, string current, int max, bool allowExplicitEmpty = false)
        {
            if (proposed == null) return current ?? string.Empty;
            var value = proposed.Trim();
            if (value.Length == 0 && !allowExplicitEmpty) return current ?? string.Empty;
            return Limit(value, max);
        }

        private static string Blank(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "（无）" : value;
        }

        private static string OneLine(string value)
        {
            return string.Join(" ", (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            foreach (var token in tokens)
                if (value.IndexOf(token, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
