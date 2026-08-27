using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Prompts;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 进入空闲时抽一件生活事：发说说、看说说、改心情等。
    /// 系统按均匀随机抽，模型不选活动；每日每项有次数上限，满了就出池。
    /// 「歇着」始终在池里，所以也可以什么都不做。
    /// </summary>
    public static class IdleDeedLogic
    {
        public const string PluginId = "idle.deed";
        public const string DocumentKey = "days";
        public const string RestId = "rest";
        public const string IdleArgumentName = "idle";
        public const string SeedArgumentName = "seed";
        private const int KeepDays = 14;
        private static readonly Random Dice = new Random();

        public static List<string> BuildPool(
            IEnumerable<TraceContributionDescriptorData> catalog,
            IMemoryStore store,
            DateTimeOffset now)
        {
            var pool = new List<string> { RestId };
            if (store == null) return pool;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RestId };
            foreach (var item in catalog ?? Enumerable.Empty<TraceContributionDescriptorData>())
            {
                if (item == null || item.IdleDailyCap <= 0) continue;
                var id = (item.Id ?? string.Empty).Trim();
                if (id.Length == 0 || !seen.Add(id)) continue;
                if (Count(store, id, now) >= item.IdleDailyCap) continue;
                pool.Add(id);
            }
            return pool;
        }

        public static string Pick(IReadOnlyList<string> pool, Random random = null)
        {
            if (pool == null || pool.Count == 0) return RestId;
            var dice = random ?? Dice;
            return PickAt(pool, dice.Next(pool.Count));
        }

        public static string PickAt(IReadOnlyList<string> pool, int index)
        {
            if (pool == null || pool.Count == 0) return RestId;
            var count = pool.Count;
            var i = index % count;
            if (i < 0) i += count;
            return pool[i];
        }

        public static int Count(IMemoryStore store, string capabilityId, DateTimeOffset now)
        {
            var id = (capabilityId ?? string.Empty).Trim();
            if (store == null || id.Length == 0) return 0;
            var day = LoadDay(store, MemoryDayLogic.CurrentDayKey(now));
            if (day == null || day.counts == null) return 0;
            var row = day.counts.FirstOrDefault(x => x != null &&
                string.Equals(x.id, id, StringComparison.OrdinalIgnoreCase));
            return row == null ? 0 : Math.Max(0, row.n);
        }

        public static void Remember(IMemoryStore store, string capabilityId, DateTimeOffset now)
        {
            if (store == null) return;
            var id = (capabilityId ?? string.Empty).Trim();
            if (id.Length == 0 || string.Equals(id, RestId, StringComparison.OrdinalIgnoreCase))
                return;
            var dayKey = MemoryDayLogic.CurrentDayKey(now);
            var log = LoadLog(store);
            log.days = log.days ?? new List<IdleDeedDayData>();
            var day = log.days.FirstOrDefault(x => x != null &&
                string.Equals(x.day, dayKey, StringComparison.Ordinal));
            if (day == null)
            {
                day = new IdleDeedDayData { day = dayKey };
                log.days.Add(day);
            }
            day.counts = day.counts ?? new List<IdleDeedCountData>();
            var row = day.counts.FirstOrDefault(x => x != null &&
                string.Equals(x.id, id, StringComparison.OrdinalIgnoreCase));
            if (row == null)
            {
                row = new IdleDeedCountData { id = id, n = 0 };
                day.counts.Add(row);
            }
            row.n = Math.Max(0, row.n) + 1;
            Prune(log, dayKey);
            store.SavePluginDocument(PluginId, DocumentKey, TraceJson.ToJson(log));
        }

        public static string FormatSeed(TraceTurnContext turn, DateTimeOffset now)
        {
            var builder = new StringBuilder();
            builder.Append(CorePrompts.IdleDeed.TimePrefix)
                .AppendLine(TimeLanguageUtil.NaturalNow(now.ToOffset(MemoryDayLogic.ChinaOffset)));
            if (turn == null || turn.Services == null || turn.Services.Storage == null)
                return builder.ToString().TrimEnd();

            var runtime = turn.Services.Storage.LoadOrCreateInnerRuntime(turn.ConversationId);
            var mood = runtime == null ? string.Empty : (runtime.Mood ?? string.Empty).Trim();
            var inner = runtime == null ? string.Empty : OneLine(runtime.Narrative, 200);
            builder.Append(CorePrompts.IdleDeed.MoodPrefix)
                .AppendLine(mood.Length == 0 ? CorePrompts.IdleDeed.Empty : mood);
            builder.Append(CorePrompts.IdleDeed.InnerPrefix)
                .AppendLine(inner.Length == 0 ? CorePrompts.IdleDeed.Empty : inner);

            var doing = string.Empty;
            if (turn.Services.LifeState != null)
            {
                var life = turn.Services.LifeState.Load(turn.ConversationId);
                doing = LifeStateLogic.FormatDoing(life);
            }
            if (doing.Length == 0 && runtime != null)
                doing = OneLine(runtime.OngoingActivity, 80);
            builder.Append(CorePrompts.IdleDeed.DoingPrefix)
                .AppendLine(doing.Length == 0 ? CorePrompts.IdleDeed.Empty : doing);

            var trajectory = turn.Services.Storage.LoadDayTrajectory(MemoryDayLogic.CurrentDayKey(now));
            var today = trajectory == null ? string.Empty : OneLine(trajectory.Text, 400);
            if (today.Length > 0)
                builder.Append(CorePrompts.IdleDeed.TodayPrefix).AppendLine(today);
            return builder.ToString().TrimEnd();
        }

        public static bool CountsAsDone(TraceCapabilityResultData result)
        {
            if (result == null) return false;
            return string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<IdleDeedOutcome> RunAsync(
            TraceTurnContext turn,
            IEnumerable<TraceContributionDescriptorData> catalog,
            Func<string, List<BrainCallArgumentData>, CancellationToken, Task<TraceCapabilityResultData>> execute,
            CancellationToken cancellationToken,
            Random random = null,
            int? pickIndex = null)
        {
            var now = DateTimeOffset.Now;
            var store = turn == null || turn.Services == null ? null : turn.Services.Storage;
            var pool = BuildPool(catalog, store, now);
            var id = pickIndex.HasValue ? PickAt(pool, pickIndex.Value) : Pick(pool, random);
            if (string.Equals(id, RestId, StringComparison.OrdinalIgnoreCase))
            {
                return new IdleDeedOutcome
                {
                    Id = RestId,
                    Rested = true,
                    Summary = "空闲生活：歇着"
                };
            }

            if (execute == null)
            {
                return new IdleDeedOutcome
                {
                    Id = id,
                    Summary = "空闲生活：抽到 " + id + "，但无法执行"
                };
            }

            var args = new List<BrainCallArgumentData>
            {
                new BrainCallArgumentData { name = IdleArgumentName, value = "true" },
                new BrainCallArgumentData { name = SeedArgumentName, value = FormatSeed(turn, now) }
            };
            TraceCapabilityResultData result;
            try
            {
                result = await execute(id, args, cancellationToken);
            }
            catch (Exception exception)
            {
                return new IdleDeedOutcome
                {
                    Id = id,
                    Summary = "空闲生活：" + id + " 失败：" + exception.Message
                };
            }

            if (CountsAsDone(result))
            {
                Remember(store, id, now);
                return new IdleDeedOutcome
                {
                    Id = id,
                    Counted = true,
                    Summary = "空闲生活：" + (string.IsNullOrWhiteSpace(result.Summary) ? id : result.Summary),
                    Payload = result.Payload
                };
            }

            return new IdleDeedOutcome
            {
                Id = id,
                Summary = "空闲生活：" + id + "｜" +
                          (result == null ? "没有结果" : (result.Status + " " + result.Summary).Trim())
            };
        }

        private static IdleDeedLogData LoadLog(IMemoryStore store)
        {
            var json = store.LoadPluginDocument(PluginId, DocumentKey);
            if (string.IsNullOrWhiteSpace(json)) return new IdleDeedLogData();
            try
            {
                return TraceJson.FromJson<IdleDeedLogData>(json) ?? new IdleDeedLogData();
            }
            catch
            {
                return new IdleDeedLogData();
            }
        }

        private static IdleDeedDayData LoadDay(IMemoryStore store, string dayKey)
        {
            var log = LoadLog(store);
            return (log.days ?? new List<IdleDeedDayData>())
                .FirstOrDefault(x => x != null &&
                                     string.Equals(x.day, dayKey, StringComparison.Ordinal));
        }

        private static void Prune(IdleDeedLogData log, string currentDay)
        {
            DateTimeOffset current;
            if (!MemoryDayLogic.TryStartOf(currentDay, out current) || log.days == null) return;
            var keepFrom = current.AddDays(1 - KeepDays);
            log.days = log.days
                .Where(x => x != null && InWindow(x.day, keepFrom))
                .ToList();
        }

        private static bool InWindow(string dayKey, DateTimeOffset keepFrom)
        {
            DateTimeOffset start;
            if (!MemoryDayLogic.TryStartOf(dayKey, out start)) return false;
            return start >= keepFrom;
        }

        private static string OneLine(string value, int max)
        {
            value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (value.Length <= max) return value;
            return value.Substring(0, max).TrimEnd();
        }
    }

    public sealed class IdleDeedOutcome
    {
        public string Id;
        public bool Rested;
        public bool Counted;
        public string Summary;
        /// <summary>做这件事带回的实在内容（读到的说说正文等），供调用方写入今日新识。</summary>
        public string Payload;
    }

    [Serializable]
    public sealed class IdleDeedLogData
    {
        public List<IdleDeedDayData> days = new List<IdleDeedDayData>();
    }

    [Serializable]
    public sealed class IdleDeedDayData
    {
        public string day;
        public List<IdleDeedCountData> counts = new List<IdleDeedCountData>();
    }

    [Serializable]
    public sealed class IdleDeedCountData
    {
        public string id;
        public int n;
    }
}
