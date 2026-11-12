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
        // Existing checks default to Version 3; later legacy contracts opt in, fresh days use the current version.
        public string ExistingEngine = "batch-v1";
        public int? ExistingVersion;
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
            // Compatibility tests explicitly seed an old checkpoint, rather than
            // enabling the retired per-item engine for newly created reviews.
            if (ExistingEngine != null && string.IsNullOrEmpty(Store.LoadPluginDocument("daily-review", day)))
            {
                var builder = Assembly.GetType("TraceSoul2.Migrate.DayBuilder", true);
                var state = Activator.CreateInstance(builder.GetNestedType("DailyReviewState"));
                var start = MemoryDayLogic.StartOf(day).ToUnixTimeMilliseconds();
                var pending = (List<MomentRecord>)DbCall("GetUnbuiltMomentsInRange", start, start + 86400000);
                var ids = pending.Select(m => m.Id).ToList();
                var stamp = (string)builder.GetMethod("SourceStamp", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { Context, ids });
                var fields = state.GetType();
                fields.GetField("Day").SetValue(state, day);
                fields.GetField("SourceIds").SetValue(state, ids);
                fields.GetField("CognitionEngine").SetValue(state, ExistingEngine);
                fields.GetField("Version").SetValue(state, ExistingVersion ?? (ExistingEngine == "batch-v1" ? 3 : ExistingEngine == "jev-v1" ? 2 : 1));
                if (ExistingVersion >= 5) fields.GetField("IdentityContext").SetValue(state,
                    builder.GetMethod("CaptureReviewIdentity", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new[] { Context }));
                fields.GetField("Stamp").SetValue(state, (ExistingEngine is "jev-v1" or "batch-v1" ? ExistingEngine + ":" : "") + stamp);
                Store.SavePluginDocument("daily-review", day, TraceJson.ToJson(state));
            }
            Context.GetType().GetProperty("Llm").SetValue(Context, llm);
            ((Task)Assembly.GetType("TraceSoul2.Migrate.DayBuilder", true).GetMethod("RunAsync")
                .Invoke(null, new object[] { Context, new[] { "--day", day } })).GetAwaiter().GetResult();
        }
        public JsonDocument State(string day = Day) => JsonDocument.Parse(Store.LoadPluginDocument("daily-review", day));
        public int Count(string table) { using var db = new SQLite.SQLiteConnection(BrainPath); return db.ExecuteScalar<int>("SELECT COUNT(*) FROM " + table); }
        public void Dispose() { ((IDisposable)Context).Dispose(); if (ownsDirectory) Directory.Delete(DirectoryPath, true); }
    }
    private const string DailyTopicFields = ",\"tag_refs\":[],\"new_tags\":[{\"name\":\"饮食意愿\",\"definition\":\"当下想吃什么及选择\",\"domain_ids\":[\"user\"],\"dimension_ids\":[\"goal\",\"scope\"],\"positive_examples\":[\"早餐想吃面包\"],\"negative_examples\":[\"吃饭时讨论数学\"]}]";
    private const string DailyEvents = "{\"events\":[{\"summary\":\"早餐想吃面包\",\"source_refs\":[1]" + DailyTopicFields + "},{\"summary\":\"一般闲谈\",\"source_refs\":[2]" + DailyTopicFields + "}]}";
    private const string DailySelection = "{\"selected_refs\":[1]}";
    private const string DailyCognition = "{\"cognitions\":[{\"operation\":\"create\",\"target_ref\":0,\"summary\":\"他早餐想吃面包，选择时应尊重当天意愿。\",\"identity_slot\":\"other\",\"domains\":[\"user\"],\"source_refs\":[1],\"tag_refs\":[1],\"scope\":\"早餐选择\",\"exceptions\":\"意愿变化后重新确认\"}]}";
    private const string DailyRuntime = "{\"cards\":[{\"slot\":\"other\",\"body\":\"他早餐想吃面包，具体选择随当天意愿变化。\",\"cognition_refs\":[1]}],\"runtime\":{\"narrative\":\"听见他对早餐的想法。\",\"mood\":\"平静\",\"relationship_lens\":\"尊重具体意愿\"}}";
    private const string EmptyDailyRuntime = "{\"cards\":[],\"runtime\":{\"narrative\":\"\",\"mood\":\"\",\"relationship_lens\":\"\"}}";
    private static List<JsonElement> ReadReviewTable(JsonElement root, string name, string columns)
    {
        var rows = root.GetProperty(name).EnumerateArray().ToList();
        if (!root.TryGetProperty(columns, out var schema)) return rows;
        var names = schema.EnumerateArray().Select(x => x.GetString()).ToArray();
        return rows.Select(row => JsonSerializer.SerializeToElement(names.Select((key, i) => (key, value: row[i].Clone()))
            .ToDictionary(x => x.key, x => x.value))).ToList();
    }
    private static List<JsonElement> ReadReviewSources(JsonElement root)
    {
        var rows = ReadReviewTable(root, "conversation", "conversation_columns");
        if (!root.TryGetProperty("source_profiles", out var sourceProfiles)) return rows;
        var profiles = sourceProfiles.EnumerateArray().ToDictionary(p => p.GetProperty("profile_ref").GetInt32());
        return rows.Select(row => {
            var fields = row.EnumerateObject().Where(p => p.Name != "profile_ref").ToDictionary(p => p.Name, p => p.Value.Clone());
            foreach (var p in profiles[row.GetProperty("profile_ref").GetInt32()].EnumerateObject())
                if (p.Name != "profile_ref") fields.Add(p.Name, p.Value.Clone());
            return JsonSerializer.SerializeToElement(fields);
        }).ToList();
    }
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
            var grownTopics = f.Store.GetActiveLifeTags();
            Require(grownTopics.Count == 1 && grownTopics[0].Label == "饮食意愿" &&
                f.Store.GetLifeTagRoutes(grownTopics[0].Id).Count == 3 &&
                f.Store.GetActiveEventIndexes().All(e => e.TagIds.Contains(grownTopics[0].Id)) &&
                f.Store.GetCognitionTagIds(f.Graph.GetCognitionNodes(20).Select(n => n.Id)).Values.All(ids => ids.Contains(grownTopics[0].Id)),
                "实际四步复盘新增多路主题，同名复用，事件和认知均挂接主题");
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
                foreach (var source in ReadReviewSources(input.RootElement)) seen.Add(source.GetProperty("text").GetString());
                return "{\"events\":[]}";
            } };
            f.Run(llm); Require(seen.Count == 250 && llm.Requests.Count == 1 && f.Count("event_entries") == 0,
                "250条原文一次完整覆盖，没有事件时不额外生成挑选、认知或runtime");
        }
        using (var f = new DailyFixture(assembly))
        {
            var sources = f.Seed(49);
            using (var db = new SQLite.SQLiteConnection(f.BrainPath))
                for (var i = 0; i < sources.Count; i++)
                {
                    sources[i].Content += new string(i == 48 ? '乙' : '甲', i == 48 ? 5000 : 2000);
                    db.Update(sources[i]);
                }
            var interrupted = new AgentSequenceLlm(DailyEvents); Require(DailyRejects(() => f.Run(interrupted)), "提取批间中断");
            f.Reopen(); var last = new AgentSequenceLlm("{\"events\":[{\"summary\":\"最后一条事件\",\"source_refs\":[1]" + DailyTopicFields + "}]}", "{\"selected_refs\":[]}");
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
                "{\"events\":[{\"summary\":\"仍越界\",\"source_refs\":[3]}]}");
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
            Require(f.Count("event_entries") == 0 && f.Count("event_indexes") == 0 && f.Store.GetActiveLifeTags().Count == 0,
                "检查点写入失败必须同时回滚事件、索引和新增主题");
            using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute("DROP TRIGGER inject_review_failure");
            f.Reopen(); f.Run(new SelectionProbeLlm { Reply = DailyReply });
            Require(f.Count("event_entries") == 2, "事务失败重试只入库一次");
        }
        RunDailyTopicGrowthChecks(assembly);
        RunEventEvidenceReviewChecks(assembly);
        RunSubjectiveReviewChecks(assembly);
        RunDailyReflectionChecks(assembly);
        RunJointReviewChecks(assembly);
        RunWholeDayReviewChecks(assembly);
        RunDailyRuntimeContractChecks(assembly);
        RunDailyTopicEvidenceChecks(assembly);
        RunDailyExistingSlotChecks(assembly);
        RunJevReviewChecks(assembly);
        RunDailyToolEvidenceCheck(assembly);
        RunDailyRuntimeConflictCheck(assembly);
        RunDailyRuntimeProseChecks(assembly);
        RunReviewPromptContractChecks(assembly);
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

    private static void RunWholeDayReviewChecks(Assembly assembly)
    {
        using (var f = new DailyFixture(assembly))
        {
            var sources = f.Seed(3);
            using (var db = new SQLite.SQLiteConnection(f.BrainPath))
            {
                sources[0].Content = "早餐\"面包\"\r\n第二行\\路径🙂"; sources[0].SourcePlatform = null;
                sources[1].Content = "早餐选择与预算"; sources[1].Realm = "reality"; sources[1].EvidenceType = "self_report";
                sources[2].Content = "早餐面包"; sources[2].ConversationId = "another-private";
                foreach (var source in sources) db.Update(source);
                db.Insert(new LifeTagRecord { Id = "ambiguous-a", Label = "早餐", Definition = "早餐面包的饮食意愿", Status = "active" });
                db.Insert(new LifeTagRecord { Id = "ambiguous-b", Label = "早餐", Definition = "早餐面包的消费预算", Status = "active" });
            }
            var llm = new SelectionProbeLlm { Reply = messages => {
                using var input = JsonDocument.Parse(messages[1].content);
                var rows = ReadReviewSources(input.RootElement);
                Require(rows.Select(r => r.GetProperty("text").GetString()).SequenceEqual(sources.Select(s => s.Content)) &&
                    rows[0].GetProperty("platform").ValueKind == JsonValueKind.Null &&
                    rows[1].GetProperty("evidence_type").GetString() == "self_report" &&
                    rows[2].GetProperty("conversation_ref").GetInt32() != rows[0].GetProperty("conversation_ref").GetInt32(),
                    "紧凑来源格式保留引号、换行、反斜线、emoji、空值、事实性质及会话边界");
                var topics = ReadReviewTable(input.RootElement, "topics", "topic_columns");
                var meanings = input.RootElement.GetProperty("topic_disambiguation").EnumerateArray().ToList();
                Require(topics.Count == 2 && topics.All(t => !t.TryGetProperty("definition", out _)) &&
                    meanings.Select(m => m.GetProperty("definition").GetString()).ToHashSet().SetEquals(new[] { "早餐面包的饮食意愿", "早餐面包的消费预算" }) &&
                    meanings.All(m => topics.Any(t => t.GetProperty("tag_ref").GetInt32() == m.GetProperty("tag_ref").GetInt32())),
                    "名称目录避免反复附完整定义，同名不同义保留可对照的定义及编号");
                return "{\"events\":[]}";
            } };
            f.Run(llm); Require(llm.Requests.Count == 1, "主题语义选用在提取环节内，不增加预筛模型请求");
        }
        using (var f = new DailyFixture(assembly))
        {
            var prior = new List<string>();
            for (var day = 20; day <= 22; day++)
            {
                var key = "2020-08-" + day;
                var marker = "独立日理解" + day;
                f.Seed(2, key);
                var llm = new SelectionProbeLlm { Reply = messages => {
                    var stage = messages[0].content;
                    using var input = JsonDocument.Parse(messages[1].content);
                    if (stage.Contains("3/4"))
                    {
                        Require(!input.RootElement.TryGetProperty("existing", out _) && prior.All(p => !messages[1].content.Contains(p)),
                            "连续三日新复盘不检索或注入昨日认知，existing字段也不回灌");
                        return DailyCognition.Replace("他早餐想吃面包，选择时应尊重当天意愿。", marker);
                    }
                    if (stage.Contains("4/4"))
                    {
                        Require(!input.RootElement.TryGetProperty("current", out _) &&
                            input.RootElement.GetProperty("cards").EnumerateArray().All(c => !c.TryGetProperty("body", out _)) &&
                            input.RootElement.GetProperty("cognitions").GetArrayLength() == 1 &&
                            prior.All(p => !messages[1].content.Contains(p)),
                            "状态生成仅包含当日认知和槽位固定标记，不重复旧卡、旧依据或上次状态");
                        return DailyRuntime.Replace("他早餐想吃面包，具体选择随当天意愿变化。", marker + "卡片")
                            .Replace("听见他对早餐的想法。", marker + "状态");
                    }
                    return DailyReply(messages);
                } };
                f.Run(llm, key);
                Require(llm.Requests.Count == 4 && f.Store.LoadIdentityCards(DailyFixture.Root).Single(c => c.Slot == "other").Body == marker + "卡片",
                    "每日四次，卡片替换为当日读视图，不串接昨日正文");
                prior.Add(marker);
            }
            Require(f.Graph.GetCognitionNodes(20).Count == 3, "历史认知仍留库，不以清理输入删除记忆");
        }
        using (var f = new DailyFixture(assembly))
        {
            f.Seed(2);
            var llm = new SelectionProbeLlm { Reply = messages => {
                if (messages[0].content.Contains("1/4")) return "{\"events\":[{\"summary\":\"双方确认早餐选择\",\"source_refs\":[1,1,2]}]}";
                if (messages[0].content.Contains("2/4")) return "{\"selected_refs\":[1,1]}";
                if (messages[0].content.Contains("3/4")) return DailyCognition.Replace(",\"tag_refs\":[1]", "").Replace("\"source_refs\":[1]", "\"source_refs\":[1,1]");
                return EmptyDailyRuntime;
            } };
            f.Run(llm);
            Require(llm.Requests.Count == 4 && f.Count("event_entries") == 1 && f.Graph.GetCognitionNodes(20).Count == 1 &&
                f.Store.GetActiveLifeTags().Count == 0, "事件没有合适主题可留空，完整依据与认识仍一次完成，不为主题重试整批");
        }
        foreach (var total in new[] { 100000, 100001 })
        {
            using var f = new DailyFixture(assembly);
            var sources = f.Seed(250);
            using (var db = new SQLite.SQLiteConnection(f.BrainPath))
                for (var i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];
                    source.Content = "完整原文" + i.ToString("D3") + new string('文', 393 + (i == 249 ? total - 100000 : 0));
                    source.Role = i % 3 == 0 ? "user" : "assistant";
                    source.Type = i % 3 == 0 ? MomentTypes.Message : MomentTypes.Expression;
                    source.ConversationId = i % 2 == 0 ? DailyFixture.Root : DailyFixture.Root + ":private:second";
                    db.Update(source);
                }
            var seen = new List<string>(); var scopes = new Dictionary<string, int>();
            var llm = new SelectionProbeLlm { Reply = messages => {
                Require(messages[0].content.Contains("主动、连续发言"), "完整互动提示明确包含助手主动与连续表达");
                using var input = JsonDocument.Parse(messages[1].content);
                var rows = ReadReviewSources(input.RootElement);
                scopes.Clear();
                for (var i = 0; i < rows.Count; i++)
                {
                    var expected = sources[seen.Count]; var row = rows[i];
                    Require(row.GetProperty("source_ref").GetInt32() == i + 1 &&
                        row.GetProperty("text").GetString() == expected.Content && row.GetProperty("role").GetString() == expected.Role &&
                        row.GetProperty("type").GetString() == expected.Type && row.GetProperty("time").GetInt64() == expected.CreatedUnixMs &&
                        row.GetProperty("realm").GetString() == expected.Realm && row.GetProperty("evidence_type").GetString() == expected.EvidenceType &&
                        row.GetProperty("platform").GetString() == expected.SourcePlatform,
                        "紧凑表格可无损还原全文、双方角色、连续表达、时间、事实性质和平台");
                    var scope = row.GetProperty("conversation_ref").GetInt32();
                    if (scopes.TryGetValue(expected.ConversationId, out var previous)) Require(scope == previous, "会话短编号稳定");
                    else { Require(!scopes.Values.Contains(scope), "不同私密会话不能伪装成同一轮问答"); scopes.Add(expected.ConversationId, scope); }
                    seen.Add(expected.Content);
                }
                return "{\"events\":[]}";
            } };
            f.Run(llm);
            Require(seen.SequenceEqual(sources.Select(m => m.Content)) && llm.Requests.Count == (total == 100000 ? 1 : 2),
                "100000字符含边界一次读取，超出才按完整消息分批，不按条数/角色/会话拆分");
        }
        foreach (var length in new[] { 100000, 100001 })
        {
            using var f = new DailyFixture(assembly); var source = f.Seed(1)[0];
            source.Content = new string('长', length);
            using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Update(source);
            var llm = new AgentSequenceLlm("{\"events\":[]}");
            if (length == 100000) { f.Run(llm); Require(llm.Requests.Count == 1, "单条十万字符仍完整读取一次"); }
            else Require(DailyRejects(() => f.Run(llm)) && llm.Requests.Count == 0 && f.Count("event_entries") == 0,
                "单条超预算在派发前明确失败，不截字、不假报完成");
        }
        foreach (var rollback in new[] { false, true })
        {
            using var f = new DailyFixture(assembly); var sources = f.Seed(50);
            using (var db = new SQLite.SQLiteConnection(f.BrainPath))
                foreach (var source in sources) { source.Content += new string('据', 1000); db.Update(source); }
            var probe = new ReviewDecisionProbe();
            f.Context.GetType().GetProperty("ReviewDecisionOverride").SetValue(f.Context,
                new Func<JsonElement, JsonElement, CancellationToken, Task<JsonElement>>(probe.Decide));
            var tags = Enumerable.Range(1, 9).Select(i => new NewLifeTagWriteData {
                name = "整批主题" + i, definition = new string('义', 250), domain_ids = new() { "user" },
                dimension_ids = new() { "owner", "subject", "about", "predicate", "object", "scope", "context", "quality", "time" },
                positive_examples = Enumerable.Repeat(new string('正', 110), 4).ToList(), negative_examples = new() { "不同含义" }
            }).ToArray();
            string Reply(List<DeepSeekMessageData> messages)
            {
                var stage = messages[0].content;
                if (stage.Contains("1/4")) return TraceJson.ToJson(new { events = Enumerable.Range(1, 131).Select(i => new {
                    summary = "完整事件" + i + new string('事', 650), source_refs = Enumerable.Range(1, 50).ToArray(),
                    tag_refs = Array.Empty<int>(), new_tags = tags
                }) });
                if (stage.Contains("2/4")) return TraceJson.ToJson(new { selected_refs = Enumerable.Range(1, 131).ToArray() });
                if (stage.Contains("3/4"))
                {
                    using var input = JsonDocument.Parse(messages[1].content);
                    Require(input.RootElement.GetProperty("evidence").GetArrayLength() == 50, "所有入选原件一次进入认知判断");
                    return TraceJson.ToJson(new { cognitions = Enumerable.Range(1, 17).Select(i => new {
                        operation = "create", target_ref = 0, summary = "整批理解" + i + new string('知', 650), identity_slot = "other",
                        domains = new[] { "user" }, source_refs = Enumerable.Range(1, 50).ToArray(), tag_refs = Enumerable.Range(1, 9).ToArray(),
                        scope = new string('域', 350), exceptions = new string('例', 351)
                    }) });
                }
                return EmptyDailyRuntime;
            }
            if (rollback)
            {
                using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute(
                    "CREATE TRIGGER fail_full_batch BEFORE INSERT ON cognition_slices WHEN NEW.Summary LIKE '整批理解4%' BEGIN SELECT RAISE(ABORT,'injected batch failure'); END");
                var failed = false;
                try { f.Run(new SelectionProbeLlm { Reply = Reply }); }
                catch (SQLite.SQLiteException e) when (e.Message.Contains("injected batch failure")) { failed = true; }
                Require(failed && f.Graph.GetCognitionNodes(100).Count == 0,
                    "第4项写入失败时前3项也必须回滚，整批不能半写成功");
                using (var state = f.State()) Require(state.RootElement.GetProperty("CognitionSources").GetArrayLength() == 0,
                    "失败的整批不得提前标记来源完成");
                using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute("DROP TRIGGER fail_full_batch");
                f.Reopen();
            }
            var llm = new SelectionProbeLlm { Reply = Reply }; f.Run(llm);
            Require(llm.Requests.Count == (rollback ? 2 : 4) && probe.Questions.Count == 0,
                "有JEV配置的新日期也按环节一次，失败恢复仅补剩余环节");
            using (var db = new SQLite.SQLiteConnection(f.BrainPath))
                Require(f.Count("event_entries") == 131 && f.Graph.GetCognitionNodes(100).Count == 17 &&
                    db.ExecuteScalar<int>("SELECT COUNT(*) FROM cognition_evidence") == 17 * 50 &&
                    db.ExecuteScalar<int>("SELECT COUNT(*) FROM cognition_tag_links") == 17 * 9,
                    "超过旧事件/选中/认知/来源/主题配额仍完整入库，不截成128/10/3/16/8项");
            Require(f.Graph.GetCognitionNodes(100).All(n => n.Summary.Length > 600 && n.Scope.Length == 350 && n.Exceptions.Length == 351) &&
                f.Store.GetActiveLifeTags().All(t => t.Definition.Length == 250 && f.Store.GetLifeTagRoutes(t.Id).Count == 10),
                "完整认知、限定及主题目录不被旧字符/维度限制截断");
            using (var state = f.State()) Require(state.RootElement.GetProperty("Version").GetInt32() == 3 &&
                state.RootElement.GetProperty("Selected").GetArrayLength() == 131 && state.RootElement.GetProperty("Done").GetBoolean(),
                "新检查点区分旧程序，选中事件无固定条数配额");
            f.Reopen(); var empty = new AgentSequenceLlm(); f.Run(empty);
            Require(empty.Requests.Count == 0 && f.Graph.GetCognitionNodes(100).Count == 17, "整批完成后重开不重放、不重复写入");
        }
        using (var f = new DailyFixture(assembly))
        {
            var sources = f.Seed(2); sources[1].ConversationId += ":private:second";
            using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Update(sources[1]);
            var decisions = 0;
            f.Run(new SelectionProbeLlm { Reply = messages => {
                if (messages[0].content.Contains("1/4")) return DailyEvents;
                if (messages[0].content.Contains("2/4")) return "{\"selected_refs\":[1,2]}";
                if (!messages[0].content.Contains("3/4")) return EmptyDailyRuntime;
                decisions++;
                if (decisions == 1) return DailyCognition.Replace("\"source_refs\":[1]", "\"source_refs\":[1,2]");
                Require(messages[^1].content.Contains("conversation_ref"), "合批不能跨会话借用证据，纠正指出来源会话");
                return TraceJson.ToJson(new { cognitions = Enumerable.Range(1, 2).Select(i => new {
                    operation = "create", target_ref = 0, summary = "会话认识" + i, identity_slot = "other", domains = new[] { "user" },
                    source_refs = new[] { i }, tag_refs = new[] { 1 }, scope = "", exceptions = ""
                }) });
            } });
            Require(decisions == 2 && f.Graph.GetCognitionNodes(20).Select(n => n.ContextConversationId).Distinct().Count() == 2,
                "同次整批判断允许不同私密会话分别写入，不能将两种受众合成同一认识");
        }
        using (var f = new DailyFixture(assembly))
        {
            f.Seed(2); var runtimeCalls = 0;
            var llm = new SelectionProbeLlm { Reply = messages => {
                if (!messages[0].content.Contains("4/4")) return DailyReply(messages);
                Require(!messages[0].content.Contains("单字段压缩"), "新复盘不进入逐字段压缩调用链");
                runtimeCalls++;
                return RuntimeProseDraft(new string('长', 124), new string('心', 91), new string('关', 250), new string('卡', 701));
            } };
            f.Run(llm);
            var runtime = (InnerRuntimeData)f.StoreCall("LoadOrCreateInnerRuntime", DailyFixture.Root);
            Require(runtimeCalls == 1 && llm.Requests.Count == 4 && runtime.Narrative.Length == 124 && runtime.Mood.Length == 91 &&
                runtime.RelationshipLens.Length == 250 && f.Store.LoadIdentityCards(DailyFixture.Root).Single(c => c.Slot == "other").Body.Length == 701,
                "状态与卡片超过旧字数限制也完整写入，一次生成、不截断、不逐字段压缩");
        }
    }

    private static void RunDailyRuntimeContractChecks(Assembly assembly)
    {
        foreach (var mode in new[] { "slot", "reference", "body", "none", "pinned" })
        {
            using var f = new DailyFixture(assembly); f.Seed(2);
            if (mode == "pinned") f.StoreCall("SetIdentityPinned", DailyFixture.Root, "relation", true);
            var runtimeCalls = 0; string rejected = null;
            var llm = new SelectionProbeLlm { Reply = messages => {
                var stage = messages[0].content;
                if (stage.Contains("1/4")) return DailyEvents;
                if (stage.Contains("2/4")) return DailySelection;
                if (stage.Contains("3/4"))
                {
                    Require(stage.Contains(CognitionFormationLogic.ReviewMeaningGuide), "认知环节使用共享的经历、理解与对象归属说明");
                    if (mode == "none") return "{\"cognitions\":[]}";
                    return TraceJson.ToJson(new { cognitions = Enumerable.Range(0, 3).Select(i => new {
                        operation = "create", target_ref = 0, summary = "合成测试理解" + i,
                        identity_slot = new[] { "relation", "", "personality" }[i],
                        domains = new[] { new[] { "relation", "world", "ass" }[i] },
                        source_refs = new[] { 1 }, tag_refs = new[] { 1 }, scope = "合成限定", exceptions = ""
                    }) });
                }
                runtimeCalls++;
                using var input = JsonDocument.Parse(messages[1].content);
                var cards = input.RootElement.GetProperty("cards").EnumerateArray().ToList();
                if (mode is "none" or "pinned")
                {
                    Require(cards.Count == 0, "无认知或唯一有依据槽位被固定时不展示可改卡片");
                    return "{\"cards\":[],\"runtime\":{\"mood\":\"平静\"}}";
                }
                Require(cards.Count == 1 && cards[0].GetProperty("slot").GetString() == "relation" &&
                    cards[0].GetProperty("purpose").GetString().Contains("双方"), "只有关系卡有合法依据，不展示无依据卡、固定人格或用户资料卡");
                var valid = cards[0].GetProperty("allowed_cognition_refs").EnumerateArray().Select(x => x.GetInt32()).Single();
                Require(input.RootElement.GetProperty("cognitions").EnumerateArray().All(c => c.GetProperty("slot").GetString() == "relation"),
                    "固定人格和无身份类别的认识不再次灌入身份卡请求，当日状态从事件取材");
                var invalid = Enumerable.Range(1, 3).First(n => n != valid);
                string Output(string slot, string body, int reference) => TraceJson.ToJson(new {
                    cards = new[] { new { slot, body, cognition_refs = new[] { reference } } }, runtime = new { mood = "平静" } });
                if (runtimeCalls == 1)
                {
                    rejected = Output(mode == "slot" ? "self" : "relation", mode == "body" ? "" : "错误草稿",
                        mode == "reference" ? invalid : valid);
                    return rejected;
                }
                Require(messages.Any(m => m.role == "assistant" && m.content == rejected) &&
                    messages.Last().content.Contains("cards[0]") &&
                    (mode == "body" ? messages.Last().content.Contains("删除整项") :
                        messages.Last().content.Contains("relation") && messages.Last().content.Contains("[" + valid + "]")),
                    "纠正同时携带原草稿、具体出错卡和合法编号或省略办法");
                return Output("relation", "双方约定先倾听，再共同决定下一步。", valid);
            } };
            f.Run(llm);
            Require(llm.Requests.Count == (mode is "none" or "pinned" ? 4 : 5), "正常四步，发生错误只补一次完整纠正");
            using var state = f.State(); Require(state.RootElement.GetProperty("RuntimeDone").GetBoolean(), "空卡或纠正后卡片均正常完成状态提交");
        }
    }

    private static void RunDailyToolEvidenceCheck(Assembly assembly)
    {
        using var f=new DailyFixture(assembly);
        var parent=f.Seed(1)[0];
        var observation=new MomentRecord { Id="observed-source", ConversationId=DailyFixture.Root, Role="system_event",
            Type=MomentTypes.Event, Content="外部材料原件标记：牛肉面口味介绍。", ParentMomentId=parent.Id,
            SourcePluginId="check.observation", Realm=TraceRealmValues.ExternalWorld,
            EvidenceType=EvidenceTypeValues.PluginObserved, MemoryVisibility="private", CreatedUnixMs=parent.CreatedUnixMs+1 };
        f.Store.SaveMoment(observation);
        var llm=new SelectionProbeLlm{Reply=messages=> {
            if(messages[0].content.Contains("1/4"))
            {
                Require(messages[1].content.Contains(observation.Content) && messages[1].content.Contains(EvidenceTypeValues.PluginObserved),
                    "工具观察原件及证据类型实际进入日复盘");
                return "{\"events\":[{\"summary\":\"阅读牛肉面口味材料\",\"source_refs\":[2]"+DailyTopicFields+"}]}";
            }
            if(messages[0].content.Contains("2/4")) return DailySelection;
            if(messages[0].content.Contains("3/4")) return DailyCognition;
            return EmptyDailyRuntime;
        }};
        f.Run(llm);
        var ids=f.Graph.GetCognitionNodes(20).Select(x=>x.Id).ToList();
        Require(f.Graph.GetCognitionEvidence(ids).Any(x=>x.MomentId==observation.Id) &&
            f.Store.GetEventEntriesByIndexIds(f.Store.GetActiveEventIndexes().Select(x=>x.Id)).Single().SourceMomentId==observation.Id &&
            f.Graph.GetEvidenceMoments(new[]{observation.Id}).Single().Content==observation.Content,
            "工具原件完成复盘后保留，事件与认知证据指向观察而非触发用户原句");
    }

    private static void RunDailyExistingSlotChecks(Assembly assembly)
    {
        foreach (var slot in new[] { "expression_habit", "" })
        foreach (var overflow in new[] { false, true })
        {
            using var f = new DailyFixture(assembly) { ExistingEngine = "legacy" }; f.Seed(2);
            var seed = DailyCognition.Replace("\"identity_slot\":\"other\"", "\"identity_slot\":\"" + slot + "\"")
                .Replace("\"domains\":[\"user\"]", "\"domains\":[\"ass\"]");
            f.Run(new AgentSequenceLlm(DailyEvents, DailySelection, seed, EmptyDailyRuntime));
            var before = TraceJson.ToJson(f.Graph.GetCognitionNodes(20));
            const string nextDay = "2020-08-21";
            var nextSource = f.Seed(2, nextDay)[0];
            var prior = f.Graph.GetCognitionNodes(20).Single();
            nextSource.Content = prior.Summary + "\n" + prior.About + "\n" + prior.Scope + "\n" + prior.Exceptions;
            using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute("UPDATE moments SET Content=? WHERE Id=?", nextSource.Content, nextSource.Id);
            var calls = 0;
            var llm = new SelectionProbeLlm { Reply = messages => {
                if (messages[0].content.Contains("1/4")) return DailyEvents;
                if (messages[0].content.Contains("2/4")) return DailySelection;
                if (messages[0].content.Contains("4/4")) return EmptyDailyRuntime;
                calls++;
                using var input = JsonDocument.Parse(messages[1].content);
                var target = input.RootElement.GetProperty("existing").EnumerateArray().Single();
                var reference = target.GetProperty("target_ref").GetInt32();
                Require(target.GetProperty("identity_slot").GetString() == slot,
                    "既有认知以与输出相同的identity_slot字段明确展示身份类别，包括空类别");
                if (calls == 2)
                {
                    var repair = messages[^1].content;
                    Require(repair.Contains("target_ref=" + reference) && repair.Contains("identity_slot=\"" + slot + "\"") &&
                        repair.Contains("最多3项") && repair.Contains("ass"),
                        "实际纠正请求须携带目标编号、确切身份类别及领域约束，同时提醒总条数上限");
                    Require(TraceJson.ToJson(f.Graph.GetCognitionNodes(20)) == before, "身份类别错误的印证不得提前提交");
                }
                var write = new {
                    operation = "reinforce", target_ref = reference, summary = "他早餐想吃面包，选择时应尊重当天意愿。",
                    identity_slot = calls == 1 ? "relation" : slot, domains = new[] { calls == 1 ? "relation" : "ass" },
                    source_refs = new[] { 1 }, tag_refs = new[] { 1 }, scope = "", exceptions = ""
                };
                return TraceJson.ToJson(new { cognitions = Enumerable.Repeat(write, calls == 2 && overflow ? 4 : 1).ToArray() });
            } };
            if (overflow)
            {
                Require(DailyRejects(() => f.Run(llm, nextDay)) && calls == 2 && TraceJson.ToJson(f.Graph.GetCognitionNodes(20)) == before,
                    "复现身份类别错误后纠正扩成4条：明确失败且无认知写入，不增加盲目重试");
                f.Reopen(); f.Run(new AgentSequenceLlm("{\"cognitions\":[]}", EmptyDailyRuntime), nextDay);
            }
            else
            {
                f.Run(llm, nextDay);
                Require(calls == 2 && f.Graph.GetCognitionNodes(20).Single().IdentitySlot == slot,
                    "纠正后印证既有认知，保持原类别且不另建重复认知");
            }
            using var state = f.State(nextDay); Require(state.RootElement.GetProperty("Done").GetBoolean(), "身份纠正或失败后续做均完成检查点");
        }
    }

    private static void RunDailyTopicEvidenceChecks(Assembly assembly)
    {
        foreach (var mode in new[] { "valid", "cross_event", "correct", "reject", "domain", "legacy" })
        {
            using var f = new DailyFixture(assembly) { ExistingEngine = mode == "legacy" ? "legacy" : "batch-v1" }; f.Seed(3);
            var calls = 0; string firstDraft = null;
            var summary = "根据本次原句形成的独立认识。" + (mode == "correct" ? new string('义', 17000) + "完整草稿结尾" : "");
            var llm = new SelectionProbeLlm { Reply = messages => {
                var title = messages[0].content;
                if (title.Contains("1/4")) return "{\"events\":[{\"summary\":\"早餐选择\",\"source_refs\":[1,2]" + DailyTopicFields +
                    "},{\"summary\":\"讨论数学\",\"source_refs\":[2,3]" + DailyTopicFields.Replace("饮食意愿", "数学联想") + "}]}";
                if (title.Contains("2/4")) return "{\"selected_refs\":[1,2]}";
                if (title.Contains("4/4")) return EmptyDailyRuntime;
                Require(title.Contains("3/4"), "引用映射夹具只执行四步复盘");
                calls++;
                using var input = JsonDocument.Parse(messages[1].content);
                var events = input.RootElement.GetProperty("events").EnumerateArray().ToList();
                int[] Refs(JsonElement item, string key) => item.GetProperty(key).EnumerateArray().Select(x => x.GetInt32()).ToArray();
                var breakfast = events.Single(e => e.GetProperty("summary").GetString() == "早餐选择");
                var maths = events.Single(e => e.GetProperty("summary").GetString() == "讨论数学");
                var foodTag = Refs(breakfast, "tag_refs").Single(); var mathsTag = Refs(maths, "tag_refs").Single();
                Require(Refs(breakfast, "source_refs").SequenceEqual(new[] { 1, 2 }) &&
                    Refs(maths, "source_refs").SequenceEqual(new[] { 2, 3 }) && foodTag != mathsTag,
                    "真实请求公开事件与本批原句、主题映射，包括一个原句支持多个事件");
                var topics = input.RootElement.GetProperty("topics").EnumerateArray().ToList();
                Require(topics.Single(t => t.GetProperty("tag_ref").GetInt32() == foodTag).GetProperty("name").GetString() == "饮食意愿" &&
                    topics.Single(t => t.GetProperty("tag_ref").GetInt32() == mathsTag).GetProperty("name").GetString() == "数学联想",
                    "映射主题编号指向实际主题候选，不混用主题ID与原句编号");
                if (calls == 2)
                {
                    Require(f.Graph.GetCognitionNodes(20).Count == 0, "错误首次输出不得提前入库");
                    var correction = messages[^1].content;
                    Require(mode == "domain" ? correction.Contains("domains") : correction.Contains("tag_refs"),
                        "纠正定位到实际错误字段");
                    if (mode != "legacy") Require(messages.Any(m => m.role == "assistant" && m.content == firstDraft) &&
                        correction.Contains("只修改报错字段") && !correction.Contains("新正文必须更短"),
                        "带回完整原稿包括16000字符之后的内容，引用错误只作局部纠正，不触发删减重写");
                }
                var invalid = mode == "reject" || (mode == "correct" && calls == 1);
                var response = TraceJson.ToJson(new { cognitions = new[] { new {
                    operation = "create", target_ref = 0, summary, identity_slot = "other",
                    domains = mode == "domain" && calls == 1 ? new[] { "self" } : new[] { "user" },
                    source_refs = mode == "valid" ? new[] { 2 } : new[] { 1 },
                    tag_refs = invalid ? new[] { topics.Count + 1 } : mode == "cross_event" || (mode == "legacy" && calls == 1)
                        ? new[] { mathsTag } : mode == "valid" ? new[] { foodTag, mathsTag } : new[] { foodTag },
                    scope = "", exceptions = ""
                } } });
                if (calls == 1) firstDraft = response;
                return response;
            } };
            if (mode == "reject")
            {
                Require(DailyRejects(() => f.Run(llm)) && calls == 2 && f.Graph.GetCognitionNodes(20).Count == 0,
                    "不存在的主题编号仍被拒绝，最多一次纠正且不写认知");
                continue;
            }
            f.Run(llm);
            var nodes = f.Graph.GetCognitionNodes(20);
            var tagIds = f.Store.GetCognitionTagIds(nodes.Select(n => n.Id));
            Require(calls == (mode is "valid" or "cross_event" ? 1 : 2) && nodes.Count == 1 && nodes[0].Summary == summary &&
                tagIds[nodes[0].Id].Count == (mode == "valid" ? 2 : 1),
                "新路径允许按认知含义选用本批其他主题，旧路径保留约束；结构纠正不丢正文且只入库一次");
            f.Reopen(); var resumed = new AgentSequenceLlm(); f.Run(resumed);
            Require(resumed.Requests.Count == 0, "映射修复保持已完成检查点零调用复用");
        }
    }

    private static void RunDailyTopicGrowthChecks(Assembly assembly)
    {
        using var f = new DailyFixture(assembly);
        var examples = new[] {
            ("2020-08-20", "想吃牛肉面了", "饮食意愿"),
            ("2020-08-21", "牛肉面我喜欢加小葱", "口味偏好"),
            ("2020-08-22", "我今天吃牛肉面的时候想到一个数学定理", "数学联想"),
            ("2020-08-23", "牛肉面我喜欢加小葱", "口味偏好") };
        foreach (var (day, statement, topic) in examples)
        {
            var source = f.Seed(1, day)[0]; source.Content = statement;
            using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Update(source);
            var llm = new SelectionProbeLlm { Reply = messages => {
                if (messages[0].content.Contains("1/4"))
                {
                    using var input = JsonDocument.Parse(messages[1].content);
                    var candidates = ReadReviewTable(input.RootElement, "topics", "topic_columns");
                    var selected = candidates.Where(t => t.GetProperty("name").GetString() == topic)
                        .Select(t => t.GetProperty("tag_ref").GetInt32()).ToList();
                    if (day == "2020-08-23") Require(selected.Count == 1, "再次谈同一含义时，旧主题须通过语义候选进入真实复盘请求");
                    return TraceJson.ToJson(new { events = new[] { new { summary=statement, source_refs=new[]{1},
                        tag_refs=selected, new_tags=selected.Count > 0 ? new List<NewLifeTagWriteData>() : new() { new() {
                            name=topic, definition=statement, domain_ids=new(){"user","world"}, dimension_ids=new(){"scope"},
                            positive_examples=new(){statement}, negative_examples=new(){"今天天气怎么样"} } } } } });
                }
                if (messages[0].content.Contains("2/4")) return DailySelection;
                if (messages[0].content.Contains("3/4")) return TraceJson.ToJson(new { cognitions = new[] { new {
                    operation="create", target_ref=0, summary=statement, source_refs=new[]{1}, tag_refs=new[]{1},
                    identity_slot="", domains=new[]{"user"}, scope="", exceptions="" } } });
                return EmptyDailyRuntime;
            } };
            f.Run(llm, day); f.Reopen();
        }
        var tags = f.Store.GetActiveLifeTags();
        Require(tags.Count == 3 && f.Store.GetActiveEventIndexes().Count == 4 && f.Graph.GetCognitionNodes(20).Count == 3,
            "四天同词不同含义分成三个主题，同义重用主题及认知，事件原件仍各自保留");
        foreach (var (_, statement, topic) in examples.Take(3))
        {
            var id = tags.Single(t => t.Label == topic).Id;
            Require(f.Store.GetActiveEventIndexes().Where(e => e.EventSummary == statement).All(e => e.TagIds == id) &&
                f.Store.GetCognitionTagIds(f.Graph.GetCognitionNodes(20).Where(n => n.Summary == statement).Select(n => n.Id)).Values.All(v => v.SequenceEqual(new[]{id})),
                "模型选择的不同含义分别挂接事件与认知：" + topic);
        }
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
            Require(resume.Requests.Count == (stage == 4 ? 0 : 2) && f.Graph.GetCognitionNodes(20).Count == 1,
                "提交中断后只补失败阶段，第4步复用已保存的合格草稿，阶段=" + stage);
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
            if (messages[0].content.Contains("【公开经历复盘】"))
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
        using var f = new DailyFixture(assembly); f.Seed(250);
        var llm = new SelectionProbeLlm { Reply = messages => {
            var title = messages[0].content;
            if (title.Contains("1/4"))
            {
                return TraceJson.ToJson(new { events = Enumerable.Range(0, 53).Select(i => new {
                    summary = "事件" + i + new string('事', 490), source_refs = new[] { 1 }, tag_refs = Array.Empty<int>(),
                    new_tags = new[] { new NewLifeTagWriteData { name="饮食意愿", definition="当下想吃什么及选择", domain_ids=new(){"user"},
                        dimension_ids=new(){"scope"}, positive_examples=new(){"早餐吃面包"}, negative_examples=new(){"数学定理"} } } }) });
            }
            if (title.Contains("2/4")) return DailySelection;
            if (title.Contains("3/4")) return "{\"cognitions\":[]}";
            if (title.Contains("4/4")) return EmptyDailyRuntime;
            throw new InvalidOperationException("unexpected stage");
        } };
        f.Run(llm);
        Require(f.Count("event_entries") == 53 && llm.Requests.Count == 4 &&
            Enumerable.Range(1, 4).All(i => llm.Requests.Count(p => p.Contains("【" + i + "/4")) == 1),
            "53件长摘要一次挑选，250条对话四阶段各一次，无逐事件扩写或逐认知判断");
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
