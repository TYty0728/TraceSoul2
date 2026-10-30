using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Util;
using TraceSoul2.Host;
using System.Net;
using System.Net.Http;

internal static partial class Program
{
    private sealed class DailyFixture : IDisposable
    {
        public readonly string DirectoryPath, BrainPath;
        public readonly Assembly Assembly;
        public object Context, ActualStore, Migration;
        public IMemoryStore Store => (IMemoryStore)ActualStore;
        public ICognitionGraphStore Graph => (ICognitionGraphStore)ActualStore;
        public const string Day = "2020-08-20", Root = "tracesoul2";
        private readonly bool ownsDirectory;
        public DailyFixture(Assembly assembly, string directory = null)
        {
            Assembly = assembly;
            ownsDirectory = directory == null;
            DirectoryPath = directory ?? Path.Combine(Path.GetTempPath(), "four-step-review-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath); BrainPath = Path.Combine(DirectoryPath, "brain.db"); Open();
        }
        private void Open()
        {
            var type = Assembly.GetType("TraceSoul2.Migrate.MigrationContext", true);
            Context = Activator.CreateInstance(type);
            ActualStore = Activator.CreateInstance(Assembly.GetType("TraceSoul2.Manager.SqliteMemoryManager", true), BrainPath);
            Migration = Activator.CreateInstance(Assembly.GetType("TraceSoul2.Migrate.MigrationDb", true),
                Path.Combine(DirectoryPath, "migration.db"), BrainPath, ActualStore);
            type.GetProperty("Store").SetValue(Context, ActualStore);
            type.GetProperty("Migration").SetValue(Context, Migration);
            type.GetProperty("CognitionEncoder").SetValue(Context, new BagOfCharsVectorEncoder());
            type.GetProperty("Providers").SetValue(Context, Activator.CreateInstance(
                Assembly.GetType("TraceSoul2.Host.LlmProviderStore", true), Path.Combine(DirectoryPath, "providers.json")));
            if (!Store.LoadPairIdentity().IsComplete) Store.SavePairIdentity("本人", "同伴", "称呼");
            Store.LoadIdentityCards(Root);
        }
        public void Reopen() { ((IDisposable)Context).Dispose(); Open(); }
        public object StoreCall(string name, params object[] args) => ActualStore.GetType().GetMethod(name).Invoke(ActualStore, args);
        public object DbCall(string name, params object[] args) => Migration.GetType().GetMethod(name).Invoke(Migration, args);
        public List<MomentRecord> Seed(int count, string day = Day, string visibility = "private", int offset = 0)
        {
            var start = MemoryDayLogic.StartOf(day).AddHours(2).ToUnixTimeMilliseconds();
            var items = Enumerable.Range(offset, count).Select(i => new MomentRecord {
                Id = day + "-" + visibility + "-" + i, ConversationId = visibility == "public" ? Root + ":environment:room" : Root,
                Role = "user", Content = i == 0 ? "选中依据：早餐想吃面包。" : "未选原句标记" + i,
                MemoryVisibility = visibility, Realm = TraceRealmValues.ExternalWorld, EvidenceType = EvidenceTypeValues.UserReported,
                CreatedUnixMs = start + i }).ToList();
            foreach (var item in items) Store.SaveMoment(item);
            return items;
        }
        public void Run(ILlmClient llm, string day = Day)
        {
            Context.GetType().GetProperty("Llm").SetValue(Context, llm);
            ((Task)Assembly.GetType("TraceSoul2.Migrate.DayBuilder", true).GetMethod("RunAsync")
                .Invoke(null, new object[] { Context, new[] { "--day", day } })).GetAwaiter().GetResult();
        }
        public JsonDocument State(string day = Day) => JsonDocument.Parse(Store.LoadPluginDocument("daily-review", day));
        public int Count(string table) { using var db = new SQLite.SQLiteConnection(BrainPath); return db.ExecuteScalar<int>("SELECT COUNT(*) FROM " + table); }
        public void Dispose() { ((IDisposable)Context).Dispose(); if (ownsDirectory) Directory.Delete(DirectoryPath, true); }
    }
    private const string DailyEvents = "{\"events\":[{\"summary\":\"早餐想吃面包\",\"source_refs\":[1]},{\"summary\":\"一般闲谈\",\"source_refs\":[2]}]}";
    private const string DailySelection = "{\"selected_refs\":[1]}";
    private const string DailyCognition = "{\"cognitions\":[{\"operation\":\"create\",\"target_ref\":0,\"summary\":\"他早餐想吃面包，选择时应尊重当天意愿。\",\"identity_slot\":\"other\",\"domains\":[\"user\"],\"source_refs\":[1],\"scope\":\"早餐选择\",\"exceptions\":\"意愿变化后重新确认\"}]}";
    private const string DailyRuntime = "{\"cards\":[{\"slot\":\"other\",\"body\":\"他早餐想吃面包，具体选择随当天意愿变化。\",\"cognition_refs\":[1]}],\"runtime\":{\"narrative\":\"听见他对早餐的想法。\",\"mood\":\"平静\",\"relationship_lens\":\"尊重具体意愿\"}}";
    private const string EmptyDailyRuntime = "{\"cards\":[],\"runtime\":{\"narrative\":\"\",\"mood\":\"\",\"relationship_lens\":\"\"}}";
    private static string DailyReply(List<DeepSeekMessageData> messages)
    {
        var title = messages[0].content;
        if (title.Contains("1/4")) return DailyEvents;
        if (title.Contains("2/4")) return DailySelection;
        if (title.Contains("3/4")) return DailyCognition;
        if (title.Contains("4/4")) return DailyRuntime;
        throw new InvalidOperationException("Unexpected review stage");
    }
    private static bool DailyRejects(Action action)
    {
        try { action(); return false; }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { return true; }
        catch (InvalidOperationException) { return true; }
    }
    private static void RunDailyReviewPipelineChecks(Assembly assembly)
    {
        using (var f = new DailyFixture(assembly))
        {
            var moments = f.Seed(2);
            var llm = new SelectionProbeLlm { Reply = DailyReply }; f.Run(llm);
            Require(llm.Requests.Count == 4 && Enumerable.Range(1, 4).All(i => llm.Requests[i - 1].Contains(i + "/4")),
                "实际日复盘严格提取、挑选、认知、runtime四阶段，无细节扩写或全量回望");
            Require(llm.Requests[2].Contains(moments[0].Content) && !llm.Requests[2].Contains(moments[1].Content) &&
                !llm.Requests.Any(x => x.Contains(moments[0].Id)), "认知只读取入选原句，模型引用短编号不复制数据库ID");
            Require(f.Count("event_entries") == 2 && f.Graph.GetCognitionNodes(20).Count == 1 &&
                ((List<MomentRecord>)f.StoreCall("GetEvidenceMoments", moments.Select(m => m.Id))).All(m => m.MemoryStatus == "built") &&
                f.Store.LoadIdentityCards(DailyFixture.Root).Single(c => c.Slot == "other").Body.Contains("面包"),
                "事件、选择依据、认知、身份卡与来源完成标记均实际入库");
            using var state = f.State(); Require(state.RootElement.GetProperty("Done").GetBoolean(), "四步提交后完成");
            var empty = new AgentSequenceLlm(); f.Reopen(); f.Run(empty);
            Require(empty.Requests.Count == 0 && f.Count("event_entries") == 2 && f.Graph.GetCognitionNodes(20).Count == 1,
                "完成后重启和重跑零模型调用、零重复事件和认知");
        }
        // Fail before each successive stage, reopen both SQLite connections and resume the real entry point.
        var responses = new[] { DailyEvents, DailySelection, DailyCognition, DailyRuntime };
        for (var stop = 1; stop <= 3; stop++)
        {
            using var f = new DailyFixture(assembly); f.Seed(2);
            var interrupted = new AgentSequenceLlm(responses.Take(stop).ToArray());
            Require(DailyRejects(() => f.Run(interrupted)), "注入阶段中断必须退出");
            var entries = f.Count("event_entries"); f.Reopen();
            var resumed = new SelectionProbeLlm { Reply = DailyReply }; f.Run(resumed);
            Require(resumed.Requests.Count == 4 - stop && resumed.Requests[0].Contains((stop + 1) + "/4") &&
                f.Count("event_entries") == entries && f.Graph.GetCognitionNodes(20).Count == 1,
                "中断后只补剩余阶段，不重复事件或已提交认知，阶段=" + stop);
        }
        using (var f = new DailyFixture(assembly))
        {
            f.Seed(250); var seen = new HashSet<string>();
            var llm = new SelectionProbeLlm { Reply = messages => {
                if (!messages[0].content.Contains("1/4")) throw new InvalidOperationException("Empty events need no later model");
                using var input = JsonDocument.Parse(messages[1].content);
                foreach (var source in input.RootElement.GetProperty("conversation").EnumerateArray()) seen.Add(source.GetProperty("text").GetString());
                return "{\"events\":[]}";
            } };
            f.Run(llm); Require(seen.Count == 250 && llm.Requests.Count == 6 && f.Count("event_entries") == 0,
                "250条原文分段完整覆盖，没有事件时不额外生成挑选、认知或runtime");
        }
        using (var f = new DailyFixture(assembly))
        {
            var sources = f.Seed(49);
            var interrupted = new AgentSequenceLlm(DailyEvents); Require(DailyRejects(() => f.Run(interrupted)), "提取批间中断");
            f.Reopen(); var last = new AgentSequenceLlm("{\"events\":[{\"summary\":\"最后一条事件\",\"source_refs\":[1]}]}", "{\"selected_refs\":[]}");
            f.Run(last); Require(last.Requests.Count == 2 && last.Requests[0].Contains(sources[48].Content) &&
                !last.Requests[0].Contains(sources[0].Content) && f.Count("event_entries") == 3,
                "提取逐段检查点跨重启续做，最后一批编号1映射新的真实来源");
            var late = f.Seed(1, offset: 49);
            var added = new AgentSequenceLlm("{\"events\":[]}", "{\"selected_refs\":[]}"); f.Run(added);
            Require(added.Requests.Count == 1 && added.Requests[0].Contains(late[0].Content) && f.Count("event_entries") == 3,
                "完成日迟到对话只提取新增来源，候选未改变则复用选择，不重复已完成事件");
        }
        using (var f = new DailyFixture(assembly))
        {
            f.Seed(2);
            var invalid = new AgentSequenceLlm("{\"events\":[{\"summary\":\"越界\",\"source_refs\":[3]}]}",
                "{\"events\":[{\"summary\":\"重复\",\"source_refs\":[1,1]}]}");
            Require(DailyRejects(() => f.Run(invalid)) && invalid.Requests.Count == 2 && f.Count("event_entries") == 0 &&
                invalid.Requests[1].Contains("source_refs[0]=3"), "非法来源最多纠正一次，失败不入库且反馈定位到字段");
            f.Run(new AgentSequenceLlm(DailyEvents, "{\"selected_refs\":[]}"));
            Require(f.Graph.GetCognitionNodes(10).Count == 0, "允许没有值得沉淀的事件，不强制产出认知");
        }
        using (var f = new DailyFixture(assembly))
        {
            f.Seed(2);
            using (var db = new SQLite.SQLiteConnection(f.BrainPath))
                db.Execute("CREATE TRIGGER inject_review_failure BEFORE INSERT ON plugin_documents WHEN NEW.PluginId='daily-review' AND json_array_length(json_extract(NEW.Json,'$.Events'))>0 BEGIN SELECT RAISE(ABORT,'injected atomic failure'); END");
            try { f.Run(new AgentSequenceLlm(DailyEvents)); } catch (Exception) { }
            Require(f.Count("event_entries") == 0 && f.Count("event_indexes") == 0, "检查点写入失败必须同时回滚事件和索引");
            using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute("DROP TRIGGER inject_review_failure");
            f.Reopen(); f.Run(new SelectionProbeLlm { Reply = DailyReply });
            Require(f.Count("event_entries") == 2, "事务失败重试只入库一次");
        }
        RunDailyRuntimeConflictCheck(assembly);
        RunDailyCommitFailureChecks(assembly);
        RunDailyValidationChecks(assembly);
        RunDailyPrivacyChecks(assembly);
        RunDailyLargeSelectionChecks(assembly);
        RunDailyRecallCheck(assembly);
        RunDailyBudgetChecks(assembly);
        RunReviewTransportChecks(assembly);
        RunDailyProcessRecovery(assembly);
        Console.WriteLine("Four-step daily review passed: complete day, batching, restart, atomic rollback, live runtime, call limit and bounded network recovery.");
    }

    private static void RunDailyRuntimeConflictCheck(Assembly assembly)
    {
        using var f = new DailyFixture(assembly); f.Seed(2);
        var runtime = assembly.GetType("TraceSoul2.Logic.SubjectRuntimeLogic", true);
        var llm = new SelectionProbeLlm { Reply = messages => {
            if (messages[0].content.Contains("4/4"))
            {
                var subject = runtime.GetMethod("Read").Invoke(null, new[] { f.ActualStore, DailyFixture.Root });
                var revision = (long)subject.GetType().GetProperty("Revision").GetValue(subject);
                var live = (InnerRuntimeData)f.StoreCall("LoadOrCreateInnerRuntime", DailyFixture.Root);
                var changes = new InnerRuntimeWriteData { narrative = "刚刚聊天的新状态" };
                var next = InnerLifeLogic.Reduce(live, changes, "live-source", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Require((bool)runtime.GetMethod("CommitReview").Invoke(null, new object[] { f.ActualStore, DailyFixture.Root, revision, next, changes }), "注入聊天状态提交");
            }
            return DailyReply(messages);
        } };
        f.Run(llm);
        Require(((InnerRuntimeData)f.StoreCall("LoadOrCreateInnerRuntime", DailyFixture.Root)).Narrative == "刚刚聊天的新状态",
            "复盘期间实时聊天更新runtime，旧复盘不覆盖较新状态");
    }
    private static void RunDailyCommitFailureChecks(Assembly assembly)
    {
        foreach (var stage in new[] { 3, 4 })
        {
            using var f = new DailyFixture(assembly); f.Seed(2);
            var beforeCards = TraceJson.ToJson(f.Store.LoadIdentityCards(DailyFixture.Root));
            var beforeInner = (InnerRuntimeData)f.StoreCall("LoadOrCreateInnerRuntime", DailyFixture.Root);
            using (var db = new SQLite.SQLiteConnection(f.BrainPath))
                db.Execute("CREATE TRIGGER fail_commit BEFORE INSERT ON plugin_documents WHEN NEW.PluginId='daily-review' AND " +
                    (stage == 3 ? "json_array_length(json_extract(NEW.Json,'$.CognitionSources'))>0" : "json_extract(NEW.Json,'$.RuntimeDone')=1") +
                    " BEGIN SELECT RAISE(ABORT,'injected commit failure'); END");
            try { f.Run(new SelectionProbeLlm { Reply = DailyReply }); } catch (Exception) { }
            Require(f.Graph.GetCognitionNodes(20).Count == (stage == 3 ? 0 : 1) &&
                TraceJson.ToJson(f.Store.LoadIdentityCards(DailyFixture.Root)) == beforeCards &&
                ((InnerRuntimeData)f.StoreCall("LoadOrCreateInnerRuntime", DailyFixture.Root)).Narrative == beforeInner.Narrative,
                "认知/runtime检查点失败与数据写入一起回滚，阶段=" + stage);
            using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute("DROP TRIGGER fail_commit");
            f.Reopen(); var resume = new SelectionProbeLlm { Reply = DailyReply }; f.Run(resume);
            Require(resume.Requests.Count == 5 - stage && f.Graph.GetCognitionNodes(20).Count == 1,
                "提交中断后只补失败阶段，阶段=" + stage);
        }
    }
    private static void RunDailyValidationChecks(Assembly assembly)
    {
        var cases = new[] {
            new[] { DailyEvents, "{\"selected_refs\":[3]}", "{\"selected_refs\":[3]}" },
            new[] { DailyEvents, DailySelection, DailyCognition.Replace("\"source_refs\":[1]", "\"source_refs\":[2]"),
                DailyCognition.Replace("\"source_refs\":[1]", "\"source_refs\":[2]") },
            new[] { DailyEvents, DailySelection, DailyCognition, DailyRuntime.Replace("\"slot\":\"other\"", "\"slot\":\"self\""),
                DailyRuntime.Replace("\"slot\":\"other\"", "\"slot\":\"self\"") },
            new[] { DailyEvents, DailySelection, DailyCognition, DailyRuntime.Replace("\"slot\":\"other\"", "\"slot\":\"personality\""),
                DailyRuntime.Replace("\"slot\":\"other\"", "\"slot\":\"personality\"") }
        };
        foreach (var outputs in cases)
        {
            using var f = new DailyFixture(assembly); f.Seed(2);
            var cards = TraceJson.ToJson(f.Store.LoadIdentityCards(DailyFixture.Root));
            var llm = new AgentSequenceLlm(outputs);
            Require(DailyRejects(() => f.Run(llm)) && llm.Requests.Count == outputs.Length &&
                f.Graph.GetCognitionNodes(20).Count == (outputs.Length == 5 ? 1 : 0) &&
                TraceJson.ToJson(f.Store.LoadIdentityCards(DailyFixture.Root)) == cards,
                "拒绝越界选择、未入选来源、跨slot依据及固定人格改写；不吞错完成");
        }
        using (var f = new DailyFixture(assembly))
        {
            var source = f.Seed(2)[0];
            var race = new SelectionProbeLlm { Reply = _ => {
                using var db = new SQLite.SQLiteConnection(f.BrainPath);
                db.Execute("UPDATE moments SET Content=? WHERE Id=?", "原文已被修订", source.Id);
                return DailyEvents;
            } };
            Require(DailyRejects(() => f.Run(race)) && f.Count("event_entries") == 0,
                "生成期间来源改变，旧快照的事件不得提交");
            f.Reopen(); var noCalls = new AgentSequenceLlm();
            Require(DailyRejects(() => f.Run(noCalls)) && noCalls.Requests.Count == 0,
                "来源不匹配在调用模型前发现，不反复花费请求");
        }
    }
    private static void RunDailyPrivacyChecks(Assembly assembly)
    {
        using var f = new DailyFixture(assembly); var sources = f.Seed(2);
        var publicSource = f.Seed(1, visibility: "public", offset: 100).Single();
        var llm = new SelectionProbeLlm { Reply = messages => {
            var text = string.Join("\n", messages.Select(x => x.content));
            if (messages[0].content.Contains("当前环境不是两人私密相处"))
            {
                Require(!text.Contains(sources[0].Content) && text.Contains(publicSource.Content), "公开整理不含私聊原文");
                return "{\"cognitions\":[]}";
            }
            Require(!text.Contains(publicSource.Content), "私密四阶段不读取公开原文");
            return DailyReply(messages);
        } };
        f.Run(llm);
        Require(llm.Requests.Count == 5 && f.Count("event_entries") == 2, "公开经历独立消费，不混入私密事件或runtime");
        var late = f.Seed(1, visibility: "public", offset: 101).Single();
        var onlyPublic = new AgentSequenceLlm("{\"cognitions\":[]}"); f.Run(onlyPublic);
        Require(onlyPublic.Requests.Count == 1 && onlyPublic.Requests[0].Contains(late.Content) && f.Count("event_entries") == 2,
            "迟到公开消息不被私密完成标记吞掉，不重做四步");
        using var empty = new DailyFixture(assembly);
        var none = new AgentSequenceLlm(); empty.Run(none);
        Require(none.Requests.Count == 0 && empty.Count("event_entries") == 0, "空日零调用，不编造经历或心境");
    }
    private static void RunDailyLargeSelectionChecks(Assembly assembly)
    {
        using var f = new DailyFixture(assembly); f.Seed(250); var segment = 0;
        var llm = new SelectionProbeLlm { Reply = messages => {
            var title = messages[0].content;
            if (title.Contains("1/4"))
            {
                var batch = segment++;
                return TraceJson.ToJson(new { events = Enumerable.Range(0, batch == 5 ? 8 : 9).Select(i => new {
                    summary = "事件" + batch + "-" + i + new string('事', 490), source_refs = new[] { 1 } }) });
            }
            if (title.Contains("2/4")) return DailySelection;
            if (title.Contains("3/4")) return "{\"cognitions\":[]}";
            if (title.Contains("4/4")) return EmptyDailyRuntime;
            throw new InvalidOperationException("unexpected stage");
        } };
        f.Run(llm);
        Require(f.Count("event_entries") == 53 && llm.Requests.Count == 11 &&
            llm.Requests.Where(p => p.Contains("【2/4")).All(p => p.Length < 18000),
            "53件长摘要分批挑选、全局收敛，250条对话共11次调用，无逐事件扩写");
    }
    private static void RunDailyRecallCheck(Assembly assembly)
    {
        using var f = new DailyFixture(assembly); f.Seed(2);
        var model = Path.Combine(Path.GetDirectoryName(assembly.Location), "Models", "BgeSmallZh");
        var encoder = Activator.CreateInstance(assembly.GetType("TraceSoul2.Tools.Memory.OnnxBgeEncoder", true),
            Path.Combine(model, "bge-small-zh-v1.5.onnx"), Path.Combine(model, "vocab.txt"), 128);
        var vectors = Activator.CreateInstance(assembly.GetType("TraceSoul2.Manager.SqliteVectorManager", true), Path.Combine(f.DirectoryPath, "vectors.db"));
        f.Context.GetType().GetField("encoder", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Context, encoder);
        f.Context.GetType().GetProperty("Vectors").SetValue(f.Context, vectors);
        f.Run(new SelectionProbeLlm { Reply = DailyReply });
        var recall = (IMemoryRecallEngine)Activator.CreateInstance(assembly.GetType("TraceSoul2.Tools.Memory.MemoryRecallEngine", true), encoder, vectors);
        using var db = new SQLite.SQLiteConnection(f.BrainPath);
        var entries = db.Table<EventEntryRecord>().ToList();
        var hits = recall.Search("早餐想吃什么", entries.Select(e => e.Id).ToList(), 1);
        Require((int)vectors.GetType().GetMethod("CountEntryEmbeddings").Invoke(vectors, null) == 2 &&
            hits.Count == 1 && entries.Single(e => e.Id == hits[0].EntryId).Summary.Contains("面包"),
            "新事件在实际BGE向量库中可召回，不能只有入库成功却无法被聊天使用");
    }
    private static void RunDailyBudgetChecks(Assembly assembly)
    {
        var inner = new SelectionProbeLlm { Reply = _ => "{}" };
        var type = assembly.GetType("TraceSoul2.Migrate.ReviewBudgetClient", true);
        var client = (ILlmClient)Activator.CreateInstance(type, inner, null, 2);
        var messages = new List<DeepSeekMessageData> { new("system", "【1/4 提取事件】") };
        Require(client.CompleteJsonAsync(messages).GetAwaiter().GetResult() == "{}" &&
            client.CompleteJsonAsync(messages).GetAwaiter().GetResult() == "{}", "请求次数范围内正常接收结果");
        Require(DailyRejects(() => client.CompleteJsonAsync(messages).GetAwaiter().GetResult()) && inner.Requests.Count == 2,
            "请求次数上限只阻止下一次派发，不能丢弃已返回结果");
        using var f = new DailyFixture(assembly); f.Seed(2);
        var yielding = new SelectionProbeLlm { Reply = request => request[0].content.Contains("1/4") ? DailyEvents :
            throw (Exception)Activator.CreateInstance(assembly.GetType("TraceSoul2.Migrate.ReviewBudgetYieldException", true)) };
        f.Run(yielding);
        using var db = new SQLite.SQLiteConnection(Path.Combine(f.DirectoryPath, "migration.db"));
        Require(db.ExecuteScalar<string>("SELECT Status FROM migration_review_state") == "deferred" &&
            db.ExecuteScalar<int>("SELECT RetryCount FROM migration_review_state") == 0 &&
            db.ExecuteScalar<long>("SELECT NextRetryUnixMs FROM migration_review_state") > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() &&
            f.Count("event_entries") == 2, "实际入口预算让出保存进度、排期续做，不消耗错误重试计数");
        var resumed = new SelectionProbeLlm { Reply = DailyReply }; f.Run(resumed);
        Require(resumed.Requests.Count == 3 && db.ExecuteScalar<string>("SELECT Status FROM migration_review_state") == "done",
            "预算续做只从挑选开始，完成后清除延期状态");
    }
    private static void RunReviewTransportChecks(Assembly assembly)
    {
        var now = TimeSpan.Zero;
        var waits = new List<TimeSpan>();
        Func<TimeSpan, CancellationToken, Task> wait = (duration, token) => {
            token.ThrowIfCancellationRequested(); waits.Add(duration); now += duration; return Task.CompletedTask;
        };
        var messages = new List<DeepSeekMessageData> { new("user", "synthetic") };
        ILlmClient Wrap(ILlmClient inner, Func<TimeSpan, CancellationToken, Task> delay = null)
            => (ILlmClient)Activator.CreateInstance(assembly.GetType("TraceSoul2.Host.ReviewTransportClient", true),
                inner, (Func<TimeSpan>)(() => now), delay ?? wait, null);
        var calls = 0;
        var probe = new SelectionProbeLlm { Reply = _ => { calls++; throw new HttpRequestException("synthetic disconnect"); } };
        var client = Wrap(probe);
        InvalidOperationException exhaustedError = null;
        try { client.CompleteJsonAsync(messages).GetAwaiter().GetResult(); }
        catch (InvalidOperationException error) { exhaustedError = error; }
        Require(exhaustedError != null && FailureProtection.SafeReason(exhaustedError).Contains("手动恢复") && calls == 3 &&
            waits.SequenceEqual(new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10) }), "断网只补试两次，按5/10分钟冷却");
        Require(DailyRejects(() => client.CompleteJsonAsync(messages).GetAwaiter().GetResult()) && calls == 3,
            "耗尽后相同客户端再次调用也不发请求");

        now = TimeSpan.Zero; waits.Clear(); calls = 0;
        probe = new SelectionProbeLlm { Reply = _ => ++calls % 2 == 1 ? throw new TimeoutException() : "{}" };
        client = Wrap(probe);
        Require(client.CompleteJsonAsync(messages).GetAwaiter().GetResult() == "{}" &&
            client.CompleteJsonAsync(messages).GetAwaiter().GetResult() == "{}" &&
            DailyRejects(() => client.CompleteJsonAsync(messages).GetAwaiter().GetResult()) && calls == 5,
            "各步骤共用整轮补试上限，中间成功不重置次数");

        now = TimeSpan.Zero; calls = 0;
        client = Wrap(new SelectionProbeLlm { Reply = _ => { calls++; throw new HttpRequestException(); } },
            (_, _) => { now += TimeSpan.FromMinutes(31); return Task.CompletedTask; });
        Require(DailyRejects(() => client.CompleteJsonAsync(messages).GetAwaiter().GetResult()) && calls == 1,
            "睡眠或调度延迟跨过30分钟窗口后不再补发");

        now = TimeSpan.Zero; calls = 0;
        client = Wrap(new SelectionProbeLlm { Reply = _ => {
            if (++calls == 1) throw new HttpRequestException();
            now += TimeSpan.FromHours(1); return "{}";
        } });
        Require(client.CompleteJsonAsync(messages).GetAwaiter().GetResult() == "{}", "在途成功跨过故障窗口仍保留结果");
        Require(client.CompleteJsonAsync(messages).GetAwaiter().GetResult() == "{}", "正常长复盘不受总时长窗口限制");

        // Retryable HTTP responses exercise the real provider client with its inner
        // retry disabled; the outer policy must be the only source of extra POSTs.
        using (var server = new FailureHttpServer(503, "synthetic outage"))
        {
            now = TimeSpan.Zero; waits.Clear();
            client = Wrap(new DeepSeekClientManager(new DeepSeekConfigData {
                ApiKey = "test", BaseUrl = server.Url, Model = "test", TransientErrorRetries = 6
            }, singleAttempt: true));
            Require(DailyRejects(() => client.CompleteJsonAsync(messages).GetAwaiter().GetResult()) &&
                server.Requests.Count == 3 && waits.Count == 2, "真实HTTP 503只有三次POST，没有供应商内层重试相乘");
        }

        foreach (var error in new Exception[] { new HttpRequestException("auth", null, HttpStatusCode.Unauthorized),
            new InvalidOperationException("API 402"), new InvalidOperationException("API 502: codex_failed_check_login_or_quota"),
            new InvalidOperationException("API 503: codex_executable_not_found"), new InvalidOperationException("API 502: codex_invalid_json"),
            new InvalidOperationException("source_refs越界"), new JsonException("invalid JSON"), new OperationCanceledException() })
        {
            calls = 0; waits.Clear();
            client = Wrap(new SelectionProbeLlm { Reply = _ => { calls++; throw error; } });
            try { client.CompleteJsonAsync(messages).GetAwaiter().GetResult(); throw new Exception("Expected failure"); }
            catch (Exception caught) when (ReferenceEquals(caught, error)) { }
            Require(calls == 1 && waits.Count == 0, "鉴权/额度/配置/内容错误及取消不进入网络补试");
        }
        using (var cancel = new CancellationTokenSource())
        {
            calls = 0;
            client = Wrap(new SelectionProbeLlm { Reply = _ => { calls++; throw new HttpRequestException(); } },
                (_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
            try { client.CompleteJsonAsync(messages, cancel.Token).GetAwaiter().GetResult(); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Require(calls == 1, "冷却等待可取消，取消后不发下一请求");
        }

        using var f = new DailyFixture(assembly); f.Seed(2);
        now = TimeSpan.Zero; calls = 0; waits.Clear();
        client = Wrap(new SelectionProbeLlm { Reply = request => {
            if (request[0].content.Contains("2/4") && ++calls == 1) throw new HttpRequestException();
            return DailyReply(request);
        } });
        f.Run(client);
        Require(f.Count("event_entries") == 2 && f.Graph.GetCognitionNodes(20).Count == 1 &&
            f.State().RootElement.GetProperty("Done").GetBoolean() && waits.Count == 1,
            "真实四步入口在挑选时断网后完成，不重复写入已提取事件与认知");

        using var failed = new DailyFixture(assembly); failed.Seed(2);
        now = TimeSpan.Zero; waits.Clear();
        client = Wrap(new SelectionProbeLlm { Reply = request => request[0].content.Contains("2/4")
            ? throw new HttpRequestException() : DailyReply(request) });
        Require(DailyRejects(() => failed.Run(client)), "真实入口持续断网会停止而不是延期无限补做");
        using (var db = new SQLite.SQLiteConnection(Path.Combine(failed.DirectoryPath, "migration.db")))
            Require(db.ExecuteScalar<string>("SELECT Status FROM migration_review_state") == "failed" &&
                db.ExecuteScalar<long>("SELECT NextRetryUnixMs FROM migration_review_state") == 0 &&
                failed.Count("event_entries") == 2 && waits.Count == 2, "失败不安排自动续做，事件检查点保留");
        failed.Reopen();
        var resumed = new SelectionProbeLlm { Reply = DailyReply }; failed.Run(resumed);
        Require(resumed.Requests.Count == 3 && !resumed.Requests.Any(x => x.Contains("1/4 提取事件")) &&
            failed.Count("event_entries") == 2 && failed.Graph.GetCognitionNodes(20).Count == 1,
            "手动恢复的新进程仅续做剩余步骤，不重复提取或增认知");
    }

    private static void RunDailyReviewPeer(string assemblyPath, string directory, string mode)
    {
        using var f = new DailyFixture(Assembly.LoadFrom(assemblyPath), directory);
        if (mode == "write") f.Seed(2);
        var llm = new SelectionProbeLlm { Reply = messages => {
            if (mode == "write" && messages[0].content.Contains("4/4"))
            {
                File.WriteAllText(Path.Combine(directory, "ready"), "cognition committed");
                Thread.Sleep(Timeout.Infinite); // parent kills this disposable test process here
            }
            return DailyReply(messages);
        } };
        f.Run(llm);
        Require(mode == "resume" && llm.Requests.Count == 1 && llm.Requests[0].Contains("4/4") &&
            f.Count("event_entries") == 2 && f.Graph.GetCognitionNodes(20).Count == 1,
            "进程被杀后新进程只更新runtime，事件和认知不重复");
    }
    private static void RunDailyProcessRecovery(Assembly assembly)
    {
        var directory = Path.Combine(Path.GetTempPath(), "daily-review-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process Start(string mode)
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { typeof(Program).Assembly.Location, "--daily-review-peer", assembly.Location, directory, mode })
                info.ArgumentList.Add(arg);
            return Process.Start(info);
        }
        try
        {
            using (var writer = Start("write"))
            {
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (!writer.HasExited && !File.Exists(Path.Combine(directory, "ready")) && DateTime.UtcNow < deadline) Thread.Sleep(25);
                    Require(File.Exists(Path.Combine(directory, "ready")), "真实复盘子进程已提交前三步");
                }
                finally { if (!writer.HasExited) writer.Kill(true); writer.WaitForExit(); }
            }
            using var reader = Start("resume");
            try { Require(reader.WaitForExit(20000) && reader.ExitCode == 0, "新进程恢复真实四步入口"); }
            finally { if (!reader.HasExited) { reader.Kill(true); reader.WaitForExit(); } }
        }
        finally { Directory.Delete(directory, true); }
    }
}
