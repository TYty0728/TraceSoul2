using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    /// <summary>有证据的显式意向。使用已有原子文档存储，与长期认知复盘及短期注意分别维护。</summary>
    public static class GoalMemoryLogic
    {
        public const int MaxActive = 32;
        private const string Owner = "kernel.goals";
        private static readonly ConditionalWeakTable<IMemoryStore, object> Gates = new();
        private static readonly JsonSerializerOptions Json = new()
        { IncludeFields = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public sealed class GoalRecord
        {
            public string Id, Kind, Content, AppliesWhen, Horizon, Origin, Status;
            public string SourceMomentId, EvidenceQuote, ReplacesId, ChangeSource;
            public long StartsUnixMs, ExpiresUnixMs, UpdatedUnixMs;
        }
        public sealed class GoalDocument
        {
            [JsonRequired] public int Version = 1;
            [JsonRequired] public List<GoalRecord> Items = new();
            [JsonRequired] public List<string> Applied = new();
        }

        private static GoalDocument Load(IMemoryStore store, string conversation)
        {
            var raw = store.LoadPluginDocument(Owner, conversation);
            if (string.IsNullOrEmpty(raw)) return new GoalDocument();
            var doc = JsonSerializer.Deserialize<GoalDocument>(raw, Json);
            // 损坏或未来版本不能静默当空表覆盖。
            if (doc == null || doc.Version != 1 || doc.Items == null || doc.Applied == null ||
                doc.Items.Any(x => x == null || !Text(x.Id, 80) || !Text(x.Content, 240) || !Text(x.SourceMomentId, 160)))
                throw new InvalidOperationException("目标记忆文档不可用，未覆盖原记录。");
            return doc;
        }

        public static List<GoalRecord> Read(IMemoryStore store, string conversation)
        {
            lock (Gates.GetValue(store, _ => new object())) return Load(store, conversation).Items;
        }

        private static bool Live(GoalRecord item, TraceTurnContext turn, long now) => item.Status == "active" &&
            (item.Horizon != "turn" || item.SourceMomentId == turn.Moment?.Id) &&
            (item.ExpiresUnixMs == 0 || item.ExpiresUnixMs > now);

        public static string BuildContext(TraceTurnContext turn, long? nowUnixMs = null)
        {
            var now = nowUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var items = Read(turn.Services.Storage, turn.ConversationId).Where(x => Live(x, turn, now)).ToList();
            return "\n【当下与未来：有效偏好和目标】\n这些是有来源、可修改的意向，不是已完成事实。先检查适用范围；未来目标未到时间不能提前当作当前任务。对方本轮的新要求优先于旧偏好；明确改口时修订或撤回。\n" +
                JsonSerializer.Serialize(items.Select(x => new { id = x.Id, kind = x.Kind, content = x.Content,
                    applies_when = x.AppliesWhen, horizon = x.Horizon, origin = x.Origin,
                    timing = x.StartsUnixMs > now ? "future" : "current",
                    starts_at = Iso(x.StartsUnixMs), expires_at = Iso(x.ExpiresUnixMs),
                    evidence_quote = x.EvidenceQuote, source_moment_id = x.SourceMomentId }), Json);
        }

        public static bool Valid(TraceTurnContext turn, List<AgentGoalUpdateData> updates)
        {
            if (updates == null || updates.Count == 0) return true;
            lock (Gates.GetValue(turn.Services.Storage, _ => new object()))
            {
                var doc = Load(turn.Services.Storage, turn.ConversationId);
                try { Reduce(doc, turn, updates); return true; }
                catch (ArgumentException) { return false; }
            }
        }

        public static void Apply(TraceTurnContext turn, List<AgentGoalUpdateData> updates)
        {
            if (updates == null || updates.Count == 0) return;
            lock (Gates.GetValue(turn.Services.Storage, _ => new object()))
            {
                var doc = Load(turn.Services.Storage, turn.ConversationId);
                Reduce(doc, turn, updates);
                // 整批先校验；任一操作失败都不会保存部分变更。
                turn.Services.Storage.SavePluginDocument(Owner, turn.ConversationId, JsonSerializer.Serialize(doc, Json));
            }
        }

        private static void Reduce(GoalDocument doc, TraceTurnContext turn, List<AgentGoalUpdateData> updates)
        {
            Check(updates.Count <= 4);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var expired in doc.Items.Where(x => x.Status == "active" && !Live(x, turn, now))) expired.Status = "expired";
            foreach (var change in updates)
            {
                Check(change != null && turn.Moment != null && !string.IsNullOrWhiteSpace(turn.Moment.Id));
                Check(change.operation is "create" or "revise" or "complete" or "cancel");
                Check((change.id?.Length ?? 0) <= 80 && (change.evidence_quote?.Length ?? 0) <= 240);
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(turn.Moment.Id + "\n" + JsonSerializer.Serialize(change, Json))));
                if (doc.Applied.Contains(key)) continue;
                var previous = string.IsNullOrEmpty(change.id) ? null : doc.Items.FirstOrDefault(x => x.Id == change.id && x.Status == "active");
                Check(change.operation == "create" ? string.IsNullOrEmpty(change.id) : previous != null);
                Check(change.source is "user" or "self" or "feedback");
                var quote = (change.evidence_quote ?? "").Trim();
                if (change.source == "user")
                    Check(turn.RequiresExpression && turn.Services.Storage.LoadPairIdentity().IsHumanMoment(turn.Moment.Role) &&
                        quote.Length >= 2 && (turn.Moment.Content ?? "").Contains(quote, StringComparison.Ordinal));
                else if (change.source == "feedback")
                    Check(change.operation == "complete" && quote.Length >= 2 && (turn.Workspace.Results.Any(x =>
                        x != null && x.Status == "success" &&
                        ((x.Summary ?? "").Contains(quote, StringComparison.Ordinal) || (x.Payload ?? "").Contains(quote, StringComparison.Ordinal))) ||
                        (turn.Moment.SourcePluginId == "runtime.execution" && turn.Services.Executions.List(turn.ConversationId).Any(x =>
                            x.ExecutionId == turn.Moment.SourceEventId && x.Status == "completed" &&
                            ((x.Summary ?? "").Contains(quote, StringComparison.Ordinal) || (x.ConfirmedContent ?? "").Contains(quote, StringComparison.Ordinal))))));
                else
                    Check(quote.Length == 0 && change.operation != "complete" &&
                        (previous == null || previous.Origin == "self"));

                if (change.operation is "create" or "revise")
                {
                    Check(change.kind is "preference" or "goal");
                    Check(previous == null || previous.Kind == change.kind);
                    Check(change.source == "user" || (change.source == "self" && change.kind == "goal"));
                    Check(Text(change.content, 240) && Text(change.applies_when, 120));
                    Check(change.horizon is "turn" or "ongoing" or "until");
                    var starts = ParseTime(change.starts_at);
                    var expires = ParseTime(change.expires_at);
                    Check(change.horizon == "until" ? expires > now && expires > starts : expires == 0);
                    Check(change.horizon != "turn" || starts == 0);
                    var content = change.content.Trim(); var scope = change.applies_when.Trim();
                    if (change.operation == "create" && doc.Items.Any(x => x.Status == "active" &&
                        x.Kind == change.kind && x.Content == content && x.AppliesWhen == scope &&
                        x.Horizon == change.horizon && x.Origin == change.source && x.StartsUnixMs == starts && x.ExpiresUnixMs == expires))
                    { doc.Applied.Add(key); continue; }
                    if (previous != null) previous.Status = "superseded";
                    doc.Items.Add(new GoalRecord { Id = Guid.NewGuid().ToString("N"), Kind = change.kind,
                        Content = content, AppliesWhen = scope, Horizon = change.horizon, Origin = change.source,
                        ChangeSource = change.source, Status = "active", StartsUnixMs = starts, ExpiresUnixMs = expires,
                        SourceMomentId = turn.Moment.Id, EvidenceQuote = quote, ReplacesId = previous?.Id, UpdatedUnixMs = now });
                }
                else
                {
                    Check(change.operation != "complete" || previous.Kind == "goal");
                    previous.Status = change.operation == "complete" ? "completed" : "cancelled";
                    // 终止也保留单独证据，原记录的提出依据不被覆盖。
                    doc.Items.Add(new GoalRecord { Id = Guid.NewGuid().ToString("N"), Kind = previous.Kind,
                        Content = previous.Content, Origin = previous.Origin, Status = previous.Status,
                        SourceMomentId = turn.Moment.Id, EvidenceQuote = quote, ChangeSource = change.source,
                        ReplacesId = previous.Id, UpdatedUnixMs = now });
                }
                doc.Applied.Add(key);
            }
            Check(doc.Items.Count(x => x.Status == "active") <= MaxActive);
            // 活跃目标绝不因历史预算被丢弃。保留最近 256 条历史版本；原文仍在 Moment。
            doc.Items = doc.Items.Where(x => x.Status == "active").Concat(doc.Items.Where(x => x.Status != "active")
                .OrderByDescending(x => x.UpdatedUnixMs).Take(256)).ToList();
            doc.Applied = doc.Applied.TakeLast(256).ToList();
        }

        private static bool Text(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
        private static void Check(bool valid) { if (!valid) throw new ArgumentException("目标变更的字段、依据、目标ID或容量不合法。"); }
        private static string Iso(long value) => value == 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(value).ToString("o");
        private static long ParseTime(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            Check(text.Length <= 40 && Regex.IsMatch(text, @"(?:Z|[+-]\d{2}:\d{2})$") &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        }
    }
}
