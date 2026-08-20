using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;

namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>一个插件同时提供文字输入、定向历史神经和文字表达器。</summary>
    public sealed class DialogueTracePlugin : ITracePlugin
    {
        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = "builtin.dialogue",
            DisplayName = "本地文字对话",
            Version = "1.0.0",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Kernel,
            Description = "接收本地文字 Moment，按需提供受限的近期原文，并执行 Brain 的文字表达。"
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
        }

        public void Shutdown() { }

        private sealed class DialogueSource : ITraceMomentSource
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "dialogue.receive",
                DisplayName = "文字输入",
                Description = "当前 {username} 可以通过本地文字界面向 {assname} 说话；这是外部感官入口，不可由 Brain 主动调用。",
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
                Description = "当当前话语依赖刚才说过的话时，读取允许数量内的近期原文。原始上下文上限为0时不可用。",
                Provides = "conversation.recent_history.read",
                WhenToUse = "当前话语包含‘刚才、那个、继续’等指代，且仅靠当前 Moment 无法理解时。",
                WhenNotToUse = "普通寒暄，或人生记忆召回。",
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
                Description = "通过当前本地对话界面对 {username} 表达一段文字。",
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
    }
}
