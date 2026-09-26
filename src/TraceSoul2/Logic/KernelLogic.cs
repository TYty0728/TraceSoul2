using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Prompts;
using TraceSoul2.Plugins;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>事件入口、Agent 行动循环与执行反馈；复盘仍由独立后台任务触发。</summary>
    public sealed class KernelLogic
    {
        private readonly IMemoryStore storage;
        private readonly AgentLoopLogic agent;
        private readonly ExpressorLogic expressor;
        private readonly TracePluginManager plugins;
        private DeferredTurnWork deferredWork;

        public TracePluginManager Plugins { get { return plugins; } }

        public sealed class DeferredTurnWork
        {
            private readonly Func<CancellationToken, Task<Func<CancellationToken, Task>>> analyze;

            public string TraceId { get; private set; }

            internal DeferredTurnWork(
                string traceId,
                Func<CancellationToken, Task<Func<CancellationToken, Task>>> analyze)
            {
                TraceId = traceId ?? string.Empty;
                this.analyze = analyze ?? throw new ArgumentNullException("analyze");
            }

            public Task<Func<CancellationToken, Task>> AnalyzeAsync(CancellationToken cancellationToken)
            {
                return analyze(cancellationToken);
            }
        }

        /// <summary>每个 Kernel 实例只产出一份轮后工作；宿主取走后放入独立后台队列。</summary>
        public DeferredTurnWork TakeDeferredWork()
        {
            var value = deferredWork;
            deferredWork = null;
            return value;
        }

        public KernelLogic(
            IMemoryStore storage,
            ILlmClient llm,
            TracePluginManager pluginManager)
        {
            this.storage = storage ?? throw new ArgumentNullException("storage");
            agent = new AgentLoopLogic(llm);
            expressor = new ExpressorLogic(llm);
            plugins = pluginManager ?? throw new ArgumentNullException("pluginManager");
            plugins.Services.Llm = llm;
            if (plugins.Services.ContextPack == null)
                plugins.Services.ContextPack = new LlmContextAssembler();
        }

        public Task<ChatTurnResultData> ChatAsync(
            string conversationId,
            string userText,
            string sourceId = "dialogue.receive",
            int historyWindowMax = 0,
            CancellationToken cancellationToken = default(CancellationToken),
            string traceId = null,
            int historyWindowAlign = 0)
        {
            var pair = storage.LoadPairIdentity();
            if (!pair.IsComplete)
                throw new InvalidOperationException("相处开始前，需要先保存两个人的名字。");
            var source = plugins.ReceiveMoment(sourceId, pair.Username, userText, null);
            source.TraceId = traceId;
            source.Breaking = true;
            return ProcessPluginEventAsync(
                conversationId, source, historyWindowMax, cancellationToken, historyWindowAlign);
        }

        public async Task<ChatTurnResultData> ProcessPluginEventAsync(
            string conversationId,
            PluginEventData source,
            int historyWindowMax = 0,
            CancellationToken cancellationToken = default(CancellationToken),
            int historyWindowAlign = 0)
        {
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("conversationId 不能为空。", "conversationId");
            if (source == null || string.IsNullOrWhiteSpace(source.Content))
                throw new ArgumentException("Moment 内容不能为空。", "source");
            if (string.IsNullOrWhiteSpace(source.TraceId))
                source.TraceId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var totalTimer = Stopwatch.StartNew();
            plugins.Services.LogTiming(source.TraceId, "Brain 整轮开始",
                detail: "source=" + (source.PluginId ?? string.Empty));
            var pair = storage.LoadPairIdentity();
            if (!pair.IsComplete)
                throw new InvalidOperationException("相处开始前，需要先保存两个人的名字。");
            var historyWindow = CommonContextPackLogic.NormalizeHistoryWindow(
                historyWindowMax, historyWindowAlign);

            var prepareTimer = Stopwatch.StartNew();
            var wake = KernelWakeLogic.Resolve(source);
            var inner = storage.LoadOrCreateInnerRuntime(conversationId);
            if (inner.Asleep && HeartbeatLogic.ShouldSkipWhileAsleep(source, pair, wake) &&
                !HeartbeatLogic.IsBreaking(source, pair))
            {
                PersistPluginEvent(conversationId, source);
                plugins.Services.LogTiming(source.TraceId, "睡着，跳过非打破性 Moment",
                    prepareTimer.ElapsedMilliseconds);
                return new ChatTurnResultData(
                    string.Empty, "sleep", "睡着", "睡着｜跳过",
                    new List<TraceContextBlockData>(),
                    new List<BrainFacetOutputData>(),
                    new List<TraceCapabilityResultData>());
            }
            if (inner.Idle && HeartbeatLogic.ShouldSkipWhileIdle(source, pair) &&
                !HeartbeatLogic.IsBreaking(source, pair))
            {
                PersistPluginEvent(conversationId, source);
                plugins.Services.LogTiming(source.TraceId, "空闲，跳过旧版连续思考事件",
                    prepareTimer.ElapsedMilliseconds);
                return new ChatTurnResultData(
                    string.Empty, "idle", "空闲", "空闲｜跳过",
                    new List<TraceContextBlockData>(),
                    new List<BrainFacetOutputData>(),
                    new List<TraceCapabilityResultData>());
            }

            if (VisionLogic.HasInboundImages(source.PayloadJson))
            {
                var visionTimer = Stopwatch.StartNew();
                var seen = await VisionLogic.SeeInboundAsync(source, plugins.Services, cancellationToken);
                source.Content = VisionLogic.AttachSeen(source.Content, seen);
                var visionStage = seen == CorePrompts.Vision.LoadFailed ? "识图未成功" :
                    seen == CorePrompts.Vision.Unconfigured ? "识图未配置" : "识图完成";
                plugins.Services.LogTiming(source.TraceId, visionStage, visionTimer.ElapsedMilliseconds,
                    "chars=" + (seen ?? string.Empty).Length);
            }

            // 运行事件也会临时成为本轮刺激，但不会进入可复盘的 Moment 账本。
            var triggerMoment = PersistPluginEvent(conversationId, source);
            if ((inner.Asleep || inner.Idle) && HeartbeatLogic.IsBreaking(source, pair))
            {
                var woken = InnerLifeLogic.WithAwake(inner, triggerMoment.Id,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (woken != inner) storage.SaveInnerRuntime(woken);
                inner = woken;
            }
            else if (inner.Idle &&
                     !KernelWakeLogic.IsNightResidue(wake) &&
                     !KernelWakeLogic.IsSubconscious(wake))
            {
                var woken = InnerLifeLogic.WithIdle(inner, false, triggerMoment.Id,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (woken != inner) storage.SaveInnerRuntime(woken);
                inner = woken;
            }
            // 原始对话只承担语言衔接。窗口长度在 [min, max] 之间，每攒满 align 条才整体前移；
            // 起点必须按全部对话条数算，不能先截成 max 条再对齐，否则每轮都会换掉历史第一条。
            var recent = LoadAlignedDialogueHistory(
                storage, pair, conversationId, triggerMoment,
                historyWindow.Min, historyWindow.Align);
            var turn = new TraceTurnContext(
                conversationId,
                triggerMoment,
                recent,
                historyWindow.Min,
                wake == KernelWakeValues.Dialogue && pair.IsHumanMoment(source.Role),
                plugins.Services,
                wake,
                source.TraceId,
                historyWindow.Align);
            MouthLogic.NoticeInbound(source, turn);
            // console 观察窗：任何来源的入站消息都在 console 留一份运行痕迹（不入对话历史）。
            if (!source.IsOperational &&
                !string.Equals(source.PluginId, "builtin.dialogue", StringComparison.Ordinal))
            {
                await MirrorToConsoleAsync(turn, conversationId, "in",
                    ConsoleViaLabel(source.PluginId), source.Content, source.Role);
            }

            var turnFinished = false;
            try
            {

            var catalog = plugins.GetAvailableCatalog(turn);
            plugins.Services.LogTiming(turn.TraceId, "输入落库与轮次准备完成",
                prepareTimer.ElapsedMilliseconds,
                "wake=" + wake + "｜catalog=" + catalog.Count +
                "｜history=" + recent.Count +
                "/" + historyWindow.Min + "-" + historyWindow.Max +
                " align=" + historyWindow.Align);
            BrainStructuredOutputData final;
            MindDecisionData mindDecision = null;
            TraceCapabilityResultData expression = null;
            var responseFlushed = false;

            if (KernelWakeLogic.IsSubconscious(wake))
            {
                var branchTimer = Stopwatch.StartNew();
                final = await RunSubconsciousAsync(turn, catalog, triggerMoment, cancellationToken);
                plugins.Services.LogTiming(turn.TraceId, "潜意识轨完成", branchTimer.ElapsedMilliseconds);
            }
            else if (KernelWakeLogic.IsNightResidue(wake))
            {
                var branchTimer = Stopwatch.StartNew();
                var night = await RunNightResidueAsync(turn, catalog, triggerMoment, cancellationToken);
                final = night.Final;
                expression = night.Expression;
                responseFlushed = night.ResponseFlushed;
                plugins.Services.LogTiming(turn.TraceId, "夜间余温轨完成", branchTimer.ElapsedMilliseconds,
                    night.Final == null ? string.Empty : night.Final.decision_summary);
            }
            else
            {
                var branchTimer = Stopwatch.StartNew();
                var lived = await RunAgentTurnAsync(turn, catalog, cancellationToken);
                final = lived.Final;
                mindDecision = lived.MindDecision;
                expression = lived.Expression;
                responseFlushed = lived.ResponseFlushed;
                plugins.Services.LogTiming(turn.TraceId, "Agent 对话循环完成", branchTimer.ElapsedMilliseconds);
            }

            if (!responseFlushed)
            {
                var hooksTimer = Stopwatch.StartNew();
                await RunTurnCompleteHooksAsync(turn);
                plugins.Services.LogTiming(turn.TraceId, "整轮收尾钩子完成", hooksTimer.ElapsedMilliseconds);
            }
            else
            {
                plugins.Services.LogTiming(turn.TraceId, "整轮收尾钩子已在记忆观察前完成", 0);
            }

            await TryCompleteExpressionAsync(turn);

            var reviewTimer = Stopwatch.StartNew();
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
                PayloadJson = BuildTurnSnapshot(turn, mindDecision),
                CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            plugins.Services.LogTiming(turn.TraceId, "轮次审查落库完成", reviewTimer.ElapsedMilliseconds);
            plugins.Services.LogTiming(turn.TraceId, "Brain 整轮完成", totalTimer.ElapsedMilliseconds,
                "mode=" + final.mode + "｜results=" + turn.Workspace.Results.Count);

            turnFinished = true;
            return new ChatTurnResultData(
                expression == null ? string.Empty : expression.ProducedEvent.Content,
                final.mode,
                final.intent,
                final.decision_summary,
                turn.Workspace.ContextBlocks.ToList(),
                turn.Workspace.FacetOutputs.ToList(),
                turn.Workspace.Results.ToList(),
                mindDecision);
            }
            finally
            {
                // 表达器、平台发送或轮次审查异常时也不能让「正在输入」悬挂。
                if (!turnFinished)
                    Interlocked.Exchange(
                        ref GetExpressionLifecycleState(turn).PendingOutboundBatches, 0);
                await TryCompleteExpressionAsync(turn);
            }
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

        private async Task<AgentTurnResult> RunNightResidueAsync(
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog,
            MomentRecord triggerMoment,
            CancellationToken cancellationToken)
        {
            var dayKey = NightResidueLogic.DayKeyFromContent(
                triggerMoment == null ? string.Empty : triggerMoment.Content);
            var seed = NightResidueLogic.LoadSeed(storage, turn.ConversationId, dayKey);
            if (!seed.HasWarmth)
            {
                NightResidueLogic.Remember(storage, dayKey, NightResidueLogic.StatusSkipped);
                return new AgentTurnResult(
                    ExpressorLogic.NormalizeStep(new BrainStructuredOutputData
                    {
                        state = BrainStepStateValues.Finish,
                        mode = BrainModeValues.Deep,
                        should_express = false,
                        intent = "夜间余温",
                        decision_summary = "夜间余温｜空天"
                    }, catalog, true, false, ResolveReplyChannel(turn)),
                    null, false, null);
            }

            var contextTimer = Stopwatch.StartNew();
            var blocks = await plugins.BuildContextBlocksAsync(turn, cancellationToken);
            plugins.Services.LogTiming(turn.TraceId, "夜间余温上下文组装完成", contextTimer.ElapsedMilliseconds,
                "blocks=" + blocks.Count + "｜events=" + seed.Events.Count);
            var expressionCatalog = FilterNightResidueCatalog(
                FilterExpressionCatalog(catalog, turn));

            var expressTimer = Stopwatch.StartNew();
            var final = await expressor.ExpressNightResidueAsync(
                turn, blocks, seed, expressionCatalog, cancellationToken);
            plugins.Services.LogTiming(turn.TraceId, "夜间余温表达完成", expressTimer.ElapsedMilliseconds,
                final.decision_summary);
            final = ExpressorLogic.NormalizeStep(
                final, expressionCatalog, true, false, ResolveReplyChannel(turn));
            CloseReplyChannel(final, turn, expressionCatalog);

            TraceCapabilityResultData expression = null;
            var responseFlushed = false;
            if (final.should_express && !string.IsNullOrWhiteSpace(final.reply))
            {
                await RunExpressionStartingHooksAsync(turn);
                var outputTimer = Stopwatch.StartNew();
                expression = await ExecuteExpressionAsync(final, turn, turn.ConversationId, cancellationToken);
                plugins.Services.LogTiming(turn.TraceId, "夜间余温对外表达链完成", outputTimer.ElapsedMilliseconds,
                    "capability=" + final.expression_capability_id);
                responseFlushed = true;
                NightResidueLogic.Remember(storage, dayKey, NightResidueLogic.StatusSent);
            }
            else
            {
                NightResidueLogic.Remember(storage, dayKey, NightResidueLogic.StatusSilent);
            }

            return new AgentTurnResult(final, expression, responseFlushed, null);
        }

        private static List<TraceContributionDescriptorData> FilterNightResidueCatalog(
            IEnumerable<TraceContributionDescriptorData> catalog)
        {
            return (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .Where(x => x == null ||
                            x.Kind != TraceContributionKindValues.Effector ||
                            string.Equals(MouthLogic.OrganOf(x), BodyOrganValues.Text, StringComparison.Ordinal))
                .ToList();
        }

        private async Task<AgentTurnResult> RunAgentTurnAsync(
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog,
            CancellationToken cancellationToken)
        {
            var contextTimer = Stopwatch.StartNew();
            var blocks = await plugins.BuildContextBlocksAsync(turn, cancellationToken);
            plugins.Services.LogTiming(turn.TraceId, "上下文组装完成", contextTimer.ElapsedMilliseconds,
                "blocks=" + blocks.Count);
            var pluginList = plugins.GetPlugins().Where(x => x.Enabled).ToList();
            var expressionCatalog = FilterExpressionCatalog(catalog, turn);
            var recallTopK = turn.Services.Recall != null && turn.Services.Recall.DefaultTopK > 0
                ? Math.Max(1, Math.Min(10, turn.Services.Recall.DefaultTopK))
                : 3;
            var preludeTimer = Stopwatch.StartNew();
            var naturallyAwakenedPast = MemoryRecallLogic.Preview(turn, recallTopK);
            turn.Workspace.SharedMemory = naturallyAwakenedPast ?? string.Empty;
            plugins.Services.LogTiming(turn.TraceId, "记忆预激活完成", preludeTimer.ElapsedMilliseconds,
                "top_k=" + recallTopK + "｜chars=" + naturallyAwakenedPast.Length);

            // QQ 等对话入口本轮必须回应：在第一次 Agent 请求前就通知平台。
            // 心跳/时间触发等可能保持沉默的轮次，仍由后面的表达分支在决定开口后通知。
            if (turn.RequiresExpression)
                await RunExpressionStartingHooksAsync(turn);

            var decision = await agent.RunAsync(turn, naturallyAwakenedPast,
                () => plugins.GetAvailableActionCatalog(turn),
                (call, token) => ExecuteAgentActionAsync(call, turn, token), cancellationToken,
                async token => { await plugins.BuildContextBlocksAsync(turn, token); });
            expressionCatalog = FilterExpressionCatalog(plugins.GetAvailableCatalog(turn), turn);
            blocks = turn.Workspace.ContextBlocks.ToList();
            // Agent 已在同一步决定是否说话；不再用旧心智启发式强迫第二次生成。
            TraceCapabilityResultData expression = null;
            var needsReply = !string.IsNullOrWhiteSpace(decision.reply);
            BrainStructuredOutputData final;
            if (needsReply)
            {
                await RunExpressionStartingHooksAsync(turn);
                final = decision.refine
                    ? await expressor.ExpressAsync(turn, pluginList, expressionCatalog, blocks, decision,
                        naturallyAwakenedPast, false, null, cancellationToken)
                    : ExpressorLogic.PrepareDirectReply(decision.reply, turn, expressionCatalog, decision);
                final = ExpressorLogic.NormalizeStep(final, expressionCatalog, true, true, ResolveReplyChannel(turn));
                CloseReplyChannel(final, turn, expressionCatalog);
            }
            else
            {
                final = new BrainStructuredOutputData
                {
                    state = BrainStepStateValues.Finish, mode = BrainModeValues.Reflex,
                    should_express = false, reply = string.Empty
                };
                // 独立动作/语音已在循环内执行，无需制造一条文字回复。
            }
            StampDecision(final, decision);
            MergePrivateFacets(final, decision, turn);
            await plugins.ApplyFacetOutputsAsync(final.facet_outputs, turn, cancellationToken);
            if (final.should_express)
                expression = await ExecuteExpressionAsync(final, turn, turn.ConversationId, cancellationToken);

            var responseFlushed = false;
            if (expression != null)
            {
                var flushTimer = Stopwatch.StartNew();
                await RunTurnCompleteHooksAsync(turn);
                responseFlushed = true;
                plugins.Services.LogTiming(turn.TraceId, "回复已发送，开始轮后慢任务", flushTimer.ElapsedMilliseconds);
                await TryCompleteExpressionAsync(turn);
            }

            var persistTimer = Stopwatch.StartNew();
            // 白天只维护本日实时样本（内心/生活/轨迹/今日新识）。长期事件、认知与身份卡
            // 统一留给完整日终复盘，避免同一批 Moment 被多条归档旁路提前消费。
            await SyncHeartbeatAsync(turn, catalog, decision, cancellationToken);
            plugins.Services.LogTiming(turn.TraceId, "轮后实时状态处理完成", persistTimer.ElapsedMilliseconds);
            return new AgentTurnResult(final, expression, responseFlushed, decision);
        }

        private sealed class AgentTurnResult
        {
            public BrainStructuredOutputData Final { get; private set; }
            public TraceCapabilityResultData Expression { get; private set; }
            public bool ResponseFlushed { get; private set; }
            public MindDecisionData MindDecision { get; private set; }

            public AgentTurnResult(
                BrainStructuredOutputData final,
                TraceCapabilityResultData expression,
                bool responseFlushed,
                MindDecisionData mindDecision)
            {
                Final = final;
                Expression = expression;
                ResponseFlushed = responseFlushed;
                MindDecision = mindDecision;
            }
        }

        private async Task<TraceCapabilityResultData> ExecuteAgentActionAsync(
            BrainCapabilityCallData call, TraceTurnContext turn, CancellationToken cancellationToken)
        {
            TraceCapabilityResultData result;
            if (call.capability_id == "memory.recall")
            {
                var query = Limit(call.GetArgument("query"), 500);
                var recalled = MemoryRecallLogic.Assemble(turn,
                    new MindDecisionData { beat = MindBeatValues.Memory, query = query }, 6, out var hasEvidence);
                result = new TraceCapabilityResultData { CallId = call.call_id, CapabilityId = call.capability_id,
                    Status = hasEvidence ? "success" : "empty",
                    Summary = hasEvidence ? "检索到候选记忆，是否能回答问题以证据为准。" : "未找到相关记忆。", Payload = recalled };
            }
            else if (call.capability_id == "execution.cancel")
            {
                var accepted = turn.Services.Executions.Cancel(call.GetArgument("execution_id"), turn.ConversationId);
                result = new TraceCapabilityResultData { CallId = call.call_id, CapabilityId = call.capability_id,
                    Status = accepted ? "success" : "failed", Summary = accepted ? "已请求停止，尚待设备确认。" : "未找到本会话可停止的执行。" };
            }
            else if ((IsQzonePublishCapability(call.capability_id) && !AllowsQzonePublish(turn)) ||
                     (IsQqMoodCapability(call.capability_id) && !AllowsQqMood(turn)))
            {
                result = new TraceCapabilityResultData { CallId = call.call_id, CapabilityId = call.capability_id,
                    Status = "skipped", Summary = "当前入站未授权该发布或状态修改，未执行。" };
            }
            else
            {
                // 每次执行重新核对能力可用性；插件自身还会验证参数和设备约束。
                var descriptor = plugins.GetAvailableActionCatalog(turn).FirstOrDefault(x => x.Id == call.capability_id);
                if (descriptor == null)
                    result = new TraceCapabilityResultData { CallId = call.call_id, CapabilityId = call.capability_id,
                        Status = "failed", Summary = "能力已离线或不可用。" };
                else
                {
                    if (descriptor.Kind == TraceContributionKindValues.Effector)
                        await RunExpressionStartingHooksAsync(turn);
                    try
                    {
                        result = await ExecuteWithQzoneMediaAsync(call, turn, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        result = new TraceCapabilityResultData { CallId = call.call_id, CapabilityId = call.capability_id,
                            Status = "failed", Summary = "执行失败；不可假设未产生副作用：" + error.Message };
                    }
                    if (result?.ProducedEvent != null &&
                        ((result.Status != "running" && result.Status != "accepted") || result.ProducedEvent.IsOperational))
                        PersistPluginEvent(turn.ConversationId, result.ProducedEvent);
                    // 平台合并发送必须在反馈给 Agent 前提交，避免把仅暂存的语音/文字当作已发送。
                    if (descriptor.Kind == TraceContributionKindValues.Effector)
                        await RunTurnCompleteHooksAsync(turn);
                }
            }
            turn.Workspace.Results.Add(result);
            return result;
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
            TraceCapabilityResultData expression = null;
            if (!string.IsNullOrWhiteSpace(final.expression_capability_id))
            {
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
                expression = await plugins.ExecuteAsync(expressionCall, turn, cancellationToken);
                if (expression.Status != "success" || expression.ProducedEvent == null)
                    throw new InvalidOperationException("外部表达器执行失败：" + expression.Summary);
                turn.Workspace.Results.Add(expression);
                PersistPluginEvent(conversationId, expression.ProducedEvent);
            }
            // console 观察窗：她说的每句话都在 console 留一份运行痕迹；
            // 没有活的真实身体时，这份打印就是保底出口。调试口直答（文字本就走了 console）不重复打印。
            if (!string.Equals(final.expression_capability_id, "dialogue.send", StringComparison.Ordinal))
            {
                await MirrorToConsoleAsync(turn, conversationId, "out",
                    ConsoleViaLabel(replyChannel), final.reply, null);
            }

            List<BrainCapabilityCallData> immediate;
            List<BrainCapabilityCallData> images;
            ExpressorLogic.PartitionExpressions(final.expressions, out immediate, out images);
            foreach (var extra in immediate)
            {
                if (IsQzonePublishCapability(extra.capability_id) && !AllowsQzonePublish(turn))
                {
                    plugins.Services.LogTiming(turn.TraceId, "空间发布已拦截", 0,
                        "当前 Moment 没有明确发布指令");
                    turn.Workspace.Results.Add(new TraceCapabilityResultData
                    {
                        CallId = string.IsNullOrWhiteSpace(extra.call_id) ? "expr-qzone-blocked" : extra.call_id,
                        CapabilityId = extra.capability_id,
                        Status = "skipped",
                        Summary = "空间发布已拦截：用户没有明确要求发布。",
                        Payload = string.Empty
                    });
                    continue;
                }
                if (IsQqMoodCapability(extra.capability_id) && !AllowsQqMood(turn))
                {
                    plugins.Services.LogTiming(turn.TraceId, "QQ心情已拦截", 0,
                        "当前 Moment 没有明确改签名/状态指令");
                    turn.Workspace.Results.Add(new TraceCapabilityResultData
                    {
                        CallId = string.IsNullOrWhiteSpace(extra.call_id) ? "expr-mood-blocked" : extra.call_id,
                        CapabilityId = extra.capability_id,
                        Status = "skipped",
                        Summary = "QQ心情已拦截：用户没有明确要求改签名或状态。",
                        Payload = string.Empty
                    });
                    continue;
                }
                await ExecuteSyncExtraAsync(extra, turn, conversationId, cancellationToken);
            }

            await RunTurnCompleteHooksAsync(turn);
            if (images.Count > 0) EnqueueImageWork(images, turn, conversationId);
            return expression;
        }

        private async Task ExecuteSyncExtraAsync(
            BrainCapabilityCallData extra,
            TraceTurnContext turn,
            string conversationId,
            CancellationToken cancellationToken)
        {
            var extraCall = CloneCall(extra, null, null);
            try
            {
                var extraResult = await ExecuteWithQzoneMediaAsync(extraCall, turn, cancellationToken);
                if (extraResult != null && extraResult.ProducedEvent != null)
                    PersistPluginEvent(conversationId, extraResult.ProducedEvent);
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

        /// <summary>
        /// 生图不挡开口：立刻开始生成，话先发出去；成功后再单独发图。
        /// 生成不碰 SQLite；发送在宿主轮后队列里重新取对话锁。
        /// </summary>
        private void EnqueueImageWork(
            List<BrainCapabilityCallData> images,
            TraceTurnContext turn,
            string conversationId)
        {
            var lifecycle = GetExpressionLifecycleState(turn);
            Interlocked.Increment(ref lifecycle.PendingOutboundBatches);
            var generateTasks = images.Select(call => PrepareImageAsync(call, turn)).ToList();
            plugins.Services.LogTiming(turn.TraceId, "TA的相机 后台生图已开始",
                detail: "calls=" + generateTasks.Count);
            EnqueueDeferred(new DeferredTurnWork(turn.TraceId, async ct =>
            {
                PreparedOutboundImage[] prepared = Array.Empty<PreparedOutboundImage>();
                try
                {
                    prepared = await Task.WhenAll(generateTasks);
                }
                catch (Exception exception)
                {
                    plugins.Services.LogTiming(turn.TraceId, "TA的相机 后台生图失败", 0, exception.Message);
                }
                return async sendCt =>
                {
                    try
                    {
                        foreach (var item in prepared)
                            await SendPreparedImageAsync(item, turn, conversationId, sendCt);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref lifecycle.PendingOutboundBatches);
                        await TryCompleteExpressionAsync(turn);
                    }
                };
            }));
        }

        private async Task<PreparedOutboundImage> PrepareImageAsync(
            BrainCapabilityCallData extra,
            TraceTurnContext turn)
        {
            if (!IsImageGenerator(extra))
                return new PreparedOutboundImage { Call = extra, Prepared = false };
            var generateCall = CloneCall(extra, "generate", null);
            var result = await plugins.ExecuteAsync(generateCall, turn, CancellationToken.None);
            return new PreparedOutboundImage
            {
                Call = extra,
                Prepared = true,
                GenerateResult = result
            };
        }

        private async Task SendPreparedImageAsync(
            PreparedOutboundImage item,
            TraceTurnContext turn,
            string conversationId,
            CancellationToken cancellationToken)
        {
            if (item == null || item.Call == null) return;
            BrainCapabilityCallData sendCall;
            if (item.Prepared)
            {
                if (item.GenerateResult == null ||
                    !string.Equals(item.GenerateResult.Status, "success", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(item.GenerateResult.Payload))
                {
                    plugins.Services.LogTiming(turn.TraceId, "TA的相机 生图未成功，不发图", 0,
                        item.GenerateResult == null ? "empty" : item.GenerateResult.Summary);
                    return;
                }
                sendCall = CloneCall(item.Call, "send", item.GenerateResult.Payload);
            }
            else sendCall = CloneCall(item.Call, null, null);

            try
            {
                var extraResult = await plugins.ExecuteAsync(sendCall, turn, cancellationToken);
                var sent = extraResult != null && extraResult.Status == "success";
                if (sent && extraResult.ProducedEvent != null)
                    PersistPluginEvent(conversationId, extraResult.ProducedEvent);
                plugins.Services.LogTiming(turn.TraceId, sent ? "TA的相机 后台图已发出" : "TA的相机 后台发图未成功", 0,
                    extraResult == null ? "null" : extraResult.Summary);
            }
            catch (Exception exception)
            {
                plugins.Services.LogTiming(turn.TraceId, "TA的相机 后台发图失败", 0, exception.Message);
            }
        }

        private void EnqueueDeferred(DeferredTurnWork next)
        {
            if (next == null) return;
            var previous = deferredWork;
            if (previous == null)
            {
                deferredWork = next;
                return;
            }
            deferredWork = new DeferredTurnWork(next.TraceId, async ct =>
            {
                var first = await previous.AnalyzeAsync(ct);
                var second = await next.AnalyzeAsync(ct);
                if (first == null) return second;
                if (second == null) return first;
                return async sendCt =>
                {
                    await first(sendCt);
                    await second(sendCt);
                };
            });
        }

        private static bool IsImageGenerator(BrainCapabilityCallData extra)
        {
            var id = extra == null ? string.Empty : extra.capability_id ?? string.Empty;
            return id.IndexOf("imagegen", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static BrainCapabilityCallData CloneCall(
            BrainCapabilityCallData source, string dispatch, string files)
        {
            var arguments = new List<BrainCallArgumentData>();
            foreach (var argument in source.arguments ?? new List<BrainCallArgumentData>())
            {
                if (argument == null || string.IsNullOrWhiteSpace(argument.name)) continue;
                if (string.Equals(argument.name, "dispatch", StringComparison.OrdinalIgnoreCase)) continue;
                if (files != null && string.Equals(argument.name, "files", StringComparison.OrdinalIgnoreCase))
                    continue;
                arguments.Add(new BrainCallArgumentData { name = argument.name, value = argument.value });
            }
            if (!string.IsNullOrWhiteSpace(dispatch))
                arguments.Add(new BrainCallArgumentData { name = "dispatch", value = dispatch });
            if (files != null)
                arguments.Add(new BrainCallArgumentData { name = "files", value = files });
            return new BrainCapabilityCallData
            {
                call_id = "expr-" + Guid.NewGuid().ToString("N"),
                capability_id = source.capability_id,
                purpose = source.purpose ?? "附加表达",
                arguments = arguments
            };
        }

        private sealed class PreparedOutboundImage
        {
            public BrainCapabilityCallData Call;
            public bool Prepared;
            public TraceCapabilityResultData GenerateResult;
        }

        private static List<TraceContributionDescriptorData> FilterExpressionCatalog(
            IEnumerable<TraceContributionDescriptorData> catalog,
            TraceTurnContext turn)
        {
            var items = (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>()).ToList();
            if (AllowsQzonePublish(turn) && AllowsQqMood(turn)) return items;
            return items.Where(x => x == null ||
                                    (!IsQzonePublish(x) || AllowsQzonePublish(turn)) &&
                                    (!IsQqMood(x) || AllowsQqMood(turn))).ToList();
        }

        private static bool IsQzonePublish(TraceContributionDescriptorData item)
        {
            if (item == null) return false;
            if (item.Kind == TraceContributionKindValues.Effector &&
                string.Equals(MouthLogic.OrganOf(item), BodyOrganValues.Qzone, StringComparison.Ordinal))
                return true;
            return IsQzonePublishCapability(item.Id);
        }

        private static bool IsQzonePublishCapability(string capabilityId)
        {
            if (string.IsNullOrWhiteSpace(capabilityId)) return false;
            var id = capabilityId;
            if (id.IndexOf(".read", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return id.IndexOf("qzone.publish", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   string.Equals(id, "qq.qzone", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsQqMood(TraceContributionDescriptorData item)
        {
            return item != null && IsQqMoodCapability(item.Id);
        }

        private static bool IsQqMoodCapability(string capabilityId)
        {
            if (string.IsNullOrWhiteSpace(capabilityId)) return false;
            return string.Equals(capabilityId, "qq.status.mood", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>改 QQ 签名/在线状态同样不可逆，只接受当前消息里的明确指令。</summary>
        private static bool AllowsQqMood(TraceTurnContext turn)
        {
            var text = turn == null || turn.Moment == null
                ? string.Empty
                : (turn.Moment.Content ?? string.Empty).Trim();
            if (text.Length == 0) return false;
            var target = text.IndexOf("签名", StringComparison.Ordinal) >= 0 ||
                         text.IndexOf("在线状态", StringComparison.Ordinal) >= 0 ||
                         text.IndexOf("QQ状态", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         (text.IndexOf("心情", StringComparison.Ordinal) >= 0 &&
                          (text.IndexOf("QQ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           text.IndexOf("空间", StringComparison.Ordinal) >= 0));
            if (!target) return false;
            return Regex.IsMatch(text,
                @"(?:改|换|设|设置|更新|同步).{0,8}(?:签名|状态|心情)") ||
                   Regex.IsMatch(text,
                @"(?:签名|状态|心情).{0,6}(?:改|换|设成|换成|更新)");
        }

        /// <summary>
        /// 空间发布是不可逆的外部副作用，只接受当前消息中的明确发布指令。
        /// 宁可漏触发并让用户再说清楚，也不能因为模型自由发挥而误发。
        /// </summary>
        private static bool HasQzoneTarget(string text)
        {
            return text.IndexOf("说说", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("空间动态", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("QQ空间", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("QZone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("空间", StringComparison.Ordinal) >= 0;
        }

        private static bool AllowsQzonePublish(TraceTurnContext turn)
        {
            var text = turn == null || turn.Moment == null
                ? string.Empty
                : (turn.Moment.Content ?? string.Empty).Trim();
            if (text.Length == 0) return false;
            if (!HasQzoneTarget(text)) return false;

            return Regex.IsMatch(text,
                @"^(?:阿循|循循|循)?[，,：:\s]*(?:请|麻烦)?(?:帮我|替我|给我)?(?:去)?(?:发|发布|发表|更新|同步)") ||
                   Regex.IsMatch(text,
                @"(?:帮我|替我|给我|请你|麻烦你).{0,6}(?:发|发布|发表|更新|同步)") ||
                   Regex.IsMatch(text,
                @"你(?:去|帮我|给我)?(?:发|发布|发表|更新|同步)一?[条个篇段]?");
        }

        private static bool WantsQzoneRead(TraceTurnContext turn)
        {
            if (AllowsQzonePublish(turn)) return false;
            var text = turn == null || turn.Moment == null
                ? string.Empty
                : (turn.Moment.Content ?? string.Empty).Trim();
            if (text.Length == 0) return false;
            if (!HasQzoneTarget(text)) return false;
            return text.IndexOf("看", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("瞧", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("翻", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("刷", StringComparison.Ordinal) >= 0;
        }

        private async Task RunIdleDeedAsync(
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog,
            CancellationToken cancellationToken)
        {
            try
            {
                var outcome = await IdleDeedLogic.RunAsync(
                    turn,
                    catalog,
                    (id, args, token) => TryExecuteNerveAsync(id, "空闲生活", turn, catalog, args, token),
                    cancellationToken);
                if (outcome != null && !string.IsNullOrWhiteSpace(outcome.Summary))
                    plugins.Services.LogTiming(turn.TraceId, outcome.Summary, 0);
                RememberIdleDeedPayload(turn, outcome);
            }
            catch (Exception exception)
            {
                plugins.Services.LogTiming(turn.TraceId, "空闲生活失败：" + exception.Message, 0);
            }
        }

        /// <summary>
        /// 空闲生活带回实在内容（读到的说说、发出的说说）时，压成一条今日新识便签。
        /// 不额外打一轮模型；下次心智/外显自然看得见，她可以默默提起。
        /// 「没有读到…」这类空结果只留运行日志，不占新识。
        /// </summary>
        private void RememberIdleDeedPayload(TraceTurnContext turn, IdleDeedOutcome outcome)
        {
            if (turn == null || turn.Services == null || turn.Services.Storage == null ||
                outcome == null || !outcome.Counted)
                return;
            var summary = (outcome.Summary ?? string.Empty).Trim();
            if (summary.IndexOf("没有读到", StringComparison.Ordinal) >= 0) return;
            var payload = (outcome.Payload ?? string.Empty).Trim();
            if (payload.Length == 0) return;

            var note = summary.StartsWith("空闲生活：", StringComparison.Ordinal)
                ? summary.Substring("空闲生活：".Length)
                : summary;
            note = Regex.Replace(note, @"QQ\s*\d+", "她的空间").TrimEnd('。', '，', ' ');
            var firstLine = payload.Split('\n')
                .Select(x => x.Trim())
                .FirstOrDefault(x => x.Length > 0) ?? string.Empty;
            var text = ("空闲时" + note +
                        (firstLine.Length == 0 ? string.Empty : "：" + firstLine)).Trim();
            if (text.Length > TodayNewItemRecord.MaxContentChars)
                text = text.Substring(0, TodayNewItemRecord.MaxContentChars).TrimEnd();

            var now = DateTimeOffset.Now;
            var added = turn.Services.Storage.AddTodayNewItems(
                turn.ConversationId,
                new[] { text },
                turn.Moment == null ? string.Empty : turn.Moment.Id,
                MemoryDayLogic.CurrentDayKey(now),
                now.ToUnixTimeMilliseconds());
            if (added > 0)
                plugins.Services.LogTiming(turn.TraceId, "空闲生活写入今日新识", 0, text);
        }

        private async Task RunPostSpeakAsync(
            MindDecisionData decision,
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog,
            CancellationToken cancellationToken)
        {
            var archiveCursor = MemoryArchivePolicyLogic.LoadArchiveCursor(
                storage, turn.ConversationId);
            var archiveGate = MemoryArchivePolicyLogic.Evaluate(
                decision,
                turn.Moment,
                storage.GetRecentMoments(turn.ConversationId, 200),
                storage.LoadPairIdentity(),
                archiveCursor);
            if (archiveGate.ShouldArchive)
            {
                plugins.Services.LogTiming(turn.TraceId, "小复盘触发",
                    detail: "dialogue_moments=" + archiveGate.UnbuiltDialogueMoments +
                            "｜reason=" + archiveGate.Reason);
                await TryExecuteNerveAsync("memory.archive", "归档刚结束的话题", turn, catalog,
                    new List<BrainCallArgumentData>
                    {
                        new BrainCallArgumentData
                        {
                            name = "summary",
                            value = Limit(decision == null ? string.Empty : decision.note, 80)
                        }
                    }, cancellationToken);
            }
            else if (archiveGate.TopicBoundarySuggested)
            {
                plugins.Services.LogTiming(turn.TraceId, "小复盘暂缓", 0,
                    "dialogue_moments=" + archiveGate.UnbuiltDialogueMoments + "/" +
                    MemoryArchivePolicyLogic.SoftDialogueMomentThreshold);
            }

            // 身份短卡复盘只由时间运行事件进入 RunSubconsciousAsync，普通对话不再派出。
        }

        private async Task<TraceCapabilityResultData> TryExecuteNerveAsync(
            string capabilityId,
            string purpose,
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog,
            List<BrainCallArgumentData> arguments,
            CancellationToken cancellationToken)
        {
            if (!(catalog ?? new List<TraceContributionDescriptorData>())
                    .Any(x => x != null && x.Id == capabilityId))
                return null;
            var call = new BrainCapabilityCallData
            {
                call_id = capabilityId + "-" + Guid.NewGuid().ToString("N"),
                capability_id = capabilityId,
                purpose = purpose,
                arguments = arguments ?? new List<BrainCallArgumentData>()
            };
            try
            {
                var result = await ExecuteWithQzoneMediaAsync(call, turn, cancellationToken);
                turn.Workspace.Results.Add(result);
                if (result != null && result.ProducedEvent != null)
                    PersistPluginEvent(turn.ConversationId, result.ProducedEvent);
                return result;
            }
            catch (Exception exception)
            {
                var failed = new TraceCapabilityResultData
                {
                    CallId = call.call_id,
                    CapabilityId = capabilityId,
                    Status = "failed",
                    Summary = purpose + "失败：" + exception.Message,
                    Payload = string.Empty
                };
                turn.Workspace.Results.Add(failed);
                return failed;
            }
        }

        private Task<TraceCapabilityResultData> ExecuteWithQzoneMediaAsync(
            BrainCapabilityCallData call, TraceTurnContext turn, CancellationToken cancellationToken)
        {
            if (call.capability_id != "qq.qzone.publish")
                return plugins.ExecuteAsync(call, turn, cancellationToken);
            return QzonePublishLogic.ExecuteAsync(call, turn, plugins.GetAvailableCatalog(turn),
                (step, token) => plugins.ExecuteAsync(step, turn, token), cancellationToken);
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

        private async Task RunExpressionStartingHooksAsync(TraceTurnContext turn)
        {
            foreach (var hook in turn.Services.ExpressionStartingHooks)
            {
                try { await hook(turn); }
                catch (Exception exception)
                {
                    plugins.Services.LogTiming(turn.TraceId, "表达开始钩子失败", 0,
                        exception.GetType().Name + ": " + exception.Message);
                }
            }
        }

        private async Task RunExpressionCompletedHooksAsync(TraceTurnContext turn)
        {
            foreach (var hook in turn.Services.ExpressionCompletedHooks)
            {
                try { await hook(turn); }
                catch (Exception exception)
                {
                    plugins.Services.LogTiming(turn.TraceId, "表达完成钩子失败", 0,
                        exception.GetType().Name + ": " + exception.Message);
                }
            }
        }

        private static ExpressionLifecycleState GetExpressionLifecycleState(TraceTurnContext turn)
        {
            return turn.Workspace.GetOrCreateState(
                "kernel.expression-lifecycle", () => new ExpressionLifecycleState());
        }

        private async Task TryCompleteExpressionAsync(TraceTurnContext turn)
        {
            var lifecycle = GetExpressionLifecycleState(turn);
            if (Volatile.Read(ref lifecycle.PendingOutboundBatches) > 0 ||
                Interlocked.CompareExchange(ref lifecycle.Completed, 1, 0) != 0) return;
            await RunExpressionCompletedHooksAsync(turn);
        }

        private sealed class ExpressionLifecycleState
        {
            public int PendingOutboundBatches;
            public int Completed;
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
                                      (decision.sleep ? "｜睡下" : string.Empty) +
                                      (decision.speak ? "｜开口" : string.Empty) +
                                      (decision.WantsImage() ? "｜出图" : string.Empty) +
                                      (decision.next_heartbeat_minutes > 0
                                          ? "｜心跳" + decision.next_heartbeat_minutes + "分"
                                          : string.Empty) +
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
            ApplyLifeState(mind, turn);
            output.facet_outputs = output.facet_outputs ?? new List<BrainFacetOutputData>();
            InnerRuntimeData runtime = null;
            if (turn != null && turn.Services != null && turn.Services.Storage != null)
                runtime = turn.Services.Storage.LoadOrCreateInnerRuntime(turn.ConversationId);
            // 真实对话是新的心理时刻：旧碎片默认沉下去，避免“自己问过的问题”
            // 被下一拍误认成仍需完成的目标。心跳等非对话入口仍可保留有温度的碎片。
            var proposed = InnerLifeLogic.ProposeFromMind(mind, runtime, turn != null && turn.RequiresExpression);
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
                if (proposed.asleep.HasValue)
                    WriteField(snapshot, "asleep", proposed.asleep.Value ? "true" : "false");
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

        private static void ApplyLifeState(MindDecisionData mind, TraceTurnContext turn)
        {
            if (mind == null || turn == null || turn.Services == null || turn.Services.LifeState == null)
                return;
            var location = mind.LocationValue();
            var activity = (mind.activity ?? string.Empty).Trim();
            if (location.Length == 0 && activity.Length == 0) return;
            turn.Services.LifeState.Update(turn.ConversationId, new LifeStatePatchData
            {
                location = location.Length == 0 ? null : location,
                activity = activity.Length == 0 ? null : activity,
                activity_detail = activity.Length == 0 ? null : mind.activity_detail,
                source = LifeStateSourceValues.Mind,
                source_id = turn.Moment == null ? string.Empty : turn.Moment.Id,
                force = mind.state_force
            });
        }

        private static void WriteField(BrainFacetOutputData snapshot, string name, string value)
        {
            if (snapshot == null || value == null) return;
            snapshot.fields = snapshot.fields ?? new List<BrainFacetFieldData>();
            snapshot.fields.RemoveAll(x => x != null && x.name == name);
            snapshot.fields.Add(new BrainFacetFieldData { name = name, value = value });
        }

        private async Task SyncHeartbeatAsync(
            TraceTurnContext turn,
            List<TraceContributionDescriptorData> catalog,
            MindDecisionData decision,
            CancellationToken cancellationToken)
        {
            if (turn == null || turn.Services == null || turn.Services.Storage == null) return;
            var runtime = turn.Services.Storage.LoadOrCreateInnerRuntime(turn.ConversationId);
            var heartbeatTurn = HeartbeatLogic.IsHeartbeatContent(
                turn.Moment == null ? string.Empty : turn.Moment.Content);
            if (runtime.Asleep || (decision != null && decision.sleep))
            {
                if (runtime.Idle)
                {
                    var cleared = InnerLifeLogic.WithIdle(runtime, false, MomentId(turn),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (cleared != runtime)
                    {
                        turn.Services.Storage.SaveInnerRuntime(cleared);
                        runtime = cleared;
                    }
                }
                await TryExecuteNerveAsync("time.continue.clear", "睡着后停止心跳", turn, catalog,
                    new List<BrainCallArgumentData>(), cancellationToken);
                return;
            }
            var min = turn.Services.HeartbeatMinMinutes;
            var max = turn.Services.HeartbeatMaxMinutes;
            if (!HeartbeatLogic.IsEnabled(min, max))
            {
                await TryExecuteNerveAsync("time.continue.clear", "心跳已关闭", turn, catalog,
                    new List<BrainCallArgumentData>(), cancellationToken);
                return;
            }
            if (heartbeatTurn || (decision != null && decision.next_heartbeat_minutes > 0))
            {
                var requestedMinutes = decision == null ? 0 : decision.next_heartbeat_minutes;
                var speak = decision != null && decision.speak;
                var runIdleDeed = false;
                if (heartbeatTurn && HeartbeatLogic.ShouldEnterIdle(speak, false, requestedMinutes))
                {
                    var entering = !runtime.Idle;
                    var idled = InnerLifeLogic.WithIdle(runtime, true, MomentId(turn),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (idled != runtime) turn.Services.Storage.SaveInnerRuntime(idled);
                    plugins.Services.LogTiming(turn.TraceId, "这次安静，进入空闲并保留下次联系计划", 0);
                    runIdleDeed = entering;
                }
                var minutes = HeartbeatLogic.ResolveFollowUpMinutes(false, requestedMinutes);
                var nextPlan = decision == null ? string.Empty : decision.next_heartbeat_plan;
                if (string.IsNullOrWhiteSpace(nextPlan))
                    nextPlan = HeartbeatLogic.DefaultLongFollowUpPlan;
                if (decision != null)
                {
                    // 审查快照和控制台应显示实际排下的兜底值，而不是误导性的“0 / 不再自醒”。
                    decision.next_heartbeat_minutes = minutes;
                    decision.next_heartbeat_plan = nextPlan;
                }
                var due = HeartbeatLogic.DueFromMinutes(minutes, DateTimeOffset.Now);
                await TryExecuteNerveAsync("time.continue", "心跳后续跳", turn, catalog,
                    HeartbeatDueArgs(due, nextPlan), cancellationToken);
                if (runIdleDeed) await RunIdleDeedAsync(turn, catalog, cancellationToken);
                return;
            }
            var firstDue = HeartbeatLogic.PickDueUnixMs(min, max, DateTimeOffset.Now);
            var firstPlan = string.IsNullOrWhiteSpace(decision?.next_heartbeat_plan)
                ? HeartbeatLogic.DefaultNextPlan : decision.next_heartbeat_plan;
            if (decision != null)
            {
                decision.next_heartbeat_minutes = Math.Max(1, (int)Math.Round(
                    (firstDue - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 60000.0));
                decision.next_heartbeat_plan = firstPlan;
            }
            await TryExecuteNerveAsync("time.continue", "入站后排一次心跳", turn, catalog,
                HeartbeatDueArgs(firstDue, firstPlan), cancellationToken);
        }

        private static List<BrainCallArgumentData> HeartbeatDueArgs(long dueUnixMs, string nextPlan)
        {
            return new List<BrainCallArgumentData>
            {
                new BrainCallArgumentData { name = "content", value = HeartbeatLogic.BuildContent(nextPlan) },
                new BrainCallArgumentData { name = "next_plan", value = nextPlan ?? string.Empty },
                new BrainCallArgumentData { name = "due_unix_ms", value = dueUnixMs.ToString() }
            };
        }

        private static string MomentId(TraceTurnContext turn)
        {
            if (turn == null || turn.Moment == null || string.IsNullOrWhiteSpace(turn.Moment.Id))
                return "heartbeat-state";
            return turn.Moment.Id;
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

        /// <summary>console 打印的来源/去向标签：插件 id 或通道 id → 人能读的身体名。</summary>
        private static string ConsoleViaLabel(string pluginOrChannel)
        {
            var s = (pluginOrChannel ?? string.Empty).Trim();
            if (s.Length == 0) return "仅本地";
            if (s.IndexOf("onebot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("qq", StringComparison.OrdinalIgnoreCase) >= 0) return "QQ";
            if (s.IndexOf("console", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("dialogue", StringComparison.OrdinalIgnoreCase) >= 0) return "console";
            return s;
        }

        /// <summary>
        /// console 观察窗镜像：把一条收/发内容交给 console 平台打印。
        /// 打印是运行痕迹（operational），不进对话历史；失败只留日志，不打断主流程。
        /// </summary>
        private async Task MirrorToConsoleAsync(
            TraceTurnContext turn, string conversationId,
            string direction, string via, string text, string role)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0 || plugins == null) return;
            try
            {
                var call = new BrainCapabilityCallData
                {
                    call_id = "console-print-" + Guid.NewGuid().ToString("N"),
                    capability_id = "dialogue.print",
                    purpose = "console 观察窗打印",
                    arguments = new List<BrainCallArgumentData>
                    {
                        new BrainCallArgumentData { name = "text", value = text },
                        new BrainCallArgumentData { name = "direction", value = direction ?? "out" },
                        new BrainCallArgumentData { name = "via", value = via ?? string.Empty },
                        new BrainCallArgumentData { name = "role", value = role ?? string.Empty }
                    }
                };
                var printed = await plugins.ExecuteAsync(call, turn, CancellationToken.None);
                if (printed != null && printed.Status == "success" && printed.ProducedEvent != null)
                    PersistPluginEvent(conversationId, printed.ProducedEvent);
            }
            catch (Exception exception)
            {
                plugins.Services.LogTiming(turn == null ? null : turn.TraceId,
                    "console 打印失败", 0, exception.Message);
            }
        }

        /// <summary>整轮链路快照：挂载块（截断）+ 回调结果（含召回证据），供控制台回看。</summary>
        private static string BuildTurnSnapshot(TraceTurnContext turn, MindDecisionData mindDecision)
        {
            var snapshot = new TurnPayloadSnapshotData();
            snapshot.mind_decision = mindDecision == null ? null : MindLogic.Normalize(mindDecision);
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

        private static List<MomentRecord> LoadAlignedDialogueHistory(
            IMemoryStore storage,
            PairIdentity pair,
            string conversationId,
            MomentRecord triggerMoment,
            int min,
            int align)
        {
            if (storage == null || min <= 0) return new List<MomentRecord>();
            var total = storage.CountDialogueMoments(conversationId);
            var triggerIsDialogue = triggerMoment != null &&
                                    (pair.IsHumanMoment(triggerMoment.Role) ||
                                     pair.IsCompanionMoment(triggerMoment.Role));
            if (triggerIsDialogue) total = Math.Max(0, total - 1);
            var take = CommonContextPackLogic.AlignedWindowTake(total, min, align);
            if (take <= 0) return new List<MomentRecord>();
            var extra = triggerIsDialogue ? 1 : 0;
            return storage.GetRecentDialogueMoments(conversationId, take + extra)
                .Where(x => x != null &&
                            (triggerMoment == null || x.Id != triggerMoment.Id) &&
                            (pair.IsHumanMoment(x.Role) || pair.IsCompanionMoment(x.Role)))
                .TakeLast(take)
                .ToList();
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

        private MomentRecord PersistPluginEvent(string conversationId, PluginEventData source)
        {
            var moment = ToMoment(conversationId, source);
            if (!source.IsOperational)
            {
                storage.SaveMoment(moment);
                return moment;
            }

            storage.SaveOperationalEvent(new OperationalEventRecord
            {
                // 与本轮临时 Moment 共用 ID，TurnReview/内心切片仍能追到真正的触发记录。
                Id = moment.Id,
                ConversationId = moment.ConversationId,
                Kind = ResolveOperationalKind(source),
                SourcePluginId = moment.SourcePluginId,
                SourceEventId = moment.SourceEventId,
                TraceId = source.TraceId ?? string.Empty,
                Role = moment.Role,
                Content = moment.Content,
                Realm = moment.Realm,
                EvidenceType = moment.EvidenceType,
                PayloadJson = moment.PayloadJson,
                OccurredUnixMs = moment.CreatedUnixMs,
                CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            return moment;
        }

        private static string ResolveOperationalKind(PluginEventData source)
        {
            if (string.Equals(source.PluginId, "builtin.time", StringComparison.Ordinal))
                return OperationalEventKindValues.SchedulerTrigger;
            var content = (source.Content ?? string.Empty).Trim();
            if (content.IndexOf("发送图片", StringComparison.Ordinal) >= 0)
                return OperationalEventKindValues.OutboundImage;
            if (content.IndexOf("表情", StringComparison.Ordinal) >= 0)
                return OperationalEventKindValues.OutboundSticker;
            if (content.IndexOf("发送语音", StringComparison.Ordinal) >= 0)
                return OperationalEventKindValues.OutboundVoice;
            if (string.Equals(source.EvidenceType, EvidenceTypeValues.AssPerformed,
                    StringComparison.Ordinal))
                return OperationalEventKindValues.ActionReceipt;
            return OperationalEventKindValues.PluginRuntime;
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
