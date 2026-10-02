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
        RunRandomCameraIntervalCheck();
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

                    var empty = CameraSharingContext.Build(turn, 6, 6);
                    Require(empty.Length == 0, "没有积累到门槛时不注入相机节奏说明");
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    void Receipt(string receiptId, string kind, string payload, long? timestamp = null) => store.SaveOperationalEvent(new OperationalEventRecord
                    {
                        Id = receiptId, ConversationId = turn.ConversationId, Kind = kind,
                        SourcePluginId = "builtin.onebot", PayloadJson = payload, CreatedUnixMs = timestamp ?? now - 1000
                    });
                    Receipt("sticker", OperationalEventKindValues.OutboundSticker, moment.PayloadJson);
                    Receipt("other-session", OperationalEventKindValues.OutboundImage, "{\"session_type\":\"private\",\"session_id\":\"456\"}");
                    Require(CameraSharingContext.Build(turn, 6, 6).Length == 0, "表情与其他会话回执不产生节奏提示");
                    Receipt("old-photo", OperationalEventKindValues.OutboundImage, moment.PayloadJson, now - 86400000);
                    Require(CameraSharingContext.Build(turn, 6, 6).Length == 0, "旧照片的间隔不直接注入模型");
                    Receipt("photo", OperationalEventKindValues.OutboundImage, moment.PayloadJson);
                    Require(CameraSharingContext.Build(turn, 6, 6).Length == 0, "成功发图后从新的文字相处重新累计");
                    for (var i = 0; i < 6; i++)
                    {
                        Require(CameraSharingContext.Build(turn, 6, 6).Length == 0, "门槛前不提前给出照片邀请");
                        store.SaveMoment(new MomentRecord
                        {
                            Id = "reply-" + i, ConversationId = turn.ConversationId, Role = "小光", Content = "在听呢",
                            SourcePluginId = "builtin.onebot", PayloadJson = moment.PayloadJson, CreatedUnixMs = now + i
                        });
                    }
                    var invitation = CameraSharingContext.Build(turn, 6, 6);
                    Require(invitation.Contains("结合当下的表达和场景") && !invitation.Any(char.IsDigit) &&
                        !invitation.Contains("计数") && !invitation.Contains("分钟") && !invitation.Contains("次文字回应"),
                        "程序计算门槛，模型只收到贴合当下的照片邀请");
                    Receipt("other-session-new", OperationalEventKindValues.OutboundImage,
                        "{\"session_type\":\"private\",\"session_id\":\"456\"}", now + 50);
                    Require(CameraSharingContext.Build(turn, 6, 6) == invitation, "其他会话成功发图不清除本会话邀请");
                    turn.Workspace.Results.Add(new TraceCapabilityResultData { CapabilityId = "qq.imagegen.generate", Status = "failed" });
                    Require(CameraSharingContext.Build(turn, 6, 6).Length == 0, "本轮相机已尝试后由反馈续推，不重复节奏邀请");
                    turn.Workspace.Results.Clear();

                    var fake = new CapturingLlm();
                    services.MindTurnPromptAppends.Add(t => CameraSharingContext.Build(t, 6, 6));
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

                    // 使用真实插件注册与目录装配，模型和执行器仍是模拟的，不调用生图或 QQ。
                    services.Platforms.Unregister(BodyIds.Qq);
                    services.Platforms.Register(new PlatformHandle { Id = BodyIds.Qq, IsConnected = () => true });
                    services.Providers = new FakeVisionDirectory { Endpoint = new LlmEndpointData
                        { ApiKey = "offline-test", BaseUrl = "https://camera.invalid", Model = "offline-image" } };
                    var camera = new QqImageGenPlugin();
                    Directory.CreateDirectory(path + ".data");
                    File.WriteAllText(Path.Combine(path + ".data", "config.json"), "{\"sharing_min_replies\":6,\"sharing_max_replies\":6}");
                    manager.RegisterExternal(camera, pluginDataDirectory: path + ".data");
                    moment.Content = "今天过得怎么样";
                    var agent = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"在听呢。\"}");
                    new AgentLoopLogic(agent).RunAsync(turn, "", () => manager.GetAvailableActionCatalog(turn),
                        (_, _) => throw new Exception("纯文字选择不应执行相机"), default).GetAwaiter().GetResult();
                    Require(agent.Requests.Single().Contains(QqImageGenPrompts.AgentUsage),
                        "真实相机目录须把主动分享语义传入 Agent");
                    Require(agent.Requests.Single().Contains(QqImageGenPrompts.AgentBoundary),
                        "真实相机目录须保留分享边界");
                    Require(agent.Requests.Single().Contains("【相机此刻】"), "真实相机动态回执须进入 Agent");
                    Require(!agent.Requests.Single().Contains("旧心智流程"), "不能带入旧 image 根协议");
                    var semantic = new AgentSequenceLlm("分享：否");
                    services.Llm = semantic;
                    var candidate = services.AutomaticImageProviders.Single()(turn, "在听呢。", "当前有效约定：不收图片。");
                    Require(candidate?.capability_id == "qq.imagegen.generate" &&
                        candidate.GetArgument("atmosphere_context").Contains("不收图片"),
                        "主模型纯文字时，达到门槛可由真实插件建立含当前约定的配图候选");
                    var skipped = manager.ExecuteAsync(candidate, turn, default).GetAwaiter().GetResult();
                    Require(skipped.Status == "skipped" && semantic.Requests.Count == 1 &&
                        semantic.Requests.Single().Contains("在听呢。") && semantic.Requests.Single().Contains("不收图片"),
                        "真实相机的语义判断选择略过时不调用生图接口、不发送图片");
                    Require(services.AutomaticImageProviders.Single()(turn, "下一句话", "") == null &&
                        !CameraSharingContext.TryReserve(turn, 6, 6), "一次判断消耗程序门槛，略过或失败后不逐轮重试");
                    var planMethod = typeof(QqImageGenPlugin).GetMethod("PlanAtmosphereAsync",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    services.Llm = new AgentSequenceLlm("分享：是\n种类：自拍\n参考：无\n画面：角色在窗边带着温和笑意的自然近景，午后的光落在脸侧。");
                    var planning = (Task)planMethod.Invoke(camera, new object[] { "在听呢。", "日常分享", turn, System.Threading.CancellationToken.None });
                    planning.GetAwaiter().GetResult();
                    Require(planning.GetType().GetProperty("Result").GetValue(planning) != null,
                        "一次语义判断同时产生可用镜头规划，不需要主模型重新选择actions");
                    services.Providers = null;
                    var unavailable = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"在听呢。\"}");
                    new AgentLoopLogic(unavailable).RunAsync(turn, "", () => manager.GetAvailableActionCatalog(turn),
                        (_, _) => throw new Exception("不可用相机不能执行"), default).GetAwaiter().GetResult();
                    Require(!unavailable.Requests.Single().Contains(QqImageGenPrompts.AgentUsage) &&
                            !unavailable.Requests.Single().Contains("【相机此刻】"),
                        "相机未配置时不得提示主动调用不可用能力");
                    Receipt("new-photo", OperationalEventKindValues.OutboundImage, moment.PayloadJson, now + 100);
                    Require(CameraSharingContext.Build(turn, 6, 6).Length == 0, "本会话成功发图后自动重新累计门槛");
                    camera.Shutdown(); camera.Shutdown();
                    Require(services.AutomaticImageProviders.Count == 0, "重复卸载相机安全，自动配图钩子不残留");
                }
            }
        }
        finally
        {
            Delete(path); Delete(path + "-wal"); Delete(path + "-shm");
            var dataPath = Path.GetFullPath(path + ".data");
            Require(dataPath.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "只清理测试临时目录");
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, true);
        }
    }

    private static void RunRandomCameraIntervalCheck()
    {
        var path = Path.Combine(Path.GetTempPath(), "tracesoul-camera-range-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using var store = new SqliteMemoryManager(path);
            store.SavePairIdentity("本人", "同伴", "称呼");
            TraceTurnContext Turn(SqliteMemoryManager db, string session = "a") => new("random-camera",
                new MomentRecord { PayloadJson = "{\"session_type\":\"private\",\"session_id\":\"" + session + "\"}" },
                new List<MomentRecord>(), 1, true, new TracePluginServices(db, new HierarchicalVectorRouterLogic(new FakeEncoder())));
            var turn = Turn(store);
            var first = CameraSharingContext.GetCycle(turn, 4, 8);
            Require(first.Threshold >= 4 && first.Threshold <= 8, "随机门槛位于含端点的配置范围内");
            for (var i = 0; i < 20; i++)
                Require(CameraSharingContext.GetCycle(turn, 4, 8).Threshold == first.Threshold, "重复读取不重新抽取门槛");
            using (var restarted = new SqliteMemoryManager(path))
                Require(CameraSharingContext.GetCycle(Turn(restarted), 4, 8).Threshold == first.Threshold, "重新打开数据库仍保留已抽取门槛");
            void Reply(int i) => store.SaveMoment(new MomentRecord { Id = "range-" + i, ConversationId = turn.ConversationId,
                Role = "同伴", Content = "在听", CreatedUnixMs = 1000 + i, PayloadJson = turn.Moment.PayloadJson });
            for (var i = 1; i <= first.Threshold; i++)
            {
                Require(CameraSharingContext.Build(turn, 4, 8).Length == 0, "抽取门槛之前不建立机会");
                Reply(i);
            }
            Require(CameraSharingContext.TryReserve(turn, 4, 8), "恰好达到抽取门槛可占用一次机会");
            var next = CameraSharingContext.GetCycle(turn, 4, 8);
            Require(next.Count == 0 && next.Threshold >= 4 && next.Threshold <= 8 &&
                !CameraSharingContext.TryReserve(turn, 4, 8), "消费后建立新的持久间隔，不重复触发");
            for (var i = first.Threshold + 1; i <= first.Threshold + 250; i++)
            {
                Reply(i);
                CameraSharingContext.GetCycle(turn, 1000, 1000);
            }
            var changed = CameraSharingContext.GetCycle(turn, 300, 300);
            Require(changed.Count == 250 && changed.Threshold == 300 && CameraSharingContext.Build(turn, 300, 300).Length == 0,
                "累计跨过200条近期窗口仍完整，配置修改保留累计，相同上下限表示固定间隔");
            var reversed = CameraSharingContext.GetCycle(turn, 9, 3);
            Require(reversed.Minimum == 3 && reversed.Maximum == 9 && reversed.Count == 250, "反向范围规范化且保留累计");
            Require(CameraSharingContext.GetCycle(Turn(store, "b"), 4, 8).Count == 0, "不同会话各自累计");
            store.SaveOperationalEvent(new OperationalEventRecord { Id = "range-photo", ConversationId = turn.ConversationId,
                SourcePluginId = "builtin.onebot", Kind = OperationalEventKindValues.OutboundImage,
                PayloadJson = turn.Moment.PayloadJson, CreatedUnixMs = 2000, OccurredUnixMs = 2000 });
            Require(CameraSharingContext.GetCycle(turn, 4, 8).Count == 0, "成功图片开始新的随机间隔");
        }
        finally { Delete(path); Delete(path + "-wal"); Delete(path + "-shm"); }
    }
}
