using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;

namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>
    /// console 平台：系统最后的对话面。平台身份（连接桥 + 翻译，不做决策），
    /// 但不可禁用——管理器对它施加与内核组件相同的关闭保护。
    /// 内置编译，同时提供文字输入、定向历史神经和文字表达器。
    /// </summary>
    public sealed class DialogueTracePlugin : ITracePlugin
    {
        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = "builtin.dialogue",
            DisplayName = "console（本地文字）",
            Version = "1.0.0",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Platform,
            PlatformId = BodyIds.Console,
            Description = "保底对话平台：接收本地文字 Moment，按需提供受限的近期原文，并执行 Brain 的文字表达。"
        };

        public void Register(TracePluginContext context)
        {
            context.Services.Platforms.Register(new PlatformHandle
            {
                Id = "console",
                DisplayName = "控制台（本地文字）",
                IsConnected = () => true
            });
            context.AddMomentSource(new DialogueSource());
            context.AddCallable(new DialogueHistoryNerve());
            context.AddCallable(new DialogueEffector());
            context.AddCallable(new ConsolePrintEffector());
        }

        public void Shutdown() { }

        private sealed class DialogueSource : ITraceMomentSource
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "dialogue.receive",
                DisplayName = "文字输入",
                Description = DialogueTracePrompts.SourceDescription,
                Provides = "moment.dialogue.text",
                Boundary = "控制台文字",
                BodyId = BodyIds.Console,
                BodyTier = BodyTierValues.Shell,
                Organ = BodyOrganValues.Text,
                ParametersJsonSchema = "{role:string,content:string,payload_json?:string}"
            };

            public bool IsAvailable { get { return true; } }

            public PluginEventData Receive(string role, string content, string payloadJson = null)
            {
                if (string.IsNullOrWhiteSpace(content))
                    throw new ArgumentException("文字输入不能为空。", "content");
                return new PluginEventData
                {
                    PluginId = Descriptor.PluginId,
                    ExternalEventId = Guid.NewGuid().ToString("N"),
                    Role = string.IsNullOrWhiteSpace(role) ? "user" : role.Trim(),
                    Content = content.Trim(),
                    Realm = TraceRealmValues.Unclassified,
                    EvidenceType = EvidenceTypeValues.DialogueExplicit,
                    Organ = BodyOrganValues.Text,
                    PayloadJson = payloadJson ?? string.Empty,
                    Breaking = true,
                    OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
        }

        private sealed class DialogueHistoryNerve : ITraceCallableContribution
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "dialogue.recent_history",
                Kind = TraceContributionKindValues.CallableNerve,
                DisplayName = "定向读取近期原文",
                Description = DialogueTracePrompts.HistoryDescription,
                Provides = "conversation.recent_history.read",
                WhenToUse = DialogueTracePrompts.HistoryWhenToUse,
                WhenNotToUse = DialogueTracePrompts.HistoryWhenNotToUse,
                ParametersJsonSchema = "{limit:int(1..20),reason:string}",
                HasExternalSideEffect = false
            };

            public bool IsAvailable(TraceTurnContext context)
            {
                return context != null && context.RawHistoryLimit > 0 && context.RecentMoments.Count > 0;
            }

            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call,
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                int requested;
                if (!int.TryParse(call.GetArgument("limit", "4"), out requested)) requested = 4;
                var limit = Math.Max(1, Math.Min(Math.Min(20, context.RawHistoryLimit), requested));
                var values = context.RecentMoments.Skip(Math.Max(0, context.RecentMoments.Count - limit)).ToList();
                var pair = context.Services.Storage.LoadPairIdentity();
                var builder = new StringBuilder();
                foreach (var item in values)
                    builder.AppendLine(pair.LabelForRole(item.Role) + "：" + item.Content);
                return Task.FromResult(new TraceCapabilityResultData
                {
                    Status = "success",
                    Summary = "读取了 " + values.Count + " 条受限近期原文。",
                    Payload = builder.ToString().TrimEnd(),
                    EvidenceRefs = values.Select(x => "moment:" + x.Id).ToList()
                });
            }
        }

        private sealed class DialogueEffector : ITraceCallableContribution
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "dialogue.send",
                Kind = TraceContributionKindValues.Effector,
                DisplayName = "发送文字",
                Description = DialogueTracePrompts.EffectorDescription,
                Provides = "expression.text.send",
                Boundary = "控制台文字｜自由文本",
                BodyId = BodyIds.Console,
                BodyTier = BodyTierValues.Shell,
                Organ = BodyOrganValues.Text,
                ParametersJsonSchema = "{text:string}",
                HasExternalSideEffect = true
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call,
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                var text = call.GetArgument("text").Trim();
                if (text.Length == 0) throw new InvalidOperationException("发送文字不能为空。");
                var pair = context.Services.Storage.LoadPairIdentity();
                var produced = new PluginEventData
                {
                    PluginId = Descriptor.PluginId,
                    ExternalEventId = Guid.NewGuid().ToString("N"),
                    Role = pair.IsComplete ? pair.Assname : "assistant",
                    Content = text,
                    Realm = TraceRealmValues.Unclassified,
                    EvidenceType = EvidenceTypeValues.AssPerformed,
                    PayloadJson = string.Empty,
                    OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                return Task.FromResult(new TraceCapabilityResultData
                {
                    Status = "success",
                    Summary = "已通过本地文字对话表达。",
                    Payload = text,
                    ProducedEvent = produced,
                    EvidenceRefs = new List<string>()
                });
            }
        }

        /// <summary>
        /// 观察窗打印：任何来源的入站消息、任何身体的出站文字，都在 console 留一份运行痕迹。
        /// 痕迹是 operational 事件（运行留痕），不进语义 Moment，不进对话历史。
        /// Shell 层 + 独立 print 器官：不参与路由竞争，只由内核直接调用。
        /// </summary>
        private sealed class ConsolePrintEffector : ITraceCallableContribution
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "dialogue.print",
                Kind = TraceContributionKindValues.Effector,
                DisplayName = "console 打印",
                Description = "观察窗打印：把收发在 console 留一份运行痕迹，不进对话历史。",
                Provides = "console.print",
                Boundary = "控制台运行痕迹｜自由文本",
                BodyId = BodyIds.Console,
                BodyTier = BodyTierValues.Shell,
                Organ = "print",
                ParametersJsonSchema = "{text:string,direction?:in|out,via?:string,role?:string}",
                HasExternalSideEffect = false
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call,
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                var text = (call.GetArgument("text") ?? string.Empty).Trim();
                if (text.Length == 0) throw new InvalidOperationException("打印内容不能为空。");
                var direction = (call.GetArgument("direction") ?? "out").Trim();
                var via = (call.GetArgument("via") ?? string.Empty).Trim();
                var role = (call.GetArgument("role") ?? string.Empty).Trim();
                var arrow = string.Equals(direction, "in", StringComparison.Ordinal) ? "←" : "→";
                var label = string.IsNullOrWhiteSpace(via) ? "console" : via;
                var produced = new PluginEventData
                {
                    PluginId = Descriptor.PluginId,
                    ExternalEventId = Guid.NewGuid().ToString("N"),
                    Role = string.IsNullOrWhiteSpace(role) ? "system_event" : role,
                    Content = "[console 打印 " + arrow + " " + label + "] " + text,
                    Realm = TraceRealmValues.Meta,
                    EvidenceType = EvidenceTypeValues.PluginObserved,
                    PayloadJson = string.Empty,
                    IsOperational = true,
                    OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                return Task.FromResult(new TraceCapabilityResultData
                {
                    Status = "success",
                    Summary = "已在 console 打印。",
                    Payload = text,
                    ProducedEvent = produced,
                    EvidenceRefs = new List<string>()
                });
            }
        }
    }
}
