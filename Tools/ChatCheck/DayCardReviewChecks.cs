using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Logic;
using TraceSoul2.Util;
using TraceSoul2.Plugins;
using TraceSoul2.Host;

internal static partial class Program
{
    private static void RunDayCardReviewChecks(Assembly assembly)
    {
        var contract = assembly.GetType("TraceSoul2.Migrate.DayCardReviewContract", true);
        var outputType = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts+DayCardReviewOutputData", true);
        var validate = contract.GetMethod("ValidationError");
        var json = new JsonSerializerOptions { IncludeFields = true };
        var cards = IdentityCardSlotValues.All.Select(slot => new IdentityCardRecord
            { Slot = slot, Body = "", Pinned = slot == "personality" }).ToList();
        var nodes = new List<CognitionSliceRecord> {
            new() { Id = "r1", Status = "active", IdentitySlot = "relation", Summary = "第一条关系理解" },
            new() { Id = "r2", Status = "active", IdentitySlot = "relation", Summary = "第二条关系理解" },
            new() { Id = "h1", Status = "active", IdentitySlot = "expression_habit", Summary = "表达理解" },
            new() { Id = "p1", Status = "active", IdentitySlot = "personality", Summary = "固定内容依据" } };
        object Parse(string raw) => JsonSerializer.Deserialize(raw, outputType, json);
        string Error(string raw) => (string)validate.Invoke(null, new object[] { Parse(raw), cards, nodes });
        object Card(string slot, string body, params string[] refs) => new { slot, body, cognition_ids = refs };
        string Output(params object[] updates) => JsonSerializer.Serialize(new { summary = "离线测试", cards = updates });
        var duplicate = Output(Card("relation", "第一部分", "r1"), Card("relation", "第二部分", "r2"),
            Card("expression_habit", "表达变化", "h1"));
        var merged = Output(Card("relation", "综合理解保留两部分", "r1", "r2"), Card("expression_habit", "表达变化", "h1"));
        var error = Error(duplicate);
        Require(error.Contains("$.cards[1].slot") && error.Contains("$.cards[0].slot") && error.Contains("重复") &&
            error.Contains("综合") && !error.Contains("第一部分") && !error.Contains("缺少有效认知"),
            "重复卡错误准确定位同名项和综合要求，不谎报依据缺失、不泄露正文");
        Require(Error(merged) == null && Error("{\"cards\":[]}") == null, "同卡多依据合法，明确不更新合法");
        Require(Error("{}")?.Contains("$.cards") == true && Error("{\"cards\":null}") != null,
            "省略 cards 不能冒充无变化");
        Require(Error(Output(Card("personality", "不应覆盖", "p1"))).Contains("本人固定"), "仍拒绝覆盖固定内容");
        Require(Error(Output(Card("relation", new string('字', 445), "r1"))) == null &&
            Error(Output(Card("relation", new string('字', 600), "r1"))) == null &&
            Error(Output(Card("relation", new string('字', 601), "r1"))).Contains(".body 超过600"),
            "300是目标，完整摘要允许余量，异常长度仍准确定位");
        Require(Error(Output(Card("relation", "测试", "h1"))).Contains(".cognition_ids[0]"), "跨 slot 依据拒绝");
        Require(Error(Output(Card("relation", "测试", "unknown-private-id"))).Contains(".cognition_ids[0]"), "未知依据拒绝且不回显值");
        var contextText = (string)contract.GetMethod("EvidenceContext").Invoke(null, new object[] { cards, nodes });
        Require(contextText.Contains("同组的多条认知") && contextText.Contains("r1") && contextText.Contains("r2") &&
            !contextText.Contains("固定内容依据"), "按可更新卡片组织依据，固定卡不作为输出目标");

        // Exercise the actual private DayBuilder entry with temporary dependencies, never production config.
        var dir = Path.Combine(Path.GetTempPath(), "day-card-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        IDisposable context = null;
        try
        {
            var contextType = assembly.GetType("TraceSoul2.Migrate.MigrationContext", true);
            var storeType = assembly.GetType("TraceSoul2.Manager.SqliteMemoryManager", true);
            var dbPath = Path.Combine(dir, "brain.db");
            context = (IDisposable)Activator.CreateInstance(contextType);
            var actualStore = Activator.CreateInstance(storeType, dbPath);
            contextType.GetProperty("Store").SetValue(context, actualStore);
            var migration = Activator.CreateInstance(assembly.GetType("TraceSoul2.Migrate.MigrationDb", true),
                Path.Combine(dir, "migration.db"), dbPath);
            contextType.GetProperty("Migration").SetValue(context, migration);
            var store = (IMemoryStore)actualStore;
            store.SavePairIdentity("本人", "同伴", "称呼");
            using (var db = new SQLite.SQLiteConnection(dbPath))
                foreach (var node in nodes) db.Insert(node);
            var run = assembly.GetType("TraceSoul2.Migrate.DayBuilder", true).GetMethod("RunDayReviewAsync", BindingFlags.NonPublic | BindingFlags.Static);
            var llm = new AgentSequenceLlm(duplicate, merged);
            var args = new object[] { context, store.LoadPairIdentity(), llm, "2026-09-30", cards, "",
                new List<EventIndexRecord>(), new List<EventEntryRecord>() };
            var task = (Task)run.Invoke(null, args);
            task.GetAwaiter().GetResult();
            var result = task.GetType().GetProperty("Result").GetValue(task);
            Require(((System.Collections.IList)outputType.GetField("cards").GetValue(result)).Count == 2 &&
                llm.Requests.Count == 2 && llm.Messages[1].Last().content.Contains("$.cards[1].slot") &&
                llm.Messages[1].Last().content.Contains("合并所用 cognition_ids"),
                "真实日复盘入口按具体错误纠正一次，两张同名卡综合后正常完成");
            var outside = new AgentSequenceLlm(Output(Card("self", "范围外的自我改写", "r1"),
                Card("other", "范围外的他者改写", "r2"), Card("personality", "范围外的种子改写", "p1"),
                Card("relation", "有依据的关系变化", "r1"), Card("expression_habit", "有依据的表达变化", "h1")));
            args[2] = outside;
            var restrictedTask = (Task)run.Invoke(null, args);
            restrictedTask.GetAwaiter().GetResult();
            var restricted = restrictedTask.GetType().GetProperty("Result").GetValue(restrictedTask);
            using (var resultJson = JsonDocument.Parse(JsonSerializer.Serialize(restricted, outputType, json)))
                Require(resultJson.RootElement.GetProperty("cards").GetArrayLength() == 2 && outside.Requests.Count == 1 &&
                    resultJson.RootElement.GetProperty("cards")[0].GetProperty("slot").GetString() == "relation" &&
                    resultJson.RootElement.GetProperty("cards")[0].GetProperty("cognition_ids")[0].GetString() == "r1",
                    "实际入口由程序保留范围外与固定卡，仅校验可写目标；有效正文及依据不变且不额外纠正");
            var wrongGroup = new AgentSequenceLlm(Output(Card("relation", "错误跨组引用", "h1")), merged);
            args[2] = wrongGroup;
            ((Task)run.Invoke(null, args)).GetAwaiter().GetResult();
            Require(wrongGroup.Requests.Count == 2 && wrongGroup.Messages[1].Last().content.Contains("cognition_ids[0]"),
                "可写卡跨组引用仍进入精确纠正，程序不改写引用或强行通过");
            var failing = new AgentSequenceLlm(duplicate, duplicate);
            args[2] = failing;
            var rejected = false;
            try { ((Task)run.Invoke(null, args)).GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("首次错误") && ex.Message.Contains("纠正后错误") && ex.Message.Contains("重复"); }
            Require(rejected && failing.Requests.Count == 2, "连续重复仍保留准确错误并停止，不放宽约束、不无限重试");

            foreach (var length in new[] { 300, 363, 365, 445, 600 })
            {
                var body = new string('长', length - 7) + "重要条件在末尾";
                var validOutput = Output(Card("relation", body, "r1", "r2"));
                var accepted = new AgentSequenceLlm(validOutput);
                args[2] = accepted;
                var acceptedTask = (Task)run.Invoke(null, args);
                acceptedTask.GetAwaiter().GetResult();
                using var acceptedJson = JsonDocument.Parse(JsonSerializer.Serialize(acceptedTask.GetType().GetProperty("Result").GetValue(acceptedTask), outputType, json));
                Require(accepted.Requests.Count == 1 && acceptedJson.RootElement.GetProperty("cards")[0].GetProperty("body").GetString() == body,
                    "完整摘要在目标余量内通过真实日复盘入口，无额外精炼调用或尾部损失");
                var saved = store.SaveIdentityCard("length-check", "relation", body, "");
                Require(saved.Body == body && store.LoadIdentityCards("length-check").Single(x => x.Slot == "relation").Body == body,
                    "底层保存和重新读取保留完整摘要");
            }
            var previous = store.LoadIdentityCards("length-check").Single(x => x.Slot == "relation");
            rejected = false;
            try { store.SaveIdentityCard("length-check", "relation", new string('长', 601), ""); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && store.LoadIdentityCards("length-check").Single(x => x.Slot == "relation").Body == previous.Body,
                "异常长度保存失败时原卡不变，不能截断后落库");
            var normalized = IdentityCardLogic.Normalize(new IdentityReviewOutputData { cards = new() {
                new() { slot = "relation", changed = true, body = new string('长', 440) + "完整末尾。" }
            } }, Array.Empty<IdentityCardRecord>(), store.LoadPairIdentity());
            Require(normalized.cards[0].body.Length == 445 && normalized.cards[0].body.EndsWith("完整末尾。"),
                "旧身份规范化同样保留略超目标的完整正文");
            var renameBody = "本人" + new string('长', 591) + "重要条件在末尾";
            store.LoadIdentityCards("rename-length");
            store.SaveIdentityCard("rename-length", "self", renameBody, "");
            store.SavePairIdentity("新的完整本人称呼", "同伴", "称呼");
            Require(store.LoadIdentityCards("rename-length").Single(x => x.Slot == "self").Body ==
                "新的完整本人称呼" + new string('长', 591) + "重要条件在末尾",
                "更换为更长称呼不截断已保存摘要或末尾条件");
            store.SavePairIdentity("本人", "同伴", "称呼");
            var longOutput = Output(Card("relation", new string('长', 745), "r1", "r2"), Card("expression_habit", "保持这张原文", "h1"));
            var repairedOutput = "{\"items\":[{\"id\":\"relation\",\"body\":\"收成这一份关系\"}]}";
            var fitting = new AgentSequenceLlm(longOutput, repairedOutput);
            args[2] = fitting;
            var fitTask = (Task)run.Invoke(null, args);
            fitTask.GetAwaiter().GetResult();
            using var fitJson = JsonDocument.Parse(JsonSerializer.Serialize(fitTask.GetType().GetProperty("Result").GetValue(fitTask), outputType, json));
            var fitCards = fitJson.RootElement.GetProperty("cards");
            using var repairRequest = JsonDocument.Parse(fitting.Messages[1].Last().content);
            Require(repairRequest.RootElement.GetProperty("targets")[0].GetProperty("previous_body_chars").GetInt32() == 745,
                "补写请求携带实际超长字数，让模型可明确缩至目标篇幅");
            Require(fitting.Requests.Count == 2 && fitting.Messages[1][0].content.Contains("复盘未完成正文") &&
                fitting.Messages[1].Last().content.Contains("previous_body_chars") && fitting.Messages[1].Last().content.Contains("max_chars") && !fitting.Requests[1].Contains("保持这张原文") &&
                fitCards.GetArrayLength() == 2 && fitCards[0].GetProperty("body").GetString() == "收成这一份关系" &&
                fitCards[1].GetProperty("body").GetString() == "保持这张原文",
                "超出篇幅时按原任务重写完整摘要，不另起压缩，也不丢掉同批有效卡");
            var unchanged = Parse(longOutput);
            var beforeCards = JsonSerializer.Serialize(unchanged, outputType, json);
            var fitMethod = contract.GetMethod("FitBodiesAsync");
            var failure = new AgentSequenceLlm();
            rejected = false;
            try { ((Task)fitMethod.Invoke(null, new object[] { failure, unchanged, cards, nodes, System.Threading.CancellationToken.None })).GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("超过600"); }
            Require(rejected && failure.Requests.Count == 0 &&
                JsonSerializer.Serialize(unchanged, outputType, json) == beforeCards,
                "超长摘要不在校验里删卡或截断，也不另调模型压缩");
            var stillLong = Output(Card("relation", new string('长', 1201), "r1", "r2"), Card("expression_habit", "保持这张原文", "h1"));
            var fittingFailure = new AgentSequenceLlm(stillLong,
                TraceJson.ToJson(new { items = new[] { new { id = "relation", body = new string('长', 1201) } } }));
            args[2] = fittingFailure;
            var pendingTask = (Task)run.Invoke(null, args);
            pendingTask.GetAwaiter().GetResult();
            var pendingResult = pendingTask.GetType().GetProperty("Result").GetValue(pendingTask);
            Require(((List<string>)outputType.GetField("PendingParts").GetValue(pendingResult)).SequenceEqual(new[] { "relation" }) &&
                ((System.Collections.IList)outputType.GetField("cards").GetValue(pendingResult)).Count == 1 && fittingFailure.Requests.Count == 2,
                "单卡持续超限保存待整理状态，其他合格卡保留，不截断或整批重试");
            var resume = new AgentSequenceLlm(repairedOutput);
            args[2] = resume;
            var resumeTask = (Task)run.Invoke(null, args);
            resumeTask.GetAwaiter().GetResult();
            Require(resume.Requests.Count == 1 && resume.Messages[0][0].content.Contains("复盘未完成正文") &&
                !resume.Requests[0].Contains(new string('长', 1201)),
                "续做只请求待整理正文，合格字段和引用从检查点恢复，超长草稿不回传");
            var broken = Parse("{\"cards\":null}");
            rejected = false;
            try { ((Task)fitMethod.Invoke(null, new object[] { new AgentSequenceLlm(), broken, cards, nodes, System.Threading.CancellationToken.None })).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "结构错误仍直接失败，不改写成一次长度压缩");
        }
        finally { context?.Dispose(); Directory.Delete(dir, true); }
        Console.WriteLine("Day card review checks passed: slot grouping, precise repair, multiple evidence, explicit empty, pinned/length/source guards and actual Migration entry.");
    }

    private static void RunDayReviewRecoveryChecks(Assembly assembly)
    {
        var directory = Path.Combine(Path.GetTempPath(), "daily-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "brain.db");
        var history = Path.Combine(directory, "migration.db");
        var contextType = assembly.GetType("TraceSoul2.Migrate.MigrationContext", true);
        var storeType = assembly.GetType("TraceSoul2.Manager.SqliteMemoryManager", true);
        var builder = assembly.GetType("TraceSoul2.Migrate.DayBuilder", true);
        MethodInfo Method(string name) => builder.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        IDisposable context = null;
        object actualStore = null;
        IMemoryStore store = null;
        void Open()
        {
            context = (IDisposable)Activator.CreateInstance(contextType);
            actualStore = Activator.CreateInstance(storeType, path);
            contextType.GetProperty("Store").SetValue(context, actualStore);
            contextType.GetProperty("Migration").SetValue(context, Activator.CreateInstance(
                assembly.GetType("TraceSoul2.Migrate.MigrationDb", true), history, path));
            store = (IMemoryStore)actualStore;
            store.SavePairIdentity("本人", "同伴", "称呼");
            store.LoadIdentityCards("tracesoul2");
        }
        List<MomentRecord> Seed(string day, int count = 1)
        {
            var start = MemoryDayLogic.StartOf(day).ToUnixTimeMilliseconds();
            var sources = Enumerable.Range(0, count).Select(i => new MomentRecord { Id = day + "-" + i,
                ConversationId = "tracesoul2", Role = "user", Content = "完整测试来源", CreatedUnixMs = start + i }).ToList();
            foreach (var source in sources) store.SaveMoment(source);
            return sources;
        }
        void Checkpoint(string day, List<MomentRecord> sources, bool summaryPending = false, bool reviewDone = false)
        {
            var core = Method("SaveCoreCheckpoint").Invoke(null, new object[] { context, day, sources,
                new List<EventIndexRecord>(), new List<EventEntryRecord>(), null });
            core.GetType().GetField("Ranked").SetValue(core, true);
            core.GetType().GetField("ReviewDone").SetValue(core, reviewDone);
            core.GetType().GetField("DaySummaryPending").SetValue(core, summaryPending);
            Method("WriteCoreCheckpoint").Invoke(null, new object[] { context, day, core });
        }
        void Run(string day, ILlmClient llm)
        {
            contextType.GetProperty("Llm").SetValue(context, llm);
            ((Task)builder.GetMethod("RunAsync").Invoke(null, new object[] { context, new[] { "--day", day } })).GetAwaiter().GetResult();
        }
        string Output(string reference, string body, string habit = "合格的表达理解") => TraceJson.ToJson(new {
            cards = new[] { new { slot = "relation", body, cognition_ids = new[] { reference } },
                new { slot = "expression_habit", body = habit, cognition_ids = new[] { "h" } } } });
        string Repair(string body) => TraceJson.ToJson(new { items = new[] { new { id = "relation", body } } });
        try
        {
            Open();
            const string day = "2020-08-20";
            var sources = Seed(day);
            using (var db = new SQLite.SQLiteConnection(path))
                foreach (var item in new[] { ("r", "relation"), ("h", "expression_habit") })
                {
                    db.Insert(new CognitionSliceRecord { Id = item.Item1, IdentitySlot = item.Item2, Status = "active",
                        Summary = "当前有效理解", ContextConversationId = "tracesoul2", MemoryVisibility = "private" });
                    db.Insert(new CognitionEvidenceRecord { Id = "e-" + item.Item1, CognitionId = item.Item1,
                        MomentId = sources[0].Id, Relation = "support", Weight = 1 });
                }
            Checkpoint(day, sources);
            var first = new AgentSequenceLlm(Output("r", new string('长', 745)), Repair(new string('长', 745)));
            Run(day, first);
            var savedHabit = store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "expression_habit");
            Require(first.Requests.Count == 2 && savedHabit.Body == "合格的表达理解" &&
                DailyBuildHistory.ReadStates(history).Single().Status == "pending",
                "真实日构建只将超限单卡列为待整理，合格卡已提交，日状态不能假报完成");
            store.SaveMoment(new MomentRecord { Id = "late-public", ConversationId = "tracesoul2:environment:test",
                Role = "user", Content = "公开测试来源", MemoryVisibility = "public",
                CreatedUnixMs = MemoryDayLogic.StartOf(day).ToUnixTimeMilliseconds() + 50 });
            var cachedCore = Method("LoadCoreCheckpoint").Invoke(null, new object[] { context, day });
            Require(!(bool)Method("CanResumeCore").Invoke(null, new object[] { context, day, cachedCore }),
                "私密核心检查点不能略过新到的公开经历，避免待整理队列反复触发却永远不处理公开来源");
            using (var db = new SQLite.SQLiteConnection(path)) db.Execute("UPDATE moments SET MemoryStatus='built' WHERE Id='late-public'");
            context.Dispose(); context = null;
            Open();
            var resumed = new AgentSequenceLlm(Repair("来源保持完整的关系理解"));
            Run(day, resumed);
            Require(resumed.Requests.Count == 1 && resumed.Messages[0][0].content.Contains("复盘未完成正文") &&
                DailyBuildHistory.ReadStates(history).Single().Status == "done" &&
                store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "relation").Body == "来源保持完整的关系理解" &&
                store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "expression_habit").Revision == savedHabit.Revision,
                "重启后仅续做待整理卡，已完成的事件/认知/排序不重跑，合格卡不重复提交");

            const string invalidDay = "2020-08-21";
            Checkpoint(invalidDay, Seed(invalidDay));
            var invalid = Output("h", new string('长', 745), "不应写入的表达");
            var rejected = false;
            try { Run(invalidDay, new AgentSequenceLlm(invalid, invalid)); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "expression_habit").Body == savedHabit.Body &&
                DailyBuildHistory.ReadStates(history).Single(x => x.DayKey == invalidDay).Status == "failed",
                "超限不能掩盖跨组引用错误，初始结构校验失败不部分写卡，仍记录真正失败");

            var changedDuringCall = new SelectionProbeLlm { Reply = _ => {
                store.SaveIdentityCard("tracesoul2", "relation", "刚刚保存的新内容", "");
                return Output("r", "不应覆盖新卡"); } };
            rejected = false;
            try { Run(invalidDay, changedDuringCall); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "relation").Body == "刚刚保存的新内容",
                "生成期间身份卡被外部修订时拒绝旧结果，不能让检查点覆盖较新内容");
            var changedEvidence = new SelectionProbeLlm { Reply = _ => {
                using var db = new SQLite.SQLiteConnection(path);
                db.Execute("UPDATE cognition_slices SET Revision=Revision+1, Summary=? WHERE Id=?", "已被反证修订的认识", "r");
                return Output("r", "根据旧认识写出的正文"); } };
            rejected = false;
            try { Run(invalidDay, changedEvidence); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && changedEvidence.Requests.Count == 1 &&
                store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "relation").Body == "刚刚保存的新内容",
                "生成期间认知版本发生变化时不提交旧正文，也不把旧文绑定到新版本依据");

            const string summaryDay = "2020-08-22";
            var summarySources = Seed(summaryDay, 13);
            foreach (var source in summarySources)
                storeType.GetMethod("AppendRuntimeSlice").Invoke(actualStore, new object[] { new RuntimeSliceRecord {
                    RootConversationId = "tracesoul2", ContextConversationId = "tracesoul2", MomentId = source.Id,
                    SnapshotJson = "{\"inner\":\"测试感受\"}" } });
            Checkpoint(summaryDay, summarySources, summaryPending: true, reviewDone: true);
            var nomination = "{\"events\":[{\"summary\":\"当天的变化\",\"source_ids\":[\"s1\"]}]}";
            var excessive = TraceJson.ToJson(new { summary = new string('长', 1001) });
            var longSummary = new AgentSequenceLlm(nomination, excessive, excessive);
            Run(summaryDay, longSummary);
            Require(DailyBuildHistory.ReadStates(history).Single(x => x.DayKey == summaryDay).Status == "pending" &&
                ((List<RuntimeSliceRecord>)storeType.GetMethod("GetRuntimeSlices").Invoke(actualStore,
                    new object[] { "tracesoul2", summaryDay, true })).Count == 13,
                "回望超限使用明确待整理状态，不阻断已完成核心，也不把未整理切片清掉或伪装完成");
            context.Dispose(); context = null;
            Open();
            var summaryResume = new AgentSequenceLlm("{\"summary\":\"符合预算的完整回望。\"}");
            Run(summaryDay, summaryResume);
            Require(summaryResume.Requests.Count == 1 && DailyBuildHistory.ReadStates(history).Single(x => x.DayKey == summaryDay).Status == "done",
                "回望恢复复用提名检查点，已经完成的身份卡和核心不再调用模型");

            using (var db = new SQLite.SQLiteConnection(path))
            {
                // Earlier concurrent-edit checks deliberately made relation a user-pinned card.
                db.Execute("UPDATE identity_cards SET Pinned=0 WHERE Slot='relation'");
                db.Insert(new CognitionSliceRecord { Id = "s", IdentitySlot = "self", Status = "active",
                    Summary = "只属于自我的认识", ContextConversationId = "tracesoul2", MemoryVisibility = "private" });
                db.Insert(new CognitionEvidenceRecord { Id = "e-s", CognitionId = "s", MomentId = sources[0].Id,
                    Relation = "support", Weight = 1 });
            }
            string MultiDraft() => TraceJson.ToJson(new { cards = new[] {
                new { slot = "relation", body = new string('关', 631), cognition_ids = new[] { "r" } },
                new { slot = "self", body = new string('我', 677), cognition_ids = new[] { "s" } },
                new { slot = "expression_habit", body = "合格表达保留", cognition_ids = new[] { "h" } } },
                inner_narrative = "合格的当日感受" });
            string SingleRepair(string id, string body) => TraceJson.ToJson(new { items = new[] { new { id, body } } });
            string Target(List<DeepSeekMessageData> messages)
            {
                using var request = JsonDocument.Parse(messages.Last().content);
                var targets = request.RootElement.GetProperty("targets");
                Require(targets.GetArrayLength() == 1, "每次补写只能有一个任务");
                var id = targets[0].GetProperty("id").GetString();
                Require(request.RootElement.GetProperty("output_example").GetProperty("items")[0].GetProperty("id").GetString() == id,
                    "示例动态对应本次目标，不能用固定relation示例诱导遗漏self");
                Require(request.RootElement.GetProperty("cognitions").EnumerateArray().All(x =>
                    x.GetProperty("IdentitySlot").GetString() == id), "补写只携带本项绑定认知，不混入其他卡材料");
                return id;
            }
            // Actual RunAsync: one omitted field stays pending while accepted fields commit once.
            var missingReplies = new[] { "{}", "{\"items\":[]}", "{\"items\":null}", SingleRepair("self", " ") };
            for (var i = 0; i < missingReplies.Length; i++)
            {
                var splitDay = "2020-09-0" + (i + 1);
                Checkpoint(splitDay, Seed(splitDay));
                var split = new SelectionProbeLlm { Reply = messages => {
                    if (!messages[0].content.Contains("复盘未完成正文")) return MultiDraft();
                    return Target(messages) == "relation" ? Repair("关系已独立写完") : missingReplies[i];
                } };
                Run(splitDay, split);
                var relation = store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "relation");
                var habit = store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "expression_habit");
                Require(split.Requests.Count == 3 && relation.Body == "关系已独立写完" && habit.Body == "合格表达保留" &&
                    DailyBuildHistory.ReadStates(history).Single(x => x.DayKey == splitDay).Status == "pending",
                    "漏项或空正文只将self列为待整理，关系与表达已提交，单次每项最多一请求");
                using (var saved = JsonDocument.Parse(store.LoadPluginDocument("daily-review-recovery-v1", "identity:" + splitDay)))
                    Require(saved.RootElement.GetProperty("Applied").EnumerateArray().Select(x => x.GetString())
                        .OrderBy(x => x).SequenceEqual(new[] { "expression_habit", "inner", "relation" }),
                        "持久进度记录已经应用的卡及内心，不能把缺失正文标成完成");
                context.Dispose(); context = null; Open();
                var finish = new SelectionProbeLlm { Reply = messages => {
                    Require(Target(messages) == "self", "重启只补self，不能重做关系、事件、认知、排序或初始草稿");
                    return SingleRepair("self", "补齐自我理解");
                } };
                Run(splitDay, finish);
                Require(finish.Requests.Count == 1 && DailyBuildHistory.ReadStates(history).Single(x => x.DayKey == splitDay).Status == "done" &&
                    store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "relation").Revision == relation.Revision &&
                    store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "expression_habit").Revision == habit.Revision,
                    "续做完成且已经提交的两张卡版本不变");
            }
            // Fail after relation succeeds: checkpoint must survive both transport and strict-contract failures.
            for (var i = 0; i < 4; i++)
            {
                var interruptedDay = "2020-09-1" + (i + 1);
                Checkpoint(interruptedDay, Seed(interruptedDay));
                var interrupted = new SelectionProbeLlm { Reply = messages => {
                    if (!messages[0].content.Contains("复盘未完成正文")) return MultiDraft();
                    if (Target(messages) == "relation") return Repair("中断前已保存的关系");
                    if (i == 0) throw new System.Net.Http.HttpRequestException("offline simulated disconnect");
                    if (i == 1) return SingleRepair("relation", "错误目标不能冒充self");
                    if (i == 2) return "{\"items\":[{\"id\":\"self\",\"body\":\"一\"},{\"id\":\"self\",\"body\":\"二\"}]}";
                    return "{\"items\":[";
                } };
                rejected = false;
                try { Run(interruptedDay, interrupted); }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is InvalidOperationException || ex is JsonException)
                    { rejected = true; }
                using (var saved = JsonDocument.Parse(store.LoadPluginDocument("daily-review-recovery-v1", "identity:" + interruptedDay)))
                    Require(rejected && interrupted.Requests.Count == 3 &&
                        saved.RootElement.GetProperty("Output").GetProperty("cards")[0].GetProperty("body").GetString() == "中断前已保存的关系" &&
                        saved.RootElement.GetProperty("Output").GetProperty("cards")[1].GetProperty("body").GetString().Length == 677,
                        "后续断网/错目标/重复/坏JSON仍严格报错，但前一项合格结果持久保留，原待稿不覆盖");
                context.Dispose(); context = null; Open();
                var finish = new SelectionProbeLlm { Reply = messages => {
                    Require(Target(messages) == "self", "失败重启不得再次请求已保存的relation");
                    return SingleRepair("self", "中断后补齐自我");
                } };
                Run(interruptedDay, finish);
                Require(finish.Requests.Count == 1 && DailyBuildHistory.ReadStates(history).Single(x => x.DayKey == interruptedDay).Status == "done" &&
                    store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "relation").Body == "中断前已保存的关系",
                    "检查点跨实例恢复，补齐后应用完整结果");
            }

            const string staleRepairDay = "2020-09-20";
            Checkpoint(staleRepairDay, Seed(staleRepairDay));
            var staleRepair = new SelectionProbeLlm { Reply = messages => {
                if (!messages[0].content.Contains("复盘未完成正文")) return MultiDraft();
                Require(Target(messages) == "relation", "版本变化前仅发出首项补写");
                store.SaveIdentityCard("tracesoul2", "relation", "补写期间的新卡", "");
                return Repair("过期修复不得覆盖新卡");
            } };
            rejected = false;
            try { Run(staleRepairDay, staleRepair); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && staleRepair.Requests.Count == 2 &&
                store.LoadIdentityCards("tracesoul2").Single(x => x.Slot == "relation").Body == "补写期间的新卡",
                "每项调用后立即核对版本，冲突时不提交旧草稿、不再花费下一项请求");

            const string staleInnerDay = "2020-09-21";
            using (var db = new SQLite.SQLiteConnection(path))
                db.Execute("UPDATE identity_cards SET Pinned=0 WHERE Slot='relation'");
            Checkpoint(staleInnerDay, Seed(staleInnerDay));
            var staleInner = new SelectionProbeLlm { Reply = messages => {
                if (!messages[0].content.Contains("复盘未完成正文")) return TraceJson.ToJson(new {
                    cards = new[] { new { slot = "relation", body = new string('长', 631), cognition_ids = new[] { "r" } } },
                    inner_narrative = new string('旧', 130), inner_mood = "旧情绪" });
                Require(Target(messages) == "relation", "实时内心已更新后不再请求过时内心草稿");
                store.SavePluginDocument("kernel.subject", "tracesoul2", "{\"Revision\":9999,\"Narrative\":{\"Text\":\"新当下\"}}");
                return Repair("保留身份补写");
            } };
            Run(staleInnerDay, staleInner);
            Require(staleInner.Requests.Count == 2 && store.LoadPluginDocument("kernel.subject", "tracesoul2").Contains("新当下") &&
                DailyBuildHistory.ReadStates(history).Single(x => x.DayKey == staleInnerDay).Status == "done",
                "拆分期间出现新当下时跳过旧内心，身份可以完成而不覆盖实时感受");

            const string innerDay = "2020-09-22";
            Checkpoint(innerDay, Seed(innerDay));
            using (var db = new SQLite.SQLiteConnection(path))
                foreach (var item in new[] { ("private", "tracesoul2", "本人私密回望"),
                    ("public", "tracesoul2", "公开回望不混入"), ("private", "tracesoul2:other", "其他环境不混入") })
                    db.Execute("INSERT INTO runtime_day_summaries (Id,RootConversationId,ContextConversationId,DayKey,MemoryVisibility,Summary,IsCurrent) VALUES (?,?,?,?,?,?,1)",
                        Guid.NewGuid().ToString("N"), "tracesoul2", item.Item2, innerDay, item.Item1, item.Item3);
            var innerOnly = new SelectionProbeLlm { Reply = messages => {
                if (!messages[0].content.Contains("复盘未完成正文")) return TraceJson.ToJson(new {
                    cards = new object[0], inner_narrative = new string('长', 130) });
                Require(Target(messages) == "inner_narrative" && messages.Last().content.Contains("本人私密回望") &&
                    !messages.Last().content.Contains("公开回望不混入") && !messages.Last().content.Contains("其他环境不混入"),
                    "内心独立补写只读取对应环境的私密回望，示例也必须是inner_narrative");
                return SingleRepair("inner_narrative", "独立整理的当天感受");
            } };
            Run(innerDay, innerOnly);
            Require(innerOnly.Requests.Count == 2 && store.LoadPluginDocument("kernel.subject", "tracesoul2").Contains("独立整理的当天感受") &&
                DailyBuildHistory.ReadStates(history).Single(x => x.DayKey == innerDay).Status == "done",
                "单独内心补写通过版本校验后同步，正常完成");
        }
        finally { context?.Dispose(); Directory.Delete(directory, true); }
        Console.WriteLine("Day review recovery checks passed: partial commit, process restart, pending states, strict references, stale cards and day summaries.");
    }

    private static void RunOct3ReviewReplay(string assemblyPath, string directory)
    {
        string Response(string pattern)
        {
            var text = File.ReadAllText(Directory.GetFiles(directory, pattern).Single());
            return text.Substring(text.IndexOf('{'), text.LastIndexOf('}') - text.IndexOf('{') + 1);
        }
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var contract = assembly.GetType("TraceSoul2.Migrate.DayCardReviewContract", true);
        var outputType = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts+DayCardReviewOutputData", true);
        var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true };
        using var request = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(directory, "20261003-122623*-request.json").Single()));
        var prompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        var start = prompt.IndexOf("\n[", prompt.IndexOf("【成长摘要与可参考的认识，按 slot 分组】", StringComparison.Ordinal), StringComparison.Ordinal);
        var end = prompt.IndexOf("【保留原文的本人设定】", start, StringComparison.Ordinal);
        using var groups = JsonDocument.Parse(prompt.Substring(start, end - start).Trim());
        var evidence = groups.RootElement.EnumerateArray().SelectMany(g => JsonSerializer.Deserialize<List<CognitionSliceRecord>>(g.GetProperty("cognitions").GetRawText(), options)).ToList();
        var current = new List<IdentityCardRecord> { new() { Slot = "personality", Pinned = true } };
        var output = JsonSerializer.Deserialize(Response("20261003-122623*-response.txt"), outputType, options);
        using var before = JsonDocument.Parse(JsonSerializer.Serialize(output, outputType, options));
        var fitting = new AgentSequenceLlm(Response("20261003-*-076-response.txt"), Response("20261003-*-077-response.txt"));
        var rejected = false;
        try { ((Task)contract.GetMethod("FitBodiesAsync").Invoke(null, new object[] { fitting, output, current, evidence, System.Threading.CancellationToken.None })).GetAwaiter().GetResult(); }
        catch (InvalidOperationException ex) { rejected = ex.Message.Contains(".body 超过") && ex.Message.Contains("重新写下"); }
        Require(rejected && fitting.Requests.Count == 0, "真实超长关系摘要不另起压缩，要求按原篇幅重新写下");
        using var after = JsonDocument.Parse(JsonSerializer.Serialize(output, outputType, options));
        Require(after.RootElement.GetProperty("cards").GetRawText() == before.RootElement.GetProperty("cards").GetRawText(),
            "超长校验不删卡、不截断正文");
        foreach (var field in before.RootElement.EnumerateObject())
            Require(after.RootElement.GetProperty(field.Name).GetRawText() == field.Value.GetRawText(), "重写要求不修改真实内心与摘要字段");
        using var store = new SqliteMemoryManager(":memory:");
        var dayStart = MemoryDayLogic.CurrentStart(DateTimeOffset.Now);
        for (var i = 0; i < 4; i++)
        {
            store.SaveMoment(new MomentRecord { Id = "replay-" + i, ConversationId = "replay", Role = "user", Content = "合成来源", CreatedUnixMs = dayStart.AddHours(7).AddMinutes(i).ToUnixTimeMilliseconds() });
            store.AppendDayTrajectory("replay", "replay-" + i, "合成事件" + i);
        }
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder())) { Llm = new AgentSequenceLlm(Response("20261003-122220*-response.txt")) };
        var turn = new TraceTurnContext("replay", new MomentRecord { ConversationId = "replay" }, new(), 0, true, services,
            environment: new EnvironmentSnapshotData { ContextConversationId = "replay", Visibility = "private" });
        var commit = DayTrajectoryOverviewLogic.Prepare(turn).AnalyzeAsync(default).GetAwaiter().GetResult();
        Require(commit != null, "真实精简响应的共同来源通过校验");
        commit(default).GetAwaiter().GetResult();
        Require(DayTrajectoryOverviewLogic.Read(store, "replay", dayStart.ToString("yyyy-MM-dd")).Count == 5,
            "真实五个短事件均保留，重复来源不退回原文");
        Console.WriteLine("Oct 3 read-only replay passed: failed card fitting preserved valid outputs; shared-source overview accepted. No private text printed or production data written.");
    }

    private static void RunDayCardScopeDumpCheck(string assemblyPath, string directory)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var contract = assembly.GetType("TraceSoul2.Migrate.DayCardReviewContract", true);
        var outputType = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts+DayCardReviewOutputData", true);
        var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true };
        var count = 0;
        foreach (var file in Directory.GetFiles(directory, "20261003-04*-response.txt").OrderBy(x => x))
        {
            var raw = File.ReadAllText(file);
            raw = raw.Substring(raw.IndexOf('{'), raw.LastIndexOf('}') - raw.IndexOf('{') + 1);
            using var original = JsonDocument.Parse(raw);
            if (!original.RootElement.TryGetProperty("cards", out var originalCards)) continue;
            using var request = JsonDocument.Parse(File.ReadAllText(file.Replace("-response.txt", "-request.json")));
            var prompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            const string marker = "【成长摘要与可参考的认识，按 slot 分组】";
            var start = prompt.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) continue;
            start = prompt.IndexOf("\n[", start, StringComparison.Ordinal);
            const string endMarker = "【保留原文的本人设定】";
            var end = prompt.IndexOf(endMarker, start, StringComparison.Ordinal);
            using var groups = JsonDocument.Parse(prompt.Substring(start, end - start).Trim());
            var evidence = groups.RootElement.EnumerateArray().SelectMany(g =>
                JsonSerializer.Deserialize<List<CognitionSliceRecord>>(g.GetProperty("cognitions").GetRawText(), options)).ToList();
            var pinned = prompt.Substring(end + endMarker.Length).TrimStart().Split('\n')[0].Split(',')
                .Select(x => new IdentityCardRecord { Slot = x.Trim(), Pinned = true }).ToList();
            var output = JsonSerializer.Deserialize(raw, outputType, options);
            var error = (string)contract.GetMethod("ValidationError").Invoke(null, new object[] { output, pinned, evidence });
            Require(error?.Contains("cards[0].cognition_ids[0]") == true, "真实响应复现范围外卡使用关系依据的原始错误");
            using var beforeProjection = JsonDocument.Parse(JsonSerializer.Serialize(output, outputType, options));
            var preserved = (List<string>)contract.GetMethod("PreserveUnavailableCards").Invoke(null, new object[] { output, pinned, evidence });
            Require(preserved.OrderBy(x => x).SequenceEqual(new[] { "other", "self" }) &&
                contract.GetMethod("ValidationError").Invoke(null, new object[] { output, pinned, evidence }) == null,
                "程序保留self/other后，真实relation和expression_habit通过原引用及长度校验");
            using var projected = JsonDocument.Parse(JsonSerializer.Serialize(output, outputType, options));
            foreach (var card in projected.RootElement.GetProperty("cards").EnumerateArray())
            {
                var source = originalCards.EnumerateArray().Single(x => x.GetProperty("slot").GetString() == card.GetProperty("slot").GetString());
                Require(source.GetProperty("body").GetString() == card.GetProperty("body").GetString() &&
                    source.GetProperty("cognition_ids").EnumerateArray().Select(x => x.GetString()).SequenceEqual(
                        card.GetProperty("cognition_ids").EnumerateArray().Select(x => x.GetString())), "有效摘要正文与引用完整不变");
            }
            foreach (var field in original.RootElement.EnumerateObject().Where(x => x.Name.StartsWith("inner_")))
                Require(JsonSerializer.Serialize(beforeProjection.RootElement.GetProperty(field.Name)) == JsonSerializer.Serialize(projected.RootElement.GetProperty(field.Name)),
                    "本次真实内心输出保持不变");
            count++;
        }
        Require(count == 2, "读取两份最新真实失败日终响应");
        Console.WriteLine("Day card scope dump checks passed: both real failures reproduced; only eligible targets retained with intact bodies/references/inner state, no model calls or production writes.");
    }

    private static void RunDayCardLengthDumpCheck(string assemblyPath, string directory)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var contract = assembly.GetType("TraceSoul2.Migrate.DayCardReviewContract", true);
        var outputType = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts+DayCardReviewOutputData", true);
        var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true };
        var count = 0;
        foreach (var file in Directory.GetFiles(directory, "20261002*-response.txt").OrderBy(x => x))
        {
            var raw = File.ReadAllText(file);
            if (raw.StartsWith("http=")) raw = raw.Substring(raw.IndexOf('\n') + 1);
            using var original = JsonDocument.Parse(raw);
            if (!original.RootElement.TryGetProperty("cards", out var outputCards)) continue;
            using var request = JsonDocument.Parse(File.ReadAllText(file.Replace("-response.txt", "-request.json")));
            var prompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            var marker = prompt.Contains("【成长摘要与可参考的认识，按 slot 分组】")
                ? "【成长摘要与可参考的认识，按 slot 分组】" : "【本次可更新的身份卡与依据，按 slot 分组】";
            var groupStart = prompt.IndexOf(marker, StringComparison.Ordinal);
            if (groupStart < 0) continue; // 独立精炼响应没有认知依据，不冒充完整日终输出。
            var jsonStart = prompt.IndexOf("\n[", groupStart, StringComparison.Ordinal);
            var pinnedMarker = marker.StartsWith("【成长") ? "【保留原文的本人设定】" : "【本人固定，不能覆盖的卡】";
            var groupEnd = prompt.IndexOf(pinnedMarker, jsonStart, StringComparison.Ordinal);
            using var groups = JsonDocument.Parse(prompt.Substring(jsonStart, groupEnd - jsonStart).Trim());
            var evidence = groups.RootElement.EnumerateArray().SelectMany(g =>
                JsonSerializer.Deserialize<List<CognitionSliceRecord>>(g.GetProperty("cognitions").GetRawText(), options)).ToList();
            var slots = prompt.Substring(groupEnd + pinnedMarker.Length).TrimStart().Split('\n')[0]
                .Split(',').Select(x => new IdentityCardRecord { Slot = x.Trim(), Pinned = true }).ToList();
            var output = JsonSerializer.Deserialize(raw, outputType, options);
            Require(contract.GetMethod("ValidationError").Invoke(null, new object[] { output, slots, evidence }) == null,
                "真实完整摘要通过长度、结构、固定卡及依据校验");
            var before = JsonSerializer.Serialize(output, outputType, options);
            var llm = new AgentSequenceLlm();
            ((Task)contract.GetMethod("FitBodiesAsync").Invoke(null,
                new object[] { llm, output, slots, evidence, System.Threading.CancellationToken.None })).GetAwaiter().GetResult();
            Require(JsonSerializer.Serialize(output, outputType, options) == before && llm.Requests.Count == 0,
                "真实略超目标的摘要完整保留，无额外调用，其他卡、引用和内心原样保留");
            count++;
        }
        Require(count > 0, "至少核对一份带完整认知依据的真实日终响应");
        Console.WriteLine("Day card length dump checks passed: actual review outputs accepted intact without fitting calls; no private text printed or runtime database modified.");
    }

    private static void RunDayCardDumpCheck(string assemblyPath, string directory)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var contract = assembly.GetType("TraceSoul2.Migrate.DayCardReviewContract", true);
        var outputType = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts+DayCardReviewOutputData", true);
        var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true };
        var checkedCount = 0;
        foreach (var requestPath in Directory.GetFiles(directory, "20261001-04*-request.json").OrderBy(x => x))
        {
            using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
            var prompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            const string marker = "【可用于摘要的认知与来源】\n";
            var start = prompt.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) continue;
            var evidenceText = prompt.Substring(start + marker.Length).Split("\n【", 2)[0];
            var evidence = JsonSerializer.Deserialize<List<CognitionSliceRecord>>(evidenceText, options);
            const string pinnedMarker = "【本人固定，不能覆盖的卡】\n";
            var pinned = prompt.Substring(prompt.IndexOf(pinnedMarker, StringComparison.Ordinal) + pinnedMarker.Length).Split('\n')[0]
                .Split(',').Select(slot => new IdentityCardRecord { Slot = slot, Pinned = true }).ToList();
            var raw = (string)typeof(DeepSeekStructuredOutputLogic).GetMethod("StripCodeFence", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { File.ReadAllText(requestPath.Replace("-request.json", "-response.txt")) });
            var output = JsonSerializer.Deserialize(raw, outputType, options);
            var error = (string)contract.GetMethod("ValidationError").Invoke(null, new[] { output, pinned, evidence });
            Require(error?.Contains("$.cards[1].slot") == true && error.Contains("重复"), "真实失败响应必须识别为重复卡");
            checkedCount++;
        }
        Require(checkedCount == 2, "只读核对两份真实失败的日终卡片输出");
        Console.WriteLine("Day card dump check passed: both original outputs rejected with exact duplicate-slot diagnosis; no source text printed or database modified.");
    }
}
