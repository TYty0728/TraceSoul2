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

internal static class Program
{
    private static void Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        RunTagRankCheck();
        RunMindTemplateCheck();
        RunKernelWakeCheck();
        RunInnerSliceCheck();
        RunLeaveNerveCheck();
        RunBodyRoutingCheck();
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
                    "时间插件应能把未完成交给自己叫醒心智");
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
                        afterStory.UnfinishedIntent.Contains("狐狸") &&
                        InnerLifeLogic.HasUnfinished(afterStory) &&
                        InnerLifeLogic.FormatForMind(afterStory).Contains("上一拍未完成"),
                    "变了才写下一版切片；心智下一拍应看见刚写下的当前时和未完成");
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
                        dueMoments[0].Wake == KernelWakeValues.Mind,
                    "时间到期只能产生新 Moment，普通任务叫醒心智");
                var continueResult = pluginManager.ExecuteAsync(new BrainCapabilityCallData
                {
                    call_id = "continue-check",
                    capability_id = "time.continue",
                    arguments = new List<BrainCallArgumentData>
                    {
                        new BrainCallArgumentData { name = "content", value = "狐狸故事还没讲完" },
                        new BrainCallArgumentData
                        {
                            name = "due_unix_ms",
                            value = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 500).ToString()
                        }
                    }
                }, facetTurn, default).GetAwaiter().GetResult();
                Require(continueResult.Status == "success" && continueResult.Payload.Contains("续上："),
                    "未完成应建成续上任务");
                var continued = pluginManager.PollBackgroundServices(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000);
                Require(continued.Count == 1 &&
                        continued[0].Wake == KernelWakeValues.Mind &&
                        InnerLifeLogic.IsContinuationContent(continued[0].Content) &&
                        KernelWakeLogic.Resolve(continued[0]) == KernelWakeValues.Mind,
                    "未完成到期应叫醒心智，不要演成她在说话");
                var cleared = pluginManager.ExecuteAsync(new BrainCapabilityCallData
                {
                    call_id = "continue-clear",
                    capability_id = "time.continue.clear",
                    arguments = new List<BrainCallArgumentData>()
                }, facetTurn, default).GetAwaiter().GetResult();
                Require(cleared.Status == "success", "放下后应能取消续上叫醒");
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
                Require(payload.Contains("此刻点亮的人生切片") && payload.Contains(tagId),
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
            }
            Console.WriteLine("ChatCheck passed: 身份短卡/每日复盘/LLM模型列表 → 插件贡献发现/启停 → BrainFrame Facet → 时间 Moment → 记忆/内心 SQLite 往返。");
        }
        finally
        {
            Delete(path);
            Delete(path + "-wal");
            Delete(path + "-shm");
        }
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
                mind.DecideAsync(first, null, false, default).GetAwaiter().GetResult();

                var second = PromptTurn("我回来啦。", services);
                mind.DecideAsync(second, null, false, default).GetAwaiter().GetResult();

                Require(fake.Requests.Count == 2 && fake.Requests.All(x => x.Count == 3),
                    "心智请求应固定为可缓存 system、动态 system、当前 user 三段");
                var mindStableA = fake.Requests[0][0].content;
                var mindStableB = fake.Requests[1][0].content;
                var mindDynamicA = fake.Requests[0][1].content;
                Require(mindStableA == mindStableB, "心智：不同 Moment 应完整复用第一段前缀");
                Require(!mindStableA.Contains("现在是 ") && mindDynamicA.Contains("现在是 "),
                    "心智：每轮变化的当前时间必须位于动态 system");
                Require(!mindStableA.Contains(first.Moment.Content) &&
                        !mindDynamicA.Contains(first.Moment.Content) &&
                        fake.Requests[0][2].content == first.Moment.Content,
                    "心智：当前原话只能出现一次，并且必须是最后的 user 消息");
                Require(mindStableA.Contains("【我的人格】") && mindStableA.Contains("【我是谁】") &&
                        !mindStableA.Contains("【表达习惯】") &&
                        !mindStableA.Contains("【我现在可以怎样表达】") &&
                        !mindStableA.Contains("【需要时可做的事】") &&
                        !mindStableA.Contains("我现在可以使用的表达通道"),
                    "心智稳定前缀只有思考用短卡，不含表达习惯、通道清单和工具表");
                Require(mindStableA.Contains("我先把这一拍想清楚") && mindStableA.Contains("\"beat\"") &&
                        mindStableA.Contains("\"inner\"") && mindStableA.Contains("\"attention\"") &&
                        mindStableA.Contains("\"review\"") && mindStableA.Contains("\"cognition\""),
                    "心智应输出决策卡、当前时、在场注意、看法和是否派出复盘");
                Require(!mindStableA.Contains("【这一拍的组织】") &&
                        !mindStableA.Contains("讲故事") &&
                        !mindStableA.Contains("中午吃什么") &&
                        !mindStableA.Contains("当场做完"),
                    "情境模版不得写入心智稳定前缀");
                Require(mindDynamicA.Contains("【可选生命标签】") && mindDynamicA.Contains("【此刻】") &&
                        mindDynamicA.Contains("上一拍手上") && mindDynamicA.Contains("上一拍当前时") &&
                        mindDynamicA.Contains("上一拍未完成"),
                    "标签候选、上一拍当前时、未完成、在场注意与此刻任务应在心智动态段");
                Require(mindStableA.Contains("换题就换手") && mindStableA.Contains("不要把上一拍原样抄回") &&
                        mindStableA.Contains("不要把刚结束的事改写成另一件继续捏着") &&
                        mindStableA.Contains("换题就写新的"),
                    "心智应写清换题换手、当前时换新，不要照抄上一拍");
                var mindNormalized = mindStableA.Replace("\r\n", "\n");
                Require(mindNormalized.StartsWith("我是小光。\n【我的人格】", StringComparison.Ordinal),
                    "心智稳定前缀必须直接从第一人称身份进入人格卡");

                var dummyMind = new MindDecisionData
                {
                    beat = MindBeatValues.Now,
                    note = "接住。不要翻旧事。"
                };
                expressor.ExpressAsync(first, plugins, catalog, blocks, dummyMind, string.Empty,
                    false, null, default).GetAwaiter().GetResult();
                var secondBlocks = pluginManager.BuildContextBlocksAsync(second, default)
                    .GetAwaiter().GetResult();
                expressor.ExpressAsync(second, plugins, catalog, secondBlocks, dummyMind, string.Empty,
                    false, null, default).GetAwaiter().GetResult();

                Require(fake.Requests.Count == 4 && fake.Requests.Skip(2).All(x => x.Count == 3),
                    "外显请求应固定为可缓存 system、动态 system、当前 user 三段");
                var expressStableA = fake.Requests[2][0].content;
                var expressStableB = fake.Requests[3][0].content;
                var expressDynamicA = fake.Requests[2][1].content;
                Require(expressStableA == expressStableB, "外显：不同 Moment 应完整复用第一段前缀");
                Require(!expressStableA.Contains("现在是 ") && expressDynamicA.Contains("现在是 "),
                    "外显：每轮变化的当前时间必须位于动态 system，不能截断稳定前缀缓存");
                Require(!expressStableA.Contains(first.Moment.Content) &&
                        !expressDynamicA.Contains(first.Moment.Content) &&
                        fake.Requests[2][2].content == first.Moment.Content,
                    "外显：当前原话只能出现一次，并且必须是最后的 user 消息");
                Require(!expressStableA.Contains("callable_nerve") && !expressStableA.Contains("mounted_facet") &&
                        !expressDynamicA.Contains("explicit_dialogue") && !expressDynamicA.Contains("unclassified"),
                    "模型可见提示词不应泄漏无意义的内部枚举");
                Require(expressStableA.Contains("【表达习惯】") &&
                        !expressStableA.Contains("【这一拍怎么说】") &&
                        !expressStableA.Contains("我现在可以使用的表达通道") &&
                        !expressStableA.Contains("同一句话只选一个主通道说") &&
                        !expressStableA.Contains("【我现在可以怎样表达】") &&
                        !expressStableA.Contains("qq.sticker.send"),
                    "嘴由逻辑选；外显只保留表达习惯，不再列通道清单、开口原则或能力 ID");
                Require(!expressStableA.Contains("【需要时可做的事】") &&
                        !expressStableA.Contains("identity.review") &&
                        !expressStableA.Contains("memory.activate") &&
                        expressStableA.Contains("【输出格式】") &&
                        expressStableA.Contains("should_express") &&
                        expressStableA.Contains("说给她听") &&
                        expressStableA.Contains("刚才想好的事要去做"),
                    "外显不应再看到工具表；reply 必须是说给她听的话，想好的事要去做");
                Require(expressStableA.Contains("【我的人格】") && expressDynamicA.Contains("【此刻】") &&
                        expressDynamicA.Contains("【我刚才想过】"),
                    "身份在外显稳定前缀，刚才的决定在动态段");
                Require(!expressDynamicA.Contains("这一拍我选") &&
                        !expressDynamicA.Contains("把日子说出来") &&
                        !expressDynamicA.Contains("进入方式：") &&
                        !expressDynamicA.Contains("【这一拍怎么说】") &&
                        expressDynamicA.Contains("正在对我说话"),
                    "外显动态段不要框架套话，只说她正在对我说话");
                Require(!expressStableA.Contains("【我刚才想过】") &&
                        !expressStableA.Contains("此刻点亮的共同记忆"),
                    "决策卡和记忆血肉不能进入外显稳定前缀");
                var expressNormalized = expressStableA.Replace("\r\n", "\n");
                Require(expressNormalized.StartsWith("我是小光。\n【我的人格】", StringComparison.Ordinal),
                    "外显稳定前缀必须直接从第一人称身份进入人格卡");
                Require(!expressStableA.Contains("你是 TraceSoul") &&
                        !expressStableA.Contains("唯一拥有第一人称的 Brain"),
                    "主线 Prompt 不应以框架 Brain 身份覆盖第一人称自我");
                var personalityAt = expressStableA.IndexOf("【我的人格】", StringComparison.Ordinal);
                var outputAt = expressStableA.IndexOf("【输出格式】", StringComparison.Ordinal);
                Require(personalityAt >= 0 && outputAt > personalityAt,
                    "外显注意力顺序必须是人格在前，输出格式在后");
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
                var waitDynamic = fake.Requests[4][1].content;
                Require(waitDynamic.Contains("出门办事") && waitDynamic.Contains("查一下天气"),
                    "出门时应让外显先说等一下，并看见心智要办的事");

                Console.WriteLine("Prompt layout passed: mind-stable=" + mindStableA.Length +
                                  " chars, express-stable=" + expressStableA.Length +
                                  " chars, current Moment only in final user message.");
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
                moved.unfinished_intent.Contains("狐狸") &&
                moved.attention != null &&
                moved.attention[0].kind == "activity",
            "变了应同时写下当前时、未完成和手上的事");
        var next = InnerLifeLogic.Reduce(current, moved, "m1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Require(next.Revision == 1 && next.Narrative.Contains("狐狸") &&
                InnerLifeLogic.HasUnfinished(next) &&
                InnerLifeLogic.FormatForMind(next).Contains("上一拍未完成"),
            "下一拍心智应看见刚写下的切片");
        var keep = InnerLifeLogic.Reduce(next, new InnerRuntimeWriteData { attention = null }, "m2",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Require(keep.UnfinishedIntent == next.UnfinishedIntent && keep.Narrative == next.Narrative,
            "未给的字段应保持上一版");
        var cleared = InnerLifeLogic.ProposeFromMind(new MindDecisionData
        {
            beat = MindBeatValues.Now,
            attention = "无"
        }, next);
        Require(cleared.attention != null && cleared.attention.Count == 0 &&
                cleared.unfinished_intent == string.Empty,
            "手上写无应放下未完成");
        Require(InnerLifeLogic.ClassifyAttention("答应明天帮她查") == "intention",
            "答应过的事应记成未完成意图");
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
            Content = "时间任务到期：续上：狐狸故事还没讲完"
        }, KernelWakeValues.Mind, PairIdentity.Missing), "自己叫醒不应再走观察");
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
            Content = "时间任务到期：想她",
            Wake = KernelWakeValues.Mind
        }) == KernelWakeValues.Mind, "普通到期应叫醒心智");
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
        var story = MindTemplateLogic.Select("小光，你给我讲个故事嘛", encoder, 2);
        Require(story.Count > 0 && story[0].Instruction.IndexOf("把内容做完", StringComparison.Ordinal) >= 0,
            "当场做完模版应写清这一拍把内容做完");
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
            if (first.IndexOf("我先把这一拍想清楚", StringComparison.Ordinal) >= 0)
            {
                return Task.FromResult(
                    "{\"beat\":\"当下\",\"tags\":\"\",\"query\":\"\",\"mood\":\"平静\"," +
                    "\"mood_changed\":false,\"archive\":false,\"new_fact\":\"\"," +
                    "\"leave\":\"\",\"note\":\"接住。\",\"today\":\"\",\"inner\":\"她在说话，我接着。\"," +
                    "\"attention\":\"\",\"review\":false,\"cognition\":\"\"}");
            }
            return Task.FromResult(
                "{\"should_express\":true,\"reply\":\"嗯。\",\"sticker\":\"\"," +
                "\"qzone\":\"\",\"voice\":\"\",\"image\":\"\",\"mood\":\"\"}");
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { Model });
        }
    }
}
