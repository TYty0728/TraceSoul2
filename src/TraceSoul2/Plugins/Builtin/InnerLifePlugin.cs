using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;

namespace TraceSoul2.Plugins.Builtin
{
    public sealed class InnerLifePlugin : ITracePlugin
    {
        private const string PluginId = "builtin.inner-life";

        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = PluginId,
            DisplayName = "内心",
            Version = "2.0.0",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Kernel,
            Description = "每轮只挂载一句当前内心；需要时提供完整自省，并在本轮完成时消费属于自己的变化。"
        };

        public void Register(TracePluginContext context)
        {
            context.AddMountedFacet(new InnerSnapshotFacet());
            context.AddCallable(new InspectInnerLifeNerve());
        }

        public void Shutdown() { }

        private sealed class InnerSnapshotFacet : ITraceMountedFacet
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "inner.snapshot",
                Kind = TraceContributionKindValues.MountedFacet,
                DisplayName = "当前内心切片",
                Description = InnerLifePrompts.FacetDescription,
                Provides = "inner_life.current_snapshot",
                OutputJsonSchema = "{changed:boolean,summary:string,fields:[narrative,relationship_update,mood,ongoing_activity,attention_topic|attention_activity|attention_concern]}",
                RefreshMode = TraceFacetRefreshValues.OncePerTurn,
                Priority = 90,
                MaxContextChars = 500,
                HasInternalMutation = true
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceContextBlockData> BuildContextAsync(
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                var runtime = context.Services.Storage.LoadOrCreateInnerRuntime(context.ConversationId);
                var mood = (runtime.Mood ?? string.Empty).Trim();
                var scene = OneLine(runtime.OngoingActivity);
                return Task.FromResult(new TraceContextBlockData
                {
                    Title = InnerLifePrompts.SnapshotTitle,
                    Content = InnerLifePrompts.SnapshotPrefix +
                              (scene.Length == 0 ? "（没有固定场景）" : scene) +
                              (mood.Length == 0 ? string.Empty : InnerLifePrompts.MoodWrapPrefix + mood + "）")
                });
            }

            public Task<TraceCapabilityResultData> ApplyOutputAsync(
                BrainFacetOutputData output,
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                if (output == null || !output.changed)
                    return Task.FromResult<TraceCapabilityResultData>(null);
                var current = context.Services.Storage.LoadOrCreateInnerRuntime(context.ConversationId);
                var clearHold = string.Equals(output.GetField("attention_clear", string.Empty), "true",
                    StringComparison.OrdinalIgnoreCase);
                var attention = clearHold ? new List<AttentionWriteData>() : null;
                if (!clearHold)
                {
                    var held = new List<AttentionWriteData>();
                    foreach (var field in output.fields ?? new List<BrainFacetFieldData>())
                    {
                        if (field == null || field.name == null || !field.name.StartsWith("attention_")) continue;
                        if (field.name == "attention_clear") continue;
                        var content = (field.value ?? string.Empty).Trim();
                        if (content.Length == 0) continue;
                        var kind = InnerLifeLogic.AttentionKindFromField(field.name);
                        held.Add(new AttentionWriteData
                        {
                            kind = kind,
                            content = content
                        });
                    }
                    if (held.Count > 0) attention = held.Take(2).ToList();
                }
                var proposed = new InnerRuntimeWriteData
                {
                    narrative = output.GetField("narrative", null),
                    relationship_update = output.GetField("relationship_update", null),
                    mood = output.GetField("mood", null),
                    ongoing_activity = output.GetField("ongoing_activity", null),
                    // 旧字段不再从内心切片写入；显式时间任务由时间插件单独保存。
                    unfinished_intent = string.Empty,
                    attention = attention,
                    asleep = ParseAsleepField(output.GetField("asleep", null))
                };
                var next = InnerLifeLogic.Reduce(current, proposed, context.Moment.Id,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                context.Services.Storage.SaveInnerRuntime(next);
                return Task.FromResult(new TraceCapabilityResultData
                {
                    Status = "success",
                    Summary = "这一拍的内心余波已更新到 revision " + next.Revision + "。",
                    Payload = InnerLifeLogic.Format(next),
                    EvidenceRefs = new List<string> { "moment:" + context.Moment.Id }
                });
            }
        }

        private sealed class InspectInnerLifeNerve : ITraceCallableContribution
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "inner.inspect",
                Kind = TraceContributionKindValues.CallableNerve,
                DisplayName = "完整自省",
                Description = InnerLifePrompts.InspectDescription,
                Provides = "inner_life.inspect",
                WhenToUse = InnerLifePrompts.InspectWhenToUse,
                WhenNotToUse = InnerLifePrompts.InspectWhenNotToUse,
                ParametersJsonSchema = "{reason:string}"
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call,
                TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                var runtime = context.Services.Storage.LoadOrCreateInnerRuntime(context.ConversationId);
                return Task.FromResult(new TraceCapabilityResultData
                {
                    Status = "success",
                    Summary = "读取了完整内心切片。",
                    Payload = InnerLifeLogic.Format(runtime),
                    EvidenceRefs = string.IsNullOrWhiteSpace(runtime.SourceMomentId)
                        ? new List<string>() : new List<string> { "moment:" + runtime.SourceMomentId }
                });
            }
        }

        private static bool? ParseAsleepField(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0) return null;
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                text == "1" || text == "睡着" || text == "睡下")
                return true;
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ||
                text == "0" || text == "醒着")
                return false;
            return null;
        }

        private static string OneLine(string value)
        {
            return string.Join(" ", (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }
    }
}
