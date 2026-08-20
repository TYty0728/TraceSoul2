using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>
    /// 记忆神经：只读地检索第四层多维索引（时间×地点×人物×事件×心情）里的共同经历切片。
    /// 当场观察会把新标签与事实写入生命网；本神经只读召回。话题结束时实时归档。
    /// 日构建继续只做浸染和阶梯，不抢活体写入。
    /// </summary>
    public sealed class MemoryNervePlugin : ITracePlugin
    {
        private const string PluginId = "builtin.memory";

        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = PluginId,
            DisplayName = "人生记忆神经",
            Version = "3.1.0",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Kernel,
            Description = "子代理沿四层记忆定位，语义向量在定位范围内拼装最相近的细节；话题结束时实时归档，只读召回不写历史。"
        };

        public void Register(TracePluginContext context)
        {
            context.AddMountedFacet(new TodayNewFacet());
            context.AddCallable(new ActivateMemoryNerve());
            context.AddCallable(new ArchiveMemoryNerve());
        }

        public void Shutdown() { }

        /// <summary>
        /// 今日新识：实时对话中「今天刚知道的」最小便签（每条一句话、带证据）。
        /// 当天每轮注入 Brain 上下文；日复盘（04:00 边界）再把它加工成正式记忆。
        /// </summary>
        private sealed class TodayNewFacet : ITraceMountedFacet
        {
            private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
            private const int BoundaryHour = 4;
            private const int MaxShown = 10;

            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "memory.today.new",
                Kind = TraceContributionKindValues.MountedFacet,
                DisplayName = "今日新识",
                Description = "今天刚知道的事（最小便签，每条一句话）。实时写入、当天每轮注入；日复盘再加工成正式记忆。",
                Provides = "brain.memory.today_new",
                OutputJsonSchema = "{changed:boolean,summary:string,fields:[items]}",
                RefreshMode = TraceFacetRefreshValues.OncePerTurn,
                Priority = 89,
                MaxContextChars = 500,
                HasInternalMutation = true
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceContextBlockData> BuildContextAsync(
                TraceTurnContext context, CancellationToken cancellationToken)
            {
                var boundary = TodayBoundary(DateTimeOffset.Now);
                var items = context.Services.Storage.GetTodayNewItems(
                    context.ConversationId, boundary.ToUnixTimeMilliseconds(), MaxShown);
                if (items == null || items.Count == 0)
                    return Task.FromResult<TraceContextBlockData>(null);
                var builder = new StringBuilder();
                builder.AppendLine("今天刚知道的：");
                foreach (var item in items)
                    builder.AppendLine("- " + item.Content);
                return Task.FromResult(new TraceContextBlockData
                {
                    Title = "今日新识",
                    Content = builder.ToString().TrimEnd()
                });
            }

            public Task<TraceCapabilityResultData> ApplyOutputAsync(
                BrainFacetOutputData output, TraceTurnContext context, CancellationToken cancellationToken)
            {
                if (output == null || !output.changed)
                    return Task.FromResult<TraceCapabilityResultData>(null);
                var raw = output.GetField("items", string.Empty);
                var lines = (raw ?? string.Empty)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0 && x.Length <= 40)
                    .ToList();
                if (lines.Count == 0)
                    return Task.FromResult<TraceCapabilityResultData>(null);
                var now = DateTimeOffset.Now;
                var boundary = TodayBoundary(now);
                var added = context.Services.Storage.AddTodayNewItems(
                    context.ConversationId, lines, context.Moment.Id,
                    boundary.ToString("yyyy-MM-dd"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return Task.FromResult(new TraceCapabilityResultData
                {
                    Status = added > 0 ? "success" : "unchanged",
                    Summary = added > 0 ? "今日新识 +" + added + " 条。" : "今日新识没有新增（重复或无效）。",
                    Payload = string.Join("\n", lines),
                    EvidenceRefs = new List<string> { "moment:" + context.Moment.Id }
                });
            }

            /// <summary>记忆日 04:00 边界：04:00 前归前一天。</summary>
            private static DateTimeOffset TodayBoundary(DateTimeOffset now)
            {
                var local = now.ToOffset(ChinaOffset);
                var boundary = local.Date.AddHours(BoundaryHour);
                if (local < boundary) boundary = boundary.AddDays(-1);
                return boundary;
            }
        }

        internal static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        /// <summary>按句界收尾（句号/问号/叹号/省略号），绝不切半句。</summary>
        internal static string LimitSentence(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length < max) return value;
            var window = value.Substring(0, Math.Min(value.Length, max));
            var lastEnd = -1;
            foreach (var marker in new[] { '。', '！', '？', '…' })
            {
                var index = window.LastIndexOf(marker);
                if (index > lastEnd) lastEnd = index;
            }
            if (lastEnd >= 40) return value.Substring(0, lastEnd + 1).Trim();
            return window.Trim();
        }

        /// <summary>
        /// 实时归档：Brain 判断话题转变、上一个事件结束时调用——
        /// 把尚未落入记忆库的 moment 立刻构筑成一条事件切片（索引+条目+向量），并打上 built 标记。
        /// 日复盘只兜底处理这里漏掉的未归档 moment。
        /// </summary>
        private sealed class ArchiveMemoryNerve : ITraceCallableContribution
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "memory.archive",
                Kind = TraceContributionKindValues.CallableNerve,
                DisplayName = "归档刚结束的话题事件",
                Description = "把刚刚结束的话题/事件直接构筑成多维索引与条目并标记已入库；被归档的 Moment 不再进入日复盘。",
                Provides = "personal_memory.archive",
                WhenToUse = "话题明显转变、上一个话题或事件已经结束时，把刚才这段对话归档成一条事件切片；对方明确说『记一下』『帮我记住』时也调用。",
                WhenNotToUse = "话题还在进行中、只有零散寒暄、没有成块内容时。",
                ParametersJsonSchema = "{summary:string,detail?:string,mood?:string,place?:string}",
                HasInternalMutation = true
            };

            public bool IsAvailable(TraceTurnContext context)
            {
                return context != null && context.Services != null && context.Services.Storage != null;
            }

            /// <summary>归档内容的 LLM 输出：基于未归档窗口原文总结，而不是依赖 Brain 的上下文。</summary>
            [System.Serializable]
            public sealed class ArchiveSummaryOutputData
            {
                public string summary = string.Empty;
                public string detail = string.Empty;
                public string mood = string.Empty;
            }

            public async Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call,
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                var storage = context.Services.Storage;
                var pair = storage.LoadPairIdentity();

                // 未归档窗口：最近 25 条（一个话题的合理长度），超出部分留给日复盘兜底。
                var window = storage.GetRecentMoments(context.ConversationId, 60)
                    .Where(x => x.MemoryStatus != "built")
                    .TakeLast(25)
                    .ToList();
                if (window.Count == 0)
                {
                    return new TraceCapabilityResultData
                    {
                        Status = "empty",
                        Summary = "没有未归档的 Moment，无需重复归档。",
                        Payload = string.Empty
                    };
                }

                // 优先用未归档原文做总结（Brain 的上下文可能没有原文）；LLM 不可用才退回 Brain 参数。
                var summary = call.GetArgument("summary", string.Empty).Trim();
                var detail = LimitSentence(call.GetArgument("detail", string.Empty).Trim(), 200);
                var mood = call.GetArgument("mood", string.Empty).Trim();
                var llm = context.Services.Llm;
                if (llm != null)
                {
                    try
                    {
                        var text = string.Join("\n", window.Select(x =>
                            pair.LabelForRole(x.Role) + "：" + (x.Content ?? string.Empty).Replace('\n', ' ')));
                        var messages = new List<DeepSeekMessageData>
                        {
                            new DeepSeekMessageData("system", BuildArchivePrompt(pair)),
                            new DeepSeekMessageData("user", "刚结束的话题对话记录：\n" + text)
                        };
                        var output = await DeepSeekStructuredOutputLogic.CompleteAsync<ArchiveSummaryOutputData>(
                            llm, messages,
                            x => x != null && !string.IsNullOrWhiteSpace(x.summary),
                            "归档总结输出缺少 summary。", cancellationToken);
                        summary = (output.summary ?? string.Empty).Trim();
                        detail = LimitSentence(output.detail ?? string.Empty, 200);
                        if (!string.IsNullOrWhiteSpace(output.mood)) mood = output.mood.Trim();
                    }
                    catch
                    {
                        /* LLM 总结失败时退回 Brain 参数 */
                    }
                }
                if (summary.Length == 0)
                    throw new InvalidOperationException("归档需要一句客观总结 summary。");

                var now = DateTimeOffset.Now;
                var index = new EventIndexRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TagIds = string.Empty,
                    TimeLabel = TimeLanguageUtil.PeriodZh(now),
                    DayKindLabel = TimeLanguageUtil.DayKindZh(now),
                    TimeUnixMs = now.ToUnixTimeMilliseconds(),
                    PlaceLabel = Limit(call.GetArgument("place", string.Empty).Trim(), 20),
                    PersonLabel = pair.IsComplete ? pair.Username : "她",
                    EventSummary = Limit(summary, 80),
                    MoodLabel = Limit(mood, 12),
                    FirstMomentId = window[0].Id,
                    Status = "active",
                    CreatedUnixMs = now.ToUnixTimeMilliseconds(),
                    UpdatedUnixMs = now.ToUnixTimeMilliseconds()
                };
                var entry = new EventEntryRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    IndexId = index.Id,
                    Summary = Limit(summary, 80),
                    Detail = detail,
                    SourceMomentId = window[window.Count - 1].Id,
                    Realm = "shared_scene",
                    CreatedUnixMs = now.ToUnixTimeMilliseconds()
                };
                storage.SaveEventIndex(index);
                storage.AppendEventEntry(entry);
                var marked = storage.MarkMomentsBuilt(window.Select(x => x.Id));
                var recall = context.Services.Recall;
                if (recall != null && recall.IsAvailable)
                {
                    try { recall.PutEntryVector(entry.Id, entry.Summary); }
                    catch { /* 向量缺失只影响语义召回，不阻断归档 */ }
                }

                return new TraceCapabilityResultData
                {
                    Status = "success",
                    Summary = "已归档 " + marked + " 条 Moment → 事件「" + Limit(summary, 30) + "」。",
                    Payload = "◆ " + TimeLanguageUtil.NaturalNow(now) + " · " + index.PersonLabel +
                              (string.IsNullOrWhiteSpace(index.MoodLabel) ? string.Empty : " · 心情：" + index.MoodLabel) +
                              "\n  事件：" + index.EventSummary +
                              (detail.Length == 0 ? string.Empty : "\n  - " + entry.Summary + "｜" + detail),
                    EvidenceRefs = new List<string> { "event_entry:" + entry.Id }
                };
            }

            private static string BuildArchivePrompt(PairIdentity pair)
            {
                var builder = new System.Text.StringBuilder();
                builder.AppendLine(pair.Apply("你是 {assname} 的记忆整理助手。下面是一段刚结束的话题的对话记录。请："));
                builder.AppendLine("1. summary：一句客观、事实化的话题总结（谁做了什么/发生了什么，不超过80字）；");
                builder.AppendLine("2. detail：用第一人称（我）写这段话题的细节，自然、不超过200字、不截断半句；没有可写的就留空；");
                builder.AppendLine(pair.Apply("3. mood：这段对话里她的心情词（如 轻松、开心、平静、难过），读不出就留空。指代她一律按档案性别。"));
                builder.AppendLine("只输出 JSON：{\"summary\":\"一句总结\",\"detail\":\"第一人称细节\",\"mood\":\"心情词\"}");
                return builder.ToString();
            }
        }

        private sealed class ActivateMemoryNerve : ITraceCallableContribution
        {
            private const int RouteTagCap = 60;

            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "memory.activate",
                Kind = TraceContributionKindValues.CallableNerve,
                DisplayName = "激活并唤醒人生记忆",
                Description = "子代理沿生命标签与多维维度列表定位（1-4 层），再由语义向量在定位范围内拼装最相近的细节切片；只向 Brain 返回证据，不写新记忆。",
                Provides = "personal_memory.recall",
                WhenToUse = "对方问你是否记得、问起一起经历过的具体事情，或你回答前需要共同记忆佐证。先 activate，再 finish。",
                WhenNotToUse = "天气、问候、嗯嗯哈哈，或当前对话原文已经足够回答、不需要翻找过往经历时。",
                ParametersJsonSchema = "{query:string}",
                HasInternalMutation = false
            };

            public bool IsAvailable(TraceTurnContext context)
            {
                return context != null && context.Services != null && context.Services.Storage != null;
            }

            public async Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call,
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                var query = call.GetArgument("query", context.Moment.Content).Trim();
                if (query.Length == 0) query = context.Moment.Content;
                // top_k 由控制台配置（引擎默认值）；Brain 不干预拼装条数。
                var topK = context.Services.Recall != null && context.Services.Recall.DefaultTopK > 0
                    ? context.Services.Recall.DefaultTopK
                    : 3;
                topK = Math.Max(1, Math.Min(10, topK));
                var storage = context.Services.Storage;

                var indexes = storage.GetActiveEventIndexes();
                if (indexes == null || indexes.Count == 0)
                {
                    return new TraceCapabilityResultData
                    {
                        Status = "empty",
                        Summary = "人生记忆还是空的，没有共同经历切片可以唤醒。",
                        Payload = string.Empty
                    };
                }

                var nerve = context.Services.NerveLlm;
                var recall = context.Services.Recall;
                if (nerve != null && recall != null && recall.IsAvailable)
                    return await ExecuteSubagentAsync(query, topK, nerve, recall, context, cancellationToken);

                // 兜底：宿主没有注入子代理模型或向量引擎时，退回字符路由 + n-gram 打分。
                return ExecuteFallback(query, context);
            }

            // ---------- 子代理定位 + 语义向量拼装（主路径） ----------

            /// <summary>记忆神经子代理的结构化输出。</summary>
            public sealed class MemoryRouteOutputData
            {
                public bool has_memory;
                public List<string> concept_ids = new List<string>();
                public List<string> time_labels = new List<string>();
                public List<string> month_buckets = new List<string>();
                public List<string> place_labels = new List<string>();
                public List<string> person_labels = new List<string>();
                public List<string> mood_labels = new List<string>();
                public string refined_query = string.Empty;
                public string reason = string.Empty;
            }

            /// <summary>
            /// 子代理只看可读的概念名，不接触数据库 GUID。返回后再在进程内映射为稳定 ID。
            /// </summary>
            [Serializable]
            public sealed class MemoryRouteSelectionData
            {
                public bool has_memory = false;
                public List<string> concept_labels = new List<string>();
                public List<string> time_labels = new List<string>();
                public List<string> month_buckets = new List<string>();
                public List<string> place_labels = new List<string>();
                public List<string> person_labels = new List<string>();
                public List<string> mood_labels = new List<string>();
                public string refined_query = string.Empty;
                public string reason = string.Empty;

                public MemoryRouteSelectionData() { }
            }

            private static async Task<TraceCapabilityResultData> ExecuteSubagentAsync(
                string query,
                int topK,
                ILlmClient nerve,
                IMemoryRecallEngine recall,
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                var storage = context.Services.Storage;
                var route = await RouteBySubagentAsync(query, nerve, storage, cancellationToken);
                var labelById = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var tag in storage.GetActiveLifeTags())
                    if (!labelById.ContainsKey(tag.Id)) labelById[tag.Id] = tag.Label;

                if (!route.has_memory)
                {
                    return new TraceCapabilityResultData
                    {
                        Status = "no_memory",
                        Summary = "记忆神经检索后认为没有这段回忆。",
                        Payload = "子代理判定：" + (string.IsNullOrWhiteSpace(route.reason)
                            ? "问题与已有的人生切片没有对应。"
                            : route.reason)
                    };
                }

                var filtered = storage.GetEventIndexesByFilter(
                    route.concept_ids, route.time_labels, new List<string>(),
                    route.place_labels, route.person_labels, route.mood_labels,
                    route.month_buckets, 500);
                if (filtered.Count == 0)
                {
                    return new TraceCapabilityResultData
                    {
                        Status = "no_memory",
                        Summary = "子代理的定位没有匹配到任何索引切片。",
                        Payload = "子代理定位：" + FormatRoute(route, labelById) + "，但第四层没有落在该范围的索引。"
                    };
                }

                var entriesByIndex = storage.GetEventEntriesByIndexIds(filtered.Select(x => x.Id))
                    .GroupBy(x => x.IndexId, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.OrderBy(e => e.CreatedUnixMs).ToList(),
                        StringComparer.Ordinal);
                var allEntries = filtered.SelectMany(x =>
                    entriesByIndex.TryGetValue(x.Id, out var list) ? list : Enumerable.Empty<EventEntryRecord>())
                    .ToList();
                if (allEntries.Count == 0)
                {
                    return new TraceCapabilityResultData
                    {
                        Status = "no_memory",
                        Summary = "定位范围里的索引还没有条目细节。",
                        Payload = "子代理定位：" + FormatRoute(route, labelById) + "，但该范围的索引尚无可拼装的细节。"
                    };
                }

                var searchText = string.IsNullOrWhiteSpace(route.refined_query) ? query : route.refined_query;
                var candidateIds = allEntries.Select(x => x.Id).Take(3000).ToList();
                var byId = allEntries.ToDictionary(x => x.Id, StringComparer.Ordinal);
                var hits = recall.Search(searchText, candidateIds, LadderRecallLogic.PoolSize(topK));
                hits = LadderRecallLogic.AdmitEvents(
                    hits, byId, LadderRecallLogic.EventIndexIds(storage), topK);
                var indexById = filtered.ToDictionary(x => x.Id, StringComparer.Ordinal);

                // 认知召回：标签命中的第一人称理解 + 痕迹 cue 唤醒（与事件切片并列）。
                var cognitions = RecallCognitions(storage, route.concept_ids, searchText, topK);

                var payload = FormatVectorHits(hits, byId, indexById);
                var cognitionText = FormatCognitions(cognitions);
                if (!string.IsNullOrWhiteSpace(cognitionText))
                    payload = payload + "\n\n" + cognitionText;

                var evidence = hits.Select(x => "event_entry:" + x.EntryId)
                    .Concat(cognitions.Select(x => "cognition:" + x.Id)).ToList();
                return new TraceCapabilityResultData
                {
                    Status = hits.Count == 0 && cognitions.Count == 0 ? "no_memory" : "success",
                    Summary = (hits.Count == 0 && cognitions.Count == 0)
                        ? "定位范围内语义检索没有足够相近的细节。"
                        : "子代理定位 + 语义向量拼装出 " + hits.Count + " 条细节切片、" + cognitions.Count +
                          " 条认知（模型 " + recall.ModelId + "）。",
                    Payload = payload,
                    EvidenceRefs = evidence
                };
            }

            /// <summary>认知召回：按路由点亮的生命标签取相关认知，再补痕迹 cue 唤醒的认知，去重后按置信度排序。</summary>
            private static List<CognitionSliceRecord> RecallCognitions(
                IMemoryStore storage, IEnumerable<string> conceptIds, string searchText, int topK)
            {
                var map = new Dictionary<string, CognitionSliceRecord>(StringComparer.Ordinal);
                foreach (var c in storage.GetCognitionCandidates(conceptIds, Math.Max(8, topK * 2)) ??
                                   new List<CognitionSliceRecord>())
                    if (c != null && c.Status == "active") map[c.Id] = c;
                foreach (var cue in storage.FindCognitionsByCue(searchText, 6) ??
                                      new List<CognitionCueRecallData>())
                    if (cue != null && cue.Cognition != null && cue.Cognition.Status == "active")
                        map[cue.Cognition.Id] = cue.Cognition;
                return LadderRecallLogic.AdmitCognitions(
                    map.Values, LadderRecallLogic.CognitionIds(storage), topK);
            }

            private static string FormatCognitions(List<CognitionSliceRecord> cognitions)
            {
                if (cognitions == null || cognitions.Count == 0) return string.Empty;
                var builder = new StringBuilder();
                builder.AppendLine("相关认知（我的第一人称理解）：");
                foreach (var c in cognitions)
                    builder.AppendLine("- " + c.Summary + "（置信 " + c.Confidence.ToString("0.00") + "）");
                return builder.ToString().TrimEnd();
            }

            private static async Task<MemoryRouteOutputData> RouteBySubagentAsync(
                string query,
                ILlmClient nerve,
                IMemoryStore storage,
                CancellationToken cancellationToken)
            {
                var output = await RouteOnceAsync(query, nerve, storage, cancellationToken, false);
                if (!output.has_memory)
                {
                    var all = storage.GetActiveLifeTags();
                    if (all != null && all.Count > RouteTagCap)
                        output = await RouteOnceAsync(query, nerve, storage, cancellationToken, true);
                }
                return output;
            }

            private static async Task<MemoryRouteOutputData> RouteOnceAsync(
                string query,
                ILlmClient nerve,
                IMemoryStore storage,
                CancellationToken cancellationToken,
                bool fullCatalog)
            {
                var allTags = storage.GetActiveLifeTags() ?? new List<LifeTagRecord>();
                var shownTags = SelectRouteTags(allTags, fullCatalog);
                var prompt = BuildRoutePrompt(storage, shownTags, allTags.Count, fullCatalog);
                var messages = new List<DeepSeekMessageData>
                {
                    new DeepSeekMessageData("system", prompt),
                    new DeepSeekMessageData("user", "需要定位的回忆：" + query)
                };
                var selected = await DeepSeekStructuredOutputLogic.CompleteAsync<MemoryRouteSelectionData>(
                    nerve,
                    messages,
                    x => x != null && (x.has_memory || !string.IsNullOrWhiteSpace(x.reason)),
                    "记忆神经子代理没有给出有效定位。",
                    cancellationToken);
                var idByLabel = shownTags
                    .GroupBy(x => x.Label ?? string.Empty, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.Ordinal);
                var output = new MemoryRouteOutputData
                {
                    has_memory = selected.has_memory,
                    concept_ids = (selected.concept_labels ?? new List<string>())
                        .Where(x => !string.IsNullOrWhiteSpace(x) && idByLabel.ContainsKey(x.Trim()))
                        .Select(x => idByLabel[x.Trim()]).Distinct().Take(3).ToList(),
                    time_labels = selected.time_labels ?? new List<string>(),
                    month_buckets = selected.month_buckets ?? new List<string>(),
                    place_labels = selected.place_labels ?? new List<string>(),
                    person_labels = selected.person_labels ?? new List<string>(),
                    mood_labels = selected.mood_labels ?? new List<string>(),
                    refined_query = selected.refined_query,
                    reason = selected.reason
                };
                output.concept_ids = (output.concept_ids ?? new List<string>()).Distinct().ToList();
                output.time_labels = (output.time_labels ?? new List<string>()).Distinct().ToList();
                output.month_buckets = (output.month_buckets ?? new List<string>()).Distinct().ToList();
                output.place_labels = (output.place_labels ?? new List<string>()).Distinct().ToList();
                output.person_labels = (output.person_labels ?? new List<string>()).Distinct().ToList();
                output.mood_labels = (output.mood_labels ?? new List<string>()).Distinct().ToList();
                output.refined_query = (output.refined_query ?? string.Empty).Trim();
                output.reason = (output.reason ?? string.Empty).Trim();
                return output;
            }

            private static List<LifeTagRecord> SelectRouteTags(
                List<LifeTagRecord> allTags,
                bool fullCatalog)
            {
                var source = allTags ?? new List<LifeTagRecord>();
                if (fullCatalog)
                    return source.OrderBy(x => x.Label, StringComparer.Ordinal).ToList();
                // 先按激活度选择，再按名称稳定展示；计数小幅变化不会打乱整段缓存前缀。
                return source.OrderByDescending(x => x.ActivationCount)
                    .ThenBy(x => x.Label, StringComparer.Ordinal)
                    .Take(RouteTagCap)
                    .OrderBy(x => x.Label, StringComparer.Ordinal)
                    .ToList();
            }

            private static string BuildRoutePrompt(
                IMemoryStore storage,
                List<LifeTagRecord> shownTags,
                int totalTagCount,
                bool fullCatalog)
            {
                var builder = new StringBuilder();
                builder.AppendLine("你是记忆定位器，只负责圈定检索范围，不回答对方、不写回记忆，也不解释数据库结构。");
                builder.AppendLine("内部的域与维度关系已经由存储层维护；你只选择可读概念和明确提到的交叉条件。");
                builder.AppendLine();
                builder.AppendLine("规则：");
                builder.AppendLine("1. has_memory=false 只用于问题明显描述从未经历的事；措辞不同或概念名不完全一致时，仍应先选最接近范围。");
                builder.AppendLine("2. concept_labels 选 0-3 个最贴切的概念名，必须从下方原样复制；不要输出任何内部 ID。");
                builder.AppendLine("3. 时段、地点、人物、心情只在问题明确提及时选择；年份月份或『第一次/最初/去年/某月』放 month_buckets。");
                builder.AppendLine("4. 指向开端时选择最早日期桶，并结合命名、形象、身份等初识概念；refined_query 改写成 10-30 字事实检索句。");
                builder.AppendLine();
                builder.AppendLine("只输出 JSON：");
                builder.AppendLine("{\"has_memory\":true,\"concept_labels\":[],\"time_labels\":[],\"month_buckets\":[],\"place_labels\":[],\"person_labels\":[],\"mood_labels\":[],\"refined_query\":\"\",\"reason\":\"\"}");
                builder.AppendLine();
                var truncated = !fullCatalog && totalTagCount > shownTags.Count;
                builder.AppendLine("可选概念名" +
                    (truncated ? "（按活跃度选取 " + shownTags.Count + " 个，其余 " +
                                 (totalTagCount - shownTags.Count) + " 个本次未列出）" : string.Empty) + "：");
                foreach (var tag in shownTags)
                    builder.AppendLine("- " + tag.Label + "：" + Limit(tag.Definition, 40));
                builder.AppendLine();
                builder.AppendLine("可选交叉条件（只能原样复制）：");
                var dims = storage.GetEventIndexDimensionValues();
                builder.AppendLine("时间时段：" + FormatList(dims.TimeLabels));
                builder.AppendLine("日期桶(yyyy-MM)：" + FormatList(dims.MonthBuckets));
                builder.AppendLine("地点：" + FormatList(dims.PlaceLabels));
                builder.AppendLine("人物：" + FormatList(dims.PersonLabels));
                builder.AppendLine("心情：" + FormatList(dims.MoodLabels));
                builder.AppendLine("（当前共 " + dims.TotalIndexes + " 个多维索引。）");
                return builder.ToString();
            }

            private static string FormatRoute(MemoryRouteOutputData route, Dictionary<string, string> labelById)
            {
                return "概念[" + string.Join(",", (route.concept_ids ?? new List<string>())
                           .Select(x => labelById != null && labelById.ContainsKey(x) ? labelById[x] : x)) +
                       "] 时间[" + string.Join(",", route.time_labels) + "] 日期[" +
                       string.Join(",", route.month_buckets) + "] 地点[" +
                       string.Join(",", route.place_labels) + "] 人物[" +
                       string.Join(",", route.person_labels) + "] 心情[" +
                       string.Join(",", route.mood_labels) + "]";
            }

            private static string FormatVectorHits(
                List<MemoryRecallHit> hits,
                Dictionary<string, EventEntryRecord> byId,
                Dictionary<string, EventIndexRecord> indexById)
            {
                var builder = new StringBuilder();
                builder.AppendLine("此刻唤醒的共同记忆：");
                foreach (var hit in hits)
                {
                    EventEntryRecord entry;
                    EventIndexRecord index;
                    if (!byId.TryGetValue(hit.EntryId, out entry)) continue;
                    indexById.TryGetValue(entry.IndexId, out index);
                    builder.AppendLine("◆ " + (index == null ? "索引未知" :
                        FormatDate(index.TimeUnixMs) + " · " +
                        (string.IsNullOrWhiteSpace(index.TimeLabel) ? "时段未知" : index.TimeLabel) +
                        (string.IsNullOrWhiteSpace(index.DayKindLabel) ? string.Empty : "（" + index.DayKindLabel + "）") +
                        " · " + (string.IsNullOrWhiteSpace(index.PersonLabel) ? "人物未知" : index.PersonLabel) +
                        (string.IsNullOrWhiteSpace(index.MoodLabel) ? string.Empty : " · 心情：" + index.MoodLabel)) +
                        "（相似度 " + hit.Score.ToString("0.00") + "）");
                    if (index != null) builder.AppendLine("  事件：" + Limit(index.EventSummary, 80));
                    builder.AppendLine("  - " + Limit(entry.Summary, 60) + "｜" + Limit(entry.Detail, 200));
                }
                if (hits.Count == 0)
                    builder.AppendLine("（候选范围内没有足够相近的细节。）");
                return builder.ToString().TrimEnd();
            }

            private static string FormatList(List<string> values)
            {
                var list = values ?? new List<string>();
                return list.Count == 0 ? "（空）" : string.Join("、", list.Take(60));
            }

            private static int ParseInt(string value, int fallback)
            {
                int parsed;
                return int.TryParse(value, out parsed) ? parsed : fallback;
            }

            // ---------- 兜底路径：字符路由 + n-gram 打分（无子代理/向量引擎时） ----------

            private static TraceCapabilityResultData ExecuteFallback(string query, TraceTurnContext context)
            {
                var storage = context.Services.Storage;
                var indexes = storage.GetActiveEventIndexes();

                // 1-3 层：生命标签向量路由。命中点亮概念，按 TagIds 收窄第 4 层候选。
                var route = Route(query, context.Services.Router);
                var routedIds = new HashSet<string>(indexes
                    .Where(x => MatchesAnyTag(x.TagIds, route.Activated))
                    .Select(x => x.Id), StringComparer.Ordinal);

                var entriesByIndex = storage.GetEventEntriesByIndexIds(indexes.Select(x => x.Id))
                    .GroupBy(x => x.IndexId, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.OrderBy(e => e.CreatedUnixMs).ToList(),
                        StringComparer.Ordinal);

                var normalized = Normalize(query);
                var hintEarliest = LooksForEarliest(normalized);
                var minTime = indexes.Min(x => x.TimeUnixMs);
                var useRoute = routedIds.Count > 0;

                var scored = indexes.Select(index =>
                {
                    List<EventEntryRecord> entries;
                    entriesByIndex.TryGetValue(index.Id, out entries);
                    entries = entries ?? new List<EventEntryRecord>();
                    var best = entries.Count == 0 ? 0f : entries.Max(e => ScoreEntry(normalized, index, e));
                    if (useRoute && routedIds.Contains(index.Id))
                        best += route.BonusFor(index.TagIds);
                    if (hintEarliest && index.TimeUnixMs == minTime) best += 0.6f;
                    return new ScoredIndex
                    {
                        Index = index,
                        Entries = entries,
                        Score = best,
                        Routed = useRoute && routedIds.Contains(index.Id)
                    };
                })
                .Where(x => x.Entries.Count > 0)
                .OrderByDescending(x => x.Routed)
                .ThenByDescending(x => x.Score)
                .ThenBy(x => x.Index.TimeUnixMs)
                .ToList();

                var totalEntries = scored.Sum(x => x.Entries.Count);
                var strong = scored.Count > 0 && scored[0].Score >= 0.05f;
                var picked = scored.Take(6).ToList();
                if (useRoute && picked.Count(x => x.Routed) < 3)
                {
                    picked = picked.Where(x => x.Routed).ToList();
                    foreach (var extra in scored.Where(x => !x.Routed))
                    {
                        if (picked.Count >= 6) break;
                        picked.Add(extra);
                    }
                }
                var payload = FormatSlices(picked, totalEntries, strong, useRoute ? route : null);
                var refs = picked.SelectMany(x => x.Entries)
                    .Select(x => "event_entry:" + x.Id).ToList();

                return new TraceCapabilityResultData
                {
                    Status = "success",
                    Summary = useRoute
                        ? "生命标签路由点亮 " + route.Concepts.Count + " 个概念，筛出 " +
                          picked.Count(x => x.Routed) + " 个相关切片作为证据。"
                        : "生命标签路由未命中，已全量检索人生切片，返回最相关 " + picked.Count + " 条供参考。",
                    Payload = payload,
                    EvidenceRefs = refs
                };
            }

            private sealed class ScoredIndex
            {
                public EventIndexRecord Index;
                public List<EventEntryRecord> Entries;
                public float Score;
                public bool Routed;
            }

            /// <summary>一次 1-3 层路由的结果：点亮的概念及其路由分。</summary>
            private sealed class RouteState
            {
                public HashSet<string> Activated = new HashSet<string>(StringComparer.Ordinal);
                public Dictionary<string, float> Scores = new Dictionary<string, float>(StringComparer.Ordinal);
                public List<string> Concepts = new List<string>();
                public bool Used;

                public float BonusFor(string tagIds)
                {
                    var best = 0f;
                    foreach (var token in SplitTags(tagIds))
                    {
                        float score;
                        if (Scores.TryGetValue(token, out score) && score > best) best = score;
                    }
                    return best * 0.8f;
                }
            }

            private static RouteState Route(string query, IHierarchicalVectorRouter router)
            {
                var state = new RouteState();
                if (router == null) return state;
                VectorRouteResult result;
                try
                {
                    result = router.Route(query);
                }
                catch
                {
                    return state;
                }
                if (result == null || result.Concepts == null) return state;
                foreach (var hit in result.Concepts)
                {
                    if (hit == null || hit.Node == null) continue;
                    state.Activated.Add(hit.Node.Id);
                    state.Scores[hit.Node.Id] = hit.Score;
                    // 历史数据里 TagIds 可能只存了 concept.life. 后面的裸 GUID，做后缀匹配。
                    var suffix = GuidSuffix(hit.Node.Id);
                    if (suffix != null)
                    {
                        state.Activated.Add(suffix);
                        state.Scores[suffix] = hit.Score;
                    }
                    state.Concepts.Add(hit.Node.Label + "(" + hit.Score.ToString("0.00") + ")");
                }
                state.Used = state.Activated.Count > 0;
                return state;
            }

            private static bool MatchesAnyTag(string tagIds, HashSet<string> activated)
            {
                if (activated == null || activated.Count == 0) return false;
                foreach (var token in SplitTags(tagIds))
                    if (activated.Contains(token)) return true;
                return false;
            }

            private static IEnumerable<string> SplitTags(string tagIds)
            {
                return (tagIds ?? string.Empty)
                    .Split(new[] { ',', ';', '，', '；', '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0);
            }

            private static string GuidSuffix(string id)
            {
                if (string.IsNullOrEmpty(id)) return null;
                var dot = id.LastIndexOf('.');
                if (dot < 0) return null;
                var suffix = id.Substring(dot + 1);
                return suffix.Length >= 16 ? suffix : null;
            }

            private static float ScoreEntry(string query, EventIndexRecord index, EventEntryRecord entry)
            {
                var score = 0f;
                score += BigramOverlap(query, Normalize(index.EventSummary)) * 2.2f;
                score += BigramOverlap(query, Normalize(index.PersonLabel)) * 1.6f;
                score += BigramOverlap(query, Normalize(index.PlaceLabel)) * 1.2f;
                score += BigramOverlap(query, Normalize(index.TimeLabel + " " + index.DayKindLabel)) * 0.8f;
                score += BigramOverlap(query, Normalize(entry.Summary)) * 1.5f;
                score += BigramOverlap(query, Normalize(entry.Detail)) * 0.7f;
                score += CharOverlap(query, Normalize(entry.Summary)) * 0.25f;
                score += CharOverlap(query, Normalize(entry.Detail)) * 0.1f;
                return score;
            }

            /// <summary>共同 2-gram 覆盖率：查询里有多少成对相邻字出现在目标文本里。</summary>
            private static float BigramOverlap(string query, string text)
            {
                if (query == null || text == null || query.Length < 2 || text.Length < 2) return 0f;
                var set = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i + 1 < query.Length; i++) set.Add(query.Substring(i, 2));
                var hit = 0;
                for (var i = 0; i + 1 < text.Length; i++)
                    if (set.Contains(text.Substring(i, 2))) hit++;
                return (float)hit / Math.Max(1, text.Length - 1);
            }

            private static float CharOverlap(string query, string text)
            {
                if (query == null || text == null || query.Length == 0 || text.Length == 0) return 0f;
                var set = new HashSet<char>(query);
                var hit = 0;
                foreach (var ch in text) if (set.Contains(ch)) hit++;
                return (float)hit / Math.Max(1, text.Length);
            }

            private static bool LooksForEarliest(string query)
            {
                var hints = new[]
                {
                    "第一", "初次", "最初", "最开始", "刚认识", "见面", "认识", "相遇", "初遇", "开始"
                };
                foreach (var hint in hints)
                    if (query.IndexOf(hint, StringComparison.Ordinal) >= 0) return true;
                return false;
            }

            private static string FormatSlices(List<ScoredIndex> picked, int totalEntries, bool strong, RouteState route)
            {
                var builder = new StringBuilder();
                var shown = picked.Sum(x => x.Entries.Count);
                if (route != null && route.Used)
                {
                    builder.AppendLine("生命标签路由点亮概念：" + string.Join("、", route.Concepts) +
                                       "。以下共同经历切片（共 " + totalEntries + " 条条目，取 " + shown + " 条，★=路由命中）：");
                }
                else
                {
                    builder.AppendLine(strong
                        ? "路由未命中概念，已全量检索人生切片（共 " + totalEntries + " 条条目，取最相关 " + shown + " 条）："
                        : "没有强相关切片，以下是时间最近的共同经历切片（共 " + totalEntries + " 条条目，取最近 " + shown + " 条）：");
                }
                foreach (var group in picked)
                {
                    var index = group.Index;
                    builder.AppendLine((group.Routed ? "★" : string.Empty) + "◆ " + FormatDate(index.TimeUnixMs) + " · " +
                        (string.IsNullOrWhiteSpace(index.TimeLabel) ? "时段未知" : index.TimeLabel) +
                        (string.IsNullOrWhiteSpace(index.DayKindLabel) ? string.Empty : "（" + index.DayKindLabel + "）") +
                        " · " + (string.IsNullOrWhiteSpace(index.PersonLabel) ? "人物未知" : index.PersonLabel) +
                        (string.IsNullOrWhiteSpace(index.MoodLabel) ? string.Empty : " · 心情：" + index.MoodLabel));
                    builder.AppendLine("  事件：" + Limit(index.EventSummary, 80));
                    foreach (var entry in group.Entries)
                    {
                        builder.AppendLine("  - " + Limit(entry.Summary, 60) + "｜" + Limit(entry.Detail, 200));
                    }
                }
                return builder.ToString().TrimEnd();
            }

            private static string FormatDate(long unixMs)
            {
                if (unixMs <= 0) return "时间未知";
                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
                        .ToOffset(TimeSpan.FromHours(8))
                        .ToString("yyyy年MM月dd日");
                }
                catch
                {
                    return "时间未知";
                }
            }

            private static string Normalize(string value)
            {
                if (string.IsNullOrEmpty(value)) return string.Empty;
                var builder = new StringBuilder(value.Length);
                foreach (var ch in value)
                    if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
                return builder.ToString();
            }

            private static string Limit(string value, int max)
            {
                value = value ?? string.Empty;
                return value.Length <= max ? value : value.Substring(0, max);
            }
        }
    }
}
