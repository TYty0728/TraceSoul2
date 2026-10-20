using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Util;

internal static partial class Program
{
    private static async Task RunMomentSemanticsChecksAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "moment-semantics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "legacy.sqlite3");
        try
        {
            using (var old = new SQLite.SQLiteConnection(path))
            {
                old.Execute("CREATE TABLE moments (Id TEXT PRIMARY KEY,ConversationId TEXT,Role TEXT,Content TEXT,Realm TEXT,EvidenceType TEXT," +
                    "SourcePluginId TEXT,SourceEventId TEXT,PayloadJson TEXT,EnvironmentJson TEXT,MemoryVisibility TEXT,MemoryStatus TEXT,CreatedUnixMs INTEGER)");
                for (var i = 0; i < 503; i++) old.Execute("INSERT INTO moments(Id,ConversationId,Role,Content,SourcePluginId,EvidenceType,CreatedUnixMs) VALUES(?,?,?,?,?,?,?)",
                    "old-" + i.ToString("D4"), "legacy", "user", "原始文本\n  不变" + i, "builtin.onebot", EvidenceTypeValues.DialogueExplicit, i);
                old.Execute("INSERT INTO moments(Id,ConversationId,Role,Content,SourcePluginId,PayloadJson,MemoryVisibility,CreatedUnixMs) VALUES(?,?,?,?,?,?,?,?)",
                    "heard", "legacy", "user", "【我听见】\n旧听写", "wxxz", "{\"input\":\"board_final_transcript\"}", "private", 504);
                old.Execute("INSERT INTO moments(Id,ConversationId,Role,Content,SourcePluginId,PayloadJson,CreatedUnixMs) VALUES(?,?,?,?,?,?,?)",
                    "spoof", "legacy", "user", "【我听见】\n用户自己写的字", "wxxz", "{\"input\":\"admin_simulation\"}", 505);
            }
            using (var store = new SqliteMemoryManager(path))
            {
                var heard = store.GetMomentFeed(100, MomentTypes.Hearing, "wxxz").Single();
                Require(heard.Id == "heard" && heard.Content == "【我听见】\n旧听写" && heard.MemoryVisibility == "private", "迁移识别旧真实听写，保留原文与可见性");
                var spoof = store.GetMomentFeed(100, MomentTypes.Message, "wxxz").Single();
                Require(spoof.Id == "spoof", "正文标签不能将手动消息伪装为声音");
                using var check = new SQLite.SQLiteConnection(path);
                Require(check.ExecuteScalar<int>("SELECT COUNT(*) FROM moments WHERE Type='message' AND SourcePlatform='qq'") == 503,
                    "旧表自动加列并分页回填全部历史，不受500行上限影响");
                Require(check.ExecuteScalar<string>("SELECT Content FROM moments WHERE Id='old-0000'") == "原始文本\n  不变0", "迁移不改原始正文");
                store.SaveMoment(new MomentRecord { Id = "touch", ConversationId = "robot", Type = MomentTypes.Touch, SourcePlatform = "robot",
                    Role = MomentTypes.PerceptionRole, Content = "外壳右侧受到轻触", CreatedUnixMs = 600, MemoryVisibility = "public" });
                store.SaveMoment(new MomentRecord { Id = "literal-qq", ConversationId = "legacy", Type = MomentTypes.Expression,
                    SourcePluginId = "builtin.onebot", Role = "assistant", EvidenceType = EvidenceTypeValues.AssPerformed,
                    Content = "[QQ 这是新正文的字面标签]", CreatedUnixMs = 601 });
                Require(store.GetMomentFeed(20, "touch", "robot").Single().Content == "外壳右侧受到轻触", "触觉与机器人平台独立入库和筛选");
                Require(PuzzleViewLogic.SubjectKey(store.GetMomentFeed(20, "touch", "robot").Single()) == "perception:robot", "感知不伪装用户自述");
                var unknownRejected = false;
                try { store.SaveMoment(new MomentRecord { Id = "invalid", Type = "qq", Role = "user", Content = "x" }); }
                catch (ArgumentException) { unknownRejected = true; }
                Require(unknownRejected, "平台名不能混入感知Type");
                Require(MomentTypes.InferPlatform(new MomentRecord { SourcePluginId = "night.residue",
                    EnvironmentJson = "{\"PlatformId\":\"builtin.onebot\"}" }) == "system", "内部事件来源不冒充目标QQ平台");
            }
            using (var reopened = new SqliteMemoryManager(path))
            {
                Require(reopened.GetMomentFeed(20, "touch", "robot").Single().Id == "touch", "重启迁移幂等，新类型不被覆盖");
                Require(reopened.GetMomentFeed(20, "expression", "qq").Single().Id == "literal-qq", "新记录按类型处理，不再因正文前缀误判运行回执");
            }

            using var memory = new SqliteMemoryManager(":memory:");
            memory.SavePairIdentity("测试者", "测试同伴", "");
            Require(EnvironmentLogic.MomentSpeaker(new MomentRecord { Role = "user", Type = MomentTypes.Message, MemoryVisibility = "public",
                EnvironmentJson = TraceJson.ToJson(new EnvironmentSnapshotData { SpeakerName = "其他在场者" }) }, memory.LoadPairIdentity()) == "其他在场者",
                "统一经历列表保留公开场景真实发言者，不冒认为本人");
            var services = new TracePluginServices(memory, new HierarchicalVectorRouterLogic(new FakeEncoder()));
            services.Providers = new FakeVisionDirectory { Client = new SeeingLlm(), Endpoint = new LlmEndpointData { ProviderId = "vision", Model = "vl", ApiKey = "fake" } };
            using var plugins = new TracePluginManager(memory, services);
            plugins.RegisterExternal(new DialogueTracePlugin());
            var qqSource = new OneBotPlatformAdapter(new OneBotPlatformPlugin()).ConvertInbound(TraceJson.ToJson(new
            {
                post_type = "message", self_id = 999, user_id = 123, message_type = "private", message_id = 42,
                message = new[] { new { type = "text", data = new { text = "原始QQ文字" } } }
            }));
            Require(qqSource.Type == MomentTypes.Message && qqSource.SourcePlatform == "qq" && qqSource.Content == "原始QQ文字",
                "QQ使用类型与平台元数据，不再把平台标签混入新正文");
            var qqMoment=new MomentRecord {Id="qq-label-check",ConversationId="qq-label-check",Role="测试者",
                Type=qqSource.Type,SourcePlatform=qqSource.SourcePlatform,Content=qqSource.Content,
                EvidenceType=EvidenceTypeValues.DialogueExplicit,CreatedUnixMs=1000};
            var qqTurn=new TraceTurnContext("qq-label-check",qqMoment,new List<MomentRecord>{qqMoment},12,true,services);
            var trigger=RuntimeContextLogic.Trigger(qqTurn);
            Require(trigger.Contains("本轮类型：我收到消息；来源平台：QQ")&&
                MomentTypes.ForPrompt(qqMoment).StartsWith("【我收到消息】\n"),"当前主模型Prompt同时提供类型和平台，正文标签只由可信字段生成");
            var qqHistory=CommonContextPackLogic.BuildRecentChatHistory(qqTurn);
            Require(qqHistory.Single().content.Contains(" · QQ · ")&&qqHistory.Single().content.Contains("【我收到消息】"),
                "历史Prompt也同时保留QQ平台与消息类型");
            var png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
            var source = new PluginEventData { PluginId = "builtin.dialogue", ExternalEventId = "image-message", Role = "测试者",
                Type = MomentTypes.Message, SourcePlatform = "qq", Content = "请看看图片", EvidenceType = EvidenceTypeValues.DialogueExplicit,
                PayloadJson = TraceJson.ToJson(new { image_urls = new[] { png } }),
                Environment = new EnvironmentObservationData { PlatformId = "builtin.dialogue", SessionType = "private", SessionId = "local",
                    SpeakerId = "local-owner", SpeakerName = "测试者", ExclusivePair = true, DirectAddress = true } };
            var llm = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"看到了。\"}");
            await new KernelLogic(memory, llm, plugins).ProcessPluginEventAsync("typed-test", source, 12);
            var all = memory.GetMomentFeed(100);
            var message = all.Single(x => x.Type == MomentTypes.Message);
            var sight = all.Single(x => x.Type == MomentTypes.Sight);
            Require(message.Content == "请看看图片" && sight.Content.Contains("热汤面") && sight.ParentMomentId == message.Id &&
                sight.Role == MomentTypes.PerceptionRole && sight.SourcePlatform == "console", "识图单独落库并关联原消息，平台以实际环境为准");
            Require(llm.Requests.Any(x => x.Contains("我看见") && x.Contains("热汤面") && x.Contains("不是对方发言")), "主模型获得类型化观察，正文不被拼写污染");
            Require(all.Single(x => x.Type == MomentTypes.Expression).SourcePlatform == "console", "输出类型与真实来源平台分别保留");
            var turn = new TraceTurnContext(message.ConversationId, message, new List<MomentRecord> { sight }, 12, true, services);
            var history = CommonContextPackLogic.BuildRecentChatHistory(turn);
            Require(history.Single().content.Contains("【感知 · 控制台") && history.Single().content.Contains("【我看见】"), "视觉进入后续历史时保持观察归属");
            Console.WriteLine("Moment semantics checks passed: legacy schema migration, type/platform filters, truthful transcripts, linked vision and prompt attribution.");
        }
        finally { Directory.Delete(folder, true); }
    }
}
