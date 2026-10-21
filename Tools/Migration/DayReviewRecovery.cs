using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Migrate
{
    public static partial class DayBuilder
    {
        private const string RecoveryDocuments = "daily-review-recovery-v1";

        public sealed class DayCoreCheckpoint
        {
            public List<string> MomentIds = new();
            public List<string> IndexIds = new();
            public List<string> EntryIds = new();
            public string SourceStamp;
            public bool Ranked;
            public bool ReviewDone;
            public bool EmptyDay;
            public bool DaySummaryPending;
        }

        public sealed class ReviewCheckpoint
        {
            public ReplayPrompts.DayCardReviewOutputData Output;
            public List<CognitionSliceRecord> Evidence = new();
            public List<string> Applied = new();
            public long SubjectRevision;
            public string Stamp;
            public string StoredCardsStamp;
        }

        public sealed class ProseRepairs { public List<ProseRepair> items; }
        public sealed class ProseRepair { public string id; public string body; }

        private static string Digest(object value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(TraceJson.ToJson(value))));

        private static DayCoreCheckpoint LoadCoreCheckpoint(MigrationContext context, string day)
        {
            var json = context.Store.LoadPluginDocument(RecoveryDocuments, "core:" + day);
            return string.IsNullOrEmpty(json) ? null : TraceJson.FromJson<DayCoreCheckpoint>(json);
        }

        private static string SourceStamp(MigrationContext context, IEnumerable<string> ids)
        {
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            var sources = context.Store.GetEvidenceMoments(wanted);
            if (sources.Count != wanted.Count)
                throw new InvalidOperationException("复盘检查点的原始来源已缺失，不能继续提交。");
            return Digest(new { Pair = context.RequirePair(), Sources = sources.OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => new { x.Id, x.ConversationId, x.Content, x.CreatedUnixMs, x.Realm,
                    x.EvidenceType, x.SourcePluginId, x.SourceEventId, x.PayloadJson, x.EnvironmentJson,
                    x.MemoryVisibility, x.Type, x.SourcePlatform, x.ParentMomentId }) });
        }

        private static void ValidateCoreSources(MigrationContext context, DayCoreCheckpoint core)
        {
            if (core.SourceStamp != SourceStamp(context, core.MomentIds))
                throw new InvalidOperationException("复盘检查点的来源或身份已修订，不能复用旧结果，请检查后重新整理。");
        }

        private static bool CanResumeCore(MigrationContext context, string dayKey, DayCoreCheckpoint core)
        {
            ValidateCoreSources(context, core);
            var day = DateTime.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var range = DateRange.Parse(new[] { "--from", dayKey, "--to", dayKey });
            if (context.Store.GetPublicMomentsInRange(range.DayStartMs(day), range.DayEndMs(day)).Count > 0) return false;
            return context.Migration.GetUnbuiltMomentsInRange(range.DayStartMs(day), range.DayEndMs(day))
                .All(x => core.MomentIds.Contains(x.Id));
        }

        private static void WriteCoreCheckpoint(MigrationContext context, string day, DayCoreCheckpoint core)
            => context.Store.SavePluginDocument(RecoveryDocuments, "core:" + day, TraceJson.ToJson(core));

        private static DayCoreCheckpoint SaveCoreCheckpoint(MigrationContext context, string day,
            List<MomentRecord> moments, List<EventIndexRecord> indexes, List<EventEntryRecord> entries,
            DayCoreCheckpoint previous = null)
        {
            var core = new DayCoreCheckpoint {
                MomentIds = (previous?.MomentIds ?? new()).Concat(moments.Select(x => x.Id)).Distinct().ToList(),
                IndexIds = (previous?.IndexIds ?? new()).Concat(indexes.Select(x => x.Id)).Distinct().ToList(),
                EntryIds = (previous?.EntryIds ?? new()).Concat(entries.Select(x => x.Id)).Distinct().ToList(),
                EmptyDay = moments.Count == 0 && previous == null };
            core.SourceStamp = SourceStamp(context, core.MomentIds);
            WriteCoreCheckpoint(context, day, core);
            // New evidence must never reuse a draft made before this core generation.
            context.Store.SavePluginDocument(RecoveryDocuments, "identity:" + day, "");
            return core;
        }

        private static string ReviewStamp(MigrationContext context, string day,
            IReadOnlyList<IdentityCardRecord> cards, IReadOnlyList<CognitionSliceRecord> nodes)
            => Digest(new { Pair = context.RequirePair(),
                Cards = cards.OrderBy(x => x.Slot, StringComparer.Ordinal),
                Nodes = nodes.Where(x => !string.IsNullOrEmpty(x.IdentitySlot))
                    .OrderBy(x => x.Id, StringComparer.Ordinal) });

        private static ReviewCheckpoint LoadReviewCheckpoint(MigrationContext context, string day,
            IReadOnlyList<IdentityCardRecord> cards, IReadOnlyList<CognitionSliceRecord> nodes)
        {
            var json = context.Store.LoadPluginDocument(RecoveryDocuments, "identity:" + day);
            if (string.IsNullOrEmpty(json)) return null;
            var state = TraceJson.FromJson<ReviewCheckpoint>(json);
            if (state.Stamp != ReviewStamp(context, day, cards, nodes) ||
                state.StoredCardsStamp != Digest(context.Store.LoadIdentityCards(MigrationContext.ConversationId)))
            {
                context.Store.SavePluginDocument(RecoveryDocuments, "identity:" + day, "");
                return null;
            }
            var error = DayCardReviewContract.StructuralError(state.Output, cards, state.Evidence);
            if (error != null) throw new InvalidOperationException("身份检查点不再满足引用契约：" + error);
            return state;
        }

        private static List<string> LongParts(ReplayPrompts.DayCardReviewOutputData output)
        {
            var result = output.cards.Where(x => x.body.Length > IdentityCardSlotValues.BodyLimit(x.slot))
                .Select(x => x.slot).ToList();
            if ((output.inner_narrative?.Trim().Length ?? 0) > 120) result.Add("inner_narrative");
            return result;
        }

        private static async Task<ReplayPrompts.DayCardReviewOutputData> RepairReviewAsync(
            MigrationContext context, ILlmClient llm, string day, IReadOnlyList<IdentityCardRecord> cards,
            ReviewCheckpoint state)
        {
            var error = DayCardReviewContract.StructuralError(state.Output, cards, state.Evidence);
            if (error != null) throw new InvalidOperationException(error);
            state.StoredCardsStamp ??= Digest(context.Store.LoadIdentityCards(MigrationContext.ConversationId));
            if ((state.Output.inner_narrative?.Trim().Length ?? 0) > 120 &&
                SubjectRuntimeLogic.Read(context.Store, MigrationContext.ConversationId).Revision != state.SubjectRevision)
            {
                state.Output.inner_narrative = state.Output.inner_mood = state.Output.inner_relationship_lens = state.Output.inner_ongoing_activity = "";
                state.Output.inner_attention = new();
                state.Output.summary += "；旧内心草稿已被较新当下替代";
            }
            var targets = LongParts(state.Output);
            void Save() => context.Store.SavePluginDocument(RecoveryDocuments, "identity:" + day, TraceJson.ToJson(state));
            if (targets.Count > 0)
            {
                // Save all validated fields before the sole, narrowly scoped repair request.
                Save();
                var boundIds = state.Output.cards.Where(x => targets.Contains(x.slot))
                    .SelectMany(x => x.cognition_ids).ToHashSet(StringComparer.Ordinal);
                var evidence = state.Evidence.Where(x => boundIds.Contains(x.Id)).ToList();
                var request = new {
                    targets = targets.Select(id => new { id,
                        target_chars = id == "inner_narrative" ? 80 : IdentityCardSlotValues.BodyTarget(id),
                        max_chars = id == "inner_narrative" ? 120 : IdentityCardSlotValues.BodyLimit(id),
                        current = id == "inner_narrative" ? "" : cards.FirstOrDefault(x => x.Slot == id)?.Body }),
                    cognitions = evidence,
                    dayReview = targets.Contains("inner_narrative")
                        ? context.Store.GetRuntimeDaySummaries(MigrationContext.ConversationId, day).Select(x => x.Summary).ToList() : new List<string>() };
                var raw = await llm.CompleteJsonAsync(new List<DeepSeekMessageData> {
                    new("system", "【复盘未完成正文】只为targets列出的id重新写完整正文。原草稿超过篇幅，不回传草稿。" +
                        "身份正文根据对应认知综合当前认识，承接仍有效的旧理解；篇幅见target_chars/max_chars。" +
                        "inner_narrative写当天留下的感受。其他已完成字段由程序保留，来源引用由程序保持。" +
                        "每个id恰好一项，不添加其他id；只输出JSON {\"items\":[{\"id\":\"relation\",\"body\":\"完整正文\"}]}。"),
                    new("user", TraceJson.ToJson(request)) }, CancellationToken.None);
                var repaired = DeepSeekStructuredOutputLogic.Read<ProseRepairs>(raw);
                if (repaired?.items == null || repaired.items.Count != targets.Count ||
                    repaired.items.Any(x => x == null || !targets.Contains(x.id) || string.IsNullOrWhiteSpace(x.body)) ||
                    repaired.items.Select(x => x.id).Distinct().Count() != targets.Count)
                    throw new InvalidOperationException("正文修复必须完整且唯一地对应待整理字段；已保存合格结果检查点。");
                foreach (var item in repaired.items)
                {
                    var body = context.RequirePair().RewriteRecordedText(item.body.Trim());
                    if (item.id == "inner_narrative") state.Output.inner_narrative = body;
                    else state.Output.cards.Single(x => x.slot == item.id).body = body;
                }
                Save();
            }
            var pending = LongParts(state.Output);
            if (state.Stamp != ReviewStamp(context, day, cards, context.Store.GetCognitionNodes(5000)) ||
                state.StoredCardsStamp != Digest(context.Store.LoadIdentityCards(MigrationContext.ConversationId)))
                throw new InvalidOperationException("生成期间认知来源或身份卡已修订，旧草稿不提交；已保存检查点供重新核对。");
            var result = TraceJson.FromJson<ReplayPrompts.DayCardReviewOutputData>(TraceJson.ToJson(state.Output));
            result.SubjectRevision = state.SubjectRevision;
            result.PendingParts = pending;
            result.cards.RemoveAll(x => pending.Contains(x.slot) || state.Applied.Contains(x.slot));
            if (pending.Contains("inner_narrative") || state.Applied.Contains("inner")) result.inner_narrative = "";
            if (pending.Contains("inner_narrative") || state.Applied.Contains("inner"))
            {
                result.inner_mood = result.inner_relationship_lens = result.inner_ongoing_activity = "";
                result.inner_attention = new();
            }
            // Structural guards are independent of prose acceptance, including restored checkpoints.
            error = DayCardReviewContract.ValidationError(result, cards, state.Evidence);
            if (error != null) throw new InvalidOperationException(error);
            if (targets.Count > 0 || LoadCoreCheckpoint(context, day) != null) Save();
            if (pending.Count == 0 && LoadCoreCheckpoint(context, day) == null)
                context.Store.SavePluginDocument(RecoveryDocuments, "identity:" + day, "");
            return result;
        }

        private static void UpdateReviewCheckpoint(MigrationContext context, string day)
        {
            var json = context.Store.LoadPluginDocument(RecoveryDocuments, "identity:" + day);
            if (string.IsNullOrEmpty(json)) return;
            var state = TraceJson.FromJson<ReviewCheckpoint>(json);
            var pending = LongParts(state.Output);
            if (pending.Count == 0)
            {
                context.Store.SavePluginDocument(RecoveryDocuments, "identity:" + day, "");
                return;
            }
            state.Applied = state.Output.cards.Where(x => !pending.Contains(x.slot)).Select(x => x.slot).ToList();
            if (!pending.Contains("inner_narrative")) state.Applied.Add("inner");
            state.Stamp = ReviewStamp(context, day, context.Store.LoadIdentityCards(MigrationContext.ConversationId),
                context.Store.GetCognitionNodes(5000));
            state.StoredCardsStamp = Digest(context.Store.LoadIdentityCards(MigrationContext.ConversationId));
            context.Store.SavePluginDocument(RecoveryDocuments, "identity:" + day, TraceJson.ToJson(state));
        }
    }
}
