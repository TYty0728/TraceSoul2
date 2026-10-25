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
            public List<DeepSeekMessageData> Messages;
            public List<EventIndexRecord> Candidates;
            public List<LifeTagRecord> Tags;
            public List<string> IndexIds = new();
            public List<string> EntryIds = new();
            public string RecordsStamp;
            public bool Applied;
        }
        public sealed class CompletedProgress { public string Stamp; public string BodyStamp; public bool ContentRejected; }

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

        private static string ObservationContextStamp(MigrationContext context, ObservationProgress snapshot = null)
        {
            if (snapshot?.Tags == null || snapshot.Candidates == null)
                return Digest(new { Tags = context.Store.GetActiveLifeTags().OrderBy(x => x.Id, StringComparer.Ordinal),
                    Indexes = context.Migration.GetActiveEventIndexes().OrderBy(x => x.Id, StringComparer.Ordinal) });
            var tagIds = snapshot.Tags.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            var indexIds = snapshot.Candidates.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            return Digest(new {
                Tags = context.Store.GetActiveLifeTags().Where(x => tagIds.Contains(x.Id)).OrderBy(x => x.Id, StringComparer.Ordinal)
                    .Select(x => new { x.Id, x.Label, x.Definition, x.Status, x.Origin, x.SourceMomentId }),
                Indexes = context.Migration.GetActiveEventIndexes().Where(x => indexIds.Contains(x.Id)).OrderBy(x => x.Id, StringComparer.Ordinal)
                    .Select(x => new { x.Id, x.TagIds, x.EventSummary, x.TimeLabel, x.DayKindLabel, x.TimeUnixMs, x.PlaceLabel, x.PersonLabel, x.MoodLabel, x.FirstMomentId, x.Status }) });
        }

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
            state.Output = await ReviewWorkLogic.CompleteAsync<ReplayPrompts.DayEventOutputData>(context.Store,
                key + ":prose", llm, messages, x => ObservationStructureError(x, state.SourceVersion == 0 ? null : chunk, candidates),
                ObservationParts, CancellationToken.None, state.Output);
            EnsureCoreProgress(context, source);
            context.Store.SavePluginDocument(CoreProgressDocuments, key, TraceJson.ToJson(state));
            return state.Output;
        }

        private static IEnumerable<ReviewWorkLogic.TextPart> ObservationParts(ReplayPrompts.DayEventOutputData output)
        {
            var i = 0;
            foreach (var item in output.event_writes ?? new())
            {
                var path = "event_writes[" + i++ + "].";
                yield return new(path + "event_summary", 120, () => item.event_summary, v => item.event_summary = v);
                yield return new(path + "entry_summary", 120, () => item.entry_summary, v => item.entry_summary = v);
                yield return new(path + "event_focus", 80, () => item.event_focus, v => item.event_focus = v);
            }
            i = 0;
            foreach (var item in output.event_appends ?? new())
            {
                var path = "event_appends[" + i++ + "].";
                yield return new(path + "entry_summary", 120, () => item.entry_summary, v => item.entry_summary = v);
                yield return new(path + "event_focus", 80, () => item.event_focus, v => item.event_focus = v);
            }
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
                if (string.IsNullOrWhiteSpace(item.event_focus))
                    return "event_focus须有1～40字，点明这一件事，不能逐条铺写细节。";
                try { ResolveEventSources(item.source_moment_ids, chunk); } catch (InvalidOperationException ex) { return ex.Message; }
                if (!seen.Add("new:" + item.entry_summary.Trim())) return "同一批中重复条目须合成一项，不重复记述。";
            }
            foreach (var item in output.event_appends)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.entry_summary) ||
                    !(candidates ?? new()).Any(x => x.Id == item.index_alias)) return "event_appends必须引用所给已有索引且写出本次进展。";
                if (string.IsNullOrWhiteSpace(item.event_focus))
                    return "event_focus须有1～40字，点明本次新增事实。";
                try { ResolveEventSources(item.source_moment_ids, chunk); } catch (InvalidOperationException ex) { return ex.Message; }
                if (!seen.Add(item.index_alias + ":" + item.entry_summary.Trim())) return "同一索引的同一进展只记一次。";
            }
            return null;
        }

        private sealed class DetailDraft { public string Stamp; public string Body; public bool ContentRejected; }
        private static async Task<string> CompleteDetailAsync(MigrationContext context, ILlmClient llm,
            string key, string stamp, List<DeepSeekMessageData> messages, CancellationToken token)
        {
            var draftKey = key + ":draft";
            var draft = PuzzleViewLogic.Read<DetailDraft>(context.Store.LoadPluginDocument(CoreProgressDocuments, draftKey));
            if (draft != null && draft.Stamp != stamp)
                throw new InvalidOperationException("细节草稿来源已变化，不能直接复用。");
            if (draft?.ContentRejected == true) throw new LlmContentRejectedException();
            try
            {
                if (draft == null)
                {
                    var output = await DeepSeekStructuredOutputLogic.CompleteAsync<ReplayPrompts.DetailOutputData>(llm,
                        messages, x => !string.IsNullOrWhiteSpace(x?.detail), "细节输出缺少detail。", token,
                        includeInvalidResponse: false,
                        typedValidationError: x => LlmContentRejectionLogic.IsRefusal(x.detail) ? new LlmContentRejectedException() : null);
                    draft = new DetailDraft { Stamp = stamp, Body = output.detail.Trim() };
                    context.Migration.SaveProgressDocument(CoreProgressDocuments, draftKey, TraceJson.ToJson(draft));
                }
                LlmContentRejectionLogic.CheckRefusal(draft.Body);
                if (draft.Body.Length <= 200) return draft.Body;
                var repaired = await ReviewWorkLogic.CompleteAsync<ReplayPrompts.DetailOutputData>(context.Store,
                    key + ":prose:" + stamp, llm, messages,
                    x => string.IsNullOrWhiteSpace(x?.detail) ? "detail必须为完整正文。" : null,
                    x => new[] { new ReviewWorkLogic.TextPart("detail", 200, () => x.detail, v => x.detail = v) }, token,
                    new ReplayPrompts.DetailOutputData { detail = draft.Body });
                LlmContentRejectionLogic.CheckRefusal(repaired.detail);
                draft.Body = repaired.detail.Trim();
                context.Migration.SaveProgressDocument(CoreProgressDocuments, draftKey, TraceJson.ToJson(draft));
                return draft.Body;
            }
            catch (LlmContentRejectedException)
            {
                token.ThrowIfCancellationRequested();
                // Persist the refusal, never its prose, so a restart does not request this item again.
                context.Migration.SaveProgressDocument(CoreProgressDocuments, draftKey,
                    TraceJson.ToJson(new DetailDraft { Stamp = stamp, Body = "", ContentRejected = true }));
                throw;
            }
        }
    }
}
