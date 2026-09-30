using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Logic;

namespace TraceSoul2.Manager
{
    public sealed partial class SqliteMemoryManager
    {
        public void AppendDayTrajectory(string conversationId, string sourceMomentId, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(sourceMomentId))
                throw new ArgumentException("轨迹缺少环境或来源。");
            var moment = connection.Find<MomentRecord>(sourceMomentId);
            var operational = moment == null ? connection.Find<OperationalEventRecord>(sourceMomentId) : null;
            if ((moment?.ConversationId ?? operational?.ConversationId) != conversationId)
                throw new InvalidOperationException("轨迹必须归属真实来源环境。");
            var time = moment?.CreatedUnixMs ?? operational.OccurredUnixMs;
            var day = MemoryDayLogic.CurrentDayKey(DateTimeOffset.FromUnixTimeMilliseconds(time));
            connection.RunInTransaction(() =>
            {
                RecoverDayTrajectory(conversationId, day);
                InsertTrajectory(conversationId, day, sourceMomentId, text, time);
            });
        }

        private void InsertTrajectory(string context, string day, string source, string text, long time)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            connection.Insert(new DayTrajectoryEntryRecord { Id = "trajectory:" + context + ":" + source,
                ConversationId = context, DayKey = day, SourceMomentId = source, Text = text.Trim(),
                CreatedUnixMs = time }, "OR IGNORE");
        }

        // Old summaries were replaced, but turn snapshots/slices still retain their original text.
        // Recover only the requested environment; keep original source time and id for replay safety.
        private void RecoverDayTrajectory(string context, string day)
        {
            if (string.IsNullOrWhiteSpace(context) || !MemoryDayLogic.TryStartOf(day, out var start)) return;
            var from = start.ToUnixTimeMilliseconds();
            var until = start.AddDays(1).ToUnixTimeMilliseconds();
            foreach (var slice in connection.Table<RuntimeSliceRecord>()
                .Where(x => x.ContextConversationId == context && x.DayKey == day).ToList())
                InsertTrajectory(context, day, slice.MomentId, Today(slice.SnapshotJson), slice.CreatedUnixMs);
            foreach (var review in connection.Query<TurnReviewRecord>(
                "SELECT r.* FROM turn_reviews r LEFT JOIN moments m ON m.Id=r.TriggerMomentId " +
                "LEFT JOIN operational_events o ON o.Id=r.TriggerMomentId WHERE r.ConversationId=? " +
                "AND COALESCE(m.CreatedUnixMs,o.OccurredUnixMs)>=? AND COALESCE(m.CreatedUnixMs,o.OccurredUnixMs)<?", context, from, until))
            {
                var moment = connection.Find<MomentRecord>(review.TriggerMomentId);
                var operational = moment == null ? connection.Find<OperationalEventRecord>(review.TriggerMomentId) : null;
                if ((moment?.ConversationId ?? operational?.ConversationId) != context) continue;
                var time = moment?.CreatedUnixMs ?? operational.OccurredUnixMs;
                if (time < from || time >= until) continue;
                InsertTrajectory(context, day, review.TriggerMomentId, Today(review.PayloadJson, true), time);
            }
            // Legacy unscoped rows are private. Never import them into a public environment.
            var legacy = context.Contains(":environment:", StringComparison.Ordinal) ? null : LoadDayTrajectory(day);
            if (!string.IsNullOrWhiteSpace(legacy?.Text) && !connection.Table<DayTrajectoryEntryRecord>()
                .Where(x => x.ConversationId == context && x.DayKey == day).ToList().Any(x => x.Text == legacy.Text.Trim()))
                InsertTrajectory(context, day, "legacy:" + day, legacy.Text, legacy.UpdatedUnixMs);
        }

        private static string Today(string json, bool snapshot = false)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (snapshot && !root.TryGetProperty("agent_decision", out root))
                    root = doc.RootElement.GetProperty("mind_decision");
                if (snapshot && root.ValueKind == JsonValueKind.Null)
                    root = doc.RootElement.GetProperty("mind_decision");
                return root.TryGetProperty("today", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
        }

        public List<DayTrajectoryEntryRecord> GetDayTrajectoryEntries(string context, string day, bool includeRetired = false)
        {
            connection.RunInTransaction(() => RecoverDayTrajectory(context, day));
            return connection.Table<DayTrajectoryEntryRecord>().Where(x => x.ConversationId == context && x.DayKey == day)
                .OrderBy(x => x.CreatedUnixMs).ThenBy(x => x.Id).ToList()
                .Where(x => includeRetired || !x.Retired).ToList();
        }
    }
}
