using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
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
            CancellationToken cancellationToken = default(CancellationToken),
            string traceId = null)
        {
            var pair = storage.LoadPairIdentity();
            if (!pair.IsComplete)
                throw new InvalidOperationException("相处开始前，需要先保存两个人的名字。");
            var source = plugins.ReceiveMoment(sourceId, pair.Username, userText, null);
            source.TraceId = traceId;
            source.Breaking = true;
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
            if (string.IsNullOrWhiteSpace(source.TraceId))
                source.TraceId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var totalTimer = Stopwatch.StartNew();
            plugins.Services.LogTiming(source.TraceId, "Brain 整轮开始",
                detail: "source=" + (source.PluginId ?? string.Empty));
            var pair = storage.LoadPairIdentity();
            if (!pair.IsComplete)
                throw new InvalidOperationException("相处开始前，需要先保存两个人的名字。");
            contextInjectionCount = Math.Max(0, Math.Min(100, contextInjectionCount));

            var prepareTimer = Stopwatch.StartNew();
            var wake = KernelWakeLogic.Resolve(source);
            // 运行事件也会临时成为本轮刺激，但不会进入可复盘的 Moment 账本。
            var triggerMoment = PersistPluginEvent(conversationId, source);
            var inner = storage.LoadOrCreateInnerRuntime(conversationId);
            if (inner.Asleep && HeartbeatLogic.IsBreaking(source, pair))
            {
                var woken = InnerLifeLogic.WithAsleep(inner, false, triggerMoment.Id,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (woken != inner) storage.SaveInnerRuntime(woken);
                inner = woken;
            }
            else if (inner.Asleep && HeartbeatLogic.ShouldSkipWhileAsleep(source, pair, wake))
            {
                plugins.Services.LogTiming(source.TraceId, "睡着，跳过非打破性 Moment",
                    prepareTimer.ElapsedMilliseconds);
                return new ChatTurnResultData(
                    string.Empty, "sleep", "睡着", "睡着｜跳过",
                    new List<TraceContextBlockData>(),
                    new List<BrainFacetOutputData>(),
                    new List<TraceCapabilityResultData>());
            }
            var recent = contextInjectionCount <= 0
                ? new List<MomentRecord>()
                : storage.GetRecentMoments(conversationId, 200)
                    .Where(x => x.Id != triggerMoment.Id &&
                                (pair.IsHumanMoment(x.Role) || pair.IsCompanionMoment(x.Role)))
                    .TakeLast(contextInjectionCount)
                    .ToList();
            var turn = new TraceTurnContext(
                conversationId,
                triggerMoment,
                recent,
                contextInjectionCount,
                wake == KernelWakeValues.Dialogue && pair.IsHumanMoment(source.Role),
                plugins.Services,
                wake,
                source.TraceId);
            MouthLogic.NoticeInbound(source, turn);

            var turnFinished = false;
            try
            {

            var catalog = plugins.GetAvailableCatalog(turn);
            plugins.Services.LogTiming(turn.TraceId, "输入落库与轮次准备完成",
                prepareTimer.ElapsedMilliseconds,
                "wake=" + wake + "｜catalog=" + catalog.Count + "｜history=" + recent.Count);
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
            else
            {
                var branchTimer = Stopwatch.StartNew();
                var lived = await RunLivedMindAsync(turn, catalog, cancellationToken);
                final = lived.Final;
                mindDecision = lived.MindDecision;
                expression = lived.Expression;
                responseFlushed = lived.ResponseFlushed;
                plugins.Services.LogTiming(turn.TraceId, "心智/对话轨完成", branchTimer.ElapsedMilliseconds);
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

        private async Task<LivedMindTurn> RunLivedMindAsync(
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
            plugins.Services.LogTiming(turn.TraceId, "记忆预激活完成", preludeTimer.ElapsedMilliseconds,
                "top_k=" + recallTopK + "｜chars=" + naturallyAwakenedPast.Length);

            // QQ 等对话入口本轮必须回应：在第一次 Mind LLM 请求前就通知平台。
            // 心跳/时间触发等可能保持沉默的轮次，仍由后面的表达分支在决定开口后通知。
            if (turn.RequiresExpression)
                await RunExpressionStartingHooksAsync(turn);

            var mindTimer = Stopwatch.StartNew();
            var decision = await mind.DecideAsync(
                turn, null, false, naturallyAwakenedPast, cancellationToken);
            if (HeartbeatLogic.IsHeartbeatContent(turn.Moment == null ? string.Empty : turn.Moment.Content) &&
                decision.speak && string.IsNullOrWhiteSpace(decision.heartbeat_intent))
            {
                decision.speak = false;
                plugins.Services.LogTiming(turn.TraceId, "心跳无独立意图，保持安静", 0);
            }
            plugins.Services.LogTiming(turn.TraceId, "心智判断完成", mindTimer.ElapsedMilliseconds,
                "beat=" + decision.BeatValue() + "｜image=" + decision.ImageValue());
            string leaveResult = null;
            TraceCapabilityResultData expression = null;
            if (decision.WantsLeave())
            {
                await ApplyInnerFacetsAsync(decision, turn, cancellationToken);
                await RunExpressionStartingHooksAsync(turn);
                var waitingTimer = Stopwatch.StartNew();
                var waiting = await expressor.ExpressAsync(
                    turn, pluginList, expressionCatalog, blocks, decision, string.Empty, true, null, cancellationToken);
                plugins.Services.LogTiming(turn.TraceId, "离场前表达生成完成", waitingTimer.ElapsedMilliseconds);
                waiting = ExpressorLogic.NormalizeStep(
                    waiting, expressionCatalog, true, true, ResolveReplyChannel(turn));
                await ExecuteExpressionAsync(waiting, turn, turn.ConversationId, cancellationToken);
                await RunTurnCompleteHooksAsync(turn);
                var leaveTimer = Stopwatch.StartNew();
                leaveResult = await ExecuteLeaveAsync(decision, catalog, turn, cancellationToken);
                plugins.Services.LogTiming(turn.TraceId, "外出链路完成", leaveTimer.ElapsedMilliseconds);
                var secondMindTimer = Stopwatch.StartNew();
                decision = await mind.DecideAsync(
                    turn, leaveResult, true, naturallyAwakenedPast, cancellationToken);
                plugins.Services.LogTiming(turn.TraceId, "外出后心智判断完成", secondMindTimer.ElapsedMilliseconds);
            }

            var recallTimer = Stopwatch.StartNew();
            var expandedRecallTopK = Math.Min(10, recallTopK * 2);
            var memoryFlesh = decision.WantsMemory()
                ? MemoryRecallLogic.Assemble(turn, decision, expandedRecallTopK)
                : string.Empty;
            plugins.Services.LogTiming(turn.TraceId, "记忆拼装完成", recallTimer.ElapsedMilliseconds,
                "selected=" + decision.WantsMemory() + "｜top_k=" + expandedRecallTopK +
                "｜chars=" + memoryFlesh.Length);
            if (!string.IsNullOrWhiteSpace(memoryFlesh))
            {
                turn.Workspace.Results.Add(new TraceCapabilityResultData
                {
                    CallId = "memory-recall",
                    CapabilityId = "memory.recall",
                    Status = "success",
                    Summary = "共同过去先自然浮起，再按这一拍的选择扩展后交给表达。",
                    Payload = memoryFlesh
                });
            }

            BrainStructuredOutputData final;
            var needsReply = turn.RequiresExpression || decision.speak;
            if (needsReply || !string.IsNullOrWhiteSpace(leaveResult))
            {
                await RunExpressionStartingHooksAsync(turn);
                var expressTimer = Stopwatch.StartNew();
                final = await expressor.ExpressAsync(
                    turn, pluginList, expressionCatalog, blocks, decision, memoryFlesh, false,
                    leaveResult, cancellationToken);
                plugins.Services.LogTiming(turn.TraceId, "表达生成完成", expressTimer.ElapsedMilliseconds);
                final = ExpressorLogic.NormalizeStep(
                    final, expressionCatalog, true, needsReply, ResolveReplyChannel(turn));
                StampDecision(final, decision);
                CloseReplyChannel(final, turn, expressionCatalog);
                MergePrivateFacets(final, decision, turn);
                var facetTimer = Stopwatch.StartNew();
                await plugins.ApplyFacetOutputsAsync(final.facet_outputs, turn, cancellationToken);
                plugins.Services.LogTiming(turn.TraceId, "全部面输出应用完成", facetTimer.ElapsedMilliseconds);
                if (final.should_express)
                {
                    var outputTimer = Stopwatch.StartNew();
                    expression = await ExecuteExpressionAsync(final, turn, turn.ConversationId, cancellationToken);
                    plugins.Services.LogTiming(turn.TraceId, "对外表达链完成", outputTimer.ElapsedMilliseconds,
                        "capability=" + final.expression_capability_id);
                }
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
                var facetTimer = Stopwatch.StartNew();
                await plugins.ApplyFacetOutputsAsync(final.facet_outputs, turn, cancellationToken);
                plugins.Services.LogTiming(turn.TraceId, "全部面输出应用完成", facetTimer.ElapsedMilliseconds);
            }

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
            // 默认对话只保留 Mind + Expressor 两次 LLM。即时事实直接使用 Mind 已给出的
            // new_fact 落库；逐句 MemoryObservation 不再启动第三次 LLM。
            MemoryLiveWriteLogic.TryCommitNewFact(turn, decision);
            CognitionLiveWriteLogic.TryCommit(turn, decision);
            await SyncHeartbeatAsync(turn, catalog, decision, cancellationToken);
            await RunPostSpeakAsync(decision, turn, catalog, cancellationToken);
            plugins.Services.LogTiming(turn.TraceId, "轮后记忆/认知处理完成", persistTimer.ElapsedMilliseconds);
            return new LivedMindTurn(final, expression, responseFlushed, decision);
        }

        private sealed class LivedMindTurn
        {
            public BrainStructuredOutputData Final { get; private set; }
            public TraceCapabilityResultData Expression { get; private set; }
            public bool ResponseFlushed { get; private set; }
            public MindDecisionData MindDecision { get; private set; }

            public LivedMindTurn(
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
            var previousBodyScene = MouthLogic.LoadState(
                turn.Services == null ? null : turn.Services.DataDirectory).scene;
            var previousLife = turn.Services == null || turn.Services.LifeState == null
                ? null : turn.Services.LifeState.Load(turn.ConversationId);
            // 外出神经真正开始执行时，身体才离开当前场景；返回（成功或失败）后恢复原场景。
            // 这与 MindDecisionData.scene（共享文字场景）是两套状态，不能混用。
            MouthLogic.SetScene(turn.Services == null ? null : turn.Services.DataDirectory,
                BodySceneValues.Out);
            if (turn.Services != null && turn.Services.LifeState != null)
                turn.Services.LifeState.Update(turn.ConversationId, new LifeStatePatchData
                {
                    location = BodySceneValues.Out,
                    source = LifeStateSourceValues.System,
                    source_id = "leave",
                    force = true
                });
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
            finally
            {
                MouthLogic.SetScene(turn.Services == null ? null : turn.Services.DataDirectory,
                    previousBodyScene);
                if (turn.Services != null && turn.Services.LifeState != null)
                    turn.Services.LifeState.Update(turn.ConversationId, new LifeStatePatchData
                    {
                        location = previousLife == null ? previousBodyScene : previousLife.location,
                        source = previousLife == null ? LifeStateSourceValues.System : previousLife.location_source,
                        source_id = previousLife == null ? "leave.return" : previousLife.location_source_id,
                        force = true
                    });
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
                PersistPluginEvent(turn.ConversationId, result.ProducedEvent);
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
            PersistPluginEvent(conversationId, expression.ProducedEvent);

            List<BrainCapabilityCallData> immediate;
            List<BrainCapabilityCallData> images;
            ExpressorLogic.PartitionExpressions(final.expressions, out immediate, out images);
            foreach (var extra in immediate)
            {
                if (IsQzoneCapability(extra.capability_id) && !AllowsQzonePublish(turn))
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
                var extraResult = await plugins.ExecuteAsync(extraCall, turn, cancellationToken);
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
                if (extraResult != null && extraResult.ProducedEvent != null)
                    PersistPluginEvent(conversationId, extraResult.ProducedEvent);
                plugins.Services.LogTiming(turn.TraceId, "TA的相机 后台图已发出", 0,
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
            if (AllowsQzonePublish(turn)) return items;
            return items.Where(x => x == null || !IsQzoneCapability(x.Id) &&
                !string.Equals(MouthLogic.OrganOf(x), BodyOrganValues.Qzone, StringComparison.Ordinal)).ToList();
        }

        private static bool IsQzoneCapability(string capabilityId)
        {
            return !string.IsNullOrWhiteSpace(capabilityId) &&
                   capabilityId.IndexOf("qzone", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 空间发布是不可逆的外部副作用，只接受当前消息中的明确发布指令。
        /// 宁可漏触发并让用户再说清楚，也不能因为模型自由发挥而误发。
        /// </summary>
        private static bool AllowsQzonePublish(TraceTurnContext turn)
        {
            var text = turn == null || turn.Moment == null
                ? string.Empty
                : (turn.Moment.Content ?? string.Empty).Trim();
            if (text.Length == 0) return false;
            var target = text.IndexOf("说说", StringComparison.Ordinal) >= 0 ||
                         text.IndexOf("空间动态", StringComparison.Ordinal) >= 0 ||
                         text.IndexOf("QQ空间", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         text.IndexOf("QZone", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!target) return false;

            return Regex.IsMatch(text,
                @"^(?:阿循[，,：:\s]*)?(?:请|麻烦)?(?:帮我|替我|给我)?(?:去)?(?:发|发布|发表|更新|同步)") ||
                   Regex.IsMatch(text,
                @"(?:帮我|替我|给我|请你|麻烦你).{0,6}(?:发|发布|发表|更新|同步)");
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
                    PersistPluginEvent(turn.ConversationId, result.ProducedEvent);
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
                await TryExecuteNerveAsync("time.continue.clear", "睡着后停止心跳", turn, catalog,
                    new List<BrainCallArgumentData>(), cancellationToken);
                return;
            }
            if (heartbeatTurn)
            {
                var minutes = decision == null ? 0 : HeartbeatLogic.ClampMinutes(decision.next_heartbeat_minutes);
                if (minutes <= 0)
                {
                    await TryExecuteNerveAsync("time.continue.clear", "心跳后不再续跳", turn, catalog,
                        new List<BrainCallArgumentData>(), cancellationToken);
                    return;
                }
                var due = HeartbeatLogic.DueFromMinutes(minutes, DateTimeOffset.Now);
                await TryExecuteNerveAsync("time.continue", "心跳后续跳", turn, catalog,
                    HeartbeatDueArgs(due, decision.next_heartbeat_plan), cancellationToken);
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
            var firstDue = HeartbeatLogic.PickDueUnixMs(min, max, DateTimeOffset.Now);
            await TryExecuteNerveAsync("time.continue", "入站后排一次心跳", turn, catalog,
                HeartbeatDueArgs(firstDue, HeartbeatLogic.DefaultNextPlan), cancellationToken);
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
