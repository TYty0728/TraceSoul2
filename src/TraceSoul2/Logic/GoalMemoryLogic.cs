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
            if (items.Count == 0) return string.Empty;
            var rows = items.Select(x =>
            {
                var row = new Dictionary<string, object>
                {
                    ["id"] = x.Id, ["kind"] = x.Kind, ["content"] = x.Content,
                    ["applies_when"] = x.AppliesWhen, ["horizon"] = x.Horizon, ["origin"] = x.Origin
                };
                if (x.StartsUnixMs > 0) { row["starts_at"] = Iso(x.StartsUnixMs); row["timing"] = x.StartsUnixMs > now ? "future" : "current"; }
                if (x.ExpiresUnixMs > 0) row["expires_at"] = Iso(x.ExpiresUnixMs);
                if (!string.IsNullOrWhiteSpace(x.EvidenceQuote)) row["evidence_quote"] = x.EvidenceQuote;
                return JsonSerializer.Serialize(row, Json);
            });
            return "\n【当下与未来：有效偏好和目标】\n按适用范围执行；future 尚未开始。新要求优先，改口时修订或撤回；记录的意向不证明已完成。\n" +
                string.Join("\n", rows) + "\n";

        }

        public static bool Valid(TraceTurnContext turn, List<AgentGoalUpdateData> updates) => ValidationError(turn, updates) == null;

        public static string ValidationError(TraceTurnContext turn, List<AgentGoalUpdateData> updates)
        {
            if (updates == null || updates.Count == 0) return null;
            lock (Gates.GetValue(turn.Services.Storage, _ => new object()))
            {
                var doc = Load(turn.Services.Storage, turn.ConversationId);
                try { Reduce(doc, turn, updates); return null; }
                catch (ArgumentException error) { return error.Message; }
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
            Check(updates.Count <= 4, "$.goal_updates 最多4项。");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var expired in doc.Items.Where(x => x.Status == "active" && !Live(x, turn, now))) expired.Status = "expired";
            for (var index = 0; index < updates.Count; index++)
            {
                var change = updates[index]; var path = "$.goal_updates[" + index + "]";
                Check(change != null, path + " 必须是对象，不能为 null。");
                Check(turn.Moment != null && !string.IsNullOrWhiteSpace(turn.Moment.Id), path + " 缺少当前事件依据，不能提交变更。");
                Check(change.operation is "create" or "revise" or "complete" or "cancel", path + ".operation 必须为 create/revise/complete/cancel。");
                Check((change.id?.Length ?? 0) <= 80, path + ".id 最多80字符。");
                Check((change.evidence_quote?.Length ?? 0) <= 240, path + ".evidence_quote 最多240字符。");
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(turn.Moment.Id + "\n" + JsonSerializer.Serialize(change, Json))));
                if (doc.Applied.Contains(key)) continue;
                var previous = string.IsNullOrEmpty(change.id) ? null : doc.Items.FirstOrDefault(x => x.Id == change.id && x.Status == "active");
                Check(change.operation == "create" ? string.IsNullOrEmpty(change.id) : previous != null,
                    path + ".id：create 时省略；其他操作必须引用当前有效目标目录中的 id。");
                Check(change.source is "user" or "self" or "feedback", path + ".source 必须为 user/self/feedback。");
                var quote = (change.evidence_quote ?? "").Trim();
                if (change.source == "user")
                    Check(turn.RequiresExpression && turn.Services.Storage.LoadPairIdentity().IsHumanMoment(turn.Moment.Role) &&
                        quote.Length >= 2 && (turn.Moment.Content ?? "").Contains(quote, StringComparison.Ordinal),
                        path + ".evidence_quote：source=user 时须逐字引用当前对方发言，长度2～240字符；不能引用历史或自行概括。");
                else if (change.source == "feedback")
                    Check(change.operation == "complete" && quote.Length >= 2 && (turn.Workspace.Results.Any(x =>
                        x != null && x.Status == "success" &&
                        ((x.Summary ?? "").Contains(quote, StringComparison.Ordinal) || (x.Payload ?? "").Contains(quote, StringComparison.Ordinal))) ||
                        (turn.Moment.SourcePluginId == "runtime.execution" && turn.Services.Executions.List(turn.ConversationId).Any(x =>
                            x.ExecutionId == turn.Moment.SourceEventId && x.Status == "completed" &&
                            ((x.Summary ?? "").Contains(quote, StringComparison.Ordinal) || (x.ConfirmedContent ?? "").Contains(quote, StringComparison.Ordinal))))),
                        path + ".evidence_quote：source=feedback 只能 complete，须逐字引用本轮成功结果或当前设备完成回执，至少2字符。");
                else
                    Check(quote.Length == 0 && change.operation != "complete" &&
                        (previous == null || previous.Origin == "self"),
                        path + ".source=self 时 evidence_quote 留空，不能 complete 或改写对方的约定。");

                if (change.operation is "create" or "revise")
                {
                    Check(change.kind is "preference" or "goal", path + ".kind 必须为 preference/goal。");
                    Check(previous == null || previous.Kind == change.kind, path + ".kind 修订时不能改变原记录类型。");
                    Check(change.source == "user" || (change.source == "self" && change.kind == "goal"), path + ".source：创建或修订只允许 user，或 self 的 goal。");
                    Check(Text(change.content, 240), path + ".content 必填且最多240字符。");
                    Check(Text(change.applies_when, 120), path + ".applies_when 必填且最多120字符。");
                    Check(change.horizon is "turn" or "ongoing" or "until", path + ".horizon 必须为 turn/ongoing/until。");
                    var starts = ParseTime(change.starts_at, path + ".starts_at");
                    var expires = ParseTime(change.expires_at, path + ".expires_at");
                    Check(change.horizon == "until" ? expires > now && expires > starts : expires == 0,
                        path + ".expires_at：until 时必须晚于现在及 starts_at，其他 horizon 留空。");
                    Check(change.horizon != "turn" || starts == 0, path + ".starts_at：turn 仅本轮有效，不能指定开始时间。");
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
                    Check(change.operation != "complete" || previous.Kind == "goal", path + ".operation：complete 只适用于 goal，preference 不能完成。");
                    previous.Status = change.operation == "complete" ? "completed" : "cancelled";
                    // 终止也保留单独证据，原记录的提出依据不被覆盖。
                    doc.Items.Add(new GoalRecord { Id = Guid.NewGuid().ToString("N"), Kind = previous.Kind,
                        Content = previous.Content, Origin = previous.Origin, Status = previous.Status,
                        SourceMomentId = turn.Moment.Id, EvidenceQuote = quote, ChangeSource = change.source,
                        ReplacesId = previous.Id, UpdatedUnixMs = now });
                }
                doc.Applied.Add(key);
            }
            Check(doc.Items.Count(x => x.Status == "active") <= MaxActive, "$.goal_updates 会超出32项有效记录容量；不要新增或擅自取消已有约定。");
            // 活跃目标绝不因历史预算被丢弃。保留最近 256 条历史版本；原文仍在 Moment。
            doc.Items = doc.Items.Where(x => x.Status == "active").Concat(doc.Items.Where(x => x.Status != "active")
                .OrderByDescending(x => x.UpdatedUnixMs).Take(256)).ToList();
            doc.Applied = doc.Applied.TakeLast(256).ToList();
        }

        private static bool Text(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
        private static void Check(bool valid, string message) { if (!valid) throw new ArgumentException(message); }
        private static string Iso(long value) => value == 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(value).ToString("o");
        private static long ParseTime(string text, string path)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            Check(text.Length <= 40 && Regex.IsMatch(text, @"(?:Z|[+-]\d{2}:\d{2})$") &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _), path + " 必须是带时区的 ISO 8601 时间字符串，最多40字符。");
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        }
    }
}
