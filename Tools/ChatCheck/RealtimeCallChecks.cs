using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.ExternalPlugins.RealtimeCall;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

internal static partial class Program
{
    private static async Task RunRealtimeCallChecksAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tracesoul-call-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "config.json"), "{\"private_call\":true,\"speaker_id\":\"voice-owner\"}");
            using var store = new SqliteMemoryManager(Path.Combine(directory, "brain.db"));
            store.SavePairIdentity("小明", "小星", "阿明");
            var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder())) { DataDirectory = directory };
            using (var manager = new TracePluginManager(store, services))
            {
                var plugin = new RealtimeCallPlugin();
                manager.RegisterExternal(plugin, pluginDataDirectory: directory);
                Require(string.IsNullOrEmpty(plugin.Metadata.LoadError), "通话插件实际注册成功");
                // Inject only transport input. Routing, environment, prompt and effector are production code.
                var hub = (CallDialogueHub)typeof(RealtimeCallPlugin).GetField("dialogue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(plugin);
                var registry = (RealtimeSessionRegistry)typeof(CallDialogueHub).GetField("registry", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hub);
                var pushed = new List<string>();
                var session = registry.Start("integration", "call-test", "debug");
                hub.Bind("integration", session, (text, _) => { pushed.Add(text); return Task.CompletedTask; });
                var input = hub.EnqueueTranscript("integration", session.SessionId, 0, "本人通过声纹后的测试发言", 1);
                Require(EnvironmentLogic.Resolve(store, "call-test", input).Visibility == "public", "声纹本身不授予私密记忆权限");
                EnvironmentLogic.SaveSettings(store, new EnvironmentSettings
                {
                    OwnerAccounts = new() { new() { PlatformId = "realtime.call", UserId = "voice-owner" } }
                });
                var environment = EnvironmentLogic.Resolve(store, "call-test", input);
                Require(environment.Visibility == "private" && environment.ContextConversationId == "call-test", "本人绑定加明确私密通话沿用原对话");
                EnvironmentLogic.Remember(store, environment);
                var wake = new PluginEventData { PluginId = "builtin.time", Role = "system_event", Content = "心跳" };
                var wakeEnvironment = EnvironmentLogic.Resolve(store, "call-test", wake);
                var turn = new TraceTurnContext("call-test", null, new(), 0, false, services, environment: wakeEnvironment, wake: KernelWakeValues.Mind);
                Require(manager.GetAvailableCatalog(turn).Any(x => x.Id == "realtime.call.text.send"), "原有主动联系唤醒能路由到当前电话");
                Require(AgentPromptContextLogic.Context(turn).Contains("无需填补沉默"), "持续通话状态进入 Agent 动态提示");
                var result = await manager.ExecuteAsync(new BrainCapabilityCallData { capability_id = "realtime.call.text.send",
                    arguments = new() { new() { name = "text", value = "在做什么呢？" } } }, turn, default);
                Require(result.Status == "success" && pushed.Count == 1 && result.ProducedEvent.IsOperational,
                    "主动通话发言走实际推送并等待播放回执");
                using var output = JsonDocument.Parse(pushed[0]);
                var id = output.RootElement.GetProperty("output_id").GetString();
                hub.TakeInbound();
                hub.ConfirmOutputDuration("integration", session.SessionId, 0, id, 1000);
                hub.ReportPlayback("integration", session.SessionId, 0, id, 1000, true);
                Require(EnvironmentLogic.Resolve(store, "call-test", hub.TakeInbound().Single()).EnvironmentId == environment.EnvironmentId,
                    "播放记忆保留同一通话环境");
                EnvironmentLogic.SaveSettings(store, new EnvironmentSettings());
                Require(!manager.GetAvailableCatalog(turn).Any(x => x.Id == "realtime.call.text.send"), "撤销本人绑定后禁止旧轮发言");
                hub.End("integration", session.SessionId);
                Require(!hub.CanSpeak(turn) && !AgentPromptContextLogic.Context(turn).Contains("【持续通话】"), "挂断后不再注入或主动发声");
            }
            Require(services.MindTurnPromptAppends.Count == 0, "卸载删除通话动态提示");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Realtime call integration checks passed: enrollment permission, private binding, existing proactive wake, playback environment and unload.");
    }
}
