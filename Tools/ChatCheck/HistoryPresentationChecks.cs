using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Util;

internal static partial class Program
{
    private static void RunHistoryPresentationChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), "tracesoul-history-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new SqliteMemoryManager(path);
            store.SavePairIdentity("小雨", "小光", "");
            var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
            var night = new DateTimeOffset(new DateTime(2026, 9, 30, 23, 18, 0, DateTimeKind.Local));
            var morning = night.AddHours(10);
            var original = "第一行\n\n  保留缩进与空行\n";
            var records = new List<MomentRecord>
            {
                new() { Role = "user", Content = original, CreatedUnixMs = night.ToUnixTimeMilliseconds() },
                new() { Role = "user", Content = "早呀", CreatedUnixMs = morning.ToUnixTimeMilliseconds() },
                new() { Role = "assistant", Content = "早", CreatedUnixMs = morning.AddMinutes(1).ToUnixTimeMilliseconds(),
                    PayloadJson = "{\"reasoning_content\":\"第一条推理\"}" },
                new() { Role = "assistant", Content = "早餐吃了吗", CreatedUnixMs = morning.AddMinutes(2).ToUnixTimeMilliseconds(),
                    PayloadJson = "{\"reasoning_content\":\"第二条推理\"}" }
            };
            TraceTurnContext Turn(List<MomentRecord> history, EnvironmentSnapshotData environment = null) =>
                new("history", Moment("history", "当前原话"), history, 20, true, services, environment: environment);
            var turn = Turn(records);
            var history = CommonContextPackLogic.BuildRecentChatHistory(turn, true);
            Require(history.Count == 4 && history.Select(x => x.role).SequenceEqual(new[] { "user", "user", "assistant", "assistant" }),
                "跨日同角色和连续同伴发言各自保留独立消息");
            Require(history[0].content == "【小雨 · " + night.ToString("yyyy年M月d日 HH:mm zzz", CultureInfo.InvariantCulture) + "】\n" + original &&
                history[1].content.Contains("2026年10月1日") && history[0].content.Contains("2026年9月30日"),
                "历史保留绝对日期、时区与正文空行缩进");
            Require(history[2].reasoning_content == "第一条推理" && history[3].reasoning_content == "第二条推理" &&
                CommonContextPackLogic.BuildRecentChatHistory(turn).All(x => x.reasoning_content == null),
                "启用推理回传时两条同伴推理各自对应，其他调用不带推理");
            var extended = new List<MomentRecord>(records) { new() { Role = "user", Content = "又一天", CreatedUnixMs = morning.AddDays(1).ToUnixTimeMilliseconds() } };
            Require(CommonContextPackLogic.SharedPrefixCount(history, CommonContextPackLogic.BuildRecentChatHistory(Turn(extended), true)) == 4,
                "新消息及跨日不会改写已有历史前缀");
            var media = new List<MomentRecord>
            {
                new() { Role = "user", Content = "看看这个\n【看见】一只猫", PayloadJson = "{\"image_urls\":[\"test-image\"]}" },
                new() { Role = "user", Content = "【看见】这是我写的标题" },
                new() { Role = "user", Content = "【看见】识图失败", PayloadJson = "{\"image_urls\":[\"test-image\"]}", CreatedUnixMs = long.MaxValue }
            };
            var shown = CommonContextPackLogic.BuildRecentChatHistory(Turn(media));
            Require(shown[0].content.EndsWith("看看这个\n【当时的图片观察】\n一只猫", StringComparison.Ordinal) &&
                shown[1].content.EndsWith(media[1].Content, StringComparison.Ordinal) &&
                shown[2].content.Contains("时间未记录") && shown[2].content.EndsWith("【当时的图片观察】\n识图失败", StringComparison.Ordinal) &&
                media[0].Content == "看看这个\n【看见】一只猫",
                "仅标注有图片载荷的观察，保留失败及用户原文，不修改存储对象，不伪造异常时间");
            var groupRecords = new List<MomentRecord>
            {
                new() { Role = "user", Content = "甲的话", ConversationId = "history", EnvironmentJson = TraceJson.ToJson(new EnvironmentSnapshotData { SpeakerName = "甲", SpeakerId = "a" }) },
                new() { Role = "user", Content = "乙的话", ConversationId = "history", EnvironmentJson = TraceJson.ToJson(new EnvironmentSnapshotData { SpeakerName = "乙", SpeakerId = "b" }) },
                new() { Role = "小光", Content = "我的话", ConversationId = "history" },
                new() { Role = "user", Content = "私人内容", ConversationId = "private" }
            };
            var group = CommonContextPackLogic.BuildRecentChatHistory(Turn(groupRecords, new EnvironmentSnapshotData { Visibility = "public" }));
            Require(group.Count == 3 && group[0].content.StartsWith("【甲 · a · ", StringComparison.Ordinal) &&
                group[1].content.StartsWith("【乙 · b · ", StringComparison.Ordinal) && group[2].content.StartsWith("【我 · ", StringComparison.Ordinal),
                "群聊不同发言者保留归属，具名同伴显示为我，私人会话仍被过滤");
            var assembled = CommonContextPackLogic.Assemble("身份", turn, "", "当前原话", "规则", "稳定规则", "动态状态");
            Require(assembled[^1].content == "当前原话" && assembled.Count(x => x.content == "当前原话") == 1 &&
                assembled[0].content == "身份", "历史格式不会重复当前原话或改变身份段");
        }
        finally { Delete(path); }
        Console.WriteLine("History presentation checks passed: dates, message boundaries, formatting, image provenance, public speakers and stable prefixes.");
    }
}
