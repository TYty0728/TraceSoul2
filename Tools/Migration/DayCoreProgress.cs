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
            public int SourceVersion;
            public Dictionary<string, List<string>> EntrySources = new();
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
            foreach (var entry in savedEntries)
                evidence[entry.Id] = state.SourceVersion == 0 ? chunk
                    : ResolveEventSources(state.EntrySources.GetValueOrDefault(entry.Id), chunk);
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
            ObservationProgress state, List<DeepSeekMessageData> messages,
            List<MomentRecord> chunk = null, List<EventIndexRecord> candidates = null)
        {
            state ??= new ObservationProgress();
            EnsureCoreProgress(context, source);
            if (state.Output == null)
            {
                state.SourceVersion = chunk == null ? 0 : 1;
                state.Output = await DeepSeekStructuredOutputLogic.CompleteAsync<ReplayPrompts.DayEventOutputData>(
                    llm, messages, null, "事件观察输出缺少 perception_summary。", CancellationToken.None,
                    validationError: x => ObservationStructureError(x, chunk, candidates), includeInvalidResponse: false);
            }
            var structureError = ObservationStructureError(state.Output, state.SourceVersion == 0 ? null : chunk, candidates);
            if (structureError != null) throw new InvalidOperationException(structureError);
            EnsureCoreProgress(context, source);
            // The validated structure and all good fields survive a failed prose repair.
            context.Store.SavePluginDocument(CoreProgressDocuments, key, TraceJson.ToJson(state));
            var targets = EventProseParts(state.Output).Where(x => (x.Value?.Trim().Length ?? 0) > 80).ToList();
            if (targets.Count > 0)
            {
                var batch = chunk ?? context.Store.GetEvidenceMoments(source.MomentIds);
                var targetSources = targets.Select(x => {
                    var parts = x.Key.Split(':');
                    var ids = parts[0] == "append" ? state.Output.event_appends[int.Parse(parts[1])].source_moment_ids
                        : state.Output.event_writes[int.Parse(parts[1])].source_moment_ids;
                    var focus = parts[0] == "append" ? state.Output.event_appends[int.Parse(parts[1])].event_focus
                        : state.Output.event_writes[int.Parse(parts[1])].event_focus;
                    return new { id = x.Key, focus, source_ids = (state.SourceVersion == 0 ? batch : ResolveEventSources(ids, batch))
                        .Select(m => m.Id).ToList() };
                }).ToList();
                var needed = targetSources.SelectMany(x => x.source_ids).ToHashSet(StringComparer.Ordinal);
                var raw = await llm.CompleteJsonAsync(new List<DeepSeekMessageData> {
                    new("system", "【事件短句定向重写】只为所列id根据对应原句重新写一句话，每条目标40字、最多80字。write中的event写这一件事，entry或append写本次事实与新进展，不罗列全部细节。保留明确发生的事实，不补造人物、动作或结果。其他字段与来源由程序保留。每个id恰好一项，只输出JSON {\"items\":[{\"id\":\"所给id\",\"body\":\"完整短句\"}]}。"),
                    new("user", TraceJson.ToJson(new { targets = targetSources,
                        sources = batch.Where(x => needed.Contains(x.Id)).Select(x => new { x.Id, x.Role, x.Content }) }))
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

        private static List<MomentRecord> ResolveEventSources(List<string> ids, List<MomentRecord> chunk)
        {
            if (ids == null || ids.Count == 0 || ids.Distinct().Count() != ids.Count ||
                ids.Any(id => !chunk.Any(m => m.Id == id)))
                throw new InvalidOperationException("事件原句须从本批逐项选择，不能缺失、重复或引用批外来源。");
            return chunk.Where(x => ids.Contains(x.Id)).ToList();
        }

        private static string ObservationStructureError(ReplayPrompts.DayEventOutputData output,
            List<MomentRecord> chunk, List<EventIndexRecord> candidates)
        {
            if (string.IsNullOrWhiteSpace(output?.perception_summary)) return "perception_summary必须非空。";
            if (chunk == null) return null; // Existing drafts retain their original, explicitly legacy scope.
            if (output.event_writes == null || output.event_appends == null) return "event_writes和event_appends须为数组。";
            if (output.event_writes.Count + output.event_appends.Count > 12) return "本批事件最多12项；只记录新事实与转折。";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in output.event_writes)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.event_summary) || string.IsNullOrWhiteSpace(item.entry_summary))
                    return "event_writes每项须有非空event_summary和entry_summary。";
                if (string.IsNullOrWhiteSpace(item.event_focus) || item.event_focus.Trim().Length > 40)
                    return "event_focus须有1～40字，点明这一件事，不能逐条铺写细节。";
                try { ResolveEventSources(item.source_moment_ids, chunk); } catch (InvalidOperationException ex) { return ex.Message; }
                if (!seen.Add("new:" + item.entry_summary.Trim())) return "同一批中重复条目须合成一项，不重复记述。";
            }
            foreach (var item in output.event_appends)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.entry_summary) ||
                    !(candidates ?? new()).Any(x => x.Id == item.index_alias)) return "event_appends必须引用所给已有索引且写出本次进展。";
                if (string.IsNullOrWhiteSpace(item.event_focus) || item.event_focus.Trim().Length > 40)
                    return "event_focus须有1～40字，点明本次新增事实。";
                try { ResolveEventSources(item.source_moment_ids, chunk); } catch (InvalidOperationException ex) { return ex.Message; }
                if (!seen.Add(item.index_alias + ":" + item.entry_summary.Trim())) return "同一索引的同一进展只记一次。";
            }
            return null;
        }

        private sealed class DetailDraft { public string Stamp; public string Body; }
        private static async Task<string> CompleteDetailAsync(MigrationContext context, ILlmClient llm,
            string key, string stamp, List<DeepSeekMessageData> messages, CancellationToken token)
        {
            var draftKey = key + ":draft";
            var draft = PuzzleViewLogic.Read<DetailDraft>(context.Store.LoadPluginDocument(CoreProgressDocuments, draftKey));
            if (draft != null && draft.Stamp != stamp)
                throw new InvalidOperationException("细节草稿来源已变化，不能直接复用。");
            if (draft == null)
            {
                var output = await DeepSeekStructuredOutputLogic.CompleteAsync<ReplayPrompts.DetailOutputData>(llm,
                    messages, x => !string.IsNullOrWhiteSpace(x?.detail), "细节输出缺少detail。", token,
                    includeInvalidResponse: false);
                draft = new DetailDraft { Stamp = stamp, Body = output.detail.Trim() };
                context.Store.SavePluginDocument(CoreProgressDocuments, draftKey, TraceJson.ToJson(draft));
            }
            if (draft.Body.Length <= 200) return draft.Body;
            // Regenerate from the selected original sources. Never feed the long draft back for polishing.
            var rewrite = new List<DeepSeekMessageData>(messages) {
                new("user", "【单件细节重新取舍】这件事仅保留一两个有辨识度的细节，目标80字、最多200字。只按所给原句重新写，不补写旧经过。输出{\"detail\":\"完整短段\"}。") };
            var raw = await llm.CompleteJsonAsync(rewrite, token);
            var repaired = DeepSeekStructuredOutputLogic.Read<ReplayPrompts.DetailOutputData>(raw);
            if (string.IsNullOrWhiteSpace(repaired?.detail) || repaired.detail.Trim().Length > 200)
                throw new InvalidOperationException("细节仍未形成200字以内的完整正文；已保存草稿，恢复只续做这一项，不截断。");
            draft.Body = repaired.detail.Trim();
            context.Store.SavePluginDocument(CoreProgressDocuments, draftKey, TraceJson.ToJson(draft));
            return draft.Body;
        }
    }
}
