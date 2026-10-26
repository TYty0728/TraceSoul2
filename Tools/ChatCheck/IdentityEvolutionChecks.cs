using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

internal static partial class Program
{
    private static string ConsolidationNewReply(string summary) => TraceJson.ToJson(new {
        decisions = new[] { new { index = 0, kind = "new", target_id = "", before = "", after = summary, explanation = "本项是独立的新认识" } } });
    private static string DailyCardReply(string body = "我们会共同核对安排，并给彼此说明变化的空间。", string effect = "changed", bool retain = false, params int[] indices)
        => TraceJson.ToJson(new { effect, body = effect == "changed" ? body : "", reason = "依据当天形成的理解，保留条件与反证", retain_current_basis = retain,
            basis_indices = indices.Length > 0 ? indices : effect == "changed" ? new[] { 0 } : Array.Empty<int>() });

    private static async Task RunIdentityEvolutionChecksAsync()
    {
        await RunCognitionComparisonChecksAsync();
        var directory = Path.Combine(Path.GetTempPath(), "daily-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "brain.db");
        const string root = "tracesoul2", day = "2020-09-01";
        var start = MemoryDayLogic.StartOf(day).ToUnixTimeMilliseconds();
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("本人", "同伴", "称呼"); store.LoadIdentityCards(root);
                store.SaveMoment(new MomentRecord { Id = "old", Role = "user", ConversationId = root, Content = "过去的经历", CreatedUnixMs = start - 86400000 });
                store.SaveMoment(new MomentRecord { Id = "today", Role = "user", ConversationId = "legacy-private-platform", Content = "今天共同核对安排", CreatedUnixMs = start + 1 });
                using var db = new SQLite.SQLiteConnection(path);
                for (var i = 0; i < 158; i++)
                {
                    db.Insert(new CognitionSliceRecord { Id = "old-" + i, Summary = "OLD_HISTORY_SENTINEL_" + i, Status = "active",
                        IdentitySlot = "relation", ContextConversationId = root, MemoryVisibility = "private" });
                    db.Insert(new CognitionEvidenceRecord { Id = "old-e-" + i, CognitionId = "old-" + i, MomentId = "old", Relation = "supports" });
                }
                foreach (var id in new[] { "today-a", "today-b" })
                {
                    db.Insert(new CognitionSliceRecord { Id = id, Summary = "当天形成的独立理解" + id, Status = "active", Scope = "双方可以回应时", Exceptions = "忙碌时先说明",
                        IdentitySlot = "relation", ContextConversationId = root, MemoryVisibility = "private" });
                    db.Insert(new CognitionEvidenceRecord { Id = "e-" + id, CognitionId = id, MomentId = "today", Relation = "supports" });
                }
                // Old failed full-regrouping drafts remain in storage but are not replayed by the new automatic path.
                store.SavePluginDocument(IdentityEvolutionLogic.StoreId, day + ":" + root + ":relation", "{\"globallyGrouped\":true,\"batchesDone\":40}");
                var material = new IdentityEvolutionLogic.DayMaterial { source_ids = new() { "today" }, events = new() {
                    new() { id = "event-today", summary = "今日安排有变化，双方共同确认", source_id = "today" } } };
                var first = new AgentSequenceLlm(DailyCardReply(indices: new[] { 0, 1 }));
                var state = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", day, first, CancellationToken.None, material);
                Require(first.Requests.Count == 1 && first.Requests[0].Contains("event-today") && first.Requests[0].Contains("current_card") &&
                    !first.Requests[0].Contains("OLD_HISTORY_SENTINEL") && state.proposal.cognition_ids.SequenceEqual(new[] { "today-a", "today-b" }),
                    "158条旧认知不进入日复盘，一次请求直接用当天事件/认知更新当前卡片并绑定真实来源");
                IdentityEvolutionLogic.Commit(store, root, "relation", state);
                var card = store.LoadIdentityCards(root).Single(c => c.Slot == "relation");
                var revision = card.Revision; var body = card.Body;
                db.Insert(new CognitionEvidenceRecord { Id = "today-supports-old", CognitionId = "old-0", MomentId = "today", Relation = "supports" });
                var noChange = new AgentSequenceLlm(DailyCardReply(effect: "unchanged"));
                var unchanged = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", day, noChange, CancellationToken.None, material);
                IdentityEvolutionLogic.Commit(store, root, "relation", unchanged);
                Require(noChange.Requests.Count == 1 && store.LoadIdentityCards(root).Single(c => c.Slot == "relation").Revision == revision,
                    "当天触及的旧认识可以保持原卡，不要求每次复盘都写新正文");
                var repeat = new AgentSequenceLlm();
                Require(await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", day, repeat, CancellationToken.None, material) == null && repeat.Requests.Count == 0,
                    "同日已完成卡片不会重复生成");
                store.SaveMoment(new MomentRecord { Id = "next", ConversationId = root, Role = "user", Content = "重复印证", CreatedUnixMs = start + 86400001 });
                db.Insert(new CognitionEvidenceRecord { Id = "repeat-proof", CognitionId = "today-a", MomentId = "next", Relation = "supports" });
                var support = new AgentSequenceLlm();
                Require(await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-09-02", support, CancellationToken.None) == null && support.Requests.Count == 0 &&
                    store.LoadIdentityCards(root).Single(c => c.Slot == "relation").Revision == revision,
                    "新一天仅重复印证时零身份调用，原始新证据仍留存");
                db.Execute("UPDATE cognition_slices SET Summary='原理解出现反证',Status='weakened',LastChallengedUnixMs=? WHERE Id='today-a'", start + 86400002);
                var invalidKeep = new AgentSequenceLlm(DailyCardReply(effect: "unchanged", indices: new[] { 0 }), DailyCardReply(effect: "unchanged", indices: new[] { 0 }));
                var rejected = false;
                try { await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-09-02", invalidKeep, CancellationToken.None); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected && invalidKeep.Requests.Count == 2 && store.LoadIdentityCards(root).Single(c => c.Slot == "relation").Body == body,
                    "受削弱的旧依据不能直接恢复为确定原卡，有限纠正后拒绝");
                var revised = new AgentSequenceLlm(DailyCardReply("我们可以共同确认安排，但不能假定对方随时能够回应。", retain: true));
                state = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-09-02", revised, CancellationToken.None);
                IdentityEvolutionLogic.Commit(store, root, "relation", state);
                Require(revised.Requests.Count == 1 && state.proposal.cognition_ids.Count == 2 && !revised.Requests[0].Contains("OLD_HISTORY_SENTINEL"),
                    "反证只读取当前受影响认知，一次修订并保留仍有效旧依据，不重做历史归并");

                db.Execute("UPDATE cognition_slices SET Summary='需要进一步调整的理解' WHERE Id='today-a'");
                var bad = new AgentSequenceLlm(DailyCardReply(indices: new[] { 999 }), DailyCardReply(indices: new[] { 999 })); rejected = false;
                try { await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-09-03", bad, CancellationToken.None); }
                catch (InvalidOperationException e) { rejected = e.Message.Contains("basis_indices"); }
                Require(rejected && bad.Requests.Count == 2, "越界依据明确指出字段，不提交错误正文或机械截断引用");
                var longer = new AgentSequenceLlm(DailyCardReply(new string('长', 900)), TraceJson.ToJson(new { text = new string('长', 800) }));
                var pending = false;
                try { await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-09-03", longer, CancellationToken.None); }
                catch (ReviewProsePendingException) { pending = true; }
                Require(pending && longer.Requests.Count == 2, "直接更新输出过长时保留来源合格草稿，只精简正文，不展开全历史");
            }
            using (var store = new SqliteMemoryManager(path))
            {
                using var db = new SQLite.SQLiteConnection(path);
                db.Execute("UPDATE plugin_documents SET Json=json_set(Json,'$.NextAttemptUnixMs',0) WHERE PluginId=?", ReviewWorkLogic.StoreId);
                var resumed = new AgentSequenceLlm("{\"text\":\"我们愿意核对安排，也承认回应需要合适时机。\"}");
                var state = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-09-03", resumed, CancellationToken.None);
                Require(resumed.Requests.Count == 1 && resumed.Requests[0].Contains("单字段篇幅整理"), "重开数据库仅补未完成正文，已经形成的判断和来源复用");
                IdentityEvolutionLogic.Commit(store, root, "relation", state);
                db.Execute("UPDATE cognition_slices SET Summary='生成期间将被修改的理解' WHERE Id='today-a'");
                var stale = new SelectionProbeLlm { Reply = _ => {
                    store.SaveIdentityCard(root, "relation", "本人新修改的卡片", ""); return DailyCardReply(); } };
                var rejected = false;
                try { await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-09-04", stale, CancellationToken.None); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected && store.LoadIdentityCards(root).Single(c => c.Slot == "relation").Body == "本人新修改的卡片", "并发人工修改拒绝旧模型结果");
                store.SetIdentityPinned(root, "relation", false);
                var wrongScope = new IdentityEvolutionLogic.DayMaterial { source_ids = new() { "old" } };
                rejected = false;
                try { await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", day, new AgentSequenceLlm(), CancellationToken.None, wrongScope); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected && store.GetCognitionNodes(5000).Count == 160, "跨日输入拒绝，全部历史认知原件仍然保留");
            }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Daily identity passed: today's events/cognitions -> current card in one call; 158 historical nodes excluded; no-change zero calls; evidence, refusal, restart and concurrent edits protected.");
    }
}
