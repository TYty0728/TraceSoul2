using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Prompts;

namespace TraceSoul2.Logic
{
    public static class InnerLifeLogic
    {
        public const string ContinuationPrefix = "续上：";
        private const long LiveFragmentMaxAgeMs = 6L * 60L * 60L * 1000L;

        private static readonly HashSet<string> AllowedAttentionKinds =
            new HashSet<string>(new[] { "topic", "activity", "concern" });

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
                UpdatedUnixMs = nowUnixMs,
                Asleep = false,
                Idle = false
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
                // 旧字段保留在数据库中，但新的内心不再把碎片写成未完成事项。
                UnfinishedIntent = string.Empty,
                Attention = proposed == null || proposed.attention == null
                    ? PruneAttention(CloneAttention(current.Attention), nowUnixMs, current.UpdatedUnixMs)
                    : NormalizeAttention(proposed.attention, current.Attention, sourceMomentId, nowUnixMs),
                SourceMomentId = sourceMomentId,
                UpdatedUnixMs = nowUnixMs,
                Asleep = ResolveAsleep(current, proposed),
                Idle = ResolveIdle(current, proposed)
            };
        }

        /// <summary>
        /// 决策卡 → 内心写集。inner 是一拍感受，scene 是共同场景，attention 是会代谢的浮动碎片。
        /// 真实对话会让旧碎片沉下去；时间醒来可以让尚有温度的碎片短暂留在背景。
        /// </summary>
        public static InnerRuntimeWriteData ProposeFromMind(
            MindDecisionData mind,
            InnerRuntimeData current,
            bool settleOldFragments = false)
        {
            mind = MindLogic.Normalize(mind);
            current = current ?? new InnerRuntimeData();
            var proposed = new InnerRuntimeWriteData { attention = null };
            var inner = OneLine(mind.inner);
            var currentNarrative = (current.Narrative ?? string.Empty).Trim();
            if (inner.Length > 0 && !string.Equals(inner, currentNarrative, StringComparison.Ordinal))
                proposed.narrative = inner;
            var scene = mind.SceneValue();
            if (mind.ClearsScene())
                proposed.ongoing_activity = string.Empty;
            else if (scene.Length > 0)
                proposed.ongoing_activity = Limit(scene, 160);
            if (mind.mood_changed && !string.IsNullOrWhiteSpace(mind.mood))
                proposed.mood = Limit(mind.mood.Trim(), 80);
            if (mind.ClearsAttention())
            {
                proposed.attention = new List<AttentionWriteData>();
            }
            else
            {
                var held = mind.ParseAttention();
                // 普通对话是新的相处时刻。没有被这一刻重新碰亮的碎片，
                // 不再因为模型省略字段而自动续命；它们可以留在记忆里，
                // 但不继续占据当前心智。
                if (settleOldFragments || held.Count > 0)
                {
                    proposed.attention = held.Select(x => new AttentionWriteData
                    {
                        kind = ClassifyAttention(x),
                        content = x
                    }).ToList();
                }
            }
            if (mind.sleep)
            {
                proposed.asleep = true;
                proposed.idle = false;
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
                   proposed.attention != null ||
                   proposed.asleep.HasValue ||
                   proposed.idle.HasValue;
        }

        public static string Format(InnerRuntimeData runtime)
        {
            if (runtime == null) return CorePrompts.InnerLife.RuntimeMissing;
            var builder = new StringBuilder();
            builder.Append(CorePrompts.InnerLife.NowPrefix).AppendLine(runtime.Narrative);
            builder.Append(CorePrompts.InnerLife.MoodPrefix).AppendLine(Blank(runtime.Mood));
            builder.Append(CorePrompts.InnerLife.RelationshipPrefix).AppendLine(Blank(runtime.RelationshipLens));
            builder.Append(CorePrompts.InnerLife.OngoingPrefix).AppendLine(Blank(runtime.OngoingActivity));
            builder.Append(CorePrompts.InnerLife.StatePrefix).AppendLine(PresenceLabel(runtime));
            builder.Append(CorePrompts.InnerLife.AttentionPrefix).AppendLine();
            builder.Append(FormatFragments(runtime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return builder.ToString().TrimEnd();
        }

        /// <summary>心智动态段：上一拍的感受和仍浮着的碎片；正在做改走生活状态，这里不再平行复述。</summary>
        public static string FormatForMind(InnerRuntimeData runtime)
        {
            var builder = new StringBuilder();
            var narrative = runtime == null ? string.Empty : OneLine(runtime.Narrative);
            var mood = runtime == null ? string.Empty : (runtime.Mood ?? string.Empty).Trim();
            builder.Append(CorePrompts.InnerLife.LastInnerPrefix).Append(narrative.Length == 0 ? CorePrompts.InnerLife.Empty : narrative);
            if (mood.Length > 0) builder.Append(CorePrompts.InnerLife.LastMoodWrapPrefix).Append(mood).Append("）");
            builder.AppendLine();
            builder.Append(CorePrompts.InnerLife.LastHoldPrefix).AppendLine();
            builder.Append(FormatFragments(runtime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            if (runtime != null && runtime.Asleep)
                builder.AppendLine().Append(CorePrompts.InnerLife.LastAsleep);
            else if (runtime != null && runtime.Idle)
                builder.AppendLine().Append(CorePrompts.InnerLife.LastIdle);
            return builder.ToString().TrimEnd();
        }

        public static string PresenceLabel(InnerRuntimeData runtime)
        {
            if (runtime != null && runtime.Asleep) return CorePrompts.InnerLife.Asleep;
            if (runtime != null && runtime.Idle) return CorePrompts.InnerLife.Idle;
            return CorePrompts.InnerLife.Awake;
        }

        public static InnerRuntimeData WithAsleep(
            InnerRuntimeData current,
            bool asleep,
            string sourceMomentId,
            long nowUnixMs)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (asleep)
            {
                if (current.Asleep && !current.Idle) return current;
                return Reduce(current, new InnerRuntimeWriteData { asleep = true, idle = false },
                    PresenceSource(current, sourceMomentId, "sleep-state"), nowUnixMs);
            }
            if (!current.Asleep) return current;
            return Reduce(current, new InnerRuntimeWriteData { asleep = false },
                PresenceSource(current, sourceMomentId, "sleep-state"), nowUnixMs);
        }

        public static InnerRuntimeData WithIdle(
            InnerRuntimeData current,
            bool idle,
            string sourceMomentId,
            long nowUnixMs)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (idle)
            {
                if (current.Idle && !current.Asleep) return current;
                return Reduce(current, new InnerRuntimeWriteData { idle = true, asleep = false },
                    PresenceSource(current, sourceMomentId, "idle-state"), nowUnixMs);
            }
            if (!current.Idle) return current;
            return Reduce(current, new InnerRuntimeWriteData { idle = false },
                PresenceSource(current, sourceMomentId, "idle-state"), nowUnixMs);
        }

        public static InnerRuntimeData WithAwake(
            InnerRuntimeData current,
            string sourceMomentId,
            long nowUnixMs)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (!current.Asleep && !current.Idle) return current;
            return Reduce(current, new InnerRuntimeWriteData { asleep = false, idle = false },
                PresenceSource(current, sourceMomentId, "awake-state"), nowUnixMs);
        }

        public static string FormatHold(InnerRuntimeData runtime)
        {
            if (runtime == null || runtime.Attention == null) return string.Empty;
            return string.Join("、", LiveAttention(runtime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.content))
                .Select(x => OneLine(x.content))
                .Where(x => x.Length > 0)
                .Take(2));
        }

        public static string FormatFragments(InnerRuntimeData runtime, long nowUnixMs)
        {
            var fragments = LiveAttention(runtime, nowUnixMs);
            if (fragments.Count == 0) return CorePrompts.InnerLife.None;
            var fallback = runtime == null ? nowUnixMs : runtime.UpdatedUnixMs;
            var builder = new StringBuilder();
            foreach (var item in fragments.Take(3))
            {
                var stamp = item.UpdatedUnixMs > 0 ? item.UpdatedUnixMs : fallback;
                builder.Append("- [").Append(item.kind ?? "topic").Append("] ")
                    .Append(OneLine(item.content)).Append("（").Append(FragmentAge(stamp, nowUnixMs)).Append("）")
                    .AppendLine();
            }
            return builder.ToString().TrimEnd();
        }

        public static bool HasUnfinished(InnerRuntimeData runtime)
        {
            // 只保留旧接口语义；新的浮动碎片不再被当成续办事项。
            return runtime != null && !string.IsNullOrWhiteSpace(runtime.UnfinishedIntent);
        }

        public static bool HasLiveFragments(InnerRuntimeData runtime)
        {
            return runtime != null && LiveAttention(runtime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Count > 0;
        }

        public static string FormatContinuation(InnerRuntimeData runtime)
        {
            if (!HasUnfinished(runtime)) return string.Empty;
            var unfinished = runtime == null ? string.Empty : OneLine(runtime.UnfinishedIntent);
            return ContinuationPrefix + Limit(unfinished, 80);
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
            string sourceMomentId,
            long nowUnixMs)
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
                        : new List<string>(old.source_refs ?? new List<string>()),
                    UpdatedUnixMs = nowUnixMs
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
                    source_refs = new List<string>(x.source_refs ?? new List<string>()),
                    UpdatedUnixMs = x.UpdatedUnixMs
                }).ToList();
        }

        private static List<AttentionItemData> PruneAttention(
            IEnumerable<AttentionItemData> source,
            long nowUnixMs,
            long fallbackUnixMs)
        {
            return (source ?? Enumerable.Empty<AttentionItemData>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.content))
                .Where(x =>
                {
                    var stamp = x.UpdatedUnixMs > 0 ? x.UpdatedUnixMs : fallbackUnixMs;
                    return stamp <= 0 || nowUnixMs <= stamp || nowUnixMs - stamp <= LiveFragmentMaxAgeMs;
                })
                .Take(3)
                .ToList();
        }

        private static List<AttentionItemData> LiveAttention(InnerRuntimeData runtime, long nowUnixMs)
        {
            if (runtime == null) return new List<AttentionItemData>();
            return PruneAttention(runtime.Attention, nowUnixMs, runtime.UpdatedUnixMs);
        }

        private static string FragmentAge(long stamp, long nowUnixMs)
        {
            if (stamp <= 0 || nowUnixMs <= stamp) return "刚浮起";
            var minutes = (nowUnixMs - stamp) / 60000L;
            if (minutes < 2) return "刚浮起";
            if (minutes < 60) return minutes + "分钟前浮起";
            var hours = minutes / 60L;
            return hours + "小时前浮起";
        }

        private static bool ResolveAsleep(InnerRuntimeData current, InnerRuntimeWriteData proposed)
        {
            if (proposed != null && proposed.asleep.HasValue) return proposed.asleep.Value;
            if (proposed != null && proposed.idle == true) return false;
            return current.Asleep;
        }

        private static bool ResolveIdle(InnerRuntimeData current, InnerRuntimeWriteData proposed)
        {
            var asleep = ResolveAsleep(current, proposed);
            if (asleep) return false;
            if (proposed != null && proposed.idle.HasValue) return proposed.idle.Value;
            return current.Idle;
        }

        private static string PresenceSource(InnerRuntimeData current, string sourceMomentId, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(sourceMomentId)) return sourceMomentId.Trim();
            if (current != null && !string.IsNullOrWhiteSpace(current.SourceMomentId))
                return current.SourceMomentId;
            return fallback;
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
