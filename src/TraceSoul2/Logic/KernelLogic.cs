using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>主运转中枢：按入口换轨——心智维护当前时，外显开口，潜意识复盘；出门走代码链。</summary>
    public sealed class KernelLogic
    {
        private readonly IMemoryStore storage;
        private readonly MindLogic mind;
        private readonly ExpressorLogic expressor;
        private readonly TracePluginManager plugins;

        public TracePluginManager Plugins { get { return plugins; } }

        public KernelLogic(
            IMemoryStore storage,
            ILlmClient llm,
            TracePluginManager pluginManager)
        {
            this.storage = storage ?? throw new ArgumentNullException("storage");
            mind = new MindLogic(llm);
            expressor = new ExpressorLogic(llm);
            plugins = pluginManager ?? throw new ArgumentNullException("pluginManager");
            plugins.Services.Llm = llm;
        }

        public Task<ChatTurnResultData> ChatAsync(
            string conversationId,
            string userText,
            string sourceId = "dialogue.receive",
            int contextInjectionCount = 0,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var pair = storage.LoadPairIdentity();
            if (!pair.IsComplete)
                throw new InvalidOperationException("相处开始前，需要先保存两个人的名字。");
            var source = plugins.ReceiveMoment(sourceId, pair.Username, userText, null);
            return ProcessPluginEventAsync(
                conversationId, source, contextInjectionCount, cancellationToken);
        }

        public async Task<ChatTurnResultData> ProcessPluginEventAsync(
            string conversationId,
            PluginEventData source,
            int contextInjectionCount = 0,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("conversationId 不能为空。", "conversationId");
            if (source == null || string.IsNullOrWhiteSpace(source.Content))
                throw new ArgumentException("Moment 内容不能为空。", "source");
            var pair = storage.LoadPairIdentity();
            if (!pair.IsComplete)
                throw new InvalidOperationException("相处开始前，需要先保存两个人的名字。");
            contextInjectionCount = Math.Max(0, Math.Min(100, contextInjectionCount));

            var wake = KernelWakeLogic.Resolve(source);
            var triggerMoment = ToMoment(conversationId, source);
            storage.SaveMoment(triggerMoment);
            var recent = contextInjectionCount <= 0
                ? new List<MomentRecord>()
                : storage.GetRecentMoments(conversationId, contextInjectionCount + 1)
                    .Where(x => x.Id != triggerMoment.Id).Take(contextInjectionCount).ToList();
            var turn = new TraceTurnContext(
                conversationId,
                triggerMoment,
                recent,
                contextInjectionCount,
                wake == KernelWakeValues.Dialogue && pair.IsHumanMoment(source.Role),
                plugins.Services,
                wake);
            MouthLogic.NoticeInbound(source, turn);

            var catalog = plugins.GetAvailableCatalog(turn);
            BrainStructuredOutputData final;
            TraceCapabilityResultData expression = null;

            if (KernelWakeLogic.IsSubconscious(wake))
            {
                final = await RunSubconsciousAsync(turn, catalog, triggerMoment, cancellationToken);
            }
            else
            {
                var lived = await RunLivedMindAsync(turn, catalog, cancellationToken);
                final = lived.Final;
                expression = lived.Expression;
            }

            await RunTurnCompleteHooksAsync(turn);

            storage.SaveTurnReview(new TurnReviewRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = conversationId,
                TriggerMomentId = triggerMoment.Id,
                BrainMode = final.mode,
                BrainIntent = final.intent,
                DecisionSummary = final.decision_summary,
                CapabilitySummary = FormatResults(turn.Workspace.Results),
                FacetSummary = FormatFacets(final.facet_outputs),
                PayloadJson = BuildTurnSnapshot(turn),
                CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            return new ChatTurnResultData(
                expression == null ? string.Empty : expression.ProducedEvent.Content,
                final.mode,
                final.intent,
                final.decision_summary,
                turn.Workspace.ContextBlocks.ToList(),
                turn.Workspace.FacetOutputs.ToList(),
                turn.Workspace.Results.ToList());
        }

        private async Task<BrainStructuredOutputData> RunSubconsciousAsync(
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog,
            MomentRecord triggerMoment,
            CancellationToken cancellationToken)
        {
            await TryExecuteNerveAsync("identity.review", "潜意识复盘修订身份短卡", turn, catalog,
                new List<BrainCallArgumentData>
                {
                    new BrainCallArgumentData
                    {
                        name = "reason",
                        value = string.IsNullOrWhiteSpace(triggerMoment.Content)
                            ? "复盘" : triggerMoment.Content
                    }
                }, cancellationToken);
            var final = ExpressorLogic.NormalizeStep(new BrainStructuredOutputData
            {
                state = BrainStepStateValues.Finish,
                mode = BrainModeValues.Deep,
                should_express = false,
                intent = "复盘",
                decision_summary = "潜意识｜复盘"
            }, catalog, true, false, ResolveReplyChannel(turn));
            return final;
        }

        private async Task<LivedMindTurn> RunLivedMindAsync(
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog,
            CancellationToken cancellationToken)
        {
            var blocks = await plugins.BuildContextBlocksAsync(turn, cancellationToken);
            var pluginList = plugins.GetPlugins().Where(x => x.Enabled).ToList();
            await MemoryLiveWriteLogic.ObserveAndCommitAsync(turn, cancellationToken);

            var decision = await mind.DecideAsync(turn, null, false, cancellationToken);
            string leaveResult = null;
            TraceCapabilityResultData expression = null;
            if (decision.WantsLeave())
            {
                await ApplyInnerFacetsAsync(decision, turn, cancellationToken);
                var waiting = await expressor.ExpressAsync(
                    turn, pluginList, catalog, blocks, decision, string.Empty, true, null, cancellationToken);
                waiting = ExpressorLogic.NormalizeStep(
                    waiting, catalog, true, true, ResolveReplyChannel(turn));
                await ExecuteExpressionAsync(waiting, turn, turn.ConversationId, cancellationToken);
                await RunTurnCompleteHooksAsync(turn);
                leaveResult = await ExecuteLeaveAsync(decision, catalog, turn, cancellationToken);
                decision = await mind.DecideAsync(turn, leaveResult, true, cancellationToken);
            }

            var memoryFlesh = decision.WantsMemory()
                ? MemoryRecallLogic.Assemble(turn, decision, 5)
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(memoryFlesh))
            {
                turn.Workspace.Results.Add(new TraceCapabilityResultData
                {
                    CallId = "memory-recall",
                    CapabilityId = "memory.recall",
                    Status = "success",
                    Summary = "已按心智勾过的标签拼装记忆。",
                    Payload = memoryFlesh
                });
            }

            BrainStructuredOutputData final;
            if (turn.RequiresExpression || !string.IsNullOrWhiteSpace(leaveResult))
            {
                final = await expressor.ExpressAsync(
                    turn, pluginList, catalog, blocks, decision, memoryFlesh, false,
                    leaveResult, cancellationToken);
                final = ExpressorLogic.NormalizeStep(
                    final, catalog, true, true, ResolveReplyChannel(turn));
                StampDecision(final, decision);
                CloseReplyChannel(final, turn, catalog);
                MergePrivateFacets(final, decision, turn);
                await plugins.ApplyFacetOutputsAsync(final.facet_outputs, turn, cancellationToken);
                if (final.should_express)
                    expression = await ExecuteExpressionAsync(final, turn, turn.ConversationId, cancellationToken);
            }
            else
            {
                final = ExpressorLogic.NormalizeStep(new BrainStructuredOutputData
                {
                    state = BrainStepStateValues.Finish,
                    mode = BrainModeValues.Reflex,
                    should_express = false
                }, catalog, true, false, ResolveReplyChannel(turn));
                StampDecision(final, decision);
                MergePrivateFacets(final, decision, turn);
                await plugins.ApplyFacetOutputsAsync(final.facet_outputs, turn, cancellationToken);
            }

            MemoryLiveWriteLogic.TryCommitNewFact(turn, decision);
            CognitionLiveWriteLogic.TryCommit(turn, decision);
            await SyncUnfinishedWakeAsync(turn, catalog, cancellationToken);
            await RunPostSpeakAsync(decision, turn, catalog, cancellationToken);
            return new LivedMindTurn(final, expression);
        }

        private sealed class LivedMindTurn
        {
            public BrainStructuredOutputData Final { get; private set; }
            public TraceCapabilityResultData Expression { get; private set; }

            public LivedMindTurn(BrainStructuredOutputData final, TraceCapabilityResultData expression)
            {
                Final = final;
                Expression = expression;
            }
        }

        private async Task<string> ExecuteLeaveAsync(
            MindDecisionData decision,
            List<TraceContributionDescriptorData> catalog,
            TraceTurnContext turn,
            CancellationToken cancellationToken)
        {
            var want = (decision.leave ?? string.Empty).Trim();
            var embedding = turn != null && turn.Services != null ? turn.Services.Embedding : null;
            var nerve = await LeaveNerveLogic.SelectAsync(catalog, want, embedding, cancellationToken);
            if (nerve == null)
            {
                var missing = "没有外出工具。想办的事：" + want;
                turn.Workspace.Results.Add(new TraceCapabilityResultData
                {
                    CallId = "leave-missing",
                    CapabilityId = "leave.external",
                    Status = "empty",
                    Summary = missing,
                    Payload = missing
                });
                return missing;
            }

            var call = new BrainCapabilityCallData
            {
                call_id = "leave-" + Guid.NewGuid().ToString("N"),
                capability_id = nerve.Id,
                purpose = want,
                arguments = new List<BrainCallArgumentData>
                {
                    new BrainCallArgumentData { name = "query", value = want },
                    new BrainCallArgumentData { name = "q", value = want },
                    new BrainCallArgumentData { name = "reason", value = want }
                }
            };
            TraceCapabilityResultData result;
            try
            {
                result = await plugins.ExecuteAsync(call, turn, cancellationToken);
            }
            catch (Exception exception)
            {
                result = new TraceCapabilityResultData
                {
                    CallId = call.call_id,
                    CapabilityId = call.capability_id,
                    Status = "failed",
                    Summary = "外出失败：" + exception.Message,
                    Payload = exception.Message
                };
            }
            if (result == null)
            {
                result = new TraceCapabilityResultData
                {
                    CallId = call.call_id,
                    CapabilityId = call.capability_id,
                    Status = "failed",
                    Summary = "外出没有返回。",
                    Payload = string.Empty
                };
            }
            turn.Workspace.Results.Add(result);
            if (result.ProducedEvent != null)
                storage.SaveMoment(ToMoment(turn.ConversationId, result.ProducedEvent));
            var body = !string.IsNullOrWhiteSpace(result.Payload)
                ? result.Payload.Trim()
                : (result.Summary ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(body) ? "外出没有带回内容。" : body;
        }

        private async Task<TraceCapabilityResultData> ExecuteExpressionAsync(
            BrainStructuredOutputData final,
            TraceTurnContext turn,
            string conversationId,
            CancellationToken cancellationToken)
        {
            var replyChannel = ResolveReplyChannel(turn);
            if (!string.IsNullOrWhiteSpace(replyChannel) &&
                !string.Equals(final.expression_capability_id, replyChannel, StringComparison.Ordinal))
            {
                final.expression_capability_id = replyChannel;
            }
            var expressionCall = new BrainCapabilityCallData
            {
                call_id = "expression-" + Guid.NewGuid().ToString("N"),
                capability_id = final.expression_capability_id,
                purpose = "执行本轮对外表达",
                arguments = new List<BrainCallArgumentData>
                {
                    new BrainCallArgumentData { name = "text", value = final.reply }
                }
            };
            var expression = await plugins.ExecuteAsync(expressionCall, turn, cancellationToken);
            if (expression.Status != "success" || expression.ProducedEvent == null)
                throw new InvalidOperationException("外部表达器执行失败：" + expression.Summary);
            turn.Workspace.Results.Add(expression);
            storage.SaveMoment(ToMoment(conversationId, expression.ProducedEvent));

            foreach (var extra in final.expressions ?? new List<BrainCapabilityCallData>())
            {
                if (extra == null || string.IsNullOrWhiteSpace(extra.capability_id)) continue;
                var extraCall = new BrainCapabilityCallData
                {
                    call_id = "expr-" + Guid.NewGuid().ToString("N"),
                    capability_id = extra.capability_id,
                    purpose = extra.purpose ?? "附加表达",
                    arguments = extra.arguments ?? new List<BrainCallArgumentData>()
                };
                try
                {
                    var extraResult = await plugins.ExecuteAsync(extraCall, turn, cancellationToken);
                    if (extraResult != null && extraResult.ProducedEvent != null)
                        storage.SaveMoment(ToMoment(conversationId, extraResult.ProducedEvent));
                    turn.Workspace.Results.Add(extraResult);
                }
                catch (Exception exception)
                {
                    turn.Workspace.Results.Add(new TraceCapabilityResultData
                    {
                        CallId = extraCall.call_id,
                        CapabilityId = extraCall.capability_id,
                        Status = "failed",
                        Summary = "附加表达失败：" + exception.Message,
                        Payload = string.Empty
                    });
                }
            }
            return expression;
        }

        private async Task RunPostSpeakAsync(
            MindDecisionData decision,
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog,
            CancellationToken cancellationToken)
        {
            if (decision != null && decision.archive)
                await TryExecuteNerveAsync("memory.archive", "归档刚结束的话题", turn, catalog,
                    new List<BrainCallArgumentData>
                    {
                        new BrainCallArgumentData { name = "summary", value = Limit(decision.note, 80) }
                    }, cancellationToken);
            if (decision != null && decision.WantsReview())
                await TryExecuteNerveAsync("identity.review", "心智派出潜意识复盘", turn, catalog,
                    new List<BrainCallArgumentData>
                    {
                        new BrainCallArgumentData
                        {
                            name = "reason",
                            value = string.IsNullOrWhiteSpace(decision.note) ? "复盘" : Limit(decision.note, 80)
                        }
                    }, cancellationToken);
        }

        private async Task TryExecuteNerveAsync(
            string capabilityId,
            string purpose,
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog,
            List<BrainCallArgumentData> arguments,
            CancellationToken cancellationToken)
        {
            if (!(catalog ?? new List<TraceContributionDescriptorData>())
                    .Any(x => x != null && x.Id == capabilityId))
                return;
            var call = new BrainCapabilityCallData
            {
                call_id = capabilityId + "-" + Guid.NewGuid().ToString("N"),
                capability_id = capabilityId,
                purpose = purpose,
                arguments = arguments ?? new List<BrainCallArgumentData>()
            };
            try
            {
                var result = await plugins.ExecuteAsync(call, turn, cancellationToken);
                turn.Workspace.Results.Add(result);
                if (result != null && result.ProducedEvent != null)
                    storage.SaveMoment(ToMoment(turn.ConversationId, result.ProducedEvent));
            }
            catch (Exception exception)
            {
                turn.Workspace.Results.Add(new TraceCapabilityResultData
                {
                    CallId = call.call_id,
                    CapabilityId = capabilityId,
                    Status = "failed",
                    Summary = purpose + "失败：" + exception.Message,
                    Payload = string.Empty
                });
            }
        }

        private async Task RunTurnCompleteHooksAsync(TraceTurnContext turn)
        {
            foreach (var hook in turn.Services.TurnCompleteHooks)
            {
                try { await hook(turn); }
                catch (Exception exception)
                {
                    turn.Workspace.Results.Add(new TraceCapabilityResultData
                    {
                        CallId = "turn-complete",
                        CapabilityId = "turn.complete",
                        Status = "failed",
                        Summary = "收尾钩子失败：" + exception.Message,
                        Payload = string.Empty
                    });
                }
            }
        }

        private static void StampDecision(BrainStructuredOutputData output, MindDecisionData decision)
        {
            decision = MindLogic.Normalize(decision);
            output.mode = BrainModeValues.Reflex;
            output.intent = decision.note ?? string.Empty;
            output.decision_summary = decision.BeatValue() +
                                      (decision.ParseTags().Count == 0
                                          ? string.Empty
                                          : "｜" + string.Join("、", decision.ParseTags())) +
                                      (decision.WantsReview() ? "｜复盘" : string.Empty) +
                                      (decision.WantsLeave() ? "｜出门" : string.Empty) +
                                      (string.IsNullOrWhiteSpace(decision.cognition) ? string.Empty : "｜看法");
        }

        private static void CloseReplyChannel(
            BrainStructuredOutputData final,
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog)
        {
            if (final == null || !final.should_express) return;
            var replyChannel = MouthLogic.WinningTextChannel(catalog);
            if (string.IsNullOrWhiteSpace(replyChannel) ||
                string.Equals(final.expression_capability_id, replyChannel, StringComparison.Ordinal))
                return;
            var available = new HashSet<string>(
                (catalog ?? new List<TraceContributionDescriptorData>())
                    .Where(x => x.Kind == TraceContributionKindValues.Effector)
                    .Select(x => x.Id));
            if (available.Contains(replyChannel))
                final.expression_capability_id = replyChannel;
        }

        private static void MergePrivateFacets(
            BrainStructuredOutputData output, MindDecisionData mind, TraceTurnContext turn)
        {
            mind = MindLogic.Normalize(mind);
            output.facet_outputs = output.facet_outputs ?? new List<BrainFacetOutputData>();
            InnerRuntimeData runtime = null;
            if (turn != null && turn.Services != null && turn.Services.Storage != null)
                runtime = turn.Services.Storage.LoadOrCreateInnerRuntime(turn.ConversationId);
            var proposed = InnerLifeLogic.ProposeFromMind(mind, runtime);
            if (InnerLifeLogic.HasProposedWrite(proposed))
            {
                var snapshot = output.facet_outputs.FirstOrDefault(x => x != null && x.facet_id == "inner.snapshot");
                if (snapshot == null)
                {
                    snapshot = new BrainFacetOutputData
                    {
                        facet_id = "inner.snapshot",
                        changed = true,
                        summary = proposed.narrative ?? InnerLifeLogic.FormatHold(runtime),
                        fields = new List<BrainFacetFieldData>()
                    };
                    if (string.IsNullOrWhiteSpace(snapshot.summary)) snapshot.summary = "内心";
                    output.facet_outputs.Add(snapshot);
                }
                snapshot.changed = true;
                snapshot.fields = snapshot.fields ?? new List<BrainFacetFieldData>();
                WriteField(snapshot, "narrative", proposed.narrative);
                WriteField(snapshot, "mood", proposed.mood);
                WriteField(snapshot, "ongoing_activity", proposed.ongoing_activity);
                WriteField(snapshot, "unfinished_intent", proposed.unfinished_intent);
                if (proposed.attention != null)
                {
                    snapshot.fields.RemoveAll(x => x != null &&
                        (x.name == "attention_clear" ||
                         (x.name != null && x.name.StartsWith("attention_", StringComparison.Ordinal))));
                    if (proposed.attention.Count == 0)
                    {
                        snapshot.fields.Add(new BrainFacetFieldData { name = "attention_clear", value = "true" });
                    }
                    else
                    {
                        foreach (var item in proposed.attention)
                        {
                            if (item == null || string.IsNullOrWhiteSpace(item.content)) continue;
                            var kind = string.IsNullOrWhiteSpace(item.kind) ? "topic" : item.kind.Trim();
                            snapshot.fields.Add(new BrainFacetFieldData
                            {
                                name = "attention_" + kind,
                                value = item.content
                            });
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(proposed.narrative)) snapshot.summary = proposed.narrative;
            }
            if (!string.IsNullOrWhiteSpace(mind.new_fact) &&
                !output.facet_outputs.Any(x => x != null && x.facet_id == "memory.today.new"))
            {
                output.facet_outputs.Add(new BrainFacetOutputData
                {
                    facet_id = "memory.today.new",
                    changed = true,
                    summary = mind.new_fact,
                    fields = new List<BrainFacetFieldData>
                    {
                        new BrainFacetFieldData { name = "items", value = mind.new_fact }
                    }
                });
            }
            if (!string.IsNullOrWhiteSpace(mind.today) &&
                !output.facet_outputs.Any(x => x != null && x.facet_id == "day.trajectory"))
            {
                output.facet_outputs.Add(new BrainFacetOutputData
                {
                    facet_id = "day.trajectory",
                    changed = true,
                    summary = mind.today,
                    fields = new List<BrainFacetFieldData>
                    {
                        new BrainFacetFieldData { name = "trajectory", value = mind.today }
                    }
                });
            }
        }

        private static void WriteField(BrainFacetOutputData snapshot, string name, string value)
        {
            if (snapshot == null || value == null) return;
            snapshot.fields = snapshot.fields ?? new List<BrainFacetFieldData>();
            snapshot.fields.RemoveAll(x => x != null && x.name == name);
            snapshot.fields.Add(new BrainFacetFieldData { name = name, value = value });
        }

        private async Task ApplyInnerFacetsAsync(
            MindDecisionData decision,
            TraceTurnContext turn,
            CancellationToken cancellationToken)
        {
            var stub = new BrainStructuredOutputData
            {
                facet_outputs = new List<BrainFacetOutputData>()
            };
            MergePrivateFacets(stub, decision, turn);
            await plugins.ApplyFacetOutputsAsync(stub.facet_outputs, turn, cancellationToken);
        }

        private async Task SyncUnfinishedWakeAsync(
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog,
            CancellationToken cancellationToken)
        {
            if (turn == null || turn.Services == null || turn.Services.Storage == null) return;
            var runtime = turn.Services.Storage.LoadOrCreateInnerRuntime(turn.ConversationId);
            if (InnerLifeLogic.HasUnfinished(runtime))
            {
                await TryExecuteNerveAsync("time.continue", "未完成由时间叫醒心智", turn, catalog,
                    new List<BrainCallArgumentData>
                    {
                        new BrainCallArgumentData
                        {
                            name = "content",
                            value = InnerLifeLogic.FormatContinuation(runtime)
                        }
                    }, cancellationToken);
            }
            else
            {
                await TryExecuteNerveAsync("time.continue.clear", "放下未完成叫醒", turn, catalog,
                    new List<BrainCallArgumentData>(), cancellationToken);
            }
        }

        private static string FormatResults(IEnumerable<TraceCapabilityResultData> values)
        {
            return string.Join("\n", (values ?? Enumerable.Empty<TraceCapabilityResultData>())
                .Select(x => x.CapabilityId + " | " + x.Status + " | " + x.Summary));
        }

        /// <summary>逻辑已按优先级收口的文字嘴。</summary>
        private string ResolveReplyChannel(TraceTurnContext turn)
        {
            return MouthLogic.WinningTextChannel(plugins.GetAvailableCatalog(turn));
        }

        /// <summary>整轮链路快照：挂载块（截断）+ 回调结果（含召回证据），供控制台回看。</summary>
        private static string BuildTurnSnapshot(TraceTurnContext turn)
        {
            var snapshot = new TurnPayloadSnapshotData();
            foreach (var block in turn.Workspace.ContextBlocks)
            {
                if (block == null) continue;
                snapshot.blocks.Add(new TurnBlockSnapshotData
                {
                    facet_id = block.FacetId ?? string.Empty,
                    title = block.Title ?? string.Empty,
                    content = Limit(block.Content, 800)
                });
            }
            foreach (var result in turn.Workspace.Results)
            {
                if (result == null) continue;
                snapshot.results.Add(new TurnResultSnapshotData
                {
                    capability_id = result.CapabilityId ?? string.Empty,
                    status = result.Status ?? string.Empty,
                    summary = Limit(result.Summary, 300),
                    payload = Limit(result.Payload, 6000)
                });
            }
            return TraceJson.ToJson(snapshot);
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string FormatFacets(IEnumerable<BrainFacetOutputData> values)
        {
            return string.Join("\n", (values ?? Enumerable.Empty<BrainFacetOutputData>())
                .Select(x => x.facet_id + " | changed=" + x.changed + " | " + x.summary));
        }

        private static MomentRecord ToMoment(string conversationId, PluginEventData source)
        {
            return new MomentRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = conversationId.Trim(),
                Role = source.Role,
                Content = source.Content,
                Realm = source.Realm,
                EvidenceType = source.EvidenceType,
                SourcePluginId = source.PluginId,
                SourceEventId = source.ExternalEventId,
                PayloadJson = source.PayloadJson ?? string.Empty,
                MemoryStatus = "live",
                CreatedUnixMs = source.OccurredUnixMs > 0
                    ? source.OccurredUnixMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
    }
}
