using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Host;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Migrate
{
    public static partial class DayBuilder
    {
        private const string CoreProgressDocuments = "daily-core-progress-v1";
        public sealed class CoreProgressSource
        {
            public List<string> MomentIds = new();
            public string Stamp;
            public string Generation;
        }
        public sealed class ObservationProgress
        {
            public ReplayPrompts.DayEventOutputData Output;
            public string ContextStamp;
            public List<string> IndexIds = new();
            public List<string> EntryIds = new();
            public string RecordsStamp;
            public bool Applied;
        }
        public sealed class CompletedProgress { public string Stamp; public string BodyStamp; }

        private static string ProgressKey(string day, string generation, string kind, object source)
            => day + ":" + generation + ":" + kind + ":" + Digest(source);

        private static CoreProgressSource OpenCoreProgress(MigrationContext context, string day,
            List<MomentRecord> moments, DayCoreCheckpoint previous)
        {
            var key = "source:" + day;
            var text = context.Store.LoadPluginDocument(CoreProgressDocuments, key);
            var current = moments.Select(x => x.Id).ToList();
            var state = string.IsNullOrEmpty(text) ? null : TraceJson.FromJson<CoreProgressSource>(text);
            if (state != null && !state.MomentIds.SequenceEqual(current))
            {
                if (previous == null || state.MomentIds.Any(x => !previous.MomentIds.Contains(x)))
                    throw new InvalidOperationException("部分复盘的来源集合已变化；已保存结果保留，请核对来源后再恢复，不能整日重复写入。");
                state = null; // Completed core plus late evidence starts a distinct generation.
            }
            if (state == null)
            {
                if (context.Migration.HasEventEntriesFrom(current))
                    throw new InvalidOperationException(DailyBuildPreflight.LegacyRecoveryRequiredMessage);
                state = new CoreProgressSource { MomentIds = current,
                    Stamp = CoreSourceStamp(context, current), Generation = Guid.NewGuid().ToString("N") };
                context.Store.SavePluginDocument(CoreProgressDocuments, key, TraceJson.ToJson(state));
            }
            EnsureCoreProgress(context, state);
            return state;
        }

        private static void EnsureCoreProgress(MigrationContext context, CoreProgressSource source)
        {
            if (source.Stamp != CoreSourceStamp(context, source.MomentIds))
                throw new InvalidOperationException("部分复盘的原始来源或身份已修订，旧输出不能提交或直接复用。");
        }

        private static string CoreSourceStamp(MigrationContext context, IEnumerable<string> ids)
        {
            var wanted = ids.ToList();
            return Digest(new { Source = SourceStamp(context, wanted),
                Roles = wanted.Chunk(256).SelectMany(context.Store.GetEvidenceMoments)
                    .OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => new { x.Id, x.Role }),
                Slices = context.Store.GetRuntimeSlicesForMoments(wanted)
                    .OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => new {
                        x.Id, x.RootConversationId, x.ContextConversationId, x.DayKey, x.MomentId,
                        x.MemoryVisibility, x.CreatedUnixMs, x.SnapshotJson }) });
        }

        private static string ObservationContextStamp(MigrationContext context)
            => Digest(new { Tags = context.Store.GetActiveLifeTags().OrderBy(x => x.Id, StringComparer.Ordinal),
                Indexes = context.Migration.GetActiveEventIndexes().OrderBy(x => x.Id, StringComparer.Ordinal) });

        private static string ObservationRecordsStamp(IEnumerable<EventIndexRecord> indexes,
            IEnumerable<EventEntryRecord> entries)
            => Digest(new { Indexes = indexes.OrderBy(x => x.Id).Select(x => new {
                x.Id, x.TagIds, x.TimeLabel, x.DayKindLabel, x.TimeUnixMs, x.PlaceLabel,
                x.PersonLabel, x.EventSummary, x.MoodLabel, x.FirstMomentId, x.Status }),
                Entries = entries.OrderBy(x => x.Id).Select(x => new {
                    x.Id, x.IndexId, x.Summary, x.SourceMomentId, x.Realm }) });

        private static bool RestoreObservation(MigrationContext context, CoreProgressSource source,
            ObservationProgress state, List<EventIndexRecord> indexes, List<EventEntryRecord> entries,
            Dictionary<string, List<MomentRecord>> evidence, List<MomentRecord> chunk)
        {
            if (state?.Applied != true) return false;
            EnsureCoreProgress(context, source);
            var savedIndexes = context.Migration.GetActiveEventIndexes().Where(x => state.IndexIds.Contains(x.Id)).ToList();
            var savedEntries = context.Migration.GetEntriesByIndexIds(state.IndexIds).Where(x => state.EntryIds.Contains(x.Id)).ToList();
            if (savedIndexes.Count != state.IndexIds.Count || savedEntries.Count != state.EntryIds.Count ||
                state.RecordsStamp != ObservationRecordsStamp(savedIndexes, savedEntries))
                throw new InvalidOperationException("事件检查点对应的记录已修订或缺失，不能重复构筑替代。");
            foreach (var index in savedIndexes) if (!indexes.Any(x => x.Id == index.Id)) indexes.Add(index);
            entries.AddRange(savedEntries);
            foreach (var entry in savedEntries) evidence[entry.Id] = chunk;
            return true;
        }

        private static bool RestoreDetail(MigrationContext context, CoreProgressSource source,
            string key, string stamp, EventEntryRecord entry)
        {
            var saved = context.Store.LoadPluginDocument(CoreProgressDocuments, key);
            if (string.IsNullOrEmpty(saved))
            {
                if (!string.IsNullOrWhiteSpace(entry.Detail))
                    throw new InvalidOperationException("已有细节缺少来源检查点，不能覆盖或重复生成。");
                return false;
            }
            EnsureCoreProgress(context, source);
            var state = TraceJson.FromJson<CompletedProgress>(saved);
            if (state.Stamp != stamp || string.IsNullOrWhiteSpace(entry.Detail) || state.BodyStamp != Digest(entry.Detail))
                throw new InvalidOperationException("细节检查点的来源、身份或正文已修订，不能直接复用。");
            return true;
        }

        private static bool RestoreCognition(MigrationContext context, CoreProgressSource source, string key, string stamp)
        {
            var saved = context.Store.LoadPluginDocument(CoreProgressDocuments, key);
            if (string.IsNullOrEmpty(saved)) return false;
            EnsureCoreProgress(context, source);
            if (TraceJson.FromJson<CompletedProgress>(saved).Stamp != stamp)
                throw new InvalidOperationException("认知检查点的来源已修订，不能重复提交。");
            return true;
        }

        private static Dictionary<string, string> EventProseParts(ReplayPrompts.DayEventOutputData output)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < (output.event_writes?.Count ?? 0); i++)
            {
                if (output.event_writes[i] == null) continue;
                result["write:" + i + ":event"] = output.event_writes[i].event_summary;
                result["write:" + i + ":entry"] = output.event_writes[i].entry_summary;
            }
            for (var i = 0; i < (output.event_appends?.Count ?? 0); i++)
                if (output.event_appends[i] != null) result["append:" + i] = output.event_appends[i].entry_summary;
            return result;
        }

        private static async Task<ReplayPrompts.DayEventOutputData> CompleteObservationAsync(
            MigrationContext context, ILlmClient llm, CoreProgressSource source, string key,
            ObservationProgress state, List<DeepSeekMessageData> messages)
        {
            state ??= new ObservationProgress();
            EnsureCoreProgress(context, source);
            if (state.Output == null)
                state.Output = await DeepSeekStructuredOutputLogic.CompleteAsync<ReplayPrompts.DayEventOutputData>(
                    llm, messages, x => !string.IsNullOrWhiteSpace(x.perception_summary),
                    "事件观察输出缺少 perception_summary。", CancellationToken.None);
            EnsureCoreProgress(context, source);
            // The validated structure and all good fields survive a failed prose repair.
            context.Store.SavePluginDocument(CoreProgressDocuments, key, TraceJson.ToJson(state));
            var targets = EventProseParts(state.Output).Where(x => (x.Value?.Trim().Length ?? 0) > 80).ToList();
            if (targets.Count > 0)
            {
                var raw = await llm.CompleteJsonAsync(new List<DeepSeekMessageData> {
                    new("system", "【事件短句定向重写】只重写所列id对应的一句话摘要，每条目标40字、最多80字。保留明确发生的事实，不补造人物、动作或结果。其他字段与来源由程序保留。每个id恰好一项，只输出JSON {\"items\":[{\"id\":\"所给id\",\"body\":\"完整短句\"}]}。"),
                    new("user", TraceJson.ToJson(targets.Select(x => new { id = x.Key, original = x.Value })))
                }, CancellationToken.None);
                var repair = DeepSeekStructuredOutputLogic.Read<ProseRepairs>(raw);
                if (repair?.items == null || repair.items.Count != targets.Count ||
                    repair.items.Any(x => x == null || !targets.Any(t => t.Key == x.id) || string.IsNullOrWhiteSpace(x.body)) ||
                    repair.items.Select(x => x.id).Distinct().Count() != targets.Count)
                    throw new InvalidOperationException("短句修复须完整对应待整理字段；已有合格内容已保存，恢复后只续做短句。");
                foreach (var item in repair.items)
                {
                    var parts = item.id.Split(':');
                    if (parts[0] == "append") state.Output.event_appends[int.Parse(parts[1])].entry_summary = item.body.Trim();
                    else if (parts[2] == "event") state.Output.event_writes[int.Parse(parts[1])].event_summary = item.body.Trim();
                    else state.Output.event_writes[int.Parse(parts[1])].entry_summary = item.body.Trim();
                }
                EnsureCoreProgress(context, source);
                context.Store.SavePluginDocument(CoreProgressDocuments, key, TraceJson.ToJson(state));
            }
            var error = EventProseError(state.Output);
            if (error != null) throw new InvalidOperationException(error + " 已保存事件草稿，恢复后只续做未合格短句。");
            return state.Output;
        }
    }
}
