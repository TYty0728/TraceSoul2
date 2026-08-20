using System;
using System.Collections.Generic;
using System.Globalization;
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
    public sealed class TimeSchedulerPlugin : ITracePlugin
    {
        private const string PluginId = "builtin.time";
        private SchedulerState state;

        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = PluginId,
            DisplayName = "时间与调度",
            Version = "1.1.0",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Kernel,
            Description = "提供当前时间、今天两人的轨迹、未来计划；到期只叫醒中枢该跑的那一套循环，不直接改短卡或开口。"
        };

        public void Register(TracePluginContext context)
        {
            state = new SchedulerState(context.Services.Storage);
            context.AddMountedFacet(new TimeContextFacet(state));
            context.AddMountedFacet(new DayTrajectoryFacet());
            context.AddCallable(new TimeNowNerve());
            context.AddCallable(new CreateScheduleNerve(state));
            context.AddCallable(new ListScheduleNerve(state));
            context.AddCallable(new CancelScheduleNerve(state));
            context.AddCallable(new ContinueScheduleNerve(state));
            context.AddCallable(new ClearContinuationNerve(state));
            context.AddBackgroundService(new SchedulerService(state));
        }

        public void Shutdown() { if (state != null) state.Save(); }

        [Serializable]
        private sealed class ScheduleEntry
        {
            public string id;
            public string conversation_id;
            public string content;
            public long due_unix_ms;
                    public string recurrence;
                    public string wake;
                    public bool enabled;
        }

        [Serializable]
        private sealed class ScheduleDocument
        {
            public List<ScheduleEntry> items = new List<ScheduleEntry>();
        }

        private sealed class SchedulerState
        {
            private readonly IMemoryStore storage;
            private readonly object gate = new object();
            private ScheduleDocument document;

            public SchedulerState(IMemoryStore storage)
            {
                this.storage = storage;
                var json = storage.LoadPluginDocument(PluginId, "schedules");
            try { document = string.IsNullOrWhiteSpace(json) ? null : TraceJson.FromJson<ScheduleDocument>(json); }
                catch { document = null; }
                if (document == null) document = new ScheduleDocument();
                if (document.items == null) document.items = new List<ScheduleEntry>();
            }

            public ScheduleEntry Add(string conversationId, string content, long due, string recurrence, string wake = null)
            {
                lock (gate)
                {
                    var item = new ScheduleEntry
                    {
                        id = Guid.NewGuid().ToString("N"),
                        conversation_id = conversationId,
                        content = Limit(content.Trim(), 500),
                        due_unix_ms = due,
                        recurrence = NormalizeRecurrence(recurrence),
                        wake = ResolveWake(content, wake),
                        enabled = true
                    };
                    document.items.Add(item);
                    SaveUnsafe();
                    return item;
                }
            }

            public static string ResolveWake(string content, string wake)
            {
                var normalized = KernelWakeValues.Normalize(wake);
                if (normalized == KernelWakeValues.Mind || normalized == KernelWakeValues.Subconscious)
                    return normalized;
                return KernelWakeValues.InferFromContent(content);
            }

            public const string DailyReviewContent = "每日复盘";

            public void EnsureDailyReview(string conversationId)
            {
                if (string.IsNullOrWhiteSpace(conversationId)) return;
                lock (gate)
                {
                    if (document.items.Any(x => x.enabled &&
                                                x.conversation_id == conversationId &&
                                                x.content == DailyReviewContent &&
                                                x.recurrence == "daily"))
                        return;
                }
                var now = DateTimeOffset.Now;
                var next = new DateTimeOffset(now.Year, now.Month, now.Day, 4, 0, 0, now.Offset);
                if (next <= now) next = next.AddDays(1);
                Add(conversationId, DailyReviewContent, next.ToUnixTimeMilliseconds(), "daily",
                    KernelWakeValues.Subconscious);
            }

            public List<ScheduleEntry> List()
            {
                lock (gate) return document.items.Where(x => x.enabled)
                    .OrderBy(x => x.due_unix_ms).Select(Clone).ToList();
            }

            public ScheduleEntry EnsureContinuation(string conversationId, string content, long dueUnixMs)
            {
                lock (gate)
                {
                    foreach (var item in document.items)
                    {
                        if (item.enabled &&
                            string.Equals(item.conversation_id, conversationId, StringComparison.Ordinal) &&
                            InnerLifeLogic.IsContinuationContent(item.content))
                            item.enabled = false;
                    }
                    var text = (content ?? string.Empty).Trim();
                    if (!text.StartsWith(InnerLifeLogic.ContinuationPrefix, StringComparison.Ordinal))
                        text = InnerLifeLogic.ContinuationPrefix + text;
                    var itemNew = new ScheduleEntry
                    {
                        id = Guid.NewGuid().ToString("N"),
                        conversation_id = conversationId,
                        content = Limit(text, 500),
                        due_unix_ms = dueUnixMs,
                        recurrence = "none",
                        wake = KernelWakeValues.Mind,
                        enabled = true
                    };
                    document.items.Add(itemNew);
                    SaveUnsafe();
                    return itemNew;
                }
            }

            public int ClearContinuation(string conversationId)
            {
                lock (gate)
                {
                    var n = 0;
                    foreach (var item in document.items)
                    {
                        if (!item.enabled) continue;
                        if (!string.Equals(item.conversation_id, conversationId, StringComparison.Ordinal))
                            continue;
                        if (!InnerLifeLogic.IsContinuationContent(item.content)) continue;
                        item.enabled = false;
                        n++;
                    }
                    if (n > 0) SaveUnsafe();
                    return n;
                }
            }

            public bool Cancel(string id)
            {
                lock (gate)
                {
                    var item = document.items.FirstOrDefault(x => x.id == id && x.enabled);
                    if (item == null) return false;
                    item.enabled = false;
                    SaveUnsafe();
                    return true;
                }
            }

            public List<PluginEventData> Poll(long now)
            {
                lock (gate)
                {
                    var due = document.items.Where(x => x.enabled && x.due_unix_ms <= now)
                        .OrderBy(x => x.due_unix_ms).Take(20).ToList();
                    var events = new List<PluginEventData>();
                    foreach (var item in due)
                    {
                        var wake = ResolveWake(item.content, item.wake);
                        events.Add(new PluginEventData
                        {
                            PluginId = PluginId,
                            ConversationId = item.conversation_id,
                            ExternalEventId = "schedule:" + item.id + ":" + item.due_unix_ms,
                            Role = "system_event",
                            Content = "时间任务到期：" + item.content,
                            Realm = TraceRealmValues.Meta,
                            EvidenceType = EvidenceTypeValues.PluginObserved,
                            Wake = wake,
                            PayloadJson = "{\"schedule_id\":\"" + item.id +
                                          "\",\"recurrence\":\"" + item.recurrence +
                                          "\",\"wake\":\"" + wake + "\"}",
                            OccurredUnixMs = now
                        });
                        var interval = item.recurrence == "daily" ? TimeSpan.FromDays(1).TotalMilliseconds
                            : item.recurrence == "weekly" ? TimeSpan.FromDays(7).TotalMilliseconds : 0;
                        if (interval <= 0) item.enabled = false;
                        else
                        {
                            do { item.due_unix_ms = checked(item.due_unix_ms + (long)interval); }
                            while (item.due_unix_ms <= now);
                        }
                    }
                    if (due.Count > 0) SaveUnsafe();
                    return events;
                }
            }

            public void Save()
            {
                lock (gate) SaveUnsafe();
            }

            private void SaveUnsafe()
            {
            storage.SavePluginDocument(PluginId, "schedules", TraceJson.ToJson(document));
            }

            private static ScheduleEntry Clone(ScheduleEntry value)
            {
                return new ScheduleEntry
                {
                    id = value.id,
                    conversation_id = value.conversation_id,
                    content = value.content,
                    due_unix_ms = value.due_unix_ms,
                    recurrence = value.recurrence,
                    wake = value.wake,
                    enabled = value.enabled
                };
            }
        }

        private sealed class TimeContextFacet : ITraceMountedFacet
        {
            private readonly SchedulerState state;
            public TimeContextFacet(SchedulerState state) { this.state = state; }

            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "time.context",
                Kind = TraceContributionKindValues.MountedFacet,
                DisplayName = "当前时间感",
                Description = "每个 Brain Step 刷新的本地时间与时区。",
                Provides = "time.current_context",
                RefreshMode = TraceFacetRefreshValues.EveryBrainStep,
                Priority = 70,
                MaxContextChars = 200
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceContextBlockData> BuildContextAsync(TraceTurnContext context, CancellationToken token)
            {
                if (state != null && context != null)
                    state.EnsureDailyReview(context.ConversationId);
                var now = DateTimeOffset.Now;
                return Task.FromResult(new TraceContextBlockData
                {
                    Title = "当前时间",
                    Content = "现在是 " + TimeLanguageUtil.NaturalNow(now) + "。"
                });
            }

            public Task<TraceCapabilityResultData> ApplyOutputAsync(
                BrainFacetOutputData output, TraceTurnContext context, CancellationToken token)
            {
                return Task.FromResult<TraceCapabilityResultData>(null);
            }
        }

        /// <summary>
        /// 今天我们的轨迹：当天共同经历的滚动摘要（约200字内），
        /// 实时对话中由 Brain 用 facet_outputs 写回；新的一天（04:00 边界）自动清空。
        /// </summary>
        private sealed class DayTrajectoryFacet : ITraceMountedFacet
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "day.trajectory",
                Kind = TraceContributionKindValues.MountedFacet,
                DisplayName = "今天我们的轨迹",
                Description = "今天两人一起经历的滚动摘要（约200字内），实时维护；新的一天自动清空。",
                Provides = "day.current_trajectory",
                OutputJsonSchema = "{changed:boolean,summary:string,fields:[trajectory]}",
                RefreshMode = TraceFacetRefreshValues.OncePerTurn,
                Priority = 80,
                MaxContextChars = 320,
                HasInternalMutation = true
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceContextBlockData> BuildContextAsync(
                TraceTurnContext context, CancellationToken cancellationToken)
            {
                var dayKey = MemoryDayKey(DateTimeOffset.Now);
                var record = context.Services.Storage.LoadDayTrajectory(dayKey);
                if (record == null || string.IsNullOrWhiteSpace(record.Text))
                    return Task.FromResult<TraceContextBlockData>(null);
                return Task.FromResult(new TraceContextBlockData
                {
                    Title = "今天我们的轨迹",
                    Content = "今天我们的轨迹：" + record.Text.Trim()
                });
            }

            public Task<TraceCapabilityResultData> ApplyOutputAsync(
                BrainFacetOutputData output,
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                if (output == null || !output.changed)
                    return Task.FromResult<TraceCapabilityResultData>(null);
                var text = LimitSentence(output.GetField("trajectory", output.summary), 200).Trim();
                if (text.Length == 0)
                    return Task.FromResult<TraceCapabilityResultData>(null);
                var dayKey = MemoryDayKey(DateTimeOffset.Now);
                context.Services.Storage.SaveDayTrajectory(dayKey, text);
                return Task.FromResult(new TraceCapabilityResultData
                {
                    Status = "success",
                    Summary = "今天的轨迹已更新（" + text.Length + " 字）。",
                    Payload = "今天我们的轨迹：" + text,
                    EvidenceRefs = new List<string> { "moment:" + context.Moment.Id }
                });
            }

            /// <summary>记忆日键：04:00 边界，04:00 前归前一天。</summary>
            internal static string MemoryDayKey(DateTimeOffset now)
            {
                return now.AddHours(-4).ToString("yyyy-MM-dd");
            }

            private static string LimitSentence(string value, int max)
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
        }

        private sealed class TimeNowNerve : ITraceCallableContribution
        {
            public TraceContributionDescriptorData Descriptor { get; } = DescriptorFor(
                "time.now", "读取精确时间", "time.now", "读取当前本地时间、UTC 和 Unix 毫秒。", "{reason:string}");
            public bool IsAvailable(TraceTurnContext context) { return context != null; }
            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken token)
            {
                var now = DateTimeOffset.Now;
                return Task.FromResult(Success("已读取精确时间。",
                    "现在：" + TimeLanguageUtil.NaturalNow(now) + "。\n" +
                    "精确：local=" + now.ToString("O") + "\nutc=" + now.UtcDateTime.ToString("O") +
                    "\nunix_ms=" + now.ToUnixTimeMilliseconds()));
            }
        }

        private sealed class CreateScheduleNerve : ITraceCallableContribution
        {
            private readonly SchedulerState state;
            public CreateScheduleNerve(SchedulerState state) { this.state = state; }
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "time.schedule",
                Kind = TraceContributionKindValues.CallableNerve,
                DisplayName = "建立时间任务",
                Description = "保存一次性、每日或每周时间任务；到期后后台服务只产生新 Moment。",
                Provides = "time.schedule.create",
                WhenToUse = "{username} 要求未来提醒、我决定安排复盘，或未来计划需要在某时重新进入意识时。",
                WhenNotToUse = "当前立即执行的动作。",
                ParametersJsonSchema = "{content:string,due_iso?:ISO8601,due_unix_ms?:long,recurrence:none|daily|weekly,wake?:mind|subconscious}",
                HasInternalMutation = true
            };
            public bool IsAvailable(TraceTurnContext context) { return context != null; }
            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken token)
            {
                var content = call.GetArgument("content").Trim();
                if (content.Length == 0) throw new InvalidOperationException("时间任务内容不能为空。");
                long due;
                if (!long.TryParse(call.GetArgument("due_unix_ms"), out due))
                {
                    DateTimeOffset parsed;
                    if (!DateTimeOffset.TryParse(call.GetArgument("due_iso"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AllowWhiteSpaces, out parsed))
                        throw new InvalidOperationException("需要有效的 due_iso 或 due_unix_ms。");
                    due = parsed.ToUnixTimeMilliseconds();
                }
                if (due <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    throw new InvalidOperationException("到期时间必须在未来。");
                var item = state.Add(context.ConversationId, content, due,
                    call.GetArgument("recurrence", "none"), call.GetArgument("wake"));
                return Task.FromResult(Success("时间任务已建立。",
                    item.id + " | " + DateTimeOffset.FromUnixTimeMilliseconds(item.due_unix_ms).ToLocalTime().ToString("O") +
                    " | " + item.recurrence + " | " + item.wake + " | " + item.content));
            }
        }

        private sealed class ListScheduleNerve : ITraceCallableContribution
        {
            private readonly SchedulerState state;
            public ListScheduleNerve(SchedulerState state) { this.state = state; }
            public TraceContributionDescriptorData Descriptor { get; } = DescriptorFor(
                "time.list", "查看时间任务", "time.schedule.read", "读取当前仍有效的未来任务。", "{reason:string}");
            public bool IsAvailable(TraceTurnContext context) { return context != null; }
            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken token)
            {
                var items = state.List();
                var payload = string.Join("\n", items.Select(x => x.id + " | " +
                    DateTimeOffset.FromUnixTimeMilliseconds(x.due_unix_ms).ToLocalTime().ToString("O") +
                    " | " + x.recurrence + " | " + (x.wake ?? string.Empty) + " | " + x.content));
                return Task.FromResult(Success("当前有 " + items.Count + " 个时间任务。", payload));
            }
        }

        private sealed class CancelScheduleNerve : ITraceCallableContribution
        {
            private readonly SchedulerState state;
            public CancelScheduleNerve(SchedulerState state) { this.state = state; }
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "time.cancel",
                Kind = TraceContributionKindValues.CallableNerve,
                DisplayName = "取消时间任务",
                Description = "取消指定的未来任务。",
                Provides = "time.schedule.cancel",
                ParametersJsonSchema = "{schedule_id:string}",
                HasInternalMutation = true
            };
            public bool IsAvailable(TraceTurnContext context) { return context != null; }
            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken token)
            {
                var id = call.GetArgument("schedule_id").Trim();
                var removed = state.Cancel(id);
                return Task.FromResult(new TraceCapabilityResultData
                {
                    Status = removed ? "success" : "failed",
                    Summary = removed ? "时间任务已取消。" : "没有找到有效的时间任务。",
                    Payload = id
                });
            }
        }

        private sealed class ContinueScheduleNerve : ITraceCallableContribution
        {
            private readonly SchedulerState state;
            public ContinueScheduleNerve(SchedulerState state) { this.state = state; }
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "time.continue",
                Kind = TraceContributionKindValues.CallableNerve,
                DisplayName = "记下未完成自己叫醒",
                Description = "未完成意图或答应过的事：在她没说话时到期叫醒心智，不要演成她在说话。",
                Provides = "time.schedule.continue",
                WhenToUse = "手上还有未完成的事、答应过回头再做时，让时间在安静之后叫醒我。",
                WhenNotToUse = "她正在说话，或这件事已经放下。",
                ParametersJsonSchema = "{content:string,due_iso?:ISO8601,due_unix_ms?:long}",
                HasInternalMutation = true
            };
            public bool IsAvailable(TraceTurnContext context) { return context != null; }
            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken token)
            {
                var content = call.GetArgument("content").Trim();
                if (content.Length == 0) throw new InvalidOperationException("续上内容不能为空。");
                long due;
                if (!long.TryParse(call.GetArgument("due_unix_ms"), out due) || due <= 0)
                {
                    DateTimeOffset parsed;
                    if (DateTimeOffset.TryParse(call.GetArgument("due_iso"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AllowWhiteSpaces, out parsed))
                        due = parsed.ToUnixTimeMilliseconds();
                    else
                        due = InnerLifeLogic.InferContinuationDueUnixMs(content, DateTimeOffset.Now);
                }
                if (due <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    due = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000;
                var item = state.EnsureContinuation(context.ConversationId, content, due);
                return Task.FromResult(Success("未完成已交给时间叫醒心智。",
                    item.id + " | " + DateTimeOffset.FromUnixTimeMilliseconds(item.due_unix_ms).ToLocalTime().ToString("O") +
                    " | " + item.wake + " | " + item.content));
            }
        }

        private sealed class ClearContinuationNerve : ITraceCallableContribution
        {
            private readonly SchedulerState state;
            public ClearContinuationNerve(SchedulerState state) { this.state = state; }
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "time.continue.clear",
                Kind = TraceContributionKindValues.CallableNerve,
                DisplayName = "放下未完成叫醒",
                Description = "手上的事已经放下时，取消自己叫醒心智的续上任务。",
                Provides = "time.schedule.continue.clear",
                WhenToUse = "未完成已经做完或明确放下。",
                WhenNotToUse = "手上还有事。",
                ParametersJsonSchema = "{reason?:string}",
                HasInternalMutation = true
            };
            public bool IsAvailable(TraceTurnContext context) { return context != null; }
            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken token)
            {
                var n = state.ClearContinuation(context.ConversationId);
                return Task.FromResult(new TraceCapabilityResultData
                {
                    Status = "success",
                    Summary = n > 0 ? "已放下 " + n + " 个续上叫醒。" : "没有待续上的叫醒。",
                    Payload = n.ToString()
                });
            }
        }

        private sealed class SchedulerService : ITraceBackgroundService
        {
            private readonly SchedulerState state;
            public SchedulerService(SchedulerState state) { this.state = state; }
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "time.scheduler.service",
                Kind = TraceContributionKindValues.BackgroundService,
                DisplayName = "时间到期监听",
                Description = "启用期间检查到期任务，并把它们转换成 system_event Moment。",
                Provides = "moment.time.due"
            };
            public bool IsAvailable { get { return true; } }
            public IEnumerable<PluginEventData> Poll(long nowUnixMs) { return state.Poll(nowUnixMs); }
            public void Shutdown() { state.Save(); }
        }

        private static TraceContributionDescriptorData DescriptorFor(
            string id, string name, string provides, string description, string schema)
        {
            return new TraceContributionDescriptorData
            {
                Id = id,
                Kind = TraceContributionKindValues.CallableNerve,
                DisplayName = name,
                Description = description,
                Provides = provides,
                ParametersJsonSchema = schema
            };
        }

        private static TraceCapabilityResultData Success(string summary, string payload)
        {
            return new TraceCapabilityResultData
            {
                Status = "success",
                Summary = summary,
                Payload = payload ?? string.Empty
            };
        }

        private static string NormalizeRecurrence(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return value == "daily" || value == "weekly" ? value : "none";
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
