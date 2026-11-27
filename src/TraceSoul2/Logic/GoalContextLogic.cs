using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Plugins;
using GoalRecord = TraceSoul2.Logic.GoalMemoryLogic.GoalRecord;

namespace TraceSoul2.Logic;

/// <summary>偏好和目标的只读视图。候选筛选和篇幅预算都不写回记录。</summary>
internal static class GoalContextLogic
{
    private const int TurnBudget = 2200;
    private const int RecallPageSize = 6;
    private static readonly long PlanningWindow = (long)TimeSpan.FromDays(7).TotalMilliseconds;
    private static readonly HashSet<string> CommonTerms = new(StringComparer.Ordinal)
    {
        "什么", "怎么", "这个", "那个", "那就", "可以", "一下", "时候", "我们", "一起", "还是", "这次", "之前", "现在"
    };

    private static HashSet<string> ReadIds(TraceTurnContext turn) =>
        turn.Workspace.GetOrCreateState("kernel.goals.read", () => new HashSet<string>(StringComparer.Ordinal));

    public static List<string> ResolveTags(TraceTurnContext turn, IReadOnlyCollection<string> labels)
    {
        if (labels == null || labels.Count == 0 || EnvironmentLogic.IsPublic(turn)) return null;
        return turn.Services.Storage.GetActiveLifeTags()
            .Where(tag => labels.Contains(tag.Label, StringComparer.Ordinal)).GroupBy(tag => tag.Label, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).Select(group => group.Single().Id).Take(3).ToList();
    }

    public static string Build(TraceTurnContext turn, long now, string change = null)
    {
        var items = GoalMemoryLogic.Read(turn.Services.Storage, turn.ConversationId)
            .Where(item => GoalMemoryLogic.Live(item, turn, now)).ToList();
        if (items.Count == 0) return string.Empty;

        var current = Terms(turn.Moment?.Content);
        var requested = Terms(change);
        foreach (var observation in turn.Workspace.Observations)
            current.UnionWith(Terms(observation.Content));
        var recent = turn.RecentMoments.Where(x => x != null && x.Id != turn.Moment?.Id).TakeLast(3).Select(x => Terms(x.Content)).ToList();
        var attention = InnerLifeLogic.LiveAttention(SubjectRuntimeLogic.View(turn), now).Select(x => Terms(x.content)).ToList();
        var topics = MemoryRecallLogic.CurrentTopics(turn).Take(6).ToList();
        var alreadyRead = ReadIds(turn);

        var candidates = items.Select(item =>
        {
            var terms = Terms(item.Content + "\n" + item.AppliesWhen);
            var topical = topics.Any(tag => MatchesTopic(item, terms, tag));
            var direct = Math.Max(Match(current, terms), Match(requested, terms));
            var carried = recent.Concat(attention).Select(query => Match(query, terms)).DefaultIfEmpty().Max();
            var priority = item.SourceMomentId == turn.Moment?.Id || alreadyRead.Contains(item.Id) ? 4 :
                IsStanding(item) ? 3 : NearSchedule(item, now) ? 2 : 0;
            var signal = direct >= 0.15f || topical || carried >= 0.25f;
            return (Item: item, Priority: priority, Signal: signal,
                Score: direct + (topical ? 0.35f : 0) + carried * 0.45f);
        }).Where(x => x.Priority > 0 || x.Signal)
            .OrderByDescending(x => x.Priority).ThenByDescending(x => x.Score).ThenByDescending(x => x.Item.UpdatedUnixMs);

        var selected = new StringBuilder();
        var selectedCount = 0;
        foreach (var candidate in candidates)
        {
            var label = candidate.Priority switch
            {
                4 => "本轮已提及",
                3 => "相处边界",
                2 => "近期安排",
                _ => "可能相关"
            };
            var row = label + "｜" + Format(candidate.Item, now);
            if (selected.Length + row.Length + 1 > TurnBudget) continue;
            selected.Append(row).Append('\n');
            selectedCount++;
        }
        turn.Services.LogDiagnostic(turn.TraceId, "偏好与目标读取", detail: $"入选={selectedCount}｜有效={items.Count}｜字符={selected.Length}");
        return selected.Length == 0 ? "" : "\n【当下与未来：本轮读到的偏好和目标】\n" +
            "结合眼前意图、适用条件和日期理解。相对日期以记录时间为锚。需要旧约定或原话时用 memory.recall 的 goals 读取。\n" +
            selected;
    }

    public static TraceCapabilityResultData Recall(TraceTurnContext turn, MemoryReadScope scope,
        IReadOnlyCollection<string> labels, string query, int offset)
    {
        var now = MemoryReadScope.ReferenceTime(turn).ToUnixTimeMilliseconds();
        var records = GoalMemoryLogic.Read(turn.Services.Storage, turn.ConversationId);
        var byId = records.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var tags = labels.Count == 0 ? new List<LifeTagRecord>() : turn.Services.Storage.GetActiveLifeTags()
            .Where(tag => labels.Contains(tag.Label, StringComparer.Ordinal)).ToList();
        var queryTerms = Terms(query);
        var pool = records.Select(item =>
        {
            // Older completion and cancellation records contain only the original content and the outcome.
            var context = item.AppliesWhen == null && item.ReplacesId != null && byId.TryGetValue(item.ReplacesId, out var previous)
                ? previous : item;
            var terms = Terms(context.Content + "\n" + context.AppliesWhen);
            return (Item: item, Context: context, SearchTerms: terms, Score: Match(queryTerms, terms));
        }).Where(x => (!scope.HasDate || WithinScope(x.Context, scope)) &&
            (tags.Count == 0 || tags.Any(tag => MatchesTopic(x.Context, x.SearchTerms, tag)))).ToList();

        var exact = pool.Where(x => x.Item.Id == query).ToList();
        if (exact.Count > 0 || Guid.TryParseExact(query, "N", out _)) pool = exact;
        var precise = exact.Count > 0 || pool.Any(x => x.Score >= 0.12f);
        if (exact.Count == 0 && !scope.HasDate && tags.Count == 0 && precise)
            pool = pool.Where(x => x.Score >= 0.12f).ToList();

        var page = pool.OrderByDescending(x => x.Score).ThenByDescending(x => GoalMemoryLogic.Live(x.Item, turn, now))
            .ThenByDescending(x => x.Item.UpdatedUnixMs).ThenBy(x => x.Item.Id, StringComparer.Ordinal)
            .Skip(offset).Take(RecallPageSize).ToList();
        var header = "【偏好与目标的本次读取】\n" + scope.Description + "；主题：" +
            (labels.Count == 0 ? "未限定" : string.Join("、", labels)) + "\n" +
            "有明确日期按计划窗口读取，其余按记录日期。计划记录不证明已经发生。\n";
        if (page.Count == 0) return new() { Status = "empty", Summary = "本范围没有更多可读的偏好或目标。", Payload = header };
        var text = new StringBuilder(header);
        if (!precise) text.AppendLine("以下按指定范围浏览，尚无明确词面匹配。");
        var readIds = ReadIds(turn);
        var evidence = new List<string>();
        foreach (var record in page)
        {
            readIds.Add(record.Item.Id);
            var status = GoalMemoryLogic.Live(record.Item, turn, now) ? "有效" : record.Item.Status switch
            {
                "completed" => "已完成",
                "cancelled" => "已撤回",
                "superseded" => "已修订",
                _ => "已过期"
            };
            text.Append(status).Append("｜").Append(Format(record.Context, now, record.Item.Id))
                .Append("｜档案时间 ").Append(Time(record.Item.UpdatedUnixMs))
                .Append("｜").AppendLine(ToolExperienceLogic.SourceReference(turn, record.Item.SourceMomentId));
            evidence.Add("moment:" + record.Item.SourceMomentId);
        }
        if (offset + page.Count < pool.Count) text.Append("还有条目，保持范围与 query，传 offset=").Append(offset + page.Count).AppendLine(" 继续。");
        return new() { Status = "success", Summary = "已读取范围内的偏好与目标档案。", Payload = text.ToString(), EvidenceRefs = evidence.Distinct().ToList() };
    }

    private static bool IsStanding(GoalRecord item)
    {
        if (item.Kind != "preference") return false;
        if (item.Standing) return true;
        // Compatibility with common explicit scopes in records written before the standing flag.
        var scope = item.AppliesWhen?.Split(new[] { '，', '。', '；', ',', ';' }, 2)[0].Trim();
        return scope is "日常聊天" or "日常相处" or "日常交流" or "所有对话" or "任何对话" or "任何时候" or "每次交流";
    }

    private static bool NearSchedule(GoalRecord item, long now) =>
        (item.StartsUnixMs > 0 && item.StartsUnixMs >= DayStart(now) && item.StartsUnixMs - now <= PlanningWindow) ||
        (item.ExpiresUnixMs > now && item.ExpiresUnixMs - now <= PlanningWindow);

    private static long DayStart(long stamp)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(stamp).ToOffset(MemoryDayLogic.ChinaOffset);
        return new DateTimeOffset(local.Date, local.Offset).ToUnixTimeMilliseconds();
    }

    private static bool WithinScope(GoalRecord item, MemoryReadScope scope)
    {
        if (item.ExpiresUnixMs > 0)
        {
            var start = item.StartsUnixMs > 0 ? item.StartsUnixMs : item.UpdatedUnixMs;
            return start < scope.EndMs && item.ExpiresUnixMs > scope.StartMs;
        }
        return scope.Contains(item.StartsUnixMs > 0 ? item.StartsUnixMs : item.UpdatedUnixMs);
    }

    private static bool MatchesTopic(GoalRecord item, HashSet<string> terms, LifeTagRecord tag)
    {
        if (item.TagIds is { Count: > 0 }) return item.TagIds.Contains(tag.Id);
        return Match(Terms(tag.Label), terms) >= 0.5f || Match(Terms(tag.Definition), terms) >= 0.35f;
    }

    private static HashSet<string> Terms(string text)
    {
        var terms = CognitionContextRecallSource.Terms(text);
        terms.ExceptWith(CommonTerms);
        return terms;
    }

    private static float Match(HashSet<string> query, HashSet<string> text) =>
        query.Count == 0 ? 0 : (float)query.Count(text.Contains) / query.Count;

    private static string Format(GoalRecord item, long now, string id = null)
    {
        var text = new StringBuilder(id ?? item.Id).Append("｜")
            .Append(item.Origin == "self" ? "自己的" : "对方的")
            .Append(item.Kind == "goal" ? "目标" : "偏好").Append("｜").Append(item.Content);
        if (!string.IsNullOrWhiteSpace(item.AppliesWhen)) text.Append("｜适用 ").Append(item.AppliesWhen);
        if (item.UpdatedUnixMs > 0) text.Append("｜记录于 ").Append(Time(item.UpdatedUnixMs));
        if (item.StartsUnixMs > 0) text.Append(item.StartsUnixMs > now ? "｜未开始，起于 " : "｜开始 ").Append(Time(item.StartsUnixMs));
        if (item.ExpiresUnixMs > 0) text.Append("｜截止 ").Append(Time(item.ExpiresUnixMs));
        return text.ToString();
    }

    private static string Time(long stamp) => stamp <= 0 ? "未知" :
        DateTimeOffset.FromUnixTimeMilliseconds(stamp).ToOffset(MemoryDayLogic.ChinaOffset).ToString("yyyy-MM-dd HH:mm zzz");
}
