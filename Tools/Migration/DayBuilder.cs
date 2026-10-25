using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Prompts;
using TraceSoul2.Tools.Memory;
using TraceSoul2.Util;
using TraceSoul2.Host;

namespace TraceSoul2.Migrate
{
    /// <summary>
    /// 新路线单天构筑：
    /// 1 天对话（已入库 moments）→ 批量构筑第四层多维索引 + 条目（一句话总结客观）
    /// → 当下切片整理、细节浸染与有依据的认知修订 → 身份摘要按依据更新（允许不变）。
    /// 时间维度由 TimeLanguage 确定性翻译。当天结束后排日榜，并按周一、月初、年初晋升周、月、年、永久榜。
    /// </summary>
    public static partial class DayBuilder
    {
        public static async Task<int> RunAsync(MigrationContext context, string[] args)
        {
            using var executionLease = ReviewExecutionLease.Acquire(context.Migration.DataDirectory);
            context.BeginReviewBudget();
            var dayKey = CliArgs.Value(args, "--day");
            if (string.IsNullOrWhiteSpace(dayKey))
                throw new InvalidOperationException("需要 --day yyyy-MM-dd。");
            if (context.Migration.IsDayCompleted(dayKey))
            {
                var lateSummaryPending = false;
                if (context.Store.GetRuntimeSlices(MigrationContext.ConversationId, dayKey, true).Count > 0 ||
                    context.Store.HasPendingRuntimeDaySummary(MigrationContext.ConversationId, dayKey))
                {
                    try { await RuntimeSliceLogic.ReviewDayAsync(context.Store, context.RequireLlm(), MigrationContext.ConversationId, dayKey); }
                    catch (ReviewProsePendingException) { lateSummaryPending = true; }
                }
                var finishedDay = DateTime.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var finishedRange = DateRange.Parse(new[] { "--from", dayKey, "--to", dayKey });
                var latePublicPending = false;
                try { if (context.Store.GetPublicMomentsInRange(finishedRange.DayStartMs(finishedDay), finishedRange.DayEndMs(finishedDay)).Count > 0)
                    await PublicExperienceLogic.BuildAsync(context.Store, context.RequireLlm(), finishedRange.DayStartMs(finishedDay), finishedRange.DayEndMs(finishedDay),
                        encoderFactory: context.RequireCognitionEncoder, cache: context.Vectors); }
                catch (ReviewProsePendingException) { latePublicPending = true; }
                // 完成标记早于临时样本清理：即使上次进程恰好在两者之间退出，也能在这里补清理。
                if (lateSummaryPending || latePublicPending)
                {
                    // The existing done marker is authoritative for core work, including pre-checkpoint versions.
                    var sources = context.Migration.GetUnbuiltMomentsInRange(finishedRange.DayStartMs(finishedDay), finishedRange.DayEndMs(finishedDay));
                    var lateCore = SaveCoreCheckpoint(context, dayKey, sources, new(), new());
                    lateCore.Ranked = lateCore.ReviewDone = true;
                    lateCore.DaySummaryPending = lateSummaryPending;
                    WriteCoreCheckpoint(context, dayKey, lateCore);
                    context.Migration.SaveReviewState(new ReviewStateRecord { DayKey = dayKey, Status = "running", Error = "" });
                    context.Migration.SetReviewStage("迟到回望待整理");
                    if (latePublicPending) context.Migration.MarkDayDeferred(dayKey, "迟到公开经历待整理");
                    else context.Migration.MarkDayPending(dayKey, new[] { "day_summary" });
                }
                else context.Store.RetireDayRuntimeSamples(MigrationContext.ConversationId, dayKey);
                Console.WriteLine((lateSummaryPending ? "核心已完成，迟到回望待整理：" : "日终复盘已完成，跳过重复构筑：") + dayKey);
                return 0;
            }
            context.Migration.SaveReviewState(new ReviewStateRecord
            {
                DayKey = dayKey,
                Status = "running",
                Error = string.Empty
            });
            try
            {
                var checkpoint = LoadCoreCheckpoint(context, dayKey);
                if (checkpoint != null && CanResumeCore(context, dayKey, checkpoint))
                {
                    var date = DateTime.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var period = DateRange.Parse(new[] { "--from", dayKey, "--to", dayKey });
                    await PublicExperienceLogic.BuildAsync(context.Store, context.RequireLlm(), period.DayStartMs(date), period.DayEndMs(date),
                        encoderFactory: context.RequireCognitionEncoder, cache: context.Vectors);
                    return await FinishCheckpointAsync(context, dayKey, checkpoint);
                }
                return await RunCoreAsync(context, args, dayKey);
            }
            catch (ReviewDependencyPendingException exception)
            {
                context.Migration.MarkDayWaiting(dayKey, exception.Message);
                return 0;
            }
            catch (ReviewProsePendingException exception)
            {
                context.Migration.MarkDayDeferred(dayKey, exception.Message);
                return 0;
            }
            catch (Exception exception)
            {
                // 完成标记是提交点。提交后的样本清理/打印即使异常，也不能把已成功复盘改回 failed；
                // 下次启动会走完成分支重试清理，而不会重复长期沉淀。
                if (!context.Migration.IsDayCompleted(dayKey))
                    context.Migration.SaveReviewState(new ReviewStateRecord
                {
                    DayKey = dayKey,
                    Status = "failed",
                    Error = Limit(exception.Message, 1000)
                });
                throw;
            }
        }

        private static async Task<int> RunCoreAsync(MigrationContext context, string[] args, string dayKey)
        {
            var day = DateTime.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var range = DateRange.Parse(new[] { "--from", dayKey, "--to", dayKey });
            var moments = context.Migration.GetUnbuiltMomentsInRange(
                range.DayStartMs(day), range.DayEndMs(day));
            var previousCore = LoadCoreCheckpoint(context, dayKey);
            if (previousCore != null)
            {
                ValidateCoreSources(context, previousCore);
                moments.RemoveAll(x => previousCore.MomentIds.Contains(x.Id));
            }
            var pair = context.RequirePair();
            var llm = context.RequireLlm();
            var progress = moments.Count > 0 ? OpenCoreProgress(context, dayKey, moments, previousCore) : null;
            context.Migration.SetReviewStage("当下切片夜间整理");
            var daySummaryPending = false;
            try { await RuntimeSliceLogic.ReviewDayAsync(context.Store, llm, MigrationContext.ConversationId, dayKey); }
            catch (ReviewProsePendingException) { daySummaryPending = true; }
            context.Migration.SetReviewStage("公开经历按环境整理");
            var publicPending = false;
            var publicCount = 0;
            try { publicCount = await PublicExperienceLogic.BuildAsync(context.Store, llm, range.DayStartMs(day), range.DayEndMs(day),
                encoderFactory: context.RequireCognitionEncoder, cache: context.Vectors); }
            catch (ReviewProsePendingException) { publicPending = true; }
            if (moments.Count == 0 && publicCount > 0 && !publicPending && !daySummaryPending && previousCore == null)
            {
                context.Migration.MarkDayCompleted(dayKey);
                return 0;
            }
            if (moments.Count == 0)
            {
                MigrationLive.Start(context);
                var emptyCore = previousCore ?? SaveCoreCheckpoint(context, dayKey, new(), new(), new());
                if (publicPending) throw new ReviewProsePendingException("公开经历待整理，私密核心进度已保存。");
                emptyCore.DaySummaryPending = daySummaryPending;
                WriteCoreCheckpoint(context, dayKey, emptyCore);
                return await FinishCheckpointAsync(context, dayKey, emptyCore, retryDaySummary: false);
            }
            MigrationLive.Start(context);
            var router = new HierarchicalVectorRouterLogic(new BagOfCharsVectorEncoder());
            RebuildRouter(context, pair, router);

            var referenceTime = new DateTimeOffset(day.Date.AddHours(12), MigrationContext.ChinaOffset);
            var lastMomentId = moments[moments.Count - 1].Id;
            var cardsBefore = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            PrintCards("构筑前身份摘要", cardsBefore, pair);

            var chunks = ChunkMoments(moments);
            var dayIndexes = new List<EventIndexRecord>();
            var dayEntries = new List<EventEntryRecord>();
            var evidenceByEntry = new Dictionary<string, List<MomentRecord>>(StringComparer.Ordinal);
            var observationCalls = 0;

            foreach (var chunk in chunks)
            {
                context.Migration.SetReviewStage("事件观察（第 " + (observationCalls + 1) + " 批）");
                var observationKey = ProgressKey(dayKey, progress.Generation, "observation", chunk.Select(x => x.Id).ToList());
                var savedObservation = context.Store.LoadPluginDocument(CoreProgressDocuments, observationKey);
                var observation = string.IsNullOrEmpty(savedObservation) ? new ObservationProgress()
                    : TraceJson.FromJson<ObservationProgress>(savedObservation);
                if (RestoreObservation(context, progress, observation, dayIndexes, dayEntries, evidenceByEntry, chunk))
                {
                    observationCalls++;
                    continue;
                }
                var observationContext = ObservationContextStamp(context, observation);
                if (observation.Output != null && observation.ContextStamp != observationContext)
                    throw new InvalidOperationException("事件草稿引用的标签或索引已修订，不能直接提交旧草稿。");
                observation.ContextStamp = observationContext;
                var chunkText = string.Join("\n", chunk.Select(x => FormatMoment(x, pair)));
                var route = router.Route(chunkText);
                var conceptIds = route.Concepts.Select(x => x.Node.Id).ToList();
                var activeTags = observation.Tags ?? context.Store.GetActiveLifeTags();
                var recentTags = activeTags.OrderByDescending(x => x.ActivationCount).Take(30).ToList();
                var indexCandidates = observation.Candidates ?? context.Migration.GetEventIndexCandidates(
                    activeTags.Select(x => x.Id).ToList(), 20);
                var anchor = DateTimeOffset.FromUnixTimeMilliseconds(chunk[0].CreatedUnixMs)
                    .ToOffset(MigrationContext.ChinaOffset);
                var periodLabel = TimeLanguage.PeriodZh(TimeLanguage.PeriodOf(anchor));
                var dayKindLabel = TimeLanguage.DayKindLabel(anchor);

                var prompt = ReplayPrompts.BuildEventObservationPrompt(
                    pair, chunk, route, indexCandidates, recentTags, periodLabel, dayKindLabel,
                    context.Migration.GetEntriesByIndexIds(indexCandidates.Select(x => x.Id).ToList()));
                var messages = observation.Messages ?? new List<DeepSeekMessageData>
                {
                    new DeepSeekMessageData("system", prompt),
                    new DeepSeekMessageData("user", CorePrompts.Migration.ObserveUser)
                };
                observation.Messages = messages; observation.Tags = activeTags; observation.Candidates = indexCandidates;
                observationContext = ObservationContextStamp(context, observation);
                observation.ContextStamp = observationContext;
                var output = await CompleteObservationAsync(context, llm, progress, observationKey, observation, messages, chunk, indexCandidates);
                observationCalls++;
                var batchIndexes = new List<EventIndexRecord>();
                var batchEntries = new List<EventEntryRecord>();
                context.Migration.CommitCoreProgress(() =>
                {
                    EnsureCoreProgress(context, progress);
                    if (ObservationContextStamp(context, observation) != observationContext)
                        throw new InvalidOperationException("事件生成期间标签或索引已修订，旧输出不能提交。");
                    // 复用旧路的 Tag 创建/激活通道：只带 Tag 信息，不带事实。
                    var tagOutput = new MemoryObservationOutputData
                    {
                        perception_summary = output.perception_summary ?? string.Empty,
                        fact_decision = output.event_decision ?? string.Empty,
                        selected_tag_ids = output.selected_tag_ids ?? new List<string>(),
                        new_tags = output.new_tags ?? new List<NewLifeTagWriteData>(),
                        fact_writes = new List<SensoryFactWriteData>(),
                        fact_wakes = new List<SensoryFactWakeData>()
                    };
                    var tagAliases = BuildTagAliases(activeTags);
                    RepairSelectedTagIds(tagOutput, tagAliases);
                    context.Migration.CreateAndActivateTags(
                        tagOutput, chunk[chunk.Count - 1], conceptIds, pair);

                    var tagByLabel = context.Migration.GetActiveLifeTagsForCommit()
                        .GroupBy(x => x.Label)
                        .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.Ordinal);
                    var aliasToIndex = new Dictionary<string, EventIndexRecord>(StringComparer.Ordinal);
                    foreach (var candidate in indexCandidates) aliasToIndex[candidate.Id] = candidate;

                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var write in output.event_writes ?? new List<ReplayPrompts.EventWriteItemData>())
                    {
                        if (write == null || string.IsNullOrWhiteSpace(write.event_summary)) continue;
                        var eventSources = observation.SourceVersion == 0 ? chunk : ResolveEventSources(write.source_moment_ids, chunk);
                        var eventTime = DateTimeOffset.FromUnixTimeMilliseconds(eventSources[0].CreatedUnixMs).ToOffset(MigrationContext.ChinaOffset);
                        var tagIds = ResolveTagIds(write.tag_ids, write.new_tag_names, tagAliases, tagByLabel);
                        if (tagIds.Count == 0)
                        {
                            Console.WriteLine("  [警告] 事件「" + Limit(write.event_summary, 20) + "」无有效 Tag，丢弃。");
                            continue;
                        }
                        var index = new EventIndexRecord
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            TagIds = string.Join(",", tagIds),
                            TimeLabel = TimeLanguage.PeriodZh(TimeLanguage.PeriodOf(eventTime)),
                            DayKindLabel = TimeLanguage.DayKindLabel(eventTime),
                            TimeUnixMs = eventTime.ToUnixTimeMilliseconds(),
                            PlaceLabel = Limit(write.place, 20),
                            PersonLabel = Limit(write.person, 20),
                            EventSummary = (write.event_summary ?? string.Empty).Trim(),
                            MoodLabel = Limit(write.mood, 12),
                            FirstMomentId = eventSources[0].Id,
                            Status = "active",
                            CreatedUnixMs = now,
                            UpdatedUnixMs = now
                        };
                        context.Migration.SaveEventIndex(index);
                        dayIndexes.Add(index);
                        batchIndexes.Add(index);
                        var entry = new EventEntryRecord
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            IndexId = index.Id,
                            Summary = (write.entry_summary ?? string.Empty).Trim(),
                            Detail = string.Empty,
                            SourceMomentId = eventSources[^1].Id,
                            Realm = NormalizeRealm(write.realm),
                            CreatedUnixMs = now
                        };
                        context.Migration.AppendEventEntry(entry);
                        dayEntries.Add(entry);
                        batchEntries.Add(entry);
                        evidenceByEntry[entry.Id] = eventSources;
                        observation.EntrySources[entry.Id] = eventSources.Select(x => x.Id).ToList();
                    }

                    foreach (var append in output.event_appends ?? new List<ReplayPrompts.EventAppendItemData>())
                    {
                        if (append == null || string.IsNullOrWhiteSpace(append.entry_summary)) continue;
                        var eventSources = observation.SourceVersion == 0 ? chunk : ResolveEventSources(append.source_moment_ids, chunk);
                        var target = ResolveIndexAlias(append.index_alias, aliasToIndex);
                        if (target == null)
                        {
                            Console.WriteLine("  [警告] 追加目标索引无法解析：" + Limit(append.index_alias, 20) + "。");
                            continue;
                        }
                        var entry = new EventEntryRecord
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            IndexId = target.Id,
                            Summary = (append.entry_summary ?? string.Empty).Trim(),
                            Detail = string.Empty,
                            SourceMomentId = eventSources[^1].Id,
                            Realm = NormalizeRealm(null),
                            CreatedUnixMs = now
                        };
                        context.Migration.AppendEventEntry(entry);
                        dayEntries.Add(entry);
                        batchEntries.Add(entry);
                        evidenceByEntry[entry.Id] = eventSources;
                        observation.EntrySources[entry.Id] = eventSources.Select(x => x.Id).ToList();
                        if (!dayIndexes.Any(x => x.Id == target.Id)) dayIndexes.Add(target);
                        if (!batchIndexes.Any(x => x.Id == target.Id)) batchIndexes.Add(target);
                    }
                    observation.IndexIds = batchIndexes.Select(x => x.Id).ToList();
                    observation.EntryIds = batchEntries.Select(x => x.Id).ToList();
                    observation.RecordsStamp = ObservationRecordsStamp(batchIndexes, batchEntries);
                    observation.Applied = true;
                    context.Migration.SaveProgressDocument(CoreProgressDocuments, observationKey, TraceJson.ToJson(observation));
                });
                Console.WriteLine("  块" + observationCalls + " 观察：" + Limit(output.perception_summary, 80));
                LogCall(context, dayKey, "event_observation", observationCalls,
                    Limit(output.perception_summary, 80), TraceJson.ToJson(output));
            }

            // ---------- 细节浸染：助手第一人称（4 路并行） ----------
            // 占位/空卡回退到种子文件（档案卡以用户填写的种子为准）。
            var personalityCard = IdentityCardLogic.ResolveBody(
                IdentityCardSlotValues.Personality,
                Card(cardsBefore, IdentityCardSlotValues.Personality).Body, pair);
            var userProfileCard = IdentityCardLogic.ResolveBody(
                IdentityCardSlotValues.UserProfile,
                Card(cardsBefore, IdentityCardSlotValues.UserProfile).Body, pair);
            var detailCalls = 0;
            var pendingDetails = 0;
            context.Migration.SetReviewStage("细节生成");
            await ProtectedParallelWork.RunAsync(dayEntries, 4, async (entry, detailToken) =>
            {
                List<MomentRecord> evidence;
                if (!evidenceByEntry.TryGetValue(entry.Id, out evidence)) return;
                var index = dayIndexes.FirstOrDefault(x => x.Id == entry.IndexId);
                var indexLine = index == null ? string.Empty
                    : index.TimeLabel + "（" + index.DayKindLabel + "）·" + index.PlaceLabel + "·"
                      + index.PersonLabel + "·" + index.EventSummary + "·" + index.MoodLabel;
                var detailKey = ProgressKey(dayKey, progress.Generation, "detail", entry.Id);
                var detailStamp = Digest(new { Source = SourceStamp(context, evidence.Select(x => x.Id)),
                    indexLine, entry.Summary, personalityCard, userProfileCard });
                if (RestoreDetail(context, progress, detailKey, detailStamp, entry)) return;
                var prompt = ReplayPrompts.BuildDetailPrompt(
                    pair, personalityCard, userProfileCard, evidence, indexLine, entry.Summary, 0, 200);
                var messages = new List<DeepSeekMessageData>
                {
                    new DeepSeekMessageData("system", prompt),
                    new DeepSeekMessageData("user", CorePrompts.Migration.DetailUser)
                };
                var contentRejected = false;
                try { entry.Detail = await CompleteDetailAsync(context, llm, detailKey, detailStamp, messages, detailToken); }
                catch (ReviewProsePendingException) { Interlocked.Increment(ref pendingDetails); return; }
                catch (LlmContentRejectedException)
                {
                    detailToken.ThrowIfCancellationRequested();
                    // The event has already passed source validation. Keep its recorded short sentence as the reading body.
                    entry.Detail = entry.Summary;
                    contentRejected = true;
                    Console.WriteLine("  [内容审核] 一条细节停止加工，沿用已保存事件短句，其余条目继续。");
                }
                context.Migration.CommitCoreProgress(() =>
                {
                    EnsureCoreProgress(context, progress);
                    var currentCards = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
                    if (personalityCard != IdentityCardLogic.ResolveBody(IdentityCardSlotValues.Personality,
                            Card(currentCards, IdentityCardSlotValues.Personality).Body, pair) ||
                        userProfileCard != IdentityCardLogic.ResolveBody(IdentityCardSlotValues.UserProfile,
                            Card(currentCards, IdentityCardSlotValues.UserProfile).Body, pair))
                        throw new InvalidOperationException("细节生成期间身份档案已修订，旧输出不能提交。");
                    context.Migration.UpdateEventEntryDetail(entry.Id, entry.Detail);
                    context.Migration.SaveProgressDocument(CoreProgressDocuments, detailKey,
                        TraceJson.ToJson(new CompletedProgress { Stamp = detailStamp, BodyStamp = Digest(entry.Detail), ContentRejected = contentRejected }));
                });
                var n = Interlocked.Increment(ref detailCalls);
                Console.WriteLine("  细节「" + Limit(entry.Summary, 20) + "」：" + Limit(entry.Detail, 60));
                LogCall(context, dayKey, "detail", n,
                    "细节：" + Limit(entry.Summary, 40), TraceJson.ToJson(new { detail = entry.Detail, content_rejected = contentRejected }));
            }, error => context.Providers.Protection.Pause("provider:" + llm.ProviderId, "复盘细节生成", error));

            // ---------- 条目语义向量：一句话总结编码（幂等，召回拼装用） ----------
            try
            {
                var encoder = context.RequireEncoder();
                var embedded = EntryEmbedder.EmbedAll(dayEntries, context.Vectors, encoder);
                if (embedded > 0) Console.WriteLine("  条目语义向量已编码 " + embedded + " 条。");
            }
            catch (Exception exception)
            {
                Console.WriteLine("  [警告] 条目向量编码失败（Host 召回将退回字符路由）：" + exception.Message);
            }

            // ---------- 认知形成：跑完事件构筑后，直接用当天新增事件提炼第一人称理解 ----------
            if (context.Migration.GetAllReviewStates().Any(x => (x.Status == "pending" || x.Status == "deferred" || x.Status == "waiting") && string.CompareOrdinal(x.DayKey, dayKey) < 0))
                throw new ReviewDependencyPendingException("较早日期尚未完成，当前事件和细节已保存，认知演化等待前置任务。");
            context.Migration.SetReviewStage("认知形成");
            await RunCognitionFormationWithRecoveryAsync(
                context, pair, llm, dayKey, dayIndexes, dayEntries, moments, progress);

            if (publicPending) throw new ReviewProsePendingException("公开经历待整理，私密核心进度已保存。");
            if (pendingDetails > 0) throw new ReviewProsePendingException("仍有" + pendingDetails + "条细节待整理，其他合格结果已保存。");
            var core = SaveCoreCheckpoint(context, dayKey, moments, dayIndexes, dayEntries, previousCore);
            core.DaySummaryPending = daySummaryPending;
            WriteCoreCheckpoint(context, dayKey, core);
            return await FinishCheckpointAsync(context, dayKey, core, retryDaySummary: false);
        }

        private static async Task<int> FinishCheckpointAsync(MigrationContext context, string dayKey, DayCoreCheckpoint core, bool retryDaySummary = true)
        {
            ValidateCoreSources(context, core);
            var pair = context.RequirePair();
            var llm = context.RequireLlm();
            if (core.DaySummaryPending && retryDaySummary)
            {
                context.Migration.SetReviewStage("续做当天回望");
                try
                {
                    await RuntimeSliceLogic.ReviewDayAsync(context.Store, llm, MigrationContext.ConversationId, dayKey);
                    core.DaySummaryPending = false;
                }
                catch (ReviewProsePendingException) { }
                WriteCoreCheckpoint(context, dayKey, core);
            }
            var day = DateTime.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var range = DateRange.Parse(new[] { "--from", dayKey, "--to", dayKey });
            var dayIndexes = context.Migration.GetActiveEventIndexes().Where(x => core.IndexIds.Contains(x.Id)).ToList();
            var dayEntries = context.Migration.GetEntriesByIndexIds(core.IndexIds).Where(x => core.EntryIds.Contains(x.Id)).ToList();
            if (dayIndexes.Count != core.IndexIds.Count || dayEntries.Count != core.EntryIds.Count)
                throw new InvalidOperationException("复盘检查点的事件或细节已变化，请检查来源后继续。");
            var lastMomentId = core.MomentIds.LastOrDefault() ?? string.Empty;
            var userProfileCard = IdentityCardLogic.ResolveBody(IdentityCardSlotValues.UserProfile,
                Card(context.Store.LoadIdentityCards(MigrationContext.ConversationId), IdentityCardSlotValues.UserProfile).Body, pair);

            // ---------- 日终身份摘要复盘 + 内心全字段同步 ----------
            context.Migration.SetReviewStage("身份复盘与内心同步");
            var cardsNow = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            var priorDeferred = context.Migration.GetAllReviewStates().Any(x => (x.Status == "deferred" || x.Status == "pending") && string.CompareOrdinal(x.DayKey, dayKey) < 0);
            if (priorDeferred) throw new ReviewDependencyPendingException("较早日期仍有依赖任务待整理；本日核心已保存，身份和晋升等待前置任务。");
            var reviewOutput = core.ReviewDone ? new ReplayPrompts.DayCardReviewOutputData { cards = new() }
                : await RunAutomaticDayReviewAsync(context, dayKey, core.EmptyDay);
            var changed = ApplyReviewOutput(context, pair, reviewOutput, cardsNow, lastMomentId, core.EmptyDay);
            UpdateReviewCheckpoint(context, dayKey);
            CompleteAutomaticInner(context, dayKey, reviewOutput);
            core.ReviewDone = reviewOutput.PendingParts.Count == 0;
            WriteCoreCheckpoint(context, dayKey, core);
            if (core.DaySummaryPending) reviewOutput.PendingParts.Add("day_summary");
            Console.WriteLine();
            Console.WriteLine("身份摘要与内心变化：" + (changed.Count == 0 ? "（无变化）" : string.Join("；", changed)));

            // ---------- 当天排序：日榜（事件/认知各持榜单）+ 周/月/年/永久晋升 ----------
            context.Migration.SetReviewStage("日榜排序与长期晋升");
            if (!core.Ranked)
            {
                var dayCognitions = context.Migration.GetCognitionsCreatedInRange(range.DayStartMs(day), range.DayEndMs(day));
                if (!core.DayRanked)
                {
                    await RankDayLadderAsync(context, pair, llm, dayKey, dayIndexes, dayCognitions);
                    core.DayRanked = true;
                    WriteCoreCheckpoint(context, dayKey, core);
                }
                await DayLadderLogic.PromoteAsync(context, pair, dayKey, llm);
                core.Ranked = true;
                WriteCoreCheckpoint(context, dayKey, core);
            }

            // ---------- 标记当天 Moment 已归档（实时归档已标记的不会重复） ----------
            context.Migration.SetReviewStage("归档收尾");
            if (!CanResumeCore(context, dayKey, core))
                throw new InvalidOperationException("复盘期间出现新来源，已保存当前阶段，需继续处理新增来源。");
            var markedBuilt = context.Migration.MarkMomentsBuiltByRange(range.DayStartMs(day), range.DayEndMs(day));
            if (markedBuilt > 0) Console.WriteLine("  已标记 " + markedBuilt + " 条 Moment 为已归档（built）。");
            if (reviewOutput.PendingParts.Count == 0) context.Migration.MarkDayCompleted(dayKey);
            else context.Migration.MarkDayPending(dayKey, reviewOutput.PendingParts);
            if (!core.DaySummaryPending) context.Store.RetireDayRuntimeSamples(MigrationContext.ConversationId, dayKey);

            var cardsAfter = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            PrintCards("构筑后身份摘要", cardsAfter, pair);
            return 0;
        }

        private static async Task<ReplayPrompts.DayCardReviewOutputData> RunDayReviewAsync(
            MigrationContext context,
            PairIdentity pair,
            ILlmClient llm,
            string dayKey,
            List<IdentityCardRecord> cardsNow,
            string userProfileCard,
            List<EventIndexRecord> dayIndexes,
            List<EventEntryRecord> dayEntries)
        {
            var graphNodes = context.Store.GetCognitionNodes(5000);
            var restored = LoadReviewCheckpoint(context, dayKey, cardsNow, graphNodes);
            if (restored != null)
                return await RepairReviewAsync(context, llm, dayKey, cardsNow, restored);
            var reviewStamp = ReviewStamp(context, dayKey, cardsNow, graphNodes);
            var storedCardsStamp = Digest(context.Store.LoadIdentityCards(MigrationContext.ConversationId));
            string CurrentBody(string slot)
            {
                var card = Card(cardsNow, slot);
                return IdentityProjectionLogic.Current(card, graphNodes) ? card.Body : "（原摘要依据已变化，需从当前认知重新形成）";
            }
            var selfCard = CurrentBody(IdentityCardSlotValues.Self);
            var otherCard = CurrentBody(IdentityCardSlotValues.Other);
            var relationCard = CurrentBody(IdentityCardSlotValues.Relation);
            var expressionCard = CurrentBody(IdentityCardSlotValues.ExpressionHabit);
            var daySummary = string.Join("\n", context.Store.GetRuntimeDaySummaries(MigrationContext.ConversationId, dayKey)
                .Where(x => x.MemoryVisibility != "public" && x.ContextConversationId == MigrationContext.ConversationId)
                .Select(x => x.Summary));
            var currentInner = context.Store.LoadOrCreateInnerRuntime(MigrationContext.ConversationId);
            var subjectRevision = SubjectRuntimeLogic.Read(context.Store, MigrationContext.ConversationId).Revision;
            var currentLife = context.LifeState == null
                ? null : context.LifeState.Load(MigrationContext.ConversationId);
            var reviewPrompt = ReplayPrompts.BuildDayCardReviewPrompt(
                pair, dayKey, selfCard, otherCard, relationCard, expressionCard,
                userProfileCard, new List<EventIndexRecord>(), new List<EventEntryRecord>(),
                string.Empty,
                new List<TodayNewItemRecord>(),
                InnerLifeLogic.FormatForMind(currentInner),
                FormatLifeState(currentLife), selectedDayReview: daySummary);
            var visibleNodes = graphNodes.Where(x => x.Status == "active" &&
                x.MemoryVisibility != "public" && (string.IsNullOrEmpty(x.ContextConversationId) || x.ContextConversationId == MigrationContext.ConversationId) &&
                !string.IsNullOrEmpty(x.IdentitySlot)).ToList();
            var identityNodes = await DayCardReviewContract.SelectEvidenceAsync(llm, cardsNow, visibleNodes);
            reviewPrompt += DayCardReviewContract.EvidenceContext(cardsNow, identityNodes);
            var reviewMessages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", reviewPrompt),
                new DeepSeekMessageData("user", CorePrompts.Migration.DayCardUser)
            };
            var preservedSlots = new HashSet<string>(StringComparer.Ordinal);
            var output = await DeepSeekStructuredOutputLogic.CompleteAsync<ReplayPrompts.DayCardReviewOutputData>(
                llm, reviewMessages, null,
                "身份摘要输出不符合卡片更新契约。", CancellationToken.None,
                validationError: x =>
                {
                    preservedSlots.UnionWith(DayCardReviewContract.PreserveUnavailableCards(x, cardsNow, identityNodes));
                    return DayCardReviewContract.StructuralError(x, cardsNow, identityNodes);
                });
            if (preservedSlots.Count > 0)
                Console.WriteLine("  程序保留本次范围外的身份卡原文：" + string.Join("、", preservedSlots.OrderBy(x => x)));
            foreach (var card in output.cards) card.body = pair.RewriteRecordedText(card.body.Trim());
            output.SubjectRevision = subjectRevision;
            output = await RepairReviewAsync(context, llm, dayKey, cardsNow,
                new ReviewCheckpoint { Output = output, Evidence = identityNodes, SubjectRevision = subjectRevision,
                    Stamp = reviewStamp, StoredCardsStamp = storedCardsStamp });
            LogCall(context, dayKey, "card_review", 0,
                "身份摘要复盘：" + Limit(output.summary, 60), TraceJson.ToJson(output));
            return output;
        }

        private static string FormatLifeState(LifeStateData life)
        {
            if (life == null) return string.Empty;
            var location = string.IsNullOrWhiteSpace(life.location) ? "未知" : BodySceneValues.Label(life.location);
            var activity = string.IsNullOrWhiteSpace(life.activity) ? "空闲" : life.activity;
            return "位置=" + location + "；活动=" + activity +
                   (string.IsNullOrWhiteSpace(life.activity_detail) ? string.Empty : "｜" + life.activity_detail);
        }

        /// <summary>应用复盘输出：身份摘要只写真正变化的；内心全字段经 Reduce 同步（空字段=保留现状）。空天不清手上未结束的事。</summary>
        private static List<string> ApplyReviewOutput(
            MigrationContext context,
            PairIdentity pair,
            ReplayPrompts.DayCardReviewOutputData reviewOutput,
            List<IdentityCardRecord> cardsNow,
            string lastMomentId,
            bool emptyDay)
        {
            var changed = new List<string>();
            foreach (var card in reviewOutput.cards ?? new List<ReplayPrompts.CardUpdateData>())
            {
                if (emptyDay || card == null || !PuzzleViewLogic.IdentitySlot(card.slot) || string.IsNullOrEmpty(card.slot) ||
                    cardsNow.Any(x => x.Slot == card.slot && x.Pinned)) continue;
                var body = (card.body ?? string.Empty).Trim();
                if (body.Length == 0) continue;
                if (body.Length > IdentityCardSlotValues.BodyLimit(card.slot))
                    throw new InvalidOperationException("身份摘要超出篇幅，原卡保留。");
                context.Store.SaveDerivedIdentityCard(MigrationContext.ConversationId, card.slot, body, card.cognition_ids, lastMomentId);
                changed.Add(IdentityCardSlotValues.Title(card.slot, pair) + "：" + Limit(card.reason, 40));
            }

            // 内心：attention 是会代谢的碎片；ongoing 是共享场景。旧 unfinished 字段不再恢复。
            var attention = reviewOutput.inner_attention;
            var ongoing = (reviewOutput.inner_ongoing_activity ?? string.Empty).Trim();
            var proposed = new InnerRuntimeWriteData
            {
                narrative = pair.RewriteRecordedText((reviewOutput.inner_narrative ?? string.Empty).Trim()),
                mood = (reviewOutput.inner_mood ?? string.Empty).Trim(),
                relationship_update = (reviewOutput.inner_relationship_lens ?? string.Empty).Trim(),
                ongoing_activity = emptyDay && ongoing.Length == 0 ? null : ongoing,
                unfinished_intent = string.Empty,
                attention = attention
            };
            var hasInner = proposed.narrative.Length > 0 || proposed.mood.Length > 0 ||
                           proposed.relationship_update.Length > 0 || (proposed.ongoing_activity?.Length ?? 0) > 0 ||
                           (attention != null && attention.Count > 0);
            if (hasInner)
            {
                var sourceMomentId = string.IsNullOrWhiteSpace(lastMomentId)
                    ? context.Store.GetRecentMoments(MigrationContext.ConversationId, 1)
                        .Select(x => x.Id).FirstOrDefault()
                    : lastMomentId;
                if (string.IsNullOrWhiteSpace(sourceMomentId))
                    sourceMomentId = "day-review";
                var currentInner = context.Store.LoadOrCreateInnerRuntime(MigrationContext.ConversationId);
                var nextInner = InnerLifeLogic.Reduce(
                    currentInner, proposed, sourceMomentId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (SubjectRuntimeLogic.CommitReview(context.Store, MigrationContext.ConversationId, reviewOutput.SubjectRevision, nextInner, proposed))
                    changed.Add("内心：" + Limit(nextInner.Narrative, 60));
                else changed.Add("已有较新当下，日复盘未覆盖实时内心");
            }
            return changed;
        }

        /// <summary>认知形成：用当天新增事件提炼第一人称理解，写认知切片（最多 3 条，挂生命标签）。</summary>
        private static Task<List<CognitionSliceRecord>> RunCognitionFormationAsync(
            MigrationContext context, PairIdentity pair, ILlmClient llm, string dayKey,
            List<EventIndexRecord> dayIndexes, List<EventEntryRecord> dayEntries, List<MomentRecord> sourceMoments = null)
            => RunCognitionFormationWithRecoveryAsync(context, pair, llm, dayKey, dayIndexes, dayEntries, sourceMoments, null);

        private static async Task<List<CognitionSliceRecord>> RunCognitionFormationWithRecoveryAsync(
            MigrationContext context, PairIdentity pair, ILlmClient llm, string dayKey,
            List<EventIndexRecord> dayIndexes, List<EventEntryRecord> dayEntries, List<MomentRecord> sourceMoments,
            CoreProgressSource progress)
        {
            var sources = sourceMoments ?? context.Store.GetEvidenceMoments(
                dayEntries.Select(x => x.SourceMomentId).Concat(dayIndexes.Select(x => x.FirstMomentId)));
            var batches = RuntimeSliceLogic.EvidenceBatches(context.Store, sources).ToList();
            var allChanged = new List<CognitionSliceRecord>();
            var batchIndex = 0;
            foreach (var evidence in batches)
            {
                batchIndex++;
                var cognitionKey = progress == null ? null
                    : ProgressKey(dayKey, progress.Generation, "cognition", evidence.Select(x => x.Id).ToList());
                var cognitionStamp = progress == null ? null : SourceStamp(context, evidence.Select(x => x.Id));
                if (progress != null && RestoreCognition(context, progress, cognitionKey, cognitionStamp)) continue;
                var evidenceContext = RuntimeSliceLogic.EvidenceContext(context.Store, evidence);
                var activeCognitions = CognitionReviewRecallLogic.Select(context.Store, evidence,
                    context.RequireCognitionEncoder, context.Vectors, evidenceContext);
                var candidateStamp = Digest(activeCognitions.OrderBy(x => x.Id, StringComparer.Ordinal));
                var activeTags = context.Store.GetActiveLifeTags().OrderByDescending(x => x.ActivationCount)
                    .ThenBy(x => x.Label, StringComparer.Ordinal).Take(60).ToList();
                var cardsNow = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
                var userPronoun = IdentityCardLogic.UserPronoun(cardsNow, pair);
                var momentIds = new HashSet<string>(evidence.Select(x => x.Id));
                var batchEntries = dayEntries.Where(x => momentIds.Contains(x.SourceMomentId)).ToList();
                var batchIndexIds = new HashSet<string>(batchEntries.Select(x => x.IndexId));
                var batchIndexes = dayIndexes.Where(x => batchIndexIds.Contains(x.Id) || momentIds.Contains(x.FirstMomentId)).ToList();
                var prompt = ReplayPrompts.BuildCognitionFormationPrompt(
                    pair, dayKey, activeCognitions, activeTags, batchIndexes, batchEntries, userPronoun, evidence)
                    + evidenceContext;
                var messages = new List<DeepSeekMessageData>
                {
                    new DeepSeekMessageData("system", prompt),
                    new DeepSeekMessageData("user", CorePrompts.Migration.CognitionUser)
                };
                var plan = cognitionKey == null ? null : PuzzleViewLogic.Read<CognitionPlanCheckpoint>(
                    context.Store.LoadPluginDocument(CoreProgressDocuments, cognitionKey + ":proposal"));
                var output = plan?.Stamp == cognitionStamp && plan?.CandidateStamp == candidateStamp ? plan.Output : null;
                if (output == null)
                {
                    output = await ReviewWorkLogic.CompleteAsync<ReplayPrompts.CognitionFormationOutputData>(
                        context.Store, cognitionKey ?? dayKey + ":cognition", llm, messages,
                        x => CognitionFormationLogic.ValidationError(x.cognitions, evidence, activeCognitions, activeTags, false),
                        x => CognitionFormationLogic.TextParts(x.cognitions));
                    if (cognitionKey != null) context.Store.SavePluginDocument(CoreProgressDocuments, cognitionKey + ":proposal",
                        TraceJson.ToJson(new CognitionPlanCheckpoint { Stamp = cognitionStamp, CandidateStamp = candidateStamp, Output = output }));
                }
                var resolution = await CognitionConsolidationLogic.ResolveAsync(context.Store, llm, evidence,
                    activeCognitions, output.cognitions, context.RequireCognitionEncoder, context.Vectors, CancellationToken.None);
                activeCognitions = resolution.Candidates;
                var validation = CognitionFormationLogic.ValidationError(resolution.Writes, evidence, activeCognitions, activeTags);
                if (validation != null) throw new InvalidOperationException(validation);
                output.cognitions = resolution.Writes;
                candidateStamp = Digest(activeCognitions.OrderBy(x => x.Id, StringComparer.Ordinal));
                var changed = progress == null ? context.Store.CommitCognitions(evidence[^1].Id, output.cognitions)
                    : context.Store.CommitCognitionsWithCheckpoint(evidence[^1].Id, output.cognitions,
                        CoreProgressDocuments, cognitionKey, () =>
                        {
                            EnsureCoreProgress(context, progress);
                            var ids = activeCognitions.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                            if (candidateStamp != Digest(context.Store.GetCognitionReviewCandidates(
                                evidence[0].ConversationId ?? "", evidence[0].MemoryVisibility == "public")
                                .Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id, StringComparer.Ordinal)))
                                throw new InvalidOperationException("认知生成期间候选已修订，旧输出不能提交。");
                        },
                        _ => TraceJson.ToJson(new CompletedProgress { Stamp = cognitionStamp }));
                allChanged.AddRange(changed);
                LogCall(context, dayKey, "cognition_formation", batchIndex,
                    "认知：" + changed.Count + " 条", TraceJson.ToJson(output));
            }
            Console.WriteLine("  认知形成：" + batches.Count + " 批，" + allChanged.Count + " 条变更。");
            return allChanged;
        }

        /// <summary>
        /// 认知回填：主线等已有事件、尚无认知的库，逐天用当天已有事件跑认知形成，
        /// 再把新认知排进该天日榜（与事件并列）；跨层晋升由 promote-all 兜底。
        /// 幂等守卫：同文 create 会跳过，但 revise/reinforce 轻微累积，不建议频繁重跑。
        /// </summary>
        public static async Task<int> BackfillCognitionsAsync(MigrationContext context, string[] args)
        {
            var pair = context.RequirePair();
            var llm = context.RequireLlm();
            var fromDay = CliArgs.Value(args, "--from");
            var dayKeys = context.Migration.GetDistinctEventDays()
                .Where(x => string.IsNullOrWhiteSpace(fromDay) ||
                            string.Compare(x, fromDay, StringComparison.Ordinal) >= 0)
                .ToList();
            Console.WriteLine("认知回填：共 " + dayKeys.Count + " 个有事件的天" +
                              (string.IsNullOrWhiteSpace(fromDay) ? "" : "（自 " + fromDay + " 起续跑）") +
                              "，逐天用当天事件跑认知形成。");
            var total = 0;
            foreach (var dayKey in dayKeys)
            {
                var day = DateTime.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var range = DateRange.Parse(new[] { "--from", dayKey, "--to", dayKey });
                var start = range.DayStartMs(day);
                var end = range.DayEndMs(day);
                var dayIndexes = context.Migration.GetActiveEventIndexes()
                    .Where(x => x.TimeUnixMs >= start && x.TimeUnixMs < end)
                    .OrderBy(x => x.TimeUnixMs)
                    .ToList();
                if (dayIndexes.Count == 0) continue;
                var dayEntries = context.Migration.GetEntriesByIndexIds(dayIndexes.Select(x => x.Id).ToList());
                var changed = await RunCognitionFormationAsync(
                    context, pair, llm, dayKey, dayIndexes, dayEntries);
                if (changed.Count > 0)
                {
                    total += changed.Count;
                    var cognitionItems = await DayLadderLogic.RankDayCognitionsAsync(
                        context, pair, dayKey, changed, llm);
                    var existing = context.Migration.GetLadder("day", dayKey);
                    var combined = existing.Concat(cognitionItems).ToList();
                    if (combined.Count > 0)
                        context.Migration.ReplaceLadder("day", dayKey, combined);
                }
                Console.WriteLine("  " + dayKey + "：+" + changed.Count + " 条认知（累计 " + total + "）");
            }
            Console.WriteLine("认知回填完成：共 " + total + " 条认知。");
            return 0;
        }

        /// <summary>Legacy lexical merging cannot prove equal meaning or preserve counterevidence.</summary>
        public static int DedupeCognitions(MigrationContext context)
            => throw new InvalidOperationException("旧dedupe-cognitions按文字相似度改写认知图的机制已停用。请使用身份重整预览当前认识；原认知、条件、反证与证据继续保留，不按相似句子合并。");

        /// <summary>日榜排序：事件与认知各自排名、合并后整批替换当天 day 榜。</summary>
        private static async Task RankDayLadderAsync(
            MigrationContext context,
            PairIdentity pair,
            ILlmClient llm,
            string dayKey,
            List<EventIndexRecord> dayIndexes,
            List<CognitionSliceRecord> dayCognitions)
        {
            var eventItems = await DayLadderLogic.RankDayAsync(context, pair, dayKey, dayIndexes, llm);
            var cognitionItems = await DayLadderLogic.RankDayCognitionsAsync(
                context, pair, dayKey, dayCognitions, llm);
            var combined = new List<LadderItemRecord>();
            combined.AddRange(eventItems);
            combined.AddRange(cognitionItems);
            if (combined.Count > 0)
                context.Migration.ReplaceLadder("day", dayKey, combined);
            else
                Console.WriteLine("  日榜：当天没有可上榜的事件或认知。");
        }

        /// <summary>打印四张卡（含 revision）。</summary>
        public static int PrintCardsCommand(MigrationContext context)
        {
            var pair = context.Store.LoadPairIdentity();
            var cards = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            PrintCards("四张身份小卡", cards, pair);
            return 0;
        }

        private static void PrintCards(string title, List<IdentityCardRecord> cards, PairIdentity pair)
        {
            Console.WriteLine("========== " + title + " ==========");
            foreach (var card in cards)
            {
                Console.WriteLine("【" + IdentityCardSlotValues.Title(card.Slot, pair) + "】rev" + card.Revision);
                Console.WriteLine(card.Body);
                Console.WriteLine();
            }
        }

        private static void PrintDayResult(
            string dayKey,
            List<MomentRecord> moments,
            List<EventIndexRecord> indexes,
            List<EventEntryRecord> entries,
            int observationCalls,
            int detailCalls,
            MigrationContext context)
        {
            var builder = new StringBuilder();
            builder.AppendLine("========== 构筑结果 " + dayKey + " ==========");
            builder.AppendLine("moments：" + moments.Count + "；观察调用：" + observationCalls
                + "；细节调用：" + detailCalls + "；索引：" + indexes.Count
                + "；条目：" + entries.Count);
            builder.AppendLine();
            foreach (var index in indexes)
            {
                var dateText = index.TimeUnixMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(index.TimeUnixMs)
                        .ToOffset(MigrationContext.ChinaOffset).ToString("yyyy年M月d日")
                    : string.Empty;
                builder.AppendLine("◆ " + dateText + index.TimeLabel + "（" + index.DayKindLabel + "）"
                    + (string.IsNullOrWhiteSpace(index.PlaceLabel) ? "" : "·" + index.PlaceLabel)
                    + (string.IsNullOrWhiteSpace(index.PersonLabel) ? "" : "·" + index.PersonLabel)
                    + "·" + index.EventSummary
                    + (string.IsNullOrWhiteSpace(index.MoodLabel) ? "" : "·" + index.MoodLabel));
                foreach (var entry in entries.Where(x => x.IndexId == index.Id))
                {
                    builder.AppendLine("   └ " + entry.Summary + " [" + entry.Realm + "]");
                    if (!string.IsNullOrWhiteSpace(entry.Detail))
                        builder.AppendLine("       " + entry.Detail);
                }
            }
            var text = builder.ToString();
            var path = Path.Combine(context.DataDirectory, "day_build_report.txt");
            File.WriteAllText(path, text, Encoding.UTF8);
            Console.WriteLine(text);
            Console.WriteLine("构筑报告已写入：" + path);
        }

        private static List<string> ResolveTagIds(
            List<string> tagIds,
            List<string> newTagNames,
            Dictionary<string, string> aliases,
            Dictionary<string, string> tagByLabel)
        {
            var result = new List<string>();
            foreach (var id in tagIds ?? new List<string>())
            {
                string full;
                if (aliases.TryGetValue(id ?? string.Empty, out full) && !result.Contains(full)) result.Add(full);
            }
            foreach (var name in newTagNames ?? new List<string>())
            {
                string id;
                if (tagByLabel.TryGetValue((name ?? string.Empty).Trim(), out id) && !result.Contains(id)) result.Add(id);
            }
            return result.Take(8).ToList();
        }

        /// <summary>Tag 完整 ID → 完整 ID 与去掉前缀的后缀 ID 的别名表，修复 LLM 截断 ID 的问题。</summary>
        private static Dictionary<string, string> BuildTagAliases(List<LifeTagRecord> tags)
        {
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in tags ?? new List<LifeTagRecord>())
            {
                aliases[tag.Id] = tag.Id;
                var dot = tag.Id.LastIndexOf('.');
                if (dot >= 0 && dot < tag.Id.Length - 1)
                {
                    var suffix = tag.Id.Substring(dot + 1);
                    if (!aliases.ContainsKey(suffix)) aliases[suffix] = tag.Id;
                }
            }
            return aliases;
        }

        private static void RepairSelectedTagIds(
            MemoryObservationOutputData output, Dictionary<string, string> aliases)
        {
            if (output == null) return;
            var selected = new List<string>();
            foreach (var id in output.selected_tag_ids ?? new List<string>())
            {
                string full;
                var mapped = aliases.TryGetValue(id ?? string.Empty, out full) ? full : id;
                if (mapped != null && !selected.Contains(mapped)) selected.Add(mapped);
            }
            output.selected_tag_ids = selected;
        }

        private static EventIndexRecord ResolveIndexAlias(
            string alias, Dictionary<string, EventIndexRecord> byId)
        {
            alias = (alias ?? string.Empty).Trim();
            if (alias.Length == 0) return null;
            EventIndexRecord exact;
            if (byId.TryGetValue(alias, out exact)) return exact;
            if (alias.Length >= 12)
            {
                var matches = byId.Keys.Where(x => x.EndsWith(alias, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1) return byId[matches[0]];
            }
            return null;
        }

        private static IdentityCardRecord Card(List<IdentityCardRecord> cards, string slot)
        {
            return cards.First(x => x.Slot == slot);
        }

        private static string NormalizeRealm(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "external_world" || value == "external") return TraceRealmValues.ExternalWorld;
            if (value == "shared_scene" || value == "shared") return TraceRealmValues.SharedScene;
            if (value == "meta") return TraceRealmValues.Meta;
            if (value == "explicit_fiction" || value == "fiction") return TraceRealmValues.ExplicitFiction;
            return TraceRealmValues.SharedScene;
        }

        private static List<List<MomentRecord>> ChunkMoments(List<MomentRecord> moments)
        {
            var result = new List<List<MomentRecord>>();
            var current = new List<MomentRecord>();
            var chars = 0;
            foreach (var moment in moments)
            {
                var length = (moment.Content ?? string.Empty).Length;
                if (length > 16000) throw new ReviewProsePendingException("单条事件原文超过16000字预算，原件保留，不能截断后标记已构筑。");
                if (current.Count >= 18 || (current.Count > 0 && chars + length > 6000))
                {
                    result.Add(current);
                    current = new List<MomentRecord>();
                    chars = 0;
                }
                current.Add(moment);
                chars += length;
            }
            if (current.Count > 0) result.Add(current);
            return result;
        }

        private static void RebuildRouter(
            MigrationContext context, PairIdentity pair, HierarchicalVectorRouterLogic router)
        {
            var ontology = LifeTagVectorLogic.BuildOntology(
                context.Store, CoreVectorOntologyFactory.Create(pair));
            router.Build(ontology);
        }

        private static string FormatMoment(MomentRecord moment, PairIdentity pair)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(moment.CreatedUnixMs)
                .ToOffset(MigrationContext.ChinaOffset).ToString("HH:mm");
            return time + " " + pair.LabelForRole(moment.Role) + "：" + (moment.Content ?? string.Empty);
        }

        /// <summary>把一次 LLM 调用写入留痕表并广播到实时监视控制台。</summary>
        internal static void LogCall(
            MigrationContext context, string dayKey, string kind, int chunkIndex, string digest, string outputJson)
        {
            context.Migration.SaveCallLog(new ReplayCallLogRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                DayKey = dayKey,
                CallKind = kind,
                ChunkIndex = chunkIndex,
                OutputJson = Limit(outputJson, 60000)
            });
            MigrationLive.Instance?.Notify(kind, digest, Limit(outputJson, 4000));
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
