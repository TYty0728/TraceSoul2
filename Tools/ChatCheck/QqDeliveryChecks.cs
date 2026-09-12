using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.ExternalPlugins;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private static void RunQqDeliveryAndCameraContextCheck()
    {
        var path = Path.Combine(Path.GetTempPath(), "tracesoul2-qq-delivery-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("小雨", "小光", "雨雨");
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()))
                {
                    DataDirectory = path + ".data", Platforms = new PlatformRegistry()
                };
                using (var manager = new TracePluginManager(store, services))
                {
                    var qq = new OneBotPlatformPlugin();
                    manager.RegisterExternal(qq);
                    string Frame(long messageId, long userId = 123, long selfId = 999) => JsonSerializer.Serialize(new
                    {
                        post_type = "message", self_id = selfId, user_id = userId,
                        message_type = "private", message_id = messageId,
                        message = new[] { new { type = "text", data = new { text = "今天很开心" } } }
                    });
                    Parallel.For(0, 32, _ => qq.HandleInbound(Frame(42), null));
                    var first = qq.TakeInbound();
                    Require(first.Count == 1, "同一 QQ 事件并发重放只能入队一次");
                    qq.HandleInbound(Frame(42), null);
                    Require(qq.TakeInbound().Count == 0, "消费过的事件重放不能再次触发回复");
                    qq.HandleInbound(Frame(43), null);
                    qq.HandleInbound(Frame(42, 456), null);
                    qq.HandleInbound(Frame(42, 123, 998), null);
                    Require(qq.TakeInbound().Count == 3, "不同消息 ID、会话和机器人不能误去重，即使文字一样");
                    qq.HandleInbound(Frame(0), null);
                    qq.HandleInbound(Frame(0), null);
                    Require(qq.TakeInbound().Count == 2, "缺少有效消息 ID 时不按文字吞掉用户重复发言");
                    qq.HandleInbound(Frame(90, 999), null);
                    Require(qq.TakeInbound().Count == 0, "自身发言回显不触发回复");

                    var source = first.Single();
                    var moment = new MomentRecord
                    {
                        Id = "source", ConversationId = "qq-delivery", SourcePluginId = "builtin.onebot",
                        Role = "小雨", Content = source.Content, PayloadJson = source.PayloadJson
                    };
                    var turn = new TraceTurnContext("qq-delivery", moment, new List<MomentRecord>(), 9, true, services);
                    qq.HandleInbound(Frame(91, 456), null);
                    Require(qq.TryResolveSession(turn, out var type, out var id) && type == "private" && id == "123",
                        "新会话入站不能改变旧轮次文字、输入状态和延迟图片的收件人");

                    var empty = CameraSharingContext.Build(turn);
                    Require(empty.Contains("最近可查") && empty.Contains("不必等索图"), "没有近期回执时提示自然分享，不能虚构已发照片");
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    void Receipt(string receiptId, string kind, string payload) => store.SaveOperationalEvent(new OperationalEventRecord
                    {
                        Id = receiptId, ConversationId = turn.ConversationId, Kind = kind,
                        SourcePluginId = "builtin.onebot", PayloadJson = payload, CreatedUnixMs = now - 1000
                    });
                    Receipt("sticker", OperationalEventKindValues.OutboundSticker, moment.PayloadJson);
                    Receipt("other-session", OperationalEventKindValues.OutboundImage, "{\"session_type\":\"private\",\"session_id\":\"456\"}");
                    Require(CameraSharingContext.Build(turn).Contains("没有给她发过照片"), "表情与其他会话照片不能当成本会话已分享");
                    Receipt("photo", OperationalEventKindValues.OutboundImage, moment.PayloadJson);
                    Require(CameraSharingContext.Build(turn).Contains("刚分享过"), "成功发图后应提醒避免机械连发");
                    for (var i = 0; i < 6; i++) store.SaveMoment(new MomentRecord
                    {
                        Id = "reply-" + i, ConversationId = turn.ConversationId, Role = "小光", Content = "在听呢",
                        SourcePluginId = "builtin.onebot", PayloadJson = moment.PayloadJson, CreatedUnixMs = now + i
                    });
                    Require(CameraSharingContext.Build(turn).Contains("之后已有6次文字回应") &&
                            CameraSharingContext.Build(turn).Contains("现在主动想一想"),
                        "持续文字相处后应重新考虑分享，不能直接强制生成图片");

                    var fake = new CapturingLlm();
                    services.MindTurnPromptAppends.Add(CameraSharingContext.Build);
                    var mind = new MindLogic(fake);
                    mind.DecideAsync(turn, null, false, default).GetAwaiter().GetResult();
                    var request = fake.Requests.Last();
                    Require(VisiblePrompt(request).Contains("【相机此刻】") &&
                            !request[0].content.Contains("【相机此刻】"), "发图反馈只能进入动态提示，不能污染身份 system");
                    services.MindTurnPromptAppends.Clear();
                    mind.DecideAsync(turn, null, false, default).GetAwaiter().GetResult();
                    Require(!VisiblePrompt(fake.Requests.Last()).Contains("【相机此刻】") &&
                            request[0].content == fake.Requests.Last()[0].content,
                        "卸载反馈后不残留相机状态，公共身份前缀不变");

                    var generator = BodyEffector("qq.imagegen.generate", "qq.imagegen",
                        BodyIds.Qq, BodyTierValues.Chat, BodyOrganValues.Image);
                    generator.ParametersJsonSchema = "{prompt:string}";
                    moment.Content = "不要发照片，先陪我说说话";
                    var declined = new ExpressorOutputData { reply = "好，在听。" };
                    ExpressorLogic.EnsureExplicitImageRequest(declined, turn, new[] { generator });
                    Require(string.IsNullOrEmpty(declined.image), "拒绝收照片不能被关键词兜底反向理解为索图");
                    moment.Content = "发张照片给我看看";
                    var waiting = new ExpressorLogic(new CapturingLlm()).ExpressAsync(turn,
                        Array.Empty<TracePluginMetadataData>(), new[] { generator },
                        Array.Empty<TraceContextBlockData>(), new MindDecisionData { beat = "出门", speak = true },
                        string.Empty, true, null, default).GetAwaiter().GetResult();
                    Require(!waiting.expressions.Any(ExpressorLogic.IsImageExpression),
                        "同一索图的离场等待不能提前补图，避免最终回应再生成一张");
                }
            }
        }
        finally
        {
            Delete(path); Delete(path + "-wal"); Delete(path + "-shm");
        }
    }
}
