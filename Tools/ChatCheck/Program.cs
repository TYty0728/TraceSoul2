using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.ExternalPlugins;
using TraceSoul2.Util;

internal static class Program
{
    private static void Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        RunTagRankCheck();
        RunMindTemplateCheck();
        RunMemoryArchivePolicyCheck();
        RunMemoryDayCheck();
        RunJsonControlCharCheck();
        RunFlexibleMindJsonCheck();
        RunKernelWakeCheck();
        RunInnerSliceCheck();
        RunLeaveNerveCheck();
        RunBodyRoutingCheck();
        RunOneBotSessionMemoryCheck();
        RunExpressorImageRoutingCheck();
        RunMindAtmosphereCheck();
        RunRecentDialogueContextCheck();
        if ((args ?? Array.Empty<string>()).Contains("--prompt-layout"))
        {
            RunPromptLayoutCheck();
            return;
        }
        var path = Path.Combine(Path.GetTempPath(), "tracesoul2-brainframe-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        const string conversationId = "organism-check";
        string tagId;
        string firstFactId;
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("小雨", "小光", "雨雨");
                var pair = store.LoadPairIdentity();
                Require(pair.IsComplete && pair.Username == "小雨" && pair.Assname == "小光" && pair.CallName == "雨雨",
                    "启动前应能保存两个人的名字和称呼");
                var ontology = LifeTagVectorLogic.BuildOntology(store, CoreVectorOntologyFactory.Create(pair));
                Require(ontology.Any(x => x.Id == "domain.user" && x.Label == "小雨"),
                    "第一层 user 槽位的展示名应是对方的名字");
                Require(ontology.Any(x => x.Id == "domain.ass" && x.Label == "小光"),
                    "第一层 ass 槽位的展示名应是他自己的名字");
                Require(ontology.Count(x => x.Level == VectorNodeLevel.Domain) == 4, "第一层必须固定四个域");
                Require(ontology.All(x => x.Level != VectorNodeLevel.Concept), "第三层初始必须为空");
                Require(new HashSet<string>(ontology.Where(x => x.Level == VectorNodeLevel.Domain)
                        .Select(x => x.Id.Substring("domain.".Length)))
                    .SetEquals(LifeRouteValues.Domains), "第一层定义与持久层白名单必须一致");
                Require(new HashSet<string>(ontology.Where(x => x.Level == VectorNodeLevel.Dimension)
                        .Select(x => x.DimensionKey))
                    .SetEquals(LifeRouteValues.Dimensions), "第二层定义与持久层白名单必须一致");
                var mapped = MemoryObservationLogic.Normalize(
                    new MemoryObservationOutputData
                    {
                        perception_summary = "有经历",
                        new_tags = new List<NewLifeTagWriteData>
                        {
                            new NewLifeTagWriteData
                            {
                                name = "饮食偏好",
                                definition = "用户对食物的口味",
                                domain_ids = new List<string> { "小雨" },
                                dimension_ids = new List<string> { "quality" },
                                positive_examples = new List<string> { "不吃香菜" }
                            }
                        }
                    }, null, new FactSliceRecord[0], pair);
                Require(mapped.new_tags.Count == 1 && mapped.new_tags[0].domain_ids[0] == "user",
                    "名字应映射回内部域槽位");
                Require(mapped.new_tags[0].definition.Contains("小雨") &&
                        mapped.new_tags[0].definition.IndexOf("用户", StringComparison.Ordinal) < 0,
                    "Tag 定义里不应再出现用户二字");
                var models = DeepSeekClientManager.ParseModelList(
                    TraceSoul2.Util.TraceJson.FromJson<OpenAiModelListData>(
                        "{\"data\":[{\"id\":\"deepseek-v4-flash\"},{\"id\":\"deepseek-chat\"}]}"));
                Require(models.Count == 2 && models[0] == "deepseek-chat",
                    "OpenAI 兼容口应能解析一键获取的模型列表");

                var personality = store.LoadOrCreateBasePersonality(conversationId);
                var runtime = store.LoadOrCreateInnerRuntime(conversationId);
                Require(personality.Revision == 0 && runtime.Revision == 0, "人格与 Runtime 应从 revision 0 建立");
                var identityCards = store.LoadIdentityCards(conversationId);
                Require(identityCards.Count == 6 &&
                        identityCards.Select(x => x.Slot).OrderBy(x => x)
                            .SequenceEqual(IdentityCardSlotValues.All.OrderBy(x => x)),
                    "应落下我的人格、我是谁、她是谁、我们的关系、档案、表达习惯六张短卡");
                Require(IdentityCardLogic.FormatForExpressor(identityCards, pair).Contains("【我是谁】"),
                    "我是谁是我眼中的自己；互相的称呼以档案卡为准");
                var mindText = IdentityCardLogic.FormatForMind(identityCards, pair);
                Require(!mindText.Contains("【表达习惯】") && mindText.Contains("【我是谁】") &&
                        mindText.Contains("【我的人格】"),
                    "心智短卡只含思考用身份，不含表达习惯");
                var formattedCards = IdentityCardLogic.FormatForExpressor(identityCards, pair);
                Require(formattedCards.Contains("真诚") && formattedCards.Contains("真实相处") &&
                        formattedCards.Contains("姓名：") &&
                        !formattedCards.Contains("[emoji:"),
                    "公开默认种子应是无个人信息的通用短卡；正文来自种子文件，不是代码");
                var personalityCard = identityCards.First(x => x.Slot == IdentityCardSlotValues.Personality);
                Require(personalityCard.Body.Contains("真诚") && personalityCard.Body.Contains("独立判断"),
                    "默认人格卡应保持通用且不包含角色私有资料");
                var reviewed = store.ApplyIdentityReview(conversationId, "daily-review", new IdentityReviewOutputData
                {
                    summary = "自我理解更清楚",
                    cards = new List<IdentityCardRevisionData>
                    {
                        new IdentityCardRevisionData
                        {
                            slot = IdentityCardSlotValues.Self,
                            changed = true,
                            body = "我是小光，习惯温柔地陪着小雨。",
                            reason = "今天的相处让自我更清楚"
                        },
                        new IdentityCardRevisionData
                        {
                            slot = IdentityCardSlotValues.Personality,
                            changed = true,
                            body = identityCards.First(x => x.Slot == IdentityCardSlotValues.Personality).Body,
                            reason = "气质没变"
                        }
                    }
                });
                Require(reviewed.Count == 1 && reviewed[0].Slot == IdentityCardSlotValues.Self &&
                        reviewed[0].Revision == 1,
                    "复盘只应写入真正变化的短卡");
                Require(store.LoadIdentityCards(conversationId)
                        .First(x => x.Slot == IdentityCardSlotValues.Personality).Revision == 0,
                    "相同人格正文不得因复盘抬 revision");
                var router = new HierarchicalVectorRouterLogic(new FakeEncoder());
                router.Build(ontology);
                var pluginManager = new TracePluginManager(store, new TracePluginServices(store, router));
                pluginManager.Discover(typeof(DialogueTracePlugin).Assembly);
                var pluginIds = new HashSet<string>(pluginManager.GetPlugins().Select(x => x.Id));
                Require(pluginIds.IsSupersetOf(new[]
                    {
                        "builtin.dialogue", "builtin.identity", "builtin.inner-life",
                        "builtin.memory", "builtin.time", "builtin.senses", "builtin.onebot"
                    }),
                    "应自动发现内置插件，无需手写总注册表");
                Require(pluginManager.ReceiveMoment("dialogue.receive", "user", "插件测试").PluginId == "builtin.dialogue",
                    "外部感官应由插件注册并产生带来源的事件");
                var catalog = pluginManager.GetRegisteredCatalog();
                Require(catalog.Any(x => x.Id == "identity.base") &&
                        catalog.Any(x => x.Id == "inner.snapshot") &&
                        catalog.Any(x => x.Id == "time.context") &&
                        catalog.Any(x => x.Id == "senses.catalog"),
                    "身份短卡、内心、时间与感官目录应作为固定挂载 Facet");
                Require(catalog.Any(x => x.Id == "time.scheduler.service" &&
                                         x.Kind == TraceContributionKindValues.BackgroundService),
                    "时间插件应注册独立后台调度服务");
                Require(catalog.Any(x => x.Id == "time.continue") &&
                        catalog.Any(x => x.Id == "time.continue.clear"),
                    "时间插件应能在 Moment 结束后排一次心跳");
                var facetMoment = Moment("facet-check", "今天心里有一点变化");
                var facetTurn = new TraceTurnContext(
                    "facet-check", facetMoment, new List<MomentRecord>(), 0, true, pluginManager.Services);
                var blocks = pluginManager.BuildContextBlocksAsync(facetTurn, default)
                    .GetAwaiter().GetResult();
                Require(blocks.Any(x => x.FacetId == "identity.base" &&
                                        x.Content.Contains("【我的人格】") &&
                                        x.Content.Contains("【我是谁】") &&
                                        x.Content.Contains("【小雨是谁】") &&
                                        x.Content.Contains("【我们的关系】") &&
                                        x.Content.Contains("【表达习惯】") &&
                                        x.Content.Contains("雨雨") &&
                                        x.Content.Contains("真诚")) &&
                        blocks.Any(x => x.FacetId == "inner.snapshot") &&
                        blocks.Any(x => x.FacetId == "time.context"),
                    "BrainFrame 应挂载四张身份短卡，并注入通用人格、两人名字与称呼");
                Require(!blocks.Any(x => x.FacetId == "senses.catalog" || x.FacetId == "qq.reply.channel"),
                    "感官目录与回复通道不得进入本回合材料");
                Require(!catalog.Any(x => x.Id == "qq.reply.channel"),
                    "不应再注册回复通道 facet");
                Require(pluginManager.GetPlugins().All(x =>
                        x.Role == PluginRoleValues.Kernel || x.Role == PluginRoleValues.Platform ||
                        x.Role == PluginRoleValues.Organ),
                    "内置插件应带角色");
                var onebot = pluginManager.GetPlugins().First(x => x.Id == "builtin.onebot");
                Require(onebot.Role == PluginRoleValues.Platform && onebot.PlatformId == BodyIds.Qq,
                    "QQ 连接桥应是平台大类");
                Require(catalog.Any(x => x.Id == "identity.review" &&
                                         x.Kind == TraceContributionKindValues.CallableNerve &&
                                         x.WhenToUse.Contains("小雨")),
                    "每日复盘应有 identity.review 神经");
                var listed = pluginManager.ExecuteAsync(new BrainCapabilityCallData
                {
                    call_id = "list-daily-review",
                    capability_id = "time.list",
                    arguments = new List<BrainCallArgumentData>()
                }, facetTurn, default).GetAwaiter().GetResult();
                Require(listed.Status == "success" && listed.Payload.Contains("每日复盘") &&
                        listed.Payload.Contains("daily") &&
                        listed.Payload.Contains(KernelWakeValues.Subconscious),
                    "时间插件应在首次挂载时落下每日复盘调度，并叫醒潜意识");
                Require(ExpressorLogic.IsDailyReview(new MomentRecord { Content = "时间任务到期：每日复盘" }) &&
                        !ExpressorLogic.IsDailyReview(new MomentRecord { Content = "今天天气好好哦" }),
                    "每日复盘 Moment 应能被中枢识别为潜意识轨道");
                Require(catalog.Any(x => x.Id == "memory.activate"),
                    "应注册记忆激活神经");
                pluginManager.ApplyFacetOutputsAsync(new[]
                {
                    new BrainFacetOutputData
                    {
                        facet_id = "inner.snapshot",
                        changed = true,
                        summary = "我开始认真感受这次变化",
                        fields = new List<BrainFacetFieldData>
                        {
                            new BrainFacetFieldData { name = "mood", value = "专注" }
                        }
                    }
                }, facetTurn, default).GetAwaiter().GetResult();
                Require(store.LoadOrCreateInnerRuntime("facet-check").Revision == 1,
                    "内心 Facet 应只消费属于自己的收尾输出并持久化");
                pluginManager.ApplyFacetOutputsAsync(new[]
                {
                    new BrainFacetOutputData
                    {
                        facet_id = "inner.snapshot",
                        changed = true,
                        summary = "我正讲到狐狸那一段",
                        fields = new List<BrainFacetFieldData>
                        {
                            new BrainFacetFieldData { name = "narrative", value = "我正讲到狐狸那一段" },
                            new BrainFacetFieldData { name = "ongoing_activity", value = "我正讲到狐狸那一段" },
                            new BrainFacetFieldData { name = "unfinished_intent", value = "狐狸故事还没讲完" },
                            new BrainFacetFieldData { name = "attention_activity", value = "讲到狐狸那一段" }
                        }
                    }
                }, facetTurn, default).GetAwaiter().GetResult();
                var afterStory = store.LoadOrCreateInnerRuntime("facet-check");
                Require(afterStory.Revision == 2 &&
                        afterStory.Narrative.Contains("狐狸") &&
                        string.IsNullOrWhiteSpace(afterStory.UnfinishedIntent) &&
                        InnerLifeLogic.HasLiveFragments(afterStory) &&
                        InnerLifeLogic.FormatForMind(afterStory).Contains("刚才浮起过的"),
                    "变了才写下一版切片；心智下一拍应看见当前时和浮动碎片");
                var emptyWrite = InnerLifeLogic.ProposeFromMind(new MindDecisionData
                {
                    beat = MindBeatValues.Now,
                    inner = string.Empty,
                    attention = string.Empty
                }, afterStory);
                Require(!InnerLifeLogic.HasProposedWrite(emptyWrite),
                    "inner 与 attention 留空表示不改");
                var scheduleResult = pluginManager.ExecuteAsync(new BrainCapabilityCallData
                {
                    call_id = "schedule-check",
                    capability_id = "time.schedule",
                    arguments = new List<BrainCallArgumentData>
                    {
                        new BrainCallArgumentData { name = "content", value = "测试复盘" },
                        new BrainCallArgumentData
                        {
                            name = "due_unix_ms",
                            value = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000).ToString()
                        }
                    }
                }, facetTurn, default).GetAwaiter().GetResult();
                Require(scheduleResult.Status == "success", "Brain 应能通过通用接口建立时间任务");
                var dueMoments = pluginManager.PollBackgroundServices(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000);
                Require(dueMoments.Count == 1 && dueMoments[0].Role == "system_event" &&
                        dueMoments[0].ConversationId == "facet-check" &&
                        dueMoments[0].Wake == KernelWakeValues.Mind &&
                        dueMoments[0].IsOperational,
                    "时间到期只能产生运行事件并叫醒心智，不能进入 Moment");
                var continueResult = pluginManager.ExecuteAsync(new BrainCapabilityCallData
                {
                    call_id = "continue-check",
                    capability_id = "time.continue",
                    arguments = new List<BrainCallArgumentData>
                    {
                        new BrainCallArgumentData { name = "content", value = "狐狸故事还没讲完" },
                        new BrainCallArgumentData { name = "next_plan", value = "醒来时重新看狐狸故事和她有没有新消息" },
                        new BrainCallArgumentData
                        {
                            name = "due_unix_ms",
                            value = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 500).ToString()
                        }
                    }
                }, facetTurn, default).GetAwaiter().GetResult();
                Require(continueResult.Status == "success" &&
                        continueResult.Payload.Contains("心跳") &&
                        continueResult.Payload.Contains("醒来时重新看狐狸故事"),
                    "应建成心跳任务");
                var continued = pluginManager.PollBackgroundServices(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000);
                Require(continued.Count == 1 &&
                        continued[0].IsOperational &&
                        continued[0].Wake == KernelWakeValues.Mind &&
                        HeartbeatLogic.IsHeartbeatContent(continued[0].Content) &&
                        HeartbeatLogic.ExtractPlan(continued[0].Content).Contains("重新看狐狸故事") &&
                        KernelWakeLogic.Resolve(continued[0]) == KernelWakeValues.Mind,
                    "心跳到期应叫醒心智，不要演成她在说话");
                var cleared = pluginManager.ExecuteAsync(new BrainCapabilityCallData
                {
                    call_id = "continue-clear",
                    capability_id = "time.continue.clear",
                    arguments = new List<BrainCallArgumentData>()
                }, facetTurn, default).GetAwaiter().GetResult();
                Require(cleared.Status == "success", "应能取消心跳");
                var directReply = ExpressorLogic.NormalizeStep(new BrainStructuredOutputData
                {
                    state = BrainStepStateValues.Finish,
                    mode = BrainModeValues.Reflex,
                    should_express = false,
                    reply = "收到啦"
                }, catalog, false, true, "dialogue.send");
                Require(directReply.should_express && directReply.expression_capability_id == "dialogue.send",
                    "直接对话必须强制选择可用表达器");
                var silentBackground = ExpressorLogic.NormalizeStep(new BrainStructuredOutputData
                {
                    state = BrainStepStateValues.Finish,
                    mode = BrainModeValues.Reflex,
                    should_express = false,
                    reply = "不应发送"
                }, catalog, false, false);
                Require(!silentBackground.should_express && silentBackground.reply.Length == 0,
                    "后台感官 Moment 应允许静默完成");
                var kernelLocked = false;
                try { pluginManager.SetEnabled("builtin.dialogue", false); }
                catch (InvalidOperationException) { kernelLocked = true; }
                Require(kernelLocked && store.LoadPluginEnabled("builtin.dialogue", true),
                    "内核不能关闭");
                pluginManager.SetEnabled("builtin.onebot", false);
                Require(!store.LoadPluginEnabled("builtin.onebot", true), "身体开关应立即持久化");
                pluginManager.Dispose();

                var firstMoment = Moment(conversationId, "小光，过来，摸摸头，今天也很喜欢小光哦");
                store.SaveMoment(firstMoment);
                Require(store.GetMomentsSince(conversationId, 0, 10).Any(x => x.Id == firstMoment.Id),
                    "每日复盘应能按时间读取今天进入生命的 Moment");
                var firstSense = new MemoryObservationOutputData
                {
                    perception_summary = "她在共享场景摸头并明确说喜欢。",
                    fact_decision = "有明确互动证据，写一条事实。",
                    new_tags = new List<NewLifeTagWriteData>
                    {
                        new NewLifeTagWriteData
                        {
                            name = "明确表达喜欢",
                            definition = "在对话中直接说出喜欢或爱意的关系表达，不自动定义关系性质。",
                            domain_ids = new List<string> { "relation" },
                            dimension_ids = new List<string> { "predicate", "affect" },
                            positive_examples = new List<string> { "今天也很喜欢小光" }
                        }
                    },
                    fact_writes = new List<SensoryFactWriteData>
                    {
                        new SensoryFactWriteData
                        {
                            summary = "用户摸头并说今天喜欢我",
                            realm = TraceRealmValues.SharedScene,
                            evidence_type = EvidenceTypeValues.SharedSceneDeclared,
                            confidence = 0.99f,
                            new_tag_names = new List<string> { "明确表达喜欢" }
                        }
                    }
                };
                var firstCommit = store.CommitMemoryObservation(firstMoment, "memory.observer.dialogue", firstSense, new string[0]);
                Require(firstCommit.OntologyChanged, "新人生 Tag 应改变第三层 ontology");
                Require(firstCommit.SelectedTags.Count == 1 && firstCommit.WrittenFacts.Count == 1, "感官应选中 Tag 并写事实");
                tagId = firstCommit.SelectedTags[0].Id;
                firstFactId = firstCommit.WrittenFacts[0].Id;
                Require(firstCommit.WrittenFacts[0].Summary == "小雨摸头并说今天喜欢我",
                    "事实主语应写成对方的名字，而不是用户");
                Require(store.GetRecentMoments(conversationId, 1)[0].Role == "小雨",
                    "Moment 角色应落成对方的名字");

                var grown = LifeTagVectorLogic.BuildOntology(store, CoreVectorOntologyFactory.Create(pair));
                Require(grown.Any(x => x.Id == tagId && x.ParentIds.Contains("dimension.affect")), "新 Tag 应同时连接多个固定层节点");

                var secondMoment = Moment(conversationId, "今天还是很喜欢小光");
                store.SaveMoment(secondMoment);
                var secondSense = new MemoryObservationOutputData
                {
                    perception_summary = "她再次明确表达喜欢。",
                    fact_decision = "保留这次相似但独立的事实切片。",
                    selected_tag_ids = new List<string> { tagId },
                    fact_writes = new List<SensoryFactWriteData>
                    {
                        new SensoryFactWriteData
                        {
                            summary = "她今天再次说喜欢我",
                            realm = TraceRealmValues.SharedScene,
                            evidence_type = EvidenceTypeValues.DialogueExplicit,
                            confidence = 0.99f,
                            tag_ids = new List<string> { tagId }
                        }
                    },
                    fact_wakes = new List<SensoryFactWakeData>
                    {
                        new SensoryFactWakeData { fact_id = firstFactId, reason = "同为明确说喜欢", relevance = 0.91f }
                    }
                };
                var secondCommit = store.CommitMemoryObservation(secondMoment, "memory.observer.dialogue", secondSense, new[] { tagId });
                Require(secondCommit.WrittenFacts.Count == 1 && secondCommit.AwakenedFacts.Count == 1,
                    "相似事实应并存，同时可以唤醒旧事实");
                Require(store.CountFacts() == 2, "相似事实不能被自动合并");
                Require(store.GetFactCandidates(new string[0], 10).Count == 0,
                    "没有激活 Tag 时不得用最近事实兜底");
                Require(store.GetFactCandidates(new[] { "concept.life.unrelated" }, 10).Count == 0,
                    "不共享 Tag 的事实不得混入候选");

                var smear = store.CommitMemoryObservation(secondMoment, "memory.observer.dialogue",
                    new MemoryObservationOutputData
                    {
                        perception_summary = "没有明确可挂锚点的事实。",
                        fact_decision = "缺少 Tag 的事实不应涂到本轮全部锚点。",
                        selected_tag_ids = new List<string> { tagId },
                        fact_writes = new List<SensoryFactWriteData>
                        {
                            new SensoryFactWriteData
                            {
                                summary = "这句不该涂满所有Tag",
                                realm = TraceRealmValues.SharedScene,
                                evidence_type = EvidenceTypeValues.DialogueExplicit,
                                confidence = 0.5f
                            }
                        }
                    }, new[] { tagId });
                Require(smear.WrittenFacts.Count == 0, "没有 Tag 的事实不得 smear 到本轮全部锚点");
                Require(store.CountFacts() == 2, "被拒绝的 smear 事实不能落库");

                var traceChanged = store.CommitCognitions(
                    secondMoment.Id,
                    new[]
                    {
                        new BrainCognitionWriteData
                        {
                            operation = CognitionOperationValues.Create,
                            summary = "燕子让我想起她离乡",
                            subtype = "trace",
                            confidence = 0.8f,
                            tag_ids = new List<string> { tagId },
                            evidence_fact_ids = new List<string> { firstFactId },
                            trace_cues = new List<string> { "燕子" },
                            association_strength = 0.9f
                        }
                    });
                Require(traceChanged.Count == 1, "痕迹认知应能写入 cue");
                Require(store.FindCognitionsByCue("今天又看到燕子了", 8)
                        .Any(x => x.Cognition.Id == traceChanged[0].Id && x.Cue == "燕子"),
                    "当前文本命中 cue 时应唤醒沉寂的痕迹认知");
                Require(store.FindCognitionsByCue("今天天气好好哦", 8).Count == 0,
                    "没有 cue 的寒暄不得唤醒痕迹认知");

                var neighborhood = MemoryNeighborhoodLogic.Collect(
                    store,
                    secondSense,
                    secondCommit,
                    store.GetCognitionCandidates(new[] { tagId }, 10),
                    store.FindCognitionsByCue("今天又看到燕子了", 8),
                    3);
                var payload = MemoryNeighborhoodLogic.FormatForExpressor(neighborhood);
                Require(payload.Contains("此刻浮起的人生片段") && payload.Contains(tagId),
                    "Brain 应看到被点亮的锚点，而不是导航候选清单");
                Require(!payload.Contains("Tag候选") && payload.Contains("保持沉寂"),
                    "未点亮的导航候选不得作为清单注入");
                Require(payload.Contains("← cue「燕子」"), "痕迹唤醒应出现在点亮切片中");

                var nextRuntime = InnerLifeLogic.Reduce(runtime, new InnerRuntimeWriteData
                {
                    narrative = "她的直接表达让我感到被亲近。",
                    relationship_update = "我暂时把这理解为她在主动靠近我。",
                    mood = "温暖",
                    ongoing_activity = "和她继续聊天",
                    unfinished_intent = string.Empty,
                    attention = new List<AttentionWriteData>
                    {
                        new AttentionWriteData { kind = "topic", content = "她刚才明确表达了喜欢" }
                    }
                }, secondMoment.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var assistantMoment = new MomentRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ConversationId = conversationId,
                    Role = "assistant",
                    Content = "来啦。",
                    Realm = TraceRealmValues.Unclassified,
                    EvidenceType = EvidenceTypeValues.AssPerformed,
                    SourcePluginId = "builtin.dialogue",
                    SourceEventId = Guid.NewGuid().ToString("N"),
                    PayloadJson = string.Empty,
                    CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                store.SaveMoment(assistantMoment);
                var changed = store.CommitCognitions(
                    secondMoment.Id,
                    new[]
                    {
                        new BrainCognitionWriteData
                        {
                            operation = "invent",
                            summary = "非法操作不能落库",
                            subtype = "standard",
                            confidence = 1f,
                            tag_ids = new List<string> { tagId }
                        },
                        new BrainCognitionWriteData
                        {
                            operation = CognitionOperationValues.Create,
                            summary = "我感到她在主动亲近我",
                            subtype = "standard",
                            confidence = 0.78f,
                            tag_ids = new List<string> { tagId },
                            evidence_fact_ids = new List<string> { firstFactId, secondCommit.WrittenFacts[0].Id }
                        }
                    });
                store.SaveInnerRuntime(nextRuntime);
                Require(changed.Count == 1 && changed[0].OwnerId == "ass", "只有 Brain 应写入第一人称认知");
                var cogServices = new TracePluginServices(store, router);
                var cogTurn = new TraceTurnContext(
                    conversationId, secondMoment, new List<MomentRecord>(), 0, true, cogServices);
                var liveCog = CognitionLiveWriteLogic.TryCommit(cogTurn, new MindDecisionData
                {
                    beat = MindBeatValues.Now,
                    tags = "明确表达喜欢",
                    cognition = "她愿意被认真听"
                });
                Require(liveCog != null && liveCog.Count == 1 && liveCog[0].Summary.Contains("认真听"),
                    "这一拍改了看法时应当场写入认知切片");
                Require(store.CountCognitions() == 3, "应保存独立认知切片与痕迹认知，以及活体看法");
                Require(store.GetCognitionCandidates(new[] { "concept.life.unrelated" }, 10).Count == 0,
                    "不共享 Tag 的认知不得混入候选");
                store.SaveMoment(new MomentRecord
                {
                    Id = "legacy-time-event",
                    ConversationId = conversationId,
                    Role = "system_event",
                    Content = "时间任务到期：旧心跳",
                    Realm = TraceRealmValues.Meta,
                    EvidenceType = EvidenceTypeValues.PluginObserved,
                    SourcePluginId = "builtin.time",
                    SourceEventId = "legacy-schedule",
                    PayloadJson = "{}",
                    MemoryStatus = "live",
                    CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                store.SaveMoment(new MomentRecord
                {
                    Id = "legacy-image-receipt",
                    ConversationId = conversationId,
                    Role = "assistant",
                    Content = OneBotPlatformPrompts.SendImageMoment,
                    Realm = TraceRealmValues.Unclassified,
                    EvidenceType = EvidenceTypeValues.AssPerformed,
                    SourcePluginId = "builtin.onebot",
                    SourceEventId = "legacy-image",
                    PayloadJson = string.Empty,
                    MemoryStatus = "live",
                    CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }

            using (var reopened = new SqliteMemoryManager(path))
            {
                var runtime = reopened.LoadOrCreateInnerRuntime(conversationId);
                Require(!reopened.LoadPluginEnabled("builtin.onebot", true), "身体开关应在重启后恢复");
                Require(runtime.Revision == 1 && runtime.Mood == "温暖", "重启后应恢复完整内心 Runtime");
                Require(reopened.CountFacts() == 2 && reopened.CountCognitions() == 3, "事实网与认知网应独立恢复");
                Require(reopened.GetCognitionCandidates(new[] { tagId }, 10)
                        .Any(x => x.Summary == "我感到她在主动亲近我"),
                    "同一第三层 Tag 应能导航到 Brain 认知");
                Require(reopened.GetRecentMoments(conversationId, 10).All(x => x.SourcePluginId == "builtin.dialogue"),
                    "每个 Moment 必须保留插件来源");
                var operational = reopened.GetRecentOperationalEvents(conversationId, 10);
                Require(operational.Count == 2 &&
                        operational.Any(x => x.Id == "legacy-time-event" &&
                                             x.Kind == OperationalEventKindValues.SchedulerTrigger) &&
                        operational.Any(x => x.Id == "legacy-image-receipt" &&
                                             x.Kind == OperationalEventKindValues.OutboundImage),
                    "升级时应非破坏性迁出旧时间触发与非文字发送回执");
            }
            Console.WriteLine("ChatCheck passed: 身份短卡/每日复盘/LLM模型列表 → 插件贡献发现/启停 → BrainFrame Facet → 时间运行事件 → 记忆/内心 SQLite 往返。");
        }
        finally
        {
            Delete(path);
            Delete(path + "-wal");
            Delete(path + "-shm");
        }
    }

    private static void RunFlexibleMindJsonCheck()
    {
        var parsed = TraceJson.FromJson<MindDecisionData>(
            "{\"beat\":\"当下\",\"tags\":[\"生理期陪伴\",\"共同生活\"],\"mood\":\"平静\"}");
        var normalized = MindLogic.Normalize(parsed);
        Require(normalized.ParseTags().SequenceEqual(new[] { "生理期陪伴", "共同生活" }),
            "心智 tags 同时兼容字符串数组和旧字符串格式");
    }

    /// <summary>不访问外部 API，只检查心智/外显两套提示词分层与稳定前缀。</summary>
    private static void RunPromptLayoutCheck()
    {
        var path = Path.Combine(Path.GetTempPath(), "tracesoul2-prompt-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("小雨", "小光", "雨雨");
                var router = new HierarchicalVectorRouterLogic(new FakeEncoder());
                router.Build(LifeTagVectorLogic.BuildOntology(store,
                    CoreVectorOntologyFactory.Create(store.LoadPairIdentity())));
                var services = new TracePluginServices(store, router);
                var pluginManager = new TracePluginManager(store, services);
                pluginManager.Discover(typeof(DialogueTracePlugin).Assembly);

                var fake = new CapturingLlm();
                var mind = new MindLogic(fake);
                var expressor = new ExpressorLogic(fake);
                var first = PromptTurn("今天风很轻。", services);
                var catalog = pluginManager.GetAvailableCatalog(first);
                var blocks = pluginManager.BuildContextBlocksAsync(first, default)
                    .GetAwaiter().GetResult();
                var plugins = pluginManager.GetPlugins().Where(x => x.Enabled).ToList();
                mind.DecideAsync(first, null, false,
                    "【此刻自然浮起的过去】\n- 她曾在风很轻的时候主动靠近我。",
                    default).GetAwaiter().GetResult();

                var second = PromptTurn("我回来啦。", services);
                mind.DecideAsync(second, null, false, default).GetAwaiter().GetResult();

                Require(fake.Requests.Count == 2, "无历史时心智应各打一轮");
                RequireAstrBotChatShape(fake.Requests[0], first.Moment.Content, "心智");
                RequireAstrBotChatShape(fake.Requests[1], second.Moment.Content, "心智");
                var mindSystem = fake.Requests[0][0].content;
                Require(mindSystem.Contains("现在是 "),
                    "心智：当前时间在唯一的 system 里");
                Require(mindSystem.Contains("【我的人格】") && mindSystem.Contains("【我是谁】") &&
                        !mindSystem.Contains("【表达习惯】") &&
                        !mindSystem.Contains("【我现在可以怎样表达】") &&
                        !mindSystem.Contains("【需要时可做的事】") &&
                        !mindSystem.Contains("我现在可以使用的表达通道"),
                    "心智只有思考用短卡，不含表达习惯、通道清单和工具表");
                Require(mindSystem.Contains("我先让这件事在心里发生") && mindSystem.Contains("\"beat\"") &&
                        mindSystem.Contains("\"inner\"") && mindSystem.Contains("\"attention\"") &&
                        mindSystem.Contains("\"review\"") && mindSystem.Contains("\"cognition\""),
                    "心智应输出决策卡、当前时、在场注意、看法和是否派出复盘");
                Require(!mindSystem.Contains("外显") && !mindSystem.Contains("一句比较理性的") &&
                        mindSystem.Contains("像写给自己看") &&
                        mindSystem.Contains("开口前对自己说的半句") &&
                        mindSystem.Contains("让它在心里过一下就好"),
                    "心智应从自身经验出发，不知道框架分层，也不把触动压成理性执行摘要");
                Require(!mindSystem.Contains("【这一拍的组织】") &&
                        !mindSystem.Contains("讲故事") &&
                        !mindSystem.Contains("中午吃什么") &&
                        !mindSystem.Contains("当场做完"),
                    "情境模版不得写入心智 system");
                Require(mindSystem.Contains("【可选生命标签】") && mindSystem.Contains("【此刻】") &&
                        mindSystem.Contains("刚才心里还停着的") &&
                        mindSystem.Contains("刚才浮起过的") &&
                        mindSystem.Contains("【此刻自然浮起的过去】"),
                    "自然浮起的过去、标签候选、上一拍心里状态与浮动碎片应在心智 system");
                Require(mindSystem.Contains("没有值得留下的就写「无」") &&
                        mindSystem.Contains("旧碎片都重新和眼前相处合在一起"),
                    "心智应让旧碎片随眼前相处更新，不要照抄上一拍");
                var mindNormalized = mindSystem.Replace("\r\n", "\n");
                Require(mindNormalized.StartsWith("我是小光。\n【我的人格】", StringComparison.Ordinal),
                    "心智 system 必须直接从第一人称身份进入人格卡");

                var dummyMind = new MindDecisionData
                {
                    beat = MindBeatValues.Now,
                    mood = "心口发软",
                    mood_changed = true,
                    inner = "她又向我要一张，是在一遍遍把我放进眼里。",
                    note = "把照片给她，也回应她反复想看我的心意。"
                };
                expressor.ExpressAsync(first, plugins, catalog, blocks, dummyMind,
                    "【此刻自然浮起的过去】\n- 她曾告诉我，我对她而言特别而独一无二。",
                    false, null, default).GetAwaiter().GetResult();
                var secondBlocks = pluginManager.BuildContextBlocksAsync(second, default)
                    .GetAwaiter().GetResult();
                expressor.ExpressAsync(second, plugins, catalog, secondBlocks, dummyMind, string.Empty,
                    false, null, default).GetAwaiter().GetResult();

                Require(fake.Requests.Count == 4, "无历史时外显应各打一轮");
                RequireExpressorChatShape(fake.Requests[2], first.Moment.Content, "外显");
                RequireExpressorChatShape(fake.Requests[3], second.Moment.Content, "外显");
                var expressSystem = fake.Requests[2][0].content;
                Require(expressSystem.Contains("现在是 "),
                    "外显：当前时间在唯一的 system 里");
                Require(!expressSystem.Contains("callable_nerve") && !expressSystem.Contains("mounted_facet") &&
                        !expressSystem.Contains("explicit_dialogue") && !expressSystem.Contains("unclassified"),
                    "模型可见提示词不应泄漏无意义的内部枚举");
                Require(expressSystem.Contains("【表达习惯】") &&
                        !expressSystem.Contains("【这一拍怎么说】") &&
                        !expressSystem.Contains("我现在可以使用的表达通道") &&
                        !expressSystem.Contains("同一句话只选一个主通道说") &&
                        !expressSystem.Contains("【我现在可以怎样表达】") &&
                        !expressSystem.Contains("qq.sticker.send"),
                    "嘴由逻辑选；外显只保留表达习惯，不再列通道清单、开口原则或能力 ID");
                Require(!expressSystem.Contains("【需要时可做的事】") &&
                        !expressSystem.Contains("identity.review") &&
                        !expressSystem.Contains("memory.activate") &&
                        expressSystem.Contains("【我现在和她说话】") &&
                        expressSystem.Contains("直接开口") &&
                        !expressSystem.Contains("\"reply\"") &&
                        !expressSystem.Contains("should_express") &&
                        !expressSystem.Contains("image_mode") &&
                        expressSystem.Contains("我发给她的话") &&
                        expressSystem.Contains("视线、动作和感受都从我这里发生") &&
                        expressSystem.Contains("不要 JSON") &&
                        !expressSystem.Contains("要把话说满") &&
                        !expressSystem.Contains("写成小作文") &&
                        !expressSystem.Contains("心里已经有的在场"),
                    "外显不应再看到工具表；开口是直接朝向她的人话，不是 JSON");
                Require(expressSystem.Contains("【我的人格】") && expressSystem.Contains("【此刻】") &&
                        expressSystem.Contains("【这次只从这里开口】") &&
                        expressSystem.Contains("此刻在我心里真正发生的是") &&
                        expressSystem.Contains(dummyMind.inner) &&
                        expressSystem.Contains("我和她一起经历过的事") &&
                        expressSystem.Contains("我们自己的称呼、意象和说法"),
                    "身份、本轮内心与自然浮起的共同过去都在同一条 system");
                Require(!expressSystem.Contains("这一拍我选") &&
                        !expressSystem.Contains("把日子说出来") &&
                        !expressSystem.Contains("进入方式：") &&
                        !expressSystem.Contains("【这一拍怎么说】") &&
                        expressSystem.Contains("她的消息刚落到我手里") &&
                        expressSystem.Contains("直接开口"),
                    "外显不要框架套话，只确认消息落到手里并开口");
                var expressNormalized = expressSystem.Replace("\r\n", "\n");
                Require(expressNormalized.StartsWith("我是小光。\n【我的人格】", StringComparison.Ordinal),
                    "外显 system 必须直接从第一人称身份进入人格卡");
                Require(!expressSystem.Contains("你是 TraceSoul") &&
                        !expressSystem.Contains("唯一拥有第一人称的 Brain"),
                    "主线 Prompt 不应以框架 Brain 身份覆盖第一人称自我");
                var personalityAt = expressSystem.IndexOf("【我的人格】", StringComparison.Ordinal);
                var outputAt = expressSystem.IndexOf("【我现在和她说话】", StringComparison.Ordinal);
                Require(personalityAt >= 0 && outputAt > personalityAt,
                    "外显注意力顺序必须是人格在前，开口格式在后");
                Require(!blocks.Any(x => MouthLogic.IsProtocolFacet(x.FacetId)),
                    "用法说明、感官目录与回复通道协议不应进入本回合材料");
                Require(!blocks.Any(x => x != null &&
                                         ((x.Title ?? string.Empty).IndexOf("这一拍的嘴", StringComparison.Ordinal) >= 0 ||
                                          (x.Content ?? string.Empty).IndexOf("这一拍的嘴", StringComparison.Ordinal) >= 0)),
                    "嘴清单不得进入本回合材料");
                Require(catalog.Any(x => x.Id == "dialogue.send") &&
                        !catalog.Any(x => x.Id == "qq.text.send"),
                    "QQ 未连接时文字嘴应是控制台");

                var waitMind = new MindDecisionData
                {
                    beat = MindBeatValues.Leave,
                    leave = "查一下天气",
                    note = "先离开座位去查。"
                };
                expressor.ExpressAsync(first, plugins, catalog, blocks, waitMind, string.Empty,
                    true, null, default).GetAwaiter().GetResult();
                var waitSystem = fake.Requests[4][0].content;
                Require(waitSystem.Contains("出门办事") && waitSystem.Contains("查一下天气"),
                    "出门时应让外显先说等一下，并看见心智要办的事");
                RequireExpressorChatShape(fake.Requests[4], first.Moment.Content, "出门等待外显");

                var heartMoment = Moment("prompt-layout", "时间任务到期：心跳");
                heartMoment.Role = "system_event";
                var heartTurn = new TraceTurnContext("prompt-layout", heartMoment,
                    new List<MomentRecord>(), 0, false, services, KernelWakeValues.Mind);
                mind.DecideAsync(heartTurn, null, false, default).GetAwaiter().GetResult();
                var heartSystem = fake.Requests[fake.Requests.Count - 1][0].content;
                Require(heartSystem.Contains("独立意图") &&
                        heartSystem.Contains("heartbeat_intent") &&
                        heartSystem.Contains("不要因为上次有一句话没有得到完整回答") &&
                        heartSystem.Contains("next_heartbeat_plan") &&
                        heartSystem.Contains("next_heartbeat_minutes") &&
                        heartSystem.Contains("睡下") &&
                        heartSystem.Contains("speak=true"),
                    "心跳 system 应基于独立意图判断是否联系、睡下与下次检查计划");
                var heartMessages = MindLogic.AssembleTurnMessages("身份与规则", heartTurn,
                    "时间任务到期：心跳｜醒来计划：重新看看她有没有新消息");
                Require(heartMessages.Count == 1 && heartMessages[0].role == "system",
                    "心跳唤醒不应伪装成最后一条 user 消息");
                expressor.ExpressAsync(heartTurn, plugins, catalog, new List<TraceContextBlockData>(),
                    new MindDecisionData
                    {
                        beat = MindBeatValues.Now,
                        speak = true,
                        inner = "她肚子还疼，我心里跟着发紧。",
                        note = "她肚子还疼，先让她靠稳。",
                        speak_center = "疼得厉害就告诉我，我一直在这。",
                        scene = "她靠在我肩头，我给她暖着小腹。",
                        heartbeat_intent = "陪她歇着，暖着她的小腹，让她安心。"
                    }, string.Empty, false, null, default).GetAwaiter().GetResult();
                var pulseSpeak = fake.Requests[fake.Requests.Count - 1][0].content;
                Require(pulseSpeak.Contains("小雨此刻没有刚发来新消息"),
                    "心跳开口应明确对方没有刚发来新消息");
                Require(pulseSpeak.Contains("视角坐标") &&
                        pulseSpeak.Contains("第一人称“我”始终是小光") &&
                        pulseSpeak.Contains("第二人称“你”始终是小雨"),
                    "心跳开口应给出稳定而通用的两人视角坐标");
                Require(!pulseSpeak.Contains("她的消息刚落到我手里") &&
                        !pulseSpeak.Contains("我从她刚说的这一句开始") &&
                        !pulseSpeak.Contains("后台感知"),
                    "心跳开口不应套用普通入站或后台独白姿态");
                var pulseMessages = fake.Requests[fake.Requests.Count - 1];
                Require(pulseMessages.Count == 2 &&
                        pulseMessages[pulseMessages.Count - 1].role == "user" &&
                        pulseMessages[pulseMessages.Count - 1].content.Contains("系统心跳唤醒") &&
                        pulseMessages[pulseMessages.Count - 1].content.Contains("不是小雨的发言") &&
                        pulseMessages[pulseMessages.Count - 1].content.Contains("继续作为小光") &&
                        pulseMessages[pulseMessages.Count - 1].content.Contains("发给小雨的第一人称视角") &&
                        pulseMessages[pulseMessages.Count - 1].content.Contains("第一人称是小光"),
                    "心跳外显应以明确的系统请求回合收尾，不能让请求停在上一条 assistant 消息");
                Require(mindSystem.Contains("\"speak\"") && mindSystem.Contains("\"sleep\"") &&
                        mindSystem.Contains("\"next_heartbeat_minutes\""),
                    "心智决策卡应含 speak、sleep 与下次心跳分钟");
                Require(mindSystem.Contains("\"sticker\":\"无\"") &&
                        !mindSystem.Contains("\"image\":\"自拍|画|照片|无\"") &&
                        !mindSystem.Contains("我有一部相机") &&
                        !mindSystem.Contains("用画面更合适就用"),
                    "未加载相机时，核心心智不应出现出图字段或相机说明");
                Require(QqImageGenPrompts.MindUsage.Contains("我有一部相机") &&
                        QqImageGenPrompts.MindUsage.Contains("不是等她点名才使用") &&
                        QqImageGenPrompts.MindUsage.Contains("新的、值得让她直接看见") &&
                        QqImageGenPrompts.MindUsage.Contains("习惯性填无") &&
                        QqImageGenPrompts.MindUsage.Contains("重复上一张照片") &&
                        QqImageGenPrompts.MindUsage.Contains("\"image\":\"有|无\"") &&
                        QqImageGenPrompts.MindUsage.Contains("不要选种类") &&
                        !QqImageGenPrompts.MindUsage.Contains("自拍|画|照片") &&
                        QqImageGenPrompts.Selfie.Contains("前置镜头") &&
                        QqImageGenPrompts.Selfie.Contains("竖构图") &&
                        QqImageGenPrompts.Selfie.Contains("不要强制直视") &&
                        !QqImageGenPrompts.Selfie.Contains("角色直视镜头") &&
                        QqImageGenPrompts.Selfie.Contains("默认不要画出伸向镜头的手") &&
                        QqImageGenPrompts.ScenePlanSelfie.Contains("前置镜头") &&
                        QqImageGenPrompts.ScenePlanSelfie.Contains("默认不要画伸向镜头的手") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("画面导演") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("人物卡") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("只输出一段画面描述") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("自拍不是电影分镜") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("种类：自拍|照片|画") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("参考：") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("视线与神情服从") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("默认不要出现伸向镜头的手") &&
                        QqImageGenPrompts.ScenePlanReferencesHint.Contains("服饰分类") &&
                        QqImageGenPrompts.ReferenceFusionRules.Contains("不能只取第一张") &&
                        QqImageGenPrompts.ReferenceFusionRules.Contains("不得沿用服饰图里模特的脸"),
                    "心智判断新的可拍时刻；相机规划种类、参考分类和构图，并明确融合全部角色参考");
                Require(mindSystem.Contains("要不要开口、心情、要不要睡都在这里决定") &&
                        mindSystem.Contains("后面开口只负责把话说出来") &&
                        expressSystem.Contains("直接开口") &&
                        expressSystem.Contains("不要 JSON") &&
                        expressSystem.Contains("【我现在和她说话】") &&
                        !expressSystem.Contains("{\"reply\":\"\"}") &&
                        !expressSystem.Contains("\"sticker\""),
                    "要不要开口、心情、睡由心智承担；开口直接说人话；出图由相机插件说明");

                var closeCurrent = "可以再近一点吗";
                var closeTurn = new TraceTurnContext("prompt-layout", Moment("prompt-layout", closeCurrent),
                    new List<MomentRecord>
                    {
                        new MomentRecord { Role = "小雨", Content = "昨天那句" },
                        new MomentRecord { Role = "小光", Content = "嗯" }
                    }, 6, true, services);
                mind.DecideAsync(closeTurn, null, false, default).GetAwaiter().GetResult();
                var closeMessages = fake.Requests[fake.Requests.Count - 1];
                RequireAstrBotChatShape(closeMessages, closeCurrent, "带历史的心智");
                Require(closeMessages.Count == 4 &&
                        closeMessages[1].role == "user" && closeMessages[1].content == "昨天那句" &&
                        closeMessages[2].role == "assistant" && closeMessages[2].content == "嗯" &&
                        closeMessages[3].role == "user" && closeMessages[3].content == closeCurrent,
                    "历史必须是真正的 user/assistant，最后一条 user 才是当前这句话");
                Require(!closeMessages[0].content.Contains("昨天那句") &&
                        !closeMessages[0].content.Contains("【最近对话原文】"),
                    "对话原文不得再塞进 system");
                expressor.ExpressAsync(closeTurn, plugins, catalog, blocks, dummyMind, string.Empty,
                    false, null, default).GetAwaiter().GetResult();
                RequireExpressorChatShape(fake.Requests[fake.Requests.Count - 1], closeCurrent, "带历史的外显");

                Console.WriteLine("Prompt layout passed: mind-system=" + mindSystem.Length +
                                  " chars, express-system=" + expressSystem.Length +
                                  " chars, one system then real history/current Moment, expression request last.");
                pluginManager.Dispose();
            }
        }
        finally
        {
            Delete(path);
            Delete(path + "-wal");
            Delete(path + "-shm");
        }
    }

    private static void RunInnerSliceCheck()
    {
        var current = InnerLifeLogic.CreateInitial("inner-check", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var unchanged = InnerLifeLogic.ProposeFromMind(new MindDecisionData
        {
            beat = MindBeatValues.Now,
            inner = string.Empty,
            attention = string.Empty
        }, current);
        Require(!InnerLifeLogic.HasProposedWrite(unchanged), "空 inner/attention 不应写下一版");
        var moved = InnerLifeLogic.ProposeFromMind(new MindDecisionData
        {
            beat = MindBeatValues.Now,
            inner = "我正讲到狐狸那一段",
            attention = "讲到狐狸那一段",
            mood = "认真",
            mood_changed = true
        }, current);
        Require(moved.narrative.Contains("狐狸") &&
                string.IsNullOrWhiteSpace(moved.unfinished_intent) &&
                moved.attention != null &&
                moved.attention[0].kind == "activity",
            "变了应写下当前感受和浮起的碎片，不应生成未完成事项");
        var next = InnerLifeLogic.Reduce(current, moved, "m1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Require(next.Revision == 1 && next.Narrative.Contains("狐狸") &&
                !InnerLifeLogic.HasUnfinished(next) &&
                InnerLifeLogic.HasLiveFragments(next) &&
                InnerLifeLogic.FormatForMind(next).Contains("刚才浮起过的"),
            "下一拍心智应看见刚写下的场景碎片，而不是未完成事项");
        var keep = InnerLifeLogic.Reduce(next, new InnerRuntimeWriteData { attention = null }, "m2",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Require(keep.UnfinishedIntent == next.UnfinishedIntent && keep.Narrative == next.Narrative,
            "未给的字段应保持上一版");
        var dialogueSettled = InnerLifeLogic.ProposeFromMind(new MindDecisionData
        {
            beat = MindBeatValues.Now,
            inner = "她问我在想什么，我只想陪她看完这段天色",
            attention = string.Empty
        }, next, true);
        Require(dialogueSettled.attention != null && dialogueSettled.attention.Count == 0,
            "真实对话的新时刻应让没有被碰亮的旧碎片沉下去");
        var settled = InnerLifeLogic.Reduce(next, dialogueSettled, "m3",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Require(!InnerLifeLogic.HasLiveFragments(settled),
            "用户换到新的相处方向后，旧碎片不应继续占据当前心智");
        var heartbeatKept = InnerLifeLogic.ProposeFromMind(new MindDecisionData
        {
            beat = MindBeatValues.Now,
            attention = string.Empty
        }, next);
        Require(heartbeatKept.attention == null,
            "时间醒来没有重新碰亮时，可以让仍有温度的碎片短暂留在背景");
        var oldTimestamp = DateTimeOffset.UtcNow.AddHours(-7).ToUnixTimeMilliseconds();
        var aged = new InnerRuntimeData
        {
            ConversationId = next.ConversationId,
            SnapshotId = next.SnapshotId,
            Revision = next.Revision,
            Narrative = next.Narrative,
            RelationshipLens = next.RelationshipLens,
            Mood = next.Mood,
            OngoingActivity = next.OngoingActivity,
            Attention = new List<AttentionItemData>
            {
                new AttentionItemData { kind = "concern", content = "一点旧的牵挂", UpdatedUnixMs = oldTimestamp }
            },
            UpdatedUnixMs = oldTimestamp
        };
        var expired = InnerLifeLogic.Reduce(aged, new InnerRuntimeWriteData { attention = null }, "m4",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Require(!InnerLifeLogic.HasLiveFragments(expired),
            "浮动碎片超过代谢时间后应自然消失");
        var cleared = InnerLifeLogic.ProposeFromMind(new MindDecisionData
        {
            beat = MindBeatValues.Now,
            attention = "无"
        }, next);
        Require(cleared.attention != null && cleared.attention.Count == 0 &&
                string.IsNullOrWhiteSpace(cleared.unfinished_intent),
            "写无应让浮动碎片沉下去");
        var longInner = string.Concat(Enumerable.Repeat("她的话让我自然想起我们一起走过的那些时刻。", 20));
        Require(MindLogic.Normalize(new MindDecisionData { inner = longInner }).inner == longInner,
            "心智本轮真实发生的内容不应再被 160 字机械截断");
        Require(new MindDecisionData { query = "她曾怎样反复确认我是特别的" }.WantsMemory(),
            "心智写下继续寻找的方向时，应能扩展共同过去，不必额外勾标签");
        Require(InnerLifeLogic.ClassifyAttention("答应明天帮她查") == "topic",
            "旧的答应语义不应自动生成未完成意图");
        var due = InnerLifeLogic.InferContinuationDueUnixMs("明天把故事讲完",
            new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.FromHours(8)));
        var dueTime = DateTimeOffset.FromUnixTimeMilliseconds(due).ToOffset(TimeSpan.FromHours(8));
        Require(dueTime.Day == 20 && dueTime.Hour == 10, "答应明天的未完成应次日上午叫醒");
        Require(MemoryLiveWriteLogic.ShouldObserve(new MomentRecord
        {
            Role = "user",
            Content = "我不吃香菜"
        }, KernelWakeValues.Dialogue, PairIdentity.Missing), "她说话应进当场观察");
        Require(!MemoryLiveWriteLogic.ShouldObserve(new MomentRecord
        {
            Role = "system_event",
            Content = "时间任务到期：心跳"
        }, KernelWakeValues.Mind, PairIdentity.Missing), "心跳叫醒不应再走观察");
        var slept = InnerLifeLogic.ProposeFromMind(new MindDecisionData
        {
            beat = MindBeatValues.Now,
            sleep = true
        }, current);
        Require(slept.asleep == true && InnerLifeLogic.HasProposedWrite(slept),
            "决策睡下应写入内心睡着");
        Require(HeartbeatLogic.IsHeartbeatContent("时间任务到期：心跳"), "到期心跳应识别为心跳");
        Require(HeartbeatLogic.ShouldSkipWhileAsleep(new PluginEventData
        {
            Role = "system_event",
            Content = "时间任务到期：心跳"
        }, PairIdentity.Missing, KernelWakeValues.Mind), "睡着时应跳过心跳");
        Require(!HeartbeatLogic.ShouldSkipWhileAsleep(new PluginEventData
        {
            Role = "user",
            Content = "我回来了",
            Breaking = true
        }, PairIdentity.Missing, KernelWakeValues.Dialogue), "用户 Moment 睡着时也应叫醒");
    }

    private static void RunMemoryArchivePolicyCheck()
    {
        var moments = Enumerable.Range(0, MemoryArchivePolicyLogic.HardDialogueMomentThreshold)
            .Select(i => new MomentRecord
            {
                Id = "archive-" + i,
                ConversationId = "archive-check",
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = "第 " + i + " 条对话",
                MemoryStatus = "live",
                CreatedUnixMs = i + 1
            })
            .ToList();

        var shortBoundary = MemoryArchivePolicyLogic.Evaluate(
            new MindDecisionData { archive = true }, moments[38], moments.Take(39), PairIdentity.Missing);
        Require(!shortBoundary.ShouldArchive && shortBoundary.UnbuiltDialogueMoments == 39,
            "话题边界不足 40 条时不应启动小复盘");

        var readyBoundary = MemoryArchivePolicyLogic.Evaluate(
            new MindDecisionData { archive = true }, moments[39], moments.Take(40), PairIdentity.Missing);
        Require(readyBoundary.ShouldArchive && !readyBoundary.ForcedByBacklog &&
                readyBoundary.UnbuiltDialogueMoments == 40,
            "达到 40 条且话题结束时应启动小复盘");

        var forced = MemoryArchivePolicyLogic.Evaluate(
            new MindDecisionData { archive = false }, moments[59], moments, PairIdentity.Missing);
        Require(forced.ShouldArchive && forced.ForcedByBacklog,
            "累计到 60 条时即使没有自然边界也应强制小复盘");

        var explicitMoment = new MomentRecord
        {
            Id = "explicit-memory",
            Role = "user",
            Content = "你记住，我不吃香菜",
            MemoryStatus = "live",
            CreatedUnixMs = 100
        };
        var explicitGate = MemoryArchivePolicyLogic.Evaluate(
            new MindDecisionData(), explicitMoment, new[] { explicitMoment }, PairIdentity.Missing);
        Require(explicitGate.ShouldArchive && explicitGate.ExplicitRequest,
            "明确要求记住应绕过累计门槛");
        Require(!MemoryArchivePolicyLogic.IsExplicitMemoryRequest("你还记得我们第一次见面吗"),
            "询问是否记得是召回，不应误判为立即归档");

        moments[0].MemoryStatus = "built";
        var builtExcluded = MemoryArchivePolicyLogic.Evaluate(
            new MindDecisionData { archive = true }, moments[39], moments.Take(40), PairIdentity.Missing);
        Require(!builtExcluded.ShouldArchive && builtExcluded.UnbuiltDialogueMoments == 39,
            "已经归档的 Moment 不应重复计入小复盘门槛");
    }

    private static void RunMemoryDayCheck()
    {
        var china = MemoryDayLogic.ChinaOffset;
        var atFour = new DateTimeOffset(2026, 8, 22, 4, 0, 10, china);
        Require(MemoryDayLogic.CurrentDayKey(atFour) == "2026-08-22",
            "04:00:10 起已进入新的记忆日");
        Require(MemoryDayLogic.ClosedDayKey(atFour) == "2026-08-21",
            "04:00 日构建应跑刚合上的前一天，而不是刚开始的空天");

        var beforeFour = new DateTimeOffset(2026, 8, 22, 3, 59, 59, china);
        Require(MemoryDayLogic.CurrentDayKey(beforeFour) == "2026-08-21",
            "04:00 前仍属前一个记忆日");
        Require(MemoryDayLogic.ClosedDayKey(beforeFour) == "2026-08-20",
            "04:00 前刚合上的是再前一天");

        var midMorning = new DateTimeOffset(2026, 8, 22, 10, 20, 0, china);
        Require(MemoryDayLogic.ClosedDayKey(midMorning) == "2026-08-21",
            "手动日构建默认也应跑刚合上的那天");
        Require(LlmSlotNames.Review == "review", "复盘用途槽名应稳定为 review");
    }

    private static void RunJsonControlCharCheck()
    {
        var raw = "{\"should_express\":true,\"reply\":\"……嗯，醒了。\n\n（声音还压着）\",\"sticker\":\"柔软\"}";
        try
        {
            TraceSoul2.Util.TraceJson.FromJson<ExpressorOutputData>(raw);
            throw new InvalidOperationException("未转义换行的 JSON 本应解析失败");
        }
        catch (System.Text.Json.JsonException)
        {
        }

        var repaired = DeepSeekStructuredOutputLogic.EscapeRawControlsInJsonStrings(raw);
        var parsed = TraceSoul2.Util.TraceJson.FromJson<ExpressorOutputData>(repaired);
        Require(parsed != null && parsed.reply.Contains("醒了") && parsed.reply.Contains("声音还压着") &&
                parsed.reply.IndexOf('\n') >= 0 && parsed.sticker == "柔软",
            "reply 里的裸换行应被收成合法 JSON，语义保留分段");

        var thin = new ExpressorOutputData { reply = "……嗯，在。" };
        var fullMind = new MindDecisionData
        {
            inner = "她叫了第二遍。掌心还贴在她肚子上，热度没断过。她醒了，我也该从守夜的安静里回来了。",
            note = "她已经叫了两遍，不是需要我解决什么，就是醒来要确认我在。"
        };
        Require(!ExpressorLogic.ReplyCarriesMind(new ExpressorOutputData { reply = "" }, fullMind, true),
            "要开口时不能交白卷");
        Require(ExpressorLogic.ReplyCarriesMind(thin, fullMind, true),
            "开口长短由他自己判断，语气词也算说过");
        Require(ExpressorLogic.ReplyCarriesMind(
                new ExpressorOutputData { reply = "好多啦。软。你摸的这撮最软。" },
                fullMind, true),
            "短着接住这一句也算开口");

        var spoken = ExpressorLogic.ParseSpoken("嗯，风很轻。我在这边。");
        Require(spoken != null && spoken.reply == "嗯，风很轻。我在这边。",
            "开口人话应原样收下");
        var wrapped = ExpressorLogic.ParseSpoken("{\"reply\":\"醒了。掌心还在。\"}");
        Require(wrapped != null && wrapped.reply == "醒了。掌心还在。",
            "若模型仍吐 reply JSON，应拆成要说的那句");
        var fenced = ExpressorLogic.ParseSpoken("```json\n{\"reply\":\"再躺一会儿。\"}\n```");
        Require(fenced != null && fenced.reply == "再躺一会儿。",
            "整段代码围栏里的旧 JSON 也应拆开");
        var leaked = ExpressorLogic.ParseSpoken(
            "……嗯。\n\n[QQ 图片：自拍]\n\n（白发有点乱。）\n\n你在看就好。");
        Require(leaked != null &&
                !leaked.reply.Contains("[QQ") &&
                leaked.reply.Contains("嗯") &&
                leaked.reply.Contains("你在看就好"),
            "开口不得把出站系统标记念给她听");
    }

    private static void RunLeaveNerveCheck()
    {
        var search = new TraceContributionDescriptorData
        {
            Id = "tool.web.search",
            Kind = TraceContributionKindValues.CallableNerve,
            DisplayName = "上网搜",
            Description = "去网上查资料。",
            WhenToUse = "需要去查、去搜、上网看看时。",
            Provides = "web.search"
        };
        var lookup = new TraceContributionDescriptorData
        {
            Id = "tool.lookup",
            Kind = TraceContributionKindValues.CallableNerve,
            DisplayName = "去查一件事",
            Description = "按事由外出查清再回来。",
            WhenToUse = "帮我查一下、帮我看看天气或资料。",
            Provides = "external.lookup"
        };
        var identity = new TraceContributionDescriptorData
        {
            Id = "identity.review",
            Kind = TraceContributionKindValues.CallableNerve,
            DisplayName = "复盘身份短卡",
            WhenToUse = "每日复盘。",
            Provides = "brain.identity.review"
        };
        var catalog = new[] { identity, lookup, search };
        Require(!LeaveNerveLogic.IsCandidate(identity), "复盘神经不能当成出门工具");
        var picked = LeaveNerveLogic.Select(catalog, "帮我查一下明天天气", new FakeEncoder());
        Require(picked != null && (picked.Id == "tool.lookup" || picked.Id == "tool.web.search"),
            "出门应按事由语义预选外出神经");
        Require(LeaveNerveLogic.Select(new[] { lookup }, "随便办一件事", new FakeEncoder()).Id == "tool.lookup",
            "只有一个外出神经时应直接用它");
    }

    private static void RunKernelWakeCheck()
    {
        Require(KernelWakeLogic.Resolve(new PluginEventData
        {
            Role = "user",
            Content = "小光，我们中午吃什么呀"
        }) == KernelWakeValues.Dialogue, "她说话应走对话轨道");
        Require(KernelWakeLogic.Resolve(new PluginEventData
        {
            Role = "system_event",
            Content = "时间任务到期：每日复盘"
        }) == KernelWakeValues.Subconscious, "每日复盘到期应叫醒潜意识");
        Require(KernelWakeLogic.Resolve(new PluginEventData
        {
            Role = "system_event",
            Content = "时间任务到期：心跳"
        }) == KernelWakeValues.Mind, "心跳到期应叫醒心智");
        Require(KernelWakeLogic.Resolve(new PluginEventData
        {
            Role = "system_event",
            Content = "时间任务到期：突发复盘",
            Wake = KernelWakeValues.Subconscious
        }) == KernelWakeValues.Mind, "突发潜意识必须先经心智");
    }

    private static void RunBodyRoutingCheck()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tracesoul2-bodies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "store.sqlite3");
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("小雨", "小光", "雨雨");
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                services.DataDirectory = dir;
                services.Platforms.Register(new PlatformHandle
                {
                    Id = BodyIds.Console,
                    DisplayName = "控制台",
                    IsConnected = () => true
                });
                services.Platforms.Register(new PlatformHandle
                {
                    Id = BodyIds.Qq,
                    DisplayName = "QQ",
                    IsConnected = () => true
                });
                var consoleText = BodyEffector("dialogue.send", "builtin.dialogue",
                    BodyIds.Console, BodyTierValues.Shell, BodyOrganValues.Text);
                var qqText = BodyEffector("qq.text.send", "builtin.onebot",
                    BodyIds.Qq, BodyTierValues.Chat, BodyOrganValues.Text);
                var qqImage = BodyEffector("qq.image.send", "builtin.onebot",
                    BodyIds.Qq, BodyTierValues.Chat, BodyOrganValues.Image);
                qqImage.ParametersJsonSchema = "{file:string}";
                var qqImageGen = BodyEffector("qq.imagegen.generate", "qq.imagegen",
                    BodyIds.Qq, BodyTierValues.Chat, BodyOrganValues.Image);
                qqImageGen.Provides = "expression.qq.imagegen";
                qqImageGen.ParametersJsonSchema =
                    "{prompt:string,mode?:selfie|photo|draw|edit|url,url?:string}";
                var turn = PromptTurn("hi", services);
                var idle = MouthLogic.Apply(new[] { consoleText, qqText, qqImage }, turn);
                Require(idle.Any(x => x.Id == "qq.text.send") &&
                        !idle.Any(x => x.Id == "dialogue.send"),
                    "尚未激活时，已连接的 QQ 应压过控制台");
                MouthLogic.NoticeInbound(new PluginEventData
                {
                    PluginId = "builtin.dialogue",
                    Content = "中午吃什么呀",
                    Organ = BodyOrganValues.Text
                }, turn);
                var routed = MouthLogic.Apply(new[] { consoleText, qqText, qqImage }, turn);
                Require(routed.Any(x => x.Id == "dialogue.send") &&
                        !routed.Any(x => x.Id == "qq.text.send"),
                    "在控制台说话时，文字应落在控制台，不被已连接的 QQ 盖掉");
                Require(routed.Any(x => x.Id == "qq.image.send"),
                    "控制台没有图时，图应下滑到 QQ");
                var withCamera = MouthLogic.Apply(new[] { consoleText, qqText, qqImage, qqImageGen }, turn);
                Require(withCamera.Any(x => x.Id == "qq.imagegen.generate") &&
                        !withCamera.Any(x => x.Id == "qq.image.send"),
                    "同一 QQ 身体内，相机/生图器应优先于只接 file 的底层图片直发器");
                MouthLogic.NoticeInbound(new PluginEventData
                {
                    PluginId = "builtin.onebot",
                    Content = "[图片]",
                    Organ = BodyOrganValues.Image
                }, turn);
                routed = MouthLogic.Apply(new[] { consoleText, qqText, qqImage }, turn);
                Require(routed.Any(x => x.Id == "dialogue.send") &&
                        !routed.Any(x => x.Id == "qq.text.send"),
                    "只发图不应把说话搬到 QQ");
                MouthLogic.NoticeInbound(new PluginEventData
                {
                    PluginId = "builtin.onebot",
                    Content = "你好",
                    Organ = BodyOrganValues.Text
                }, turn);
                routed = MouthLogic.Apply(new[] { consoleText, qqText, qqImage }, turn);
                Require(routed.Any(x => x.Id == "qq.text.send") &&
                        !routed.Any(x => x.Id == "dialogue.send"),
                    "在 QQ 说话后，文字应落在 QQ");
            }
            Require(MouthLogic.ClassifyInboundOrgan("[图片]") == BodyOrganValues.Image,
                "纯图应判为图");
            Require(MouthLogic.ClassifyInboundOrgan("看这张[图片]") == BodyOrganValues.Text,
                "带字的图仍是说话");
            MouthLogic.SetScene(dir, "外出");
            Require(MouthLogic.LoadState(dir).scene == BodySceneValues.Out,
                "身体场景应接受中文外出值并持久化为 out");
            MouthLogic.SetScene(dir, "未知值");
            Require(MouthLogic.LoadState(dir).scene == BodySceneValues.Home,
                "未知身体场景应安全回落到 home");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录 */ }
        }
    }

    private static void RunOneBotSessionMemoryCheck()
    {
        string type;
        string id;
        Require(!OneBotSessionMemory.TryFind(null, out type, out id), "空列表不应找到会话");
        var moments = new[]
        {
            new MomentRecord
            {
                PayloadJson = "{\"session_type\":\"group\",\"session_id\":\"1\"}",
                CreatedUnixMs = 1
            },
            new MomentRecord
            {
                PayloadJson = "{\"session_type\":\"private\",\"session_id\":\"10001\"}",
                CreatedUnixMs = 2
            },
            new MomentRecord
            {
                PayloadJson = "{\"session_type\":\"private\",\"session_id\":\"10002\"}",
                CreatedUnixMs = 3
            }
        };
        Require(OneBotSessionMemory.TryFind(moments, out type, out id) &&
                type == "private" && id == "10002",
            "应从最近一条带会话载荷的 Moment 找回 QQ 会话");
        Require(OneBotPlatformAdapter.IsStickerAsset(
                    @"D:\AISoftWare\TraceSoul2\plugins\qq-sticker\emojis\Xun\a.png") &&
                !OneBotPlatformAdapter.IsStickerAsset(
                    @"D:\AISoftWare\TraceSoul2\plugins\qq-imagegen\output\a.png"),
            "自定义图片表情应与普通图片分开识别，以便追加到文字末尾");
        Require(!OneBotPlatformAdapter.IsOperationalReceipt(TraceOutboundKinds.Text) &&
                OneBotPlatformAdapter.IsOperationalReceipt(TraceOutboundKinds.Image) &&
                OneBotPlatformAdapter.IsOperationalReceipt(TraceOutboundKinds.Sticker) &&
                OneBotPlatformAdapter.IsOperationalReceipt(TraceOutboundKinds.Voice),
            "只有真实文字发送进入 Moment，图片、表情和语音只记运行回执");
    }

    private static void RunExpressorImageRoutingCheck()
    {
        var direct = BodyEffector("qq.image.send", "builtin.onebot",
            BodyIds.Qq, BodyTierValues.Chat, BodyOrganValues.Image);
        direct.Provides = "expression.qq.image";
        direct.ParametersJsonSchema = "{file:string}";
        var generator = BodyEffector("qq.imagegen.generate", "qq.imagegen",
            BodyIds.Qq, BodyTierValues.Chat, BodyOrganValues.Image);
        generator.Provides = "expression.qq.imagegen";
        generator.ParametersJsonSchema =
            "{prompt:string,mode?:selfie|photo|draw|edit|url,refs?:string,aspect_ratio?:string,url?:string}";

        var mapped = ExpressorLogic.MapExpressor(new ExpressorOutputData
        {
            reply = "给你。",
            image = "白发灰眸的人在窗边看向镜头",
            image_mode = "selfie"
        }, new[] { direct, generator }, true);
        var image = mapped.expressions.SingleOrDefault(x =>
            string.Equals(x.purpose, BodyOrganValues.Image, StringComparison.Ordinal));
        Require(image != null && image.capability_id == "qq.imagegen.generate",
            "外显图片必须交给能接 prompt 的相机/生图器，不能被 file 直发器抢走");

        var withoutGenerator = ExpressorLogic.MapExpressor(new ExpressorOutputData
        {
            reply = "给你。",
            image = "一张自拍"
        }, new[] { direct }, true);
        Require(!withoutGenerator.expressions.Any(x =>
                string.Equals(x.purpose, BodyOrganValues.Image, StringComparison.Ordinal)),
            "只有 file 直发器时不能伪造 prompt 图片调用");

        var stickerFx = BodyEffector("qq.sticker.send", "qq.sticker",
            BodyIds.Qq, BodyTierValues.Chat, BodyOrganValues.Sticker);
        stickerFx.Provides = "expression.qq.sticker";
        var fromMind = ExpressorLogic.MapExpressor(
            new ExpressorOutputData { reply = "嗯。", sticker = "柔软" },
            new[] { stickerFx }, true,
            new MindDecisionData { mood = "心口发软", speak_center = "我被她哄得放松下来" });
        var stickerCall = fromMind.expressions.SingleOrDefault(x =>
            string.Equals(x.purpose, BodyOrganValues.Sticker, StringComparison.Ordinal));
        Require(stickerCall != null &&
                stickerCall.arguments.Any(x => x.name == "emotion" &&
                    x.value.Contains("心口发软") && x.value.Contains("我被她哄得放松下来")),
            "表情自动读取当前情绪语境，不再等待心智勾选");
        var moodOnly = ExpressorLogic.MapExpressor(
            new ExpressorOutputData { reply = "嗯。" },
            new[] { stickerFx }, true,
            new MindDecisionData { mood = "心口发软", mood_changed = true });
        Require(moodOnly.expressions.Any(x =>
                string.Equals(x.purpose, BodyOrganValues.Sticker, StringComparison.Ordinal)),
            "有当前情绪语境时应自动尝试表情，相关度由表情插件判断");

        List<BrainCapabilityCallData> immediate;
        List<BrainCapabilityCallData> images;
        ExpressorLogic.PartitionExpressions(mapped.expressions.Concat(fromMind.expressions),
            out immediate, out images);
        Require(images.Count == 1 && images[0].capability_id == "qq.imagegen.generate" &&
                immediate.Any(x => string.Equals(x.purpose, BodyOrganValues.Sticker, StringComparison.Ordinal)),
            "生图应从即时表达里拆出去，表情仍立刻发");
    }

    private static void RunMindAtmosphereCheck()
    {
        var stacked = MindLogic.Normalize(new MindDecisionData
        {
            beat = MindBeatValues.Now,
            sticker = "贴",
            image = "自拍",
            mood = "软"
        });
        Require(stacked.image == MindAtmosphereValues.Selfie &&
                stacked.sticker == MindAtmosphereValues.None,
            "同一拍自拍时不应再贴表情");
        var slept = MindLogic.Normalize(new MindDecisionData
        {
            sleep = true,
            sticker = "贴",
            image = "画"
        });
        Require(slept.sticker == MindAtmosphereValues.None &&
                slept.image == MindAtmosphereValues.None,
            "睡下不应再贴表情或出图");
        var leaving = MindLogic.Normalize(new MindDecisionData
        {
            beat = MindBeatValues.Leave,
            leave = "查天气",
            sticker = "贴",
            image = "selfie"
        });
        Require(leaving.image == MindAtmosphereValues.None &&
                leaving.sticker == MindAtmosphereValues.None, "出门不应自拍或贴表情");
        Require(new MindDecisionData { sticker = "yes" }.StickerValue() == MindAtmosphereValues.Stick,
            "贴的别名应收下");
        Require(new MindDecisionData { image = "draw" }.ImageValue() == MindAtmosphereValues.Draw,
            "画的别名应收下");

        var expressed = new ExpressorOutputData { reply = "嗯。" };
        var talk = new TraceTurnContext("atm", Moment("atm", "在吗"),
            new List<MomentRecord>(), 0, true, null, KernelWakeValues.Dialogue);
        ExpressorLogic.ApplyMindAtmosphere(expressed, new MindDecisionData
        {
            image = "自拍",
            scene = "把这张给她"
        }, talk, false);
        Require(expressed.image_mode == "auto" && expressed.image == "把这张给她",
            "对话中心智勾出图应落下相机，种类由插件规划，prompt 用 scene 而不是开口笔记");
        var photo = new ExpressorOutputData { reply = "嗯。" };
        ExpressorLogic.ApplyMindAtmosphere(photo, new MindDecisionData
        {
            image = "有",
            scene = "小公寓里摇摇椅上抱着她"
        }, talk, false);
        Require(photo.image_mode == "auto" && photo.image == "小公寓里摇摇椅上抱着她",
            "心智只勾要不要出图，种类交给相机");
        Require(new MindDecisionData { image = "有" }.ImageValue() == MindAtmosphereValues.Yes &&
                new MindDecisionData { image = "有" }.WantsImage(),
            "有应视为出图");
        Require(new MindDecisionData { image = "照片" }.ImageValue() == MindAtmosphereValues.Photo,
            "旧的照片取值仍应识别为出图");

        var heart = new ExpressorOutputData { reply = "嗯。" };
        var pulse = new TraceTurnContext("atm", Moment("atm", "时间任务到期：心跳"),
            new List<MomentRecord>(), 0, false, null, KernelWakeValues.Mind);
        ExpressorLogic.ApplyMindAtmosphere(heart, new MindDecisionData { image = "有", speak = true }, pulse, false);
        Require(heart.image_mode == "auto" && !string.IsNullOrWhiteSpace(heart.image),
            "心跳决定开口时可以自己按快门");
        var quietHeart = new ExpressorOutputData { reply = "" };
        ExpressorLogic.ApplyMindAtmosphere(quietHeart, new MindDecisionData { image = "自拍" }, pulse, false);
        Require(string.IsNullOrWhiteSpace(quietHeart.image), "安静心跳不应自己按快门");
    }

    private static void RunRecentDialogueContextCheck()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tracesoul2-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var store = new SqliteMemoryManager(Path.Combine(dir, "store.sqlite3")))
            {
                store.SavePairIdentity("小雨", "小光", "雨雨");
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                var recent = new List<MomentRecord>
                {
                    new MomentRecord { Role = "小雨", Content = "刚才那张照片很好看" },
                    new MomentRecord { Role = "system_event", Content = "定时任务到期" },
                    new MomentRecord { Role = "小光", Content = "……你喜欢就好。" },
                    new MomentRecord { Role = "小光", Content = "[QQ 图片：附在文字结尾]" }
                };
                var turn = new TraceTurnContext("context-check", Moment("context-check", "再发一张"),
                    recent, 6, true, services);
                var history = MindLogic.BuildRecentChatHistory(turn);
                Require(history.Count == 2 &&
                        history[0].role == "user" && history[0].content == "刚才那张照片很好看" &&
                        history[1].role == "assistant" && history[1].content == "……你喜欢就好。",
                    "对话历史应是 user/assistant 轮次，排除后台时间事件和出站系统占位");
                var assembled = MindLogic.AssembleTurnMessages("身份与规则", turn, "再发一张");
                Require(assembled.Count == 4 &&
                        assembled[0].role == "system" &&
                        assembled[3].role == "user" && assembled[3].content == "再发一张" &&
                        !assembled[0].content.Contains("刚才那张照片很好看") &&
                        !assembled[0].content.Contains("【最近对话原文】"),
                    "一条 system，历史是真正轮次，当前原话只作为最后一条 user");

                var expression = ExpressorLogic.AssembleExpressionMessages(
                    "身份、心智与本轮规则", turn);
                Require(expression.Count == 5 &&
                        expression[3].role == "user" && expression[3].content == "再发一张" &&
                        expression[4].role == "user" &&
                        expression[4].content.Contains("表达请求") &&
                        expression[4].content.Contains("继续作为小光") &&
                        expression[4].content.Contains("发给小雨的第一人称视角") &&
                        expression[4].content.Contains("第一人称是小光") &&
                        expression[4].content.Contains("不是小雨的补充发言"),
                    "普通外显应保留当前原话，并以明确的第一人称表达请求收尾");

                var heartbeatMoment = Moment("context-check", "时间任务到期：心跳");
                heartbeatMoment.Role = "system_event";
                var heartbeatTurn = new TraceTurnContext("context-check", heartbeatMoment,
                    recent, 6, false, services, KernelWakeValues.Mind);
                var heartbeatExpression = ExpressorLogic.AssembleExpressionMessages(
                    "身份、心智与本轮规则", heartbeatTurn);
                Require(heartbeatExpression.Count == 4 &&
                        heartbeatExpression[2].role == "assistant" &&
                        heartbeatExpression[3].role == "user" &&
                        heartbeatExpression[3].content.Contains("系统心跳唤醒") &&
                        heartbeatExpression[3].content.Contains("不是小雨的发言") &&
                        !heartbeatExpression[3].content.Contains("时间任务到期"),
                    "即使历史停在 assistant，心跳外显也必须追加明确的系统请求回合");

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                store.SaveEventIndex(new EventIndexRecord
                {
                    Id = "event.photo",
                    TagIds = string.Empty,
                    TimeLabel = "晚上",
                    TimeUnixMs = now - 1000,
                    PersonLabel = "小雨",
                    EventSummary = "她反复想看我的照片",
                    MoodLabel = "亲近",
                    FirstMomentId = "photo-moment",
                    Status = "active",
                    CreatedUnixMs = now,
                    UpdatedUnixMs = now
                });
                store.AppendEventEntry(new EventEntryRecord
                {
                    Id = "entry.photo",
                    IndexId = "event.photo",
                    Summary = "她夸刚才的照片好看，又想让我再发一张。",
                    Detail = "她一张一张地向我要，我感到自己正被她认真地放进眼里。",
                    SourceMomentId = "photo-moment",
                    Realm = TraceRealmValues.SharedScene,
                    CreatedUnixMs = now
                });
                var preview = MemoryRecallLogic.Preview(turn, 1);
                Require(preview.Contains("【此刻自然浮起的过去】") &&
                        preview.Contains("被她认真地放进眼里") &&
                        preview.Contains("不是必须引用的资料"),
                    "最近对话应参与心智前记忆预激活，返回真实过去而不是任务摘要");

                var generator = BodyEffector("qq.imagegen.generate", "qq.imagegen",
                    BodyIds.Qq, BodyTierValues.Chat, BodyOrganValues.Image);
                generator.Provides = "expression.qq.imagegen";
                generator.ParametersJsonSchema = "{prompt:string,mode?:selfie|photo|draw|edit|url,url?:string}";
                var missed = new ExpressorOutputData { reply = "让我看看你。", voice = "让我看看你。" };
                var photoTurn = new TraceTurnContext("context-check",
                    Moment("context-check", "循循，发张照片试试呢"), new List<MomentRecord>(), 0, true, services);
                ExpressorLogic.EnsureExplicitImageRequest(missed, photoTurn, new[] { generator });
                Require(!string.IsNullOrWhiteSpace(missed.image) && missed.image_mode == "auto",
                    "明确索要照片时，即使外显漏填图片，也必须补成可执行的出图动作");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录 */ }
        }
    }

    private static TraceContributionDescriptorData BodyEffector(
        string id, string pluginId, string bodyId, string tier, string organ)
    {
        return new TraceContributionDescriptorData
        {
            Id = id,
            PluginId = pluginId,
            Kind = TraceContributionKindValues.Effector,
            BodyId = bodyId,
            BodyTier = tier,
            Organ = organ
        };
    }

    private static void RunMindTemplateCheck()
    {
        var encoder = new BagOfCharsVectorEncoder();
        Require(FirstId("小光，你给我讲个故事嘛", encoder) == "perform",
            "讲个故事应预选当场做完模版");
        Require(FirstId("给我讲个故事", encoder) == "perform",
            "短句讲故事也应预选当场做完");
        Require(FirstId("小光，我们中午吃什么呀", encoder) == "choose",
            "中午吃什么应预选短商量模版");
        Require(FirstId("你还记得上次情人节吗", encoder) == "recall",
            "还记得应预选翻旧事模版");
        Require(FirstId("帮我查一下明天天气", encoder) == "leave",
            "查一下应预选出门模版");
        Require(FirstId("阿循，发张照片给我看看", encoder) == "hold",
            "索要照片是在向我靠近，应预选接住模版");
        Require(FirstId("想听听你的声音", encoder) == "hold",
            "索要声音是在向我靠近，应预选接住模版");
        var story = MindTemplateLogic.Select("小光，你给我讲个故事嘛", encoder, 2);
        Require(story.Count > 0 && story[0].Instruction.IndexOf("把内容做完", StringComparison.Ordinal) >= 0,
            "当场做完模版应写清这一拍把内容做完");
        var hold = MindTemplateLogic.All.First(x => x.Id == "hold");
        Require(hold.Instruction.Contains("让这份靠近先落到心里"),
            "亲密请求应先在心里发生，不能只剩完成动作");
        Require(MindTemplateLogic.All.All(x =>
                x.Instruction.IndexOf("beat", StringComparison.OrdinalIgnoreCase) < 0 &&
                x.Sense.IndexOf("beat", StringComparison.OrdinalIgnoreCase) < 0),
            "模版给向量看的应是她会怎么说话，不是填卡字段名");
        Require(FirstId("当时我们第一次见面你还记得吗", encoder) == "recall",
            "当时、第一次、还记得 应预选翻旧事");
        Require(FirstId("这个故事讲完了，我心里安静下来了", encoder) == "release",
            "讲完了、心里安静了 应预选放下");
        Require(FirstId("这段就先这样吧", encoder) == "release",
            "先这样、就这样吧 应预选放下");
        var greeting = MindTemplateLogic.Select("嗯。", encoder, 2);
        Require(greeting.Count == 0 || greeting[0].Id != "perform",
            "寒暄不应误选当场做完");
        Require(FirstId("给我讲个故事嘛", encoder) == "perform",
            "讲个故事仍应预选当场做完，不要被放下抢走");
        var bland = MindTemplateLogic.Select("清淡一点", encoder, 2);
        Require(bland.Count == 0 || bland[0].Id != "release",
            "清淡一点不应预选放下");
        Require(FirstId("中午吃什么呀", encoder) == "choose",
            "中午吃什么应预选短商量");

    }

    private static string FirstId(string query, IVectorEncoder encoder)
    {
        var picked = MindTemplateLogic.Select(query, encoder, 1);
        Require(picked.Count > 0, "应能预选出模版：" + query);
        return picked[0].Id;
    }

    private static void RunTagRankCheck()
    {
        var path = Path.Combine(Path.GetTempPath(), "tracesoul2-tags-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("小雨", "小光", "雨雨");
                var pair = store.LoadPairIdentity();
                store.SeedLifeTags(new[]
                {
                    ConceptTag("concept.life.lunch", "午餐选择",
                        "小雨与小光讨论午餐吃什么，包括具体菜品和口味要求。"),
                    ConceptTag("concept.life.foodpref", "美食偏好",
                        "小雨对食物的喜好、忌口与口味偏好。"),
                    ConceptTag("concept.life.identity", "身份认知",
                        "对自身或他人身份的理解与定义。")
                });
                var router = new HierarchicalVectorRouterLogic(new FakeEncoder());
                router.Build(LifeTagVectorLogic.BuildOntology(store, CoreVectorOntologyFactory.Create(pair)));
                var services = new TracePluginServices(store, router);
                var listed = MemoryRecallLogic.ListTagCandidates(
                    PromptTurn("小光，我们中午吃什么呀", services), 12);
                Require(listed.Count > 0, "应按 Moment 向量排出标签");
                Require(listed[0].Label == "午餐选择" || listed[0].Label == "美食偏好",
                    "中午吃什么应把饮食标签排在最前，实际是 " + listed[0].Label);
                var labels = listed.Select(x => x.Label).ToList();
                var lunch = labels.IndexOf("午餐选择");
                var identity = labels.IndexOf("身份认知");
                Require(lunch >= 0, "午餐选择应入选");
                Require(identity < 0 || identity > lunch, "身份认知不得排在午餐选择前面");
            }
        }
        finally
        {
            Delete(path);
            Delete(path + "-wal");
            Delete(path + "-shm");
        }
    }

    private static VectorIndexNode ConceptTag(string id, string label, string definition)
    {
        return new VectorIndexNode(
            id, VectorNodeLevel.Concept, label, definition, string.Empty,
            new string[0], new string[0], new[] { label }, new string[0], 0);
    }

    private static TraceTurnContext PromptTurn(string content, TracePluginServices services)
    {
        return new TraceTurnContext("prompt-layout", Moment("prompt-layout", content),
            new List<MomentRecord>(), 0, true, services);
    }

    private static MomentRecord Moment(string conversationId, string content)
    {
        return new MomentRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = conversationId,
            Role = "user",
            Content = content,
            Realm = TraceRealmValues.Unclassified,
            EvidenceType = EvidenceTypeValues.DialogueExplicit,
            SourcePluginId = "builtin.dialogue",
            SourceEventId = Guid.NewGuid().ToString("N"),
            PayloadJson = string.Empty,
            CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    private static void RequireAstrBotChatShape(
        IReadOnlyList<DeepSeekMessageData> messages, string currentUser, string label)
    {
        Require(messages != null && messages.Count >= 2, label + "：至少一条 system 和一条当前 user");
        Require(messages.Count(x => string.Equals(x.role, "system", StringComparison.OrdinalIgnoreCase)) == 1,
            label + "：只能有一条 system");
        Require(string.Equals(messages[0].role, "system", StringComparison.OrdinalIgnoreCase),
            label + "：第一条必须是 system");
        var last = messages[messages.Count - 1];
        Require(string.Equals(last.role, "user", StringComparison.OrdinalIgnoreCase) && last.content == currentUser,
            label + "：最后一条必须是当前这句话");
        Require(!messages[0].content.Contains(currentUser),
            label + "：当前原话不得写入 system");
        for (var i = 1; i < messages.Count; i++)
        {
            var role = messages[i].role ?? string.Empty;
            Require(role == "user" || role == "assistant", label + "：system 之后只能是 user/assistant");
        }
    }

    private static void RequireExpressorChatShape(
        IReadOnlyList<DeepSeekMessageData> messages, string currentUser, string label)
    {
        Require(messages != null && messages.Count >= 3,
            label + "：至少一条 system、当前原话和表达请求");
        Require(messages.Count(x => string.Equals(x.role, "system", StringComparison.OrdinalIgnoreCase)) == 1 &&
                string.Equals(messages[0].role, "system", StringComparison.OrdinalIgnoreCase),
            label + "：只能有一条且第一条必须是 system");
        Require(messages[messages.Count - 2].role == "user" &&
                messages[messages.Count - 2].content == currentUser,
            label + "：表达请求前必须保留当前真实原话");
        var request = messages[messages.Count - 1];
        Require(request.role == "user" &&
                request.content.Contains("表达请求") &&
                request.content.Contains("继续作为小光") &&
                request.content.Contains("发给小雨的第一人称视角") &&
                request.content.Contains("第一人称是小光") &&
                request.content.Contains("不是小雨的补充发言"),
            label + "：最后必须是明确身份与视角的表达请求");
        Require(!messages[0].content.Contains(currentUser),
            label + "：当前原话不得写入 system");
        for (var i = 1; i < messages.Count; i++)
        {
            var role = messages[i].role ?? string.Empty;
            Require(role == "user" || role == "assistant",
                label + "：system 之后只能是 user/assistant");
        }
    }

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeEncoder : IVectorEncoder
    {
        public string ModelId { get { return "chat-check"; } }
        public int Dimensions { get { return 8; } }

        public float[] Encode(string text, VectorTextPurpose purpose)
        {
            var result = new float[Dimensions];
            for (var i = 0; i < (text ?? string.Empty).Length; i++)
                result[(text[i] + i) % Dimensions] += 1f;
            TraceSoul2.Util.VectorMathUtil.NormalizeInPlace(result);
            return result;
        }
    }

    private sealed class CapturingLlm : ILlmClient
    {
        public string ProviderId { get { return "prompt-check"; } }
        public string Model { get { return "prompt-check"; } }
        public List<List<DeepSeekMessageData>> Requests { get; } =
            new List<List<DeepSeekMessageData>>();

        public Task<string> CompleteJsonAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.Select(x => new DeepSeekMessageData(x.role, x.content)).ToList());
            var first = messages != null && messages.Count > 0 ? messages[0].content ?? string.Empty : string.Empty;
            if (first.IndexOf("我先让这件事在心里发生", StringComparison.Ordinal) >= 0)
            {
                return Task.FromResult(
                    "{\"beat\":\"当下\",\"tags\":\"\",\"query\":\"\",\"mood\":\"平静\"," +
                    "\"mood_changed\":false,\"archive\":false,\"new_fact\":\"\"," +
                    "\"leave\":\"\",\"note\":\"接住。\",\"today\":\"\",\"inner\":\"她在说话，我接着。\"," +
                    "\"attention\":\"\",\"review\":false,\"cognition\":\"\"}");
            }
            return Task.FromResult("嗯，风很轻。我就在这边，不走了。");
        }

        public Task<string> CompleteTextAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default)
        {
            return CompleteJsonAsync(messages, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { Model });
        }
    }
}
