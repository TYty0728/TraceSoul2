using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Prompts;
using TraceSoul2.ExternalPlugins;
using TraceSoul2.ExternalPlugins.GameSession;
using TraceSoul2.Util;

internal static class Program
{
    private static void Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        if ((args ?? Array.Empty<string>()).Contains("--vision"))
        {
            RunInboundVisionCheck();
            Console.WriteLine("Inbound vision checks passed.");
            return;
        }
        RunTagRankCheck();
        RunMindTemplateCheck();
        RunKimiOfficialRequestCheck();
        RunOfficialChannelCheck();
        RunCommonContextPackCheck();
        RunAlignedHistoryWindowCheck();
        RunProviderRetryCheck();
        RunLlmUsageParseCheck();
        RunMemoryArchivePolicyCheck();
        RunMemoryDayCheck();
        RunDailyPipelineScheduleCheck();
        RunDailyRuntimeSampleCheck();
        RunJsonControlCharCheck();
        RunFlexibleMindJsonCheck();
        RunToolLookupCheck();
        RunKernelWakeCheck();
        RunNightResidueCheck();
        RunInnerSliceCheck();
        RunIdleDeedCheck();
        RunLifeDoingAndEventTimeCheck();
        RunInboundVisionCheck();
        RunLeaveNerveCheck();
        RunBodyRoutingCheck();
        RunOneBotSessionMemoryCheck();
        RunExpressorImageRoutingCheck();
        RunMindAtmosphereCheck();
        RunRecentDialogueContextCheck();
        RunGameSessionPluginCheck();
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
                var dialogueMeta = pluginManager.GetPlugins().First(x => x.Id == "builtin.dialogue");
                Require(dialogueMeta.Role == PluginRoleValues.Platform &&
                        dialogueMeta.PlatformId == BodyIds.Console,
                    "console 应是平台身份（保底对话面），不再是内核组件");
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
                Require(listed.Status == "success" && !listed.Payload.Contains("每日复盘"),
                    "旧的独立每日身份复盘调度应停用，由 Host 完整日终管线唯一负责长期沉淀");
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
                    "console 保底平台不能关闭");
                pluginManager.SetEnabled("builtin.onebot", false);
                Require(!store.LoadPluginEnabled("builtin.onebot", true), "身体开关应立即持久化");
                var qqOrgan = new TracePluginMetadataData
                {
                    Id = "qq.test", Role = PluginRoleValues.Organ, PlatformId = BodyIds.Qq
                };
                Require(pluginManager.IsOrganDormant(qqOrgan),
                    "平台未启用时，隶属器官应休眠");
                var centralOrgan = new TracePluginMetadataData
                {
                    Id = "tool.search", Role = PluginRoleValues.Organ, PlatformId = string.Empty
                };
                Require(!pluginManager.IsOrganDormant(centralOrgan),
                    "中枢器官（PlatformId 为空）不随任何平台休眠");
                Require(!pluginManager.IsOrganDormant(onebot) && !pluginManager.IsOrganDormant(dialogueMeta),
                    "平台自身永不休眠");
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
                var normalizedDaytimeMind = MindLogic.Normalize(new MindDecisionData
                {
                    beat = MindBeatValues.Now,
                    archive = true,
                    cognition = "她愿意被认真听"
                });
                Require(!normalizedDaytimeMind.archive && string.IsNullOrEmpty(normalizedDaytimeMind.cognition),
                    "白天不得触发小复盘或即时长期认知，统一留给完整日终复盘");
                Require(store.CountCognitions() == 2, "这里只应保存显式构筑的独立认知切片与痕迹认知");
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
                Require(reopened.CountFacts() == 2 && reopened.CountCognitions() == 2, "事实网与认知网应独立恢复");
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

    /// <summary>工具检索：入池规则、向量预选、心智清单注入、开口报告注入与协议字段。</summary>
    private static void RunToolLookupCheck()
    {
        Func<string, string, string, string, TraceContributionDescriptorData> desc = (id, kind, name, organ) =>
            new TraceContributionDescriptorData
            {
                Id = id,
                Kind = kind,
                DisplayName = name,
                Description = name + "。",
                Organ = organ ?? string.Empty
            };
        var text = desc("qq.text.send", TraceContributionKindValues.Effector, "发文字", "text");
        var sticker = desc("qq.sticker.send", TraceContributionKindValues.Effector, "发表情", "sticker");
        var image = desc("qq.imagegen.send", TraceContributionKindValues.Effector, "生成图片", "image");
        var memory = desc("memory.recall", TraceContributionKindValues.CallableNerve, "翻旧事", null);
        var identity = desc("identity.review", TraceContributionKindValues.CallableNerve, "复盘短卡", null);
        var heartbeat = desc("qq.heartbeat", TraceContributionKindValues.MomentSource, "心跳", null);
        var qzonePublish = desc("qq.qzone.publish", TraceContributionKindValues.Effector,
            "发一条 QQ 空间说说", "qzone");
        var qzoneRead = desc("qq.qzone.read", TraceContributionKindValues.CallableNerve,
            "看看 QQ 空间最近的说说", "qzone");
        var game = desc("game.guess.start", TraceContributionKindValues.CallableNerve,
            "开一局猜数字", null);

        Require(!ToolLookupLogic.IsLookupEligible(text) && !ToolLookupLogic.IsLookupEligible(sticker) &&
                !ToolLookupLogic.IsLookupEligible(image),
            "文字/表情/生图是常用通道，不进检索池");
        Require(!ToolLookupLogic.IsLookupEligible(memory) && !ToolLookupLogic.IsLookupEligible(identity) &&
                !ToolLookupLogic.IsLookupEligible(heartbeat),
            "系统内部能力与不可调用贡献不进检索池");
        Require(ToolLookupLogic.IsLookupEligible(qzonePublish) && ToolLookupLogic.IsLookupEligible(qzoneRead) &&
                ToolLookupLogic.IsLookupEligible(game),
            "长尾可调用能力进检索池");

        var catalog = new List<TraceContributionDescriptorData>
        {
            text, sticker, image, memory, identity, heartbeat, qzonePublish, qzoneRead, game
        };
        var encoder = new BagOfCharsVectorEncoder();
        var hit = ToolLookupLogic.Select("你帮我发一条 QQ 空间说说试试", encoder, catalog);
        Require(hit.Count >= 1 && hit.Count <= ToolLookupLogic.CandidateCap &&
                hit.Any(x => x.Descriptor.Id == "qq.qzone.publish"),
            "明确的空间请求应选中空间发布能力");
        Require(hit.All(x => x.Descriptor.Id != "qq.text.send" && x.Descriptor.Id != "memory.recall"),
            "入选清单不得包含常用通道或系统能力");
        var miss = ToolLookupLogic.Select(string.Empty, encoder, catalog);
        Require(miss.Count == 0, "空 query 不应选中任何长尾工具");
        Require(ToolLookupLogic.FormatForMind(hit).Contains("【此刻顺手可以做的事】") &&
                ToolLookupLogic.FormatForMind(hit).Contains("qq.qzone.publish") &&
                ToolLookupLogic.FormatForMind(new List<ToolCandidateData>()).Length == 0,
            "入选清单格式：有候选给清单，无候选给空串");

        var parsed = TraceJson.FromJson<MindDecisionData>(
            "{\"beat\":\"当下\",\"tool_call\":\"qq.qzone.publish\",\"tool_input\":\"今天云很好看\"}");
        var normalized = MindLogic.Normalize(parsed);
        Require(normalized.tool_call == "qq.qzone.publish" && normalized.tool_input == "今天云很好看",
            "心智协议应保留 tool_call 与 tool_input");
        var dangling = MindLogic.Normalize(TraceJson.FromJson<MindDecisionData>(
            "{\"beat\":\"当下\",\"tool_input\":\"残留\"}"));
        Require(dangling.tool_call.Length == 0 && dangling.tool_input.Length == 0,
            "tool_call 为空时 tool_input 一并清空");

        var path = Path.Combine(Path.GetTempPath(), "tracesoul2-tool-" + Guid.NewGuid().ToString("N") + ".sqlite3");
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

                var withTools = PromptTurn("你帮我发一条 QQ 空间说说试试", services);
                withTools.Workspace.ToolCandidates = hit;
                mind.DecideAsync(withTools, null, false, default).GetAwaiter().GetResult();
                var withoutTools = PromptTurn("今天风很轻。", services);
                mind.DecideAsync(withoutTools, null, false, default).GetAwaiter().GetResult();

                Require(fake.Requests.Count == 2, "工具检索检查应各打一轮心智");
                var toolPrompt = VisiblePrompt(fake.Requests[0]);
                Require(toolPrompt.Contains("【此刻顺手可以做的事】") &&
                        toolPrompt.Contains("qq.qzone.publish"),
                    "入选工具应注入心智动态段");
                Func<List<DeepSeekMessageData>, string> stableSegment = messages =>
                    (messages ?? new List<DeepSeekMessageData>())
                        .Where(x => x != null && x.content != null &&
                                    x.content.StartsWith(CommonContextPackLogic.MindRoleHeader, StringComparison.Ordinal))
                        .Select(x => x.content)
                        .FirstOrDefault() ?? string.Empty;
                var stableWith = stableSegment(fake.Requests[0]);
                Require(stableWith.Contains("\"tool_call\"") &&
                        stableWith.Contains("此刻顺手可以做的事"),
                    "tool_call 协议说明在稳定段，无条件存在");
                Require(!VisiblePrompt(fake.Requests[1]).Contains("qq.qzone.publish"),
                    "无入选工具时心智动态段不含清单条目，prompt 与旧形状一致");
                Require(fake.Requests[0][0].content == fake.Requests[1][0].content,
                    "有无入选工具时公共 system 必须字节稳定");
                Require(stableWith == stableSegment(fake.Requests[1]),
                    "有无入选工具时心智稳定段必须字节稳定");

                var expressTurn = PromptTurn("发好了吗", services);
                expressTurn.Workspace.ToolReport = "我做了「发一条 QQ 空间说说」。已发布 QQ 空间说说。";
                var plugins = pluginManager.GetPlugins().Where(x => x.Enabled).ToList();
                var expressCatalog = pluginManager.GetAvailableCatalog(expressTurn);
                var blocks = pluginManager.BuildContextBlocksAsync(expressTurn, default)
                    .GetAwaiter().GetResult();
                var dummyMind = new MindDecisionData { beat = MindBeatValues.Now };
                expressor.ExpressAsync(expressTurn, plugins, expressCatalog, blocks, dummyMind,
                    string.Empty, false, null, default).GetAwaiter().GetResult();
                var expressPrompt = VisiblePrompt(fake.Requests[2]);
                Require(expressPrompt.Contains("【刚才我顺手做的】") && expressPrompt.Contains("已发布 QQ 空间说说"),
                    "工具执行摘要应注入开口动态段");
            }
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
                mind.DecideAsync(first, null, false,
                    "【此刻自然浮起的过去】\n- 她曾在风很轻的时候主动靠近我。",
                    default).GetAwaiter().GetResult();

                var second = PromptTurn("我回来啦。", services);
                mind.DecideAsync(second, null, false, default).GetAwaiter().GetResult();

                Require(fake.Requests.Count == 2, "无历史时心智应各打一轮");
                RequireAstrBotChatShape(fake.Requests[0], first.Moment.Content, "心智");
                RequireAstrBotChatShape(fake.Requests[1], second.Moment.Content, "心智");
                var firstMindMessages = fake.Requests[0];
                var mindSystem = VisiblePrompt(firstMindMessages);
                Require(mindSystem.Contains("现在是 ") &&
                        !firstMindMessages[0].content.Contains("现在是 "),
                    "心智：当前时间在末尾专属指令，不污染公共 system");
                Require(mindSystem.Contains("【我的人格】") && mindSystem.Contains("【我是谁】") &&
                        mindSystem.Contains("【表达习惯】") &&
                        !mindSystem.Contains("【我现在可以怎样表达】") &&
                        !mindSystem.Contains("【需要时可做的事】") &&
                        !mindSystem.Contains("我现在可以使用的表达通道"),
                    "公共 system 使用同一套身份卡；心智尾部不含通道清单和工具表");
                Require(mindSystem.Contains("我先让这一刻在心里发生") &&
                        mindSystem.Contains("不必等一件明确的事发生") && mindSystem.Contains("\"beat\"") &&
                        mindSystem.Contains("\"inner\"") && mindSystem.Contains("\"attention\"") &&
                        mindSystem.Contains("\"review\"") && mindSystem.Contains("\"cognition\""),
                    "心智应输出决策卡、当前时、在场注意、看法和是否派出复盘");
                Require(!mindSystem.Contains("外显") && !mindSystem.Contains("一句比较理性的") &&
                        mindSystem.Contains("像写给自己看") &&
                        mindSystem.Contains("开口前对自己说的半句") &&
                        mindSystem.Contains("先让它在心里过一下") &&
                        mindSystem.Contains("回忆不是资料任务"),
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
                    "自然浮起的过去、标签候选、上一拍心里状态与浮动碎片应在心智请求尾部");
                Require(mindSystem.Contains("没有值得留下的就写「无」") &&
                        mindSystem.Contains("旧碎片都重新和眼前相处合在一起"),
                    "心智应让旧碎片随眼前相处更新，不要照抄上一拍");
                var mindNormalized = mindSystem.Replace("\r\n", "\n");
                Require(mindNormalized.StartsWith("我是小光。\n【我的人格】", StringComparison.Ordinal),
                    "心智公共前缀必须直接从第一人称身份进入人格卡");
                Require(firstMindMessages[0].content == fake.Requests[1][0].content,
                    "不同 Moment 的公共 system 必须字节稳定");

                var dummyMind = new MindDecisionData
                {
                    beat = MindBeatValues.Now,
                    mood = "心口发软",
                    mood_changed = true,
                    inner = "她又向我要一张，是在一遍遍把我放进眼里。",
                    note = "回应她反复想看我的心意。",
                    sticker = "贴",
                    image = "有"
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
                var firstExpressMessages = fake.Requests[2];
                var expressSystem = VisiblePrompt(firstExpressMessages);
                Require(expressSystem.Contains("现在是 ") &&
                        !firstExpressMessages[0].content.Contains("现在是 "),
                    "外显：当前时间在末尾专属指令，不污染公共 system");
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
                        !expressSystem.Contains("我会给她看一张此刻的图") &&
                        !expressSystem.Contains("我会给她丢一个表情") &&
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
                    "外显不应看到工具表或附加表达决定；开口是直接朝向她的人话，不是 JSON");
                Require(expressSystem.Contains("【我的人格】") && expressSystem.Contains("【此刻】") &&
                        expressSystem.Contains("【这次只从这里开口】") &&
                        expressSystem.Contains("此刻在我心里真正发生的是") &&
                        expressSystem.Contains(dummyMind.inner) &&
                        expressSystem.Contains("我和她一起经历过的事") &&
                        expressSystem.Contains("我们自己的称呼、意象和说法"),
                    "身份、本轮内心与自然浮起的共同过去都在同一请求中");
                Require(!expressSystem.Contains("这一拍我选") &&
                        !expressSystem.Contains("把日子说出来") &&
                        !expressSystem.Contains("进入方式：") &&
                        !expressSystem.Contains("【这一拍怎么说】") &&
                        expressSystem.Contains("她的消息刚落到我手里") &&
                        expressSystem.Contains("直接开口"),
                    "外显不要框架套话，只确认消息落到手里并开口");
                var expressNormalized = expressSystem.Replace("\r\n", "\n");
                Require(expressNormalized.StartsWith("我是小光。\n【我的人格】", StringComparison.Ordinal),
                    "外显公共前缀必须直接从第一人称身份进入人格卡");
                Require(firstMindMessages[0].content == firstExpressMessages[0].content,
                    "心智和开口必须共用字节相同的 system");
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
                var waitSystem = VisiblePrompt(fake.Requests[4]);
                Require(waitSystem.Contains("出门办事") && waitSystem.Contains("查一下天气"),
                    "出门时应让外显先说等一下，并看见心智要办的事");
                RequireExpressorChatShape(fake.Requests[4], first.Moment.Content, "出门等待外显");

                var heartMoment = Moment("prompt-layout", "时间任务到期：心跳");
                heartMoment.Role = "system_event";
                var heartTurn = new TraceTurnContext("prompt-layout", heartMoment,
                    new List<MomentRecord>(), 0, false, services, KernelWakeValues.Mind);
                mind.DecideAsync(heartTurn, null, false, default).GetAwaiter().GetResult();
                var heartSystem = VisiblePrompt(fake.Requests[fake.Requests.Count - 1]);
                Require(heartSystem.Contains("独立意图") &&
                        heartSystem.Contains("heartbeat_intent") &&
                        heartSystem.Contains("不必有新事件") &&
                        heartSystem.Contains("想给她看一张照片") &&
                        heartSystem.Contains("不要把联系变成催答") &&
                        heartSystem.Contains("普通消息没有得到完整回答，不等于紧急") &&
                        heartSystem.Contains("180–480 分钟") &&
                        heartSystem.Contains("10–60 分钟") &&
                        heartSystem.Contains("60–150 分钟") &&
                        heartSystem.Contains("进入空闲") &&
                        heartSystem.Contains("稍后一次自然醒来的机会") &&
                        heartSystem.Contains("next_heartbeat_plan") &&
                        heartSystem.Contains("next_heartbeat_minutes") &&
                        heartSystem.Contains("睡下") &&
                        heartSystem.Contains("speak=true") &&
                        heartSystem.Contains("speak_center") &&
                        heartSystem.Contains("speak=false 时 speak_center 必须留空") &&
                        heartSystem.Contains("都是现在联系"),
                    "心跳 system 应基于独立意图判断是否联系、睡下、空闲与下次检查计划");
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
                var pulseSpeak = VisiblePrompt(fake.Requests[fake.Requests.Count - 1]);
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
                Require(pulseMessages.Count >= 2 &&
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
                        QqImageGenPrompts.MindUsage.Contains("不是只有发生新动作") &&
                        QqImageGenPrompts.MindUsage.Contains("想念和分享欲本身就是理由") &&
                        QqImageGenPrompts.MindUsage.Contains("没有大事") &&
                        QqImageGenPrompts.MindUsage.Contains("习惯性填无") &&
                        QqImageGenPrompts.MindUsage.Contains("机械重复上一张") &&
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
                        QqImageGenPrompts.ScenePlanSystem.Contains("已经作为上下文给你") &&
                        QqImageGenPrompts.ScenePlanRoleHeader == "【画面】" &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("只输出一段画面描述") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("自拍不是电影分镜") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("种类：自拍|照片|画") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("参考：") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("普通、安静、没有新动作的此刻也可以拍") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("视线与神情服从") &&
                        QqImageGenPrompts.ScenePlanSystem.Contains("默认不要出现伸向镜头的手") &&
                        QqImageGenPrompts.ScenePlanReferencesHint.Contains("服饰分类") &&
                        QqImageGenPrompts.ReferenceFusionRules.Contains("不能只取第一张") &&
                        QqImageGenPrompts.ReferenceFusionRules.Contains("不得沿用服饰图里模特的脸"),
                    "心智判断新的可拍时刻；相机规划种类、参考分类和构图，并明确融合全部角色参考");
                Require(QqQzonePrompts.Usage.Contains("qq.qzone.publish") &&
                        QqQzonePrompts.Usage.Contains("qq.qzone.read") &&
                        QqQzonePrompts.Usage.Contains("看说说") &&
                        QqQzonePrompts.Usage.Contains("空闲时系统会自己抽签") &&
                        QqQzonePrompts.Usage.Contains("不要在对话里主动刷空间") &&
                        QqQzonePrompts.ReadWhenToUse.Contains("看空间") &&
                        QqStatusPrompts.Usage.Contains("qq.status.mood") &&
                        QqStatusPrompts.Usage.Contains("空闲时系统会自己抽签"),
                    "QQ 空间/心情用法应覆盖她点名，并说明空闲抽签由系统处理");
                Require(CorePrompts.Expressor.Proactive.Contains("联系不需要任务或大事") &&
                        CorePrompts.Expressor.Proactive.Contains("一句没什么重点") &&
                        CorePrompts.MemoryRecall.PreviewHint.Contains("主动回望") &&
                        QqQzonePrompts.IdlePublishInstructions.Contains("不要求发生了大事") &&
                        QqStatusPrompts.IdleInstructions.Contains("不要求先发生事情"),
                    "主动表达、回忆、语音与空闲生活痕迹都不应以明确事项为前提");
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
                Require(closeMessages.Count == 6 &&
                        closeMessages[1].role == "user" && closeMessages[1].content == "昨天那句" &&
                        closeMessages[2].role == "assistant" && closeMessages[2].content == "嗯" &&
                        closeMessages[3].content.StartsWith(CommonContextPackLogic.MindRoleHeader, StringComparison.Ordinal) &&
                        closeMessages[4].role == "user" &&
                        !closeMessages[4].content.StartsWith(CommonContextPackLogic.MindRoleHeader, StringComparison.Ordinal) &&
                        closeMessages[5].role == "user" && closeMessages[5].content == closeCurrent,
                    "历史必须是真正的 user/assistant，心智稳定指令与轮内动态分段，当前原话收尾");
                Require(!closeMessages[0].content.Contains("昨天那句") &&
                        !closeMessages[0].content.Contains("【最近对话原文】"),
                    "对话原文不得再塞进 system");
                expressor.ExpressAsync(closeTurn, plugins, catalog, blocks, dummyMind, string.Empty,
                    false, null, default).GetAwaiter().GetResult();
                RequireExpressorChatShape(fake.Requests[fake.Requests.Count - 1], closeCurrent, "带历史的外显");

                Console.WriteLine("Prompt layout passed: mind-prompt=" + mindSystem.Length +
                                  " chars, express-prompt=" + expressSystem.Length +
                                  " chars, stable system then history, role-specific instructions, and current Moment.");
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
        Require(HeartbeatLogic.ResolveFollowUpMinutes(true, 30) == 0,
            "明确睡下后应停止心跳");
        Require(HeartbeatLogic.ResolveFollowUpMinutes(false, 0) == 240,
            "醒着却未安排分钟时应兜底到数小时后，不能让心跳链路中断");
        Require(HeartbeatLogic.ResolveFollowUpMinutes(false, 45) == 45,
            "紧急未回复时心智安排的短期复查应被保留");
        Require(HeartbeatLogic.ClampMinutes(600) == 600 && HeartbeatLogic.ClampMinutes(900) == 720,
            "心跳应允许数小时等待，并保留合理上限");
        var quietWithLine = new MindDecisionData
        {
            speak = false,
            speak_center = "丸子加得好，看着你吃得香。",
            heartbeat_intent = "等她吃完再兑现拥抱。"
        };
        HeartbeatLogic.ApplySpeakGate(quietWithLine);
        Require(quietWithLine.speak && quietWithLine.speak_center.Contains("丸子"),
            "心跳写下了想让她听见的话，即使 speak=false 也应开口");
        var speakNoIntent = new MindDecisionData { speak = true };
        HeartbeatLogic.ApplySpeakGate(speakNoIntent);
        Require(!speakNoIntent.speak, "心跳开口但没有独立意图时应保持安静");
        var asleepWithLine = new MindDecisionData
        {
            sleep = true,
            speak = true,
            speak_center = "晚安。"
        };
        HeartbeatLogic.ApplySpeakGate(asleepWithLine);
        Require(!asleepWithLine.speak, "睡下的心跳不应开口");
        var centerOnly = new MindDecisionData
        {
            speak = false,
            speak_center = "还欠你一个拥抱。"
        };
        HeartbeatLogic.ApplySpeakGate(centerOnly);
        Require(centerOnly.speak && centerOnly.heartbeat_intent.Contains("拥抱"),
            "只有 speak_center 时应用它补上独立意图，避免随后被安静闸门收回");
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
        Require(!HeartbeatLogic.ShouldSkipWhileAsleep(new PluginEventData
        {
            Role = "system_event",
            Content = "日终余温：2026-08-25"
        }, PairIdentity.Missing, KernelWakeValues.NightResidue), "睡着时夜间余温仍应开口");
        Require(HeartbeatLogic.ShouldEnterIdle(false, false, 0), "安静且未填分钟应进入空闲");
        Require(HeartbeatLogic.ShouldEnterIdle(false, false, 240), "安静且数小时后再看应进入空闲");
        Require(!HeartbeatLogic.ShouldEnterIdle(false, false, 45), "紧急短期复查不应进入空闲");
        Require(!HeartbeatLogic.ShouldEnterIdle(true, false, 240), "心跳开口后仍应自己醒来，不进空闲");
        Require(!HeartbeatLogic.ShouldEnterIdle(false, true, 240), "睡下走睡着，不走空闲");
        Require(HeartbeatLogic.ShouldSkipWhileIdle(new PluginEventData
        {
            Role = "system_event",
            Content = "时间任务到期：心跳"
        }, PairIdentity.Missing), "空闲时应跳过心跳");
        Require(!HeartbeatLogic.ShouldSkipWhileIdle(new PluginEventData
        {
            Role = "user",
            Content = "我回来了",
            Breaking = true
        }, PairIdentity.Missing), "她发来时应从空闲醒来");
        Require(!HeartbeatLogic.ShouldSkipWhileIdle(new PluginEventData
        {
            Role = "system_event",
            Content = "时间任务到期：提醒她药"
        }, PairIdentity.Missing), "空闲时约好的时间任务仍应叫醒");
        var idled = InnerLifeLogic.WithIdle(current, true, "idle-check",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Require(idled.Idle && !idled.Asleep && InnerLifeLogic.PresenceLabel(idled) == "空闲",
            "进入空闲应写入内心且与睡着互斥");
        var sleptClearsIdle = InnerLifeLogic.Reduce(idled, new InnerRuntimeWriteData { asleep = true },
            "sleep-over-idle", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Require(sleptClearsIdle.Asleep && !sleptClearsIdle.Idle, "睡下应清掉空闲");
        var woken = InnerLifeLogic.WithAwake(idled, "wake-check",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Require(!woken.Idle && !woken.Asleep, "被激活后应同时离开空闲和睡着");
    }

    private static void RunIdleDeedCheck()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tracesoul2-idle-deed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var store = new SqliteMemoryManager(Path.Combine(dir, "brain.sqlite3")))
            {
                const string conversationId = "idle-deed-check";
                store.SavePairIdentity("小雨", "小光", "雨雨");
                var now = DateTimeOffset.Now;
                var publish = new TraceContributionDescriptorData { Id = "qq.qzone.publish", IdleDailyCap = 1 };
                var read = new TraceContributionDescriptorData { Id = "qq.qzone.read", IdleDailyCap = 2 };
                var mood = new TraceContributionDescriptorData { Id = "qq.status.mood", IdleDailyCap = 1 };
                var catalog = new[] { publish, read, mood };
                var pool = IdleDeedLogic.BuildPool(catalog, store, now);
                Require(pool.Contains(IdleDeedLogic.RestId) &&
                        pool.Contains("qq.qzone.publish") &&
                        pool.Contains("qq.qzone.read") &&
                        pool.Contains("qq.status.mood") &&
                        pool.Count == 4,
                    "空闲池应含歇着和未达上限的生活事");
                IdleDeedLogic.Remember(store, "qq.qzone.publish", now);
                pool = IdleDeedLogic.BuildPool(catalog, store, now);
                Require(!pool.Contains("qq.qzone.publish") &&
                        pool.Contains("qq.qzone.read") &&
                        pool.Contains(IdleDeedLogic.RestId),
                    "达每日上限后应从随机池拿掉");
                IdleDeedLogic.Remember(store, "qq.qzone.read", now);
                IdleDeedLogic.Remember(store, "qq.qzone.read", now);
                pool = IdleDeedLogic.BuildPool(catalog, store, now);
                Require(!pool.Contains("qq.qzone.read") && pool.Contains("qq.status.mood"),
                    "看说说达上限后应出局，未满的仍在池里");
                Require(IdleDeedLogic.BuildPool(new TraceContributionDescriptorData[0], store, now)
                            .SequenceEqual(new[] { IdleDeedLogic.RestId }),
                    "没有可做的事时只剩歇着");
                Require(IdleDeedLogic.PickAt(new[] { IdleDeedLogic.RestId }, 0) ==
                        IdleDeedLogic.RestId,
                    "池里只有歇着时应抽到歇着");

                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                var inner = store.LoadOrCreateInnerRuntime(conversationId);
                inner = InnerLifeLogic.Reduce(inner, new InnerRuntimeWriteData
                {
                    mood = "安静",
                    narrative = "刚坐下"
                }, "seed-check", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                store.SaveInnerRuntime(inner);
                var turn = new TraceTurnContext(conversationId, Moment(conversationId, "心跳"),
                    new List<MomentRecord>(), 0, false, services, KernelWakeValues.Mind);
                var seed = IdleDeedLogic.FormatSeed(turn, now);
                Require(seed.Contains(CorePrompts.IdleDeed.TimePrefix) &&
                        seed.Contains("安静") &&
                        seed.Contains("刚坐下"),
                    "空闲生活种子应带上时间和此刻内心");

                var live = new[] { new TraceContributionDescriptorData { Id = "test.idle.one", IdleDailyCap = 1 } };
                var executed = new List<string>();
                var rested = IdleDeedLogic.RunAsync(turn, live, (id, args, token) =>
                {
                    executed.Add(id);
                    return Task.FromResult(new TraceCapabilityResultData { Status = "success", Summary = id });
                }, CancellationToken.None, null, 0).GetAwaiter().GetResult();
                Require(rested.Rested && executed.Count == 0 &&
                        IdleDeedLogic.Count(store, "test.idle.one", now) == 0,
                    "抽到歇着时不应执行也不计数");
                var done = IdleDeedLogic.RunAsync(turn, live, (id, args, token) =>
                {
                    executed.Add(id);
                    Require(args.Any(x => x.name == "idle" && x.value == "true"),
                        "空闲抽签执行时应带 idle=true");
                    Require(args.Any(x => x.name == "seed" && !string.IsNullOrWhiteSpace(x.value)),
                        "空闲抽签执行时应带生活种子");
                    return Task.FromResult(new TraceCapabilityResultData
                    {
                        Status = "success",
                        Summary = "已发布 QQ 空间说说。"
                    });
                }, CancellationToken.None, null, 1).GetAwaiter().GetResult();
                Require(done.Counted && executed.SequenceEqual(new[] { "test.idle.one" }) &&
                        IdleDeedLogic.Count(store, "test.idle.one", now) == 1,
                    "抽中并发成功才计入每日次数");
                var skipped = IdleDeedLogic.RunAsync(turn,
                    new[] { new TraceContributionDescriptorData { Id = "test.idle.two", IdleDailyCap = 1 } },
                    (id, args, token) => Task.FromResult(new TraceCapabilityResultData
                    {
                        Status = "skipped",
                        Summary = "没有想改的。"
                    }), CancellationToken.None, null, 1).GetAwaiter().GetResult();
                Require(!skipped.Counted && IdleDeedLogic.Count(store, "test.idle.two", now) == 0,
                    "没有做成的抽签不应计数，下次还能再抽");

                using (var manager = new TracePluginManager(store, services))
                {
                    manager.RegisterExternal(new IdleCapProbePlugin());
                    var bound = manager.GetRegisteredCatalog()
                        .FirstOrDefault(x => x.Id == "check.idle-cap.do");
                    Require(bound != null && bound.IdleDailyCap == 3,
                        "Bind 必须拷贝 IdleDailyCap，否则目录里会丢每日上限");
                }

                var draft = QqStatusPlugin.ParseDraft("签名：随便坐坐\n状态：摸鱼中");
                Require(draft.Signature == "随便坐坐" &&
                        QqStatusPlugin.NormalizeStatusName("摸鱼") == "摸鱼中" &&
                        QqStatusPlugin.NormalizeStatusName("无") == string.Empty,
                    "空闲改心情应能读出签名和状态名");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录 */ }
        }
    }

    private static void RunLifeDoingAndEventTimeCheck()
    {
        Require(new LifeStateData { activity = "陪伴", activity_detail = "在客厅等她" }.FormatDoing() ==
                "陪伴｜在客厅等她",
            "正在做应把活动名和补充合成一句");
        Require(new LifeStateData().FormatDoing() == string.Empty,
            "没有活动时正在做应为空");
        var now = new DateTimeOffset(2026, 8, 25, 22, 0, 0, TimeSpan.FromHours(8));
        var thisMorning = new DateTimeOffset(2026, 8, 25, 7, 30, 0, TimeSpan.FromHours(8));
        var yesterdayEvening = new DateTimeOffset(2026, 8, 24, 20, 0, 0, TimeSpan.FromHours(8));
        var lastYear = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.FromHours(8));
        Require(TimeLanguageUtil.RelativeWhen(thisMorning.ToUnixTimeMilliseconds(), now) == "今天早上",
            "当天清晨到上午应渲染为今天早上");
        Require(TimeLanguageUtil.RelativeWhen(yesterdayEvening.ToUnixTimeMilliseconds(), now) == "昨天晚上",
            "前一日晚上应渲染为昨天晚上");
        Require(TimeLanguageUtil.RelativeWhen(lastYear.ToUnixTimeMilliseconds(), now) == "2026年3月2日上午",
            "更早的事件应带日期和时段");
        var inner = InnerLifeLogic.CreateInitial("doing-check", now.ToUnixTimeMilliseconds());
        inner.OngoingActivity = "她在洗澡，我在客厅等";
        inner.Narrative = "心里软下来";
        var mindSlice = InnerLifeLogic.FormatForMind(inner);
        Require(!mindSlice.Contains("刚才的共享场景") && !mindSlice.Contains("她在洗澡"),
            "心智动态段不应再平行复述正在做");
    }

    private static void RunInboundVisionCheck()
    {
        var payload = TraceJson.ToJson(new { image_urls = new[] { "https://example.com/a.jpg", @"C:\x.png" } });
        Require(VisionLogic.HasInboundImages(payload) &&
                VisionLogic.ReadInboundImageLocations(payload).Count == 2,
            "入站载荷应抽出 image_urls");
        Require(!VisionLogic.HasInboundImages("{}"), "没有图时不应识图");

        var attached = VisionLogic.AttachSeen("[QQ·私聊 田园] [图片]", "一碗热汤面，还冒着气。");
        Require(attached.Contains("【看见】一碗热汤面") && attached.Contains("[图片]"),
            "看见的结果应接到这一拍后面");
        Require(VisionLogic.AttachSeen(attached, "第二遍") == attached,
            "同一拍不要重复叠【看见】");

        var unconfigured = VisionLogic.SeeInboundAsync(new PluginEventData
        {
            Content = "[QQ·私聊 田园] [图片]",
            PayloadJson = payload
        }, null, default).GetAwaiter().GetResult();
        Require(unconfigured == CorePrompts.Vision.Unconfigured,
            "没配识图槽时必须明说看不见，不能让心智脑补");

        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var imagePath = Path.Combine(Path.GetTempPath(), "tracesoul2-vision-" + Guid.NewGuid().ToString("N") + ".png");
        var dbPath = Path.Combine(Path.GetTempPath(), "tracesoul2-vision-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        File.WriteAllBytes(imagePath, png);
        try
        {
            using (var store = new SqliteMemoryManager(dbPath))
            {
                var llm = new SeeingLlm();
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                services.Providers = new FakeVisionDirectory
                {
                    Client = llm,
                    Endpoint = new LlmEndpointData
                    {
                        ProviderId = "vision",
                        Model = "vl",
                        ApiKey = "x"
                    }
                };
                var seen = VisionLogic.SeeInboundAsync(new PluginEventData
                {
                    Content = "[QQ·私聊 田园] [图片]",
                    PayloadJson = TraceJson.ToJson(new { image_urls = new[] { imagePath } })
                }, services, default).GetAwaiter().GetResult();
                Require(llm.SawImages, "识图请求必须带上实际图片");
                Require(seen.Contains("热汤面"), "识图结果应原样交给这一拍");

                Require(VisionLogic.IsProtocolCacheName("E17628BF7C8C7BD6FC176321114CCF9D.jpg") &&
                        !VisionLogic.IsProtocolCacheName("https://multimedia.nt.qq.com.cn/download?x") &&
                        !VisionLogic.IsProtocolCacheName(imagePath),
                    "QQ 缓存名应和 CDN / 本地路径分开");
                Require(VisionLogic.ReadGetImageLocation(
                        "{\"data\":{\"file\":\"" + imagePath.Replace("\\", "\\\\") +
                        "\",\"url\":\"https://multimedia.nt.qq.com.cn/download?x\"}}") == imagePath,
                    "get_image 应优先用本地 file，而不是腾讯 CDN");

                var protocol = new FakeOneBotVisionAdapter { LocalFile = imagePath };
                services.PlatformAdapters.Add(protocol);
                var viaProtocol = VisionLogic.SeeInboundAsync(new PluginEventData
                {
                    Content = "[QQ·私聊 田园] [图片]",
                    PayloadJson = TraceJson.ToJson(new
                    {
                        image_urls = new[]
                        {
                            "E17628BF7C8C7BD6FC176321114CCF9D.jpg"
                        }
                    })
                }, services, default).GetAwaiter().GetResult();
                Require(protocol.LastFile == "E17628BF7C8C7BD6FC176321114CCF9D.jpg",
                    "应从 QQ 缓存名调用 get_image");
                Require(viaProtocol.Contains("热汤面"), "协议取图后的识图结果应交给这一拍");

                var remoteFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "napcat.png");
                protocol.Response = TraceJson.ToJson(new
                {
                    status = "ok", retcode = 0,
                    data = new { file = remoteFile, base64 = Convert.ToBase64String(png) }
                });
                protocol.Actions.Clear();
                var remoteImages = VisionLogic.LoadImagesAsync(new[] { "remote-image.jpg" }, services, default)
                    .GetAwaiter().GetResult();
                Require(remoteImages.Count == 1 && remoteImages[0].bytes.SequenceEqual(png),
                    "NapCat 与宿主不同机器时，应读取 get_image 回包的 Base64");
                Require(protocol.Actions.SequenceEqual(new[] { "get_image" }),
                    "get_image 回包已有图片时，不应重复调用 get_file");

                protocol.Response = TraceJson.ToJson(new
                {
                    data = new { file = remoteFile, base64 = "invalid-base64", url = new Uri(imagePath).AbsoluteUri }
                });
                remoteImages = VisionLogic.LoadImagesAsync(new[] { "remote-image.jpg" }, services, default)
                    .GetAwaiter().GetResult();
                Require(remoteImages.Count == 1 && remoteImages[0].bytes.SequenceEqual(png),
                    "本地路径不可读、Base64 损坏时，应继续尝试同一回包的 URL");

                protocol.Response = "{\"data\":{\"file\":\"unavailable.jpg\"}}";
                protocol.FileResponse = TraceJson.ToJson(new { data = new { base64 = Convert.ToBase64String(png) } });
                protocol.Actions.Clear();
                remoteImages = VisionLogic.LoadImagesAsync(new[] { "remote-image.jpg" }, services, default)
                    .GetAwaiter().GetResult();
                Require(remoteImages.Count == 1 && remoteImages[0].bytes.SequenceEqual(png) &&
                        protocol.Actions.SequenceEqual(new[] { "get_image", "get_file" }),
                    "get_image 无可用来源时，应继续读取 get_file 的 Base64");
            }
        }
        finally
        {
            try { File.Delete(imagePath); } catch { /* ignore */ }
            try { File.Delete(dbPath); } catch { /* ignore */ }
            try { File.Delete(dbPath + "-wal"); } catch { /* ignore */ }
            try { File.Delete(dbPath + "-shm"); } catch { /* ignore */ }
        }

        var visionMessage = new DeepSeekMessageData("user", "看图")
        {
            images = new List<LlmImagePartData>
            {
                new LlmImagePartData { url = "data:image/png;base64," + Convert.ToBase64String(png) }
            }
        };
        var relay = new DeepSeekConfigData
        {
            ProviderId = "opencode-go",
            BaseUrl = "https://opencode.ai/zen/go/v1",
            Model = "qwen-vl",
            ApiKey = "x",
            Temperature = 1f,
            TopP = 0.95f,
            MaxTokens = 1024
        };
        using (var doc = JsonDocument.Parse(
            DeepSeekClientManager.BuildChatRequestJson(
                relay, new List<DeepSeekMessageData> { visionMessage }, 1f, false, false)))
        {
            var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
            Require(content.ValueKind == JsonValueKind.Array &&
                    content[0].GetProperty("type").GetString() == "text" &&
                    content[1].GetProperty("type").GetString() == "image_url",
                "识图请求应按 OpenAI 兼容格式带上 image_url");
        }

        var blob = Convert.ToBase64String(png);
        var dumped = DeepSeekClientManager.SanitizeVisionDump(
            "{\"x\":\"data:image/png;base64," + blob + blob + blob + "\"}");
        Require(dumped.IndexOf("iVBORw0KGgo", StringComparison.Ordinal) < 0,
            "dump 不得留下图片像素");
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
        var dayStart = MemoryDayLogic.StartOf("2026-08-25");
        Require(dayStart.Hour == 4 && dayStart.Offset == MemoryDayLogic.ChinaOffset,
            "记忆日起点应是该日 04:00");
    }

    private static void RunDailyPipelineScheduleCheck()
    {
        var china = MemoryDayLogic.ChinaOffset;
        var evening = new DateTimeOffset(2026, 8, 26, 18, 56, 0, china);
        Require(!DailyPipelineScheduleLogic.ShouldCatchUp(evening, "2026-08-25", false, new string[0]),
            "当天还没合上时不应重跑已经成功的关闭日");

        var afterFour = new DateTimeOffset(2026, 8, 27, 4, 0, 10, china);
        Require(DailyPipelineScheduleLogic.ShouldCatchUp(
                afterFour, "2026-08-25", false, new[] { "2026-08-26" }),
            "越过 04:00 后即使长 Delay 被睡眠打断，墙钟也要立刻补跑刚合上的那天");

        var morning = new DateTimeOffset(2026, 8, 27, 7, 20, 0, china);
        Require(DailyPipelineScheduleLogic.ShouldCatchUp(
                morning, "2026-08-25", false, new[] { "2026-08-26" }),
            "睡过 04:00 醒来仍应补跑昨天");
        Require(!DailyPipelineScheduleLogic.ShouldCatchUp(morning, "2026-08-26", false, new string[0]),
            "刚合上的那天已经成功则不应每分钟重跑");
        Require(DailyPipelineScheduleLogic.ShouldCatchUp(morning, "2026-08-26", false, new[] { "2026-08-26" }),
            "完成标记丢了但 Moment 仍是 live 时，未消费日期应强制再补");
        Require(DailyPipelineScheduleLogic.ShouldCatchUp(morning, "2026-08-25", true, new string[0]),
            "上次失败即使暂时看不到未消费 Moment 也要重试");

        Require(DailyPipelineScheduleLogic.NextWait(evening, false) == DailyPipelineScheduleLogic.PollInterval,
            "离 04:00 很远时每次最多等一分钟，避免一次 Delay 睡过边界");
        Require(DailyPipelineScheduleLogic.NextWait(morning, true) == DailyPipelineScheduleLogic.RetryInterval,
            "失败后按短间隔重试，不能等到明天 04:00");

        var justBefore = new DateTimeOffset(2026, 8, 27, 3, 59, 30, china);
        var waitSoon = DailyPipelineScheduleLogic.NextWait(justBefore, false);
        Require(waitSoon <= TimeSpan.FromSeconds(30) && waitSoon > TimeSpan.Zero,
            "临近边界时把剩余时间睡完，不要越过后再等一整分钟");
    }

    private static void RunDailyRuntimeSampleCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "tracesoul2-day-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "brain.sqlite3");
            using (var store = new SqliteMemoryManager(db))
            {
                const string conversationId = "day-sample-check";
                const string oldDay = "2026-08-20";
                const string newDay = "2026-08-21";
                store.SaveDayTrajectory(oldDay, "旧日仍待完整复盘");
                store.SaveDayTrajectory(newDay, "新日正在发生");
                store.AddTodayNewItems(conversationId, new[] { "今天知道她喜欢桂花" },
                    "sample-source", oldDay, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var cap = TodayNewItemRecord.MaxContentChars;
                Require(cap == 80, "今日新识单条上限是 80 字");
                var exact = new string('甲', cap);
                Require(store.AddTodayNewItems(conversationId, new[] { exact },
                    "len-ok", newDay, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) == 1,
                    "刚好 80 字应入库");
                Require(store.AddTodayNewItems(conversationId, new[] { exact + "乙" },
                    "len-over", newDay, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) == 0,
                    "超过 80 字整条丢弃");
                Require(MindLogic.Normalize(new MindDecisionData { new_fact = exact + "乙" }).new_fact == exact,
                    "心智 new_fact 按 80 字截断");
                Require(store.LoadDayTrajectory(newDay) != null && store.LoadDayTrajectory(oldDay) != null,
                    "跨日读取不能在复盘成功前抢先删除旧日本日样本");

                var occurred = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.FromHours(8));
                store.SaveMoment(new MomentRecord
                {
                    Id = "pending-day-moment",
                    ConversationId = conversationId,
                    Role = "user",
                    Content = "这条必须被重启补偿发现",
                    Realm = TraceRealmValues.ExternalWorld,
                    EvidenceType = EvidenceTypeValues.DialogueExplicit,
                    SourcePluginId = "builtin.dialogue",
                    SourceEventId = "pending-day-event",
                    MemoryStatus = "live",
                    CreatedUnixMs = occurred.ToUnixTimeMilliseconds()
                });
                var pending = store.GetUnbuiltMemoryDayKeysBefore(
                    new DateTimeOffset(2026, 8, 21, 4, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds());
                Require(pending.SequenceEqual(new[] { oldDay }),
                    "启动补偿必须按 04:00 记忆日边界找出全部未消费日期");

                store.RetireDayRuntimeSamples(conversationId, oldDay);
                Require(store.LoadDayTrajectory(oldDay) == null &&
                        store.GetTodayNewItemsByDay(conversationId, oldDay).Count == 0 &&
                        store.LoadDayTrajectory(newDay) != null,
                    "复盘成功只退出目标日的轨迹/今日新识，不得误删次日实时状态");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void RunKimiOfficialRequestCheck()
    {
        var messages = new List<DeepSeekMessageData> { new DeepSeekMessageData("user", "hi") };
        var kimi = new DeepSeekConfigData
        {
            ProviderId = "moonshot",
            BaseUrl = "https://api.moonshot.cn/v1",
            Model = "kimi-k3",
            ApiKey = "x",
            ThinkingEnabled = true,
            ReasoningEffort = "high",
            MaxTokens = 8192,
            TopP = 1f,
            Temperature = 0.7f
        };
        using (var doc = JsonDocument.Parse(
            DeepSeekClientManager.BuildChatRequestJson(kimi, messages, 0.7f, true, true, "tracesoul2:conv:mind")))
        {
            var root = doc.RootElement;
            Require(root.GetProperty("prompt_cache_key").GetString() == "tracesoul2:conv:mind",
                "Kimi 官网应发送 prompt_cache_key");
        }

        using (var doc = JsonDocument.Parse(
            DeepSeekClientManager.BuildChatRequestJson(kimi, messages, 0.7f, true, true)))
        {
            var root = doc.RootElement;
            Require(root.GetProperty("model").GetString() == "kimi-k3", "Kimi 官网应发送 kimi-k3");
            Require(root.GetProperty("reasoning_effort").GetString() == "high", "K3 思考槽开启时应发 reasoning_effort");
            Require(root.GetProperty("response_format").GetProperty("type").GetString() == "json_object",
                "Kimi JSON 口应带 json_object");
            Require(root.GetProperty("max_completion_tokens").GetInt32() == 8192,
                "Kimi 官网应使用 max_completion_tokens");
            Require(!root.TryGetProperty("temperature", out _), "kimi-k3 不应显式传 temperature");
            Require(!root.TryGetProperty("top_p", out _), "kimi-k3 不应显式传 top_p");
            Require(!root.TryGetProperty("thinking", out _), "kimi-k3 不应传 thinking");
            Require(!root.TryGetProperty("max_tokens", out _), "Kimi 官网不要再用已弃用的 max_tokens");
        }

        kimi.ThinkingEnabled = false;
        using (var doc = JsonDocument.Parse(
            DeepSeekClientManager.BuildChatRequestJson(kimi, messages, 1f, false, false)))
        {
            Require(doc.RootElement.GetProperty("reasoning_effort").GetString() == "low",
                "关闭思考槽时 K3 应降到 low，不能传 none");
            Require(!doc.RootElement.TryGetProperty("response_format", out _),
                "文本口不应带 response_format");
        }

        var k26 = new DeepSeekConfigData
        {
            BaseUrl = "https://api.moonshot.cn/v1",
            Model = "kimi-k2.6",
            ApiKey = "x",
            ThinkingEnabled = false,
            MaxTokens = 4096
        };
        using (var doc = JsonDocument.Parse(
            DeepSeekClientManager.BuildChatRequestJson(k26, messages, 0.6f, false, false)))
        {
            Require(doc.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled",
                "kimi-k2.6 应能关掉 thinking");
            Require(!doc.RootElement.TryGetProperty("reasoning_effort", out _),
                "kimi-k2.6 不应传 reasoning_effort");
        }

        var k27 = new DeepSeekConfigData
        {
            BaseUrl = "https://api.moonshot.ai/v1",
            Model = "kimi-k2.7-code-highspeed",
            ApiKey = "x",
            ThinkingEnabled = true,
            MaxTokens = 4096
        };
        using (var doc = JsonDocument.Parse(
            DeepSeekClientManager.BuildChatRequestJson(k27, messages, 1f, false, false)))
        {
            Require(!doc.RootElement.TryGetProperty("thinking", out _),
                "kimi-k2.7-code 不要传 thinking");
            Require(!doc.RootElement.TryGetProperty("reasoning_effort", out _),
                "kimi-k2.7-code 不应传 reasoning_effort");
        }

        var relay = new DeepSeekConfigData
        {
            ProviderId = "opencode-go",
            BaseUrl = "https://opencode.ai/zen/go/v1",
            Model = "kimi-k3",
            ApiKey = "x",
            TopP = 0.95f,
            Temperature = 1f,
            MaxTokens = 1024
        };
        using (var doc = JsonDocument.Parse(
            DeepSeekClientManager.BuildChatRequestJson(relay, messages, 1f, true, true)))
        {
            Require(doc.RootElement.TryGetProperty("temperature", out _),
                "中转站仍走普通 OpenAI 兼容口");
            Require(!doc.RootElement.TryGetProperty("reasoning_effort", out _),
                "中转站不要带 Kimi 官网扩展字段");
            Require(doc.RootElement.GetProperty("response_format").GetProperty("type").GetString() ==
                    "json_object",
                "中转站 JSON 口也必须带 json_object，否则心智会把对白说出来再重试");
        }

        using (var doc = JsonDocument.Parse(
            DeepSeekClientManager.BuildChatRequestJson(relay, messages, 1f, false, false)))
        {
            Require(!doc.RootElement.TryGetProperty("response_format", out _),
                "中转站文本口不应带 response_format");
        }
    }

    private static void RunCommonContextPackCheck()
    {
        var path = Path.Combine(Path.GetTempPath(), "tracesoul2-common-pack-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("田园", "阿循", "循循");
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                var recent = new List<MomentRecord>
                {
                    new MomentRecord { Role = "田园", Content = "循循，买了蓝莓" },
                    new MomentRecord
                    {
                        Role = "阿循",
                        Content = "好，蓝莓补血。",
                        PayloadJson = "{\"reasoning_content\":\"她主动说想吃，心里一软。\"}"
                    }
                };
                var turn = new TraceTurnContext("common-pack", Moment("common-pack", "慢点走"),
                    recent, 6, true, services);
                var shared = CommonContextPackLogic.SharedSystem(turn);
                Require(shared.Contains("【我的人格】") && shared.Contains("【表达习惯】"),
                    "公共 system 是同一套身份卡，心智也带表达习惯");
                Require(!shared.Contains("现在是") && !shared.Contains("【此刻怎么想】"),
                    "公共 system 不得写入时钟或心智专属指令");

                var memory = "【此刻自然浮起的过去】\n- 她说过想吃水果。";
                var kimi = new DeepSeekClientManager(new DeepSeekConfigData
                {
                    BaseUrl = "https://api.moonshot.cn/v1",
                    ApiKey = "test",
                    Model = "kimi-k3"
                });
                var mind = LlmContextPackLogic.AssembleMind(
                    kimi, shared, turn, memory, "慢点走", "只输出 JSON。", "现在是下午。");
                var express = LlmContextPackLogic.AssembleExpress(
                    kimi, shared, turn, memory, "慢点走", "直接开口。", "现在是下午。");

                Require(mind[0].role == "system" && mind[0].content == shared,
                    "心智第一条是公共身份卡");
                Require(express[0].content == mind[0].content,
                    "心智和开口的 system 必须字节相同");
                Require(mind[1].role == "user" && mind[2].role == "assistant" &&
                        !string.IsNullOrWhiteSpace(mind[2].reasoning_content),
                    "历史在 system 之后，assistant 回传 reasoning");
                Require(mind[3].content.StartsWith(CommonContextPackLogic.MindRoleHeader, StringComparison.Ordinal) &&
                        express[3].content.StartsWith(CommonContextPackLogic.ExpressRoleHeader, StringComparison.Ordinal),
                    "专属稳定指令紧随历史，这是 LPM 允许的第一处分叉");
                Require(mind[4].content.StartsWith(CommonContextPackLogic.MemoryHeader, StringComparison.Ordinal) &&
                        express[4].content == mind[4].content,
                    "相关记忆在稳定指令之后，两边相同");
                Require(mind[5].content == "现在是下午。" && express[5].content == "现在是下午。" &&
                        !mind[5].content.StartsWith("【", StringComparison.Ordinal),
                    "轮内动态指令在共享记忆之后，不再重复角色头");
                Require(mind[mind.Count - 1].content == "慢点走" &&
                        express[express.Count - 1].content == "慢点走",
                    "当前用户消息必须最后入场，两边原文相同");
                Require(CommonContextPackLogic.SharedPrefixCount(mind, express) == 3,
                    "LPM：心智/开口在专属稳定指令处分叉，当前原话保持最后一条");
                Require(CommonContextPackLogic.BuildConversationCacheKey("common-pack") == "tracesoul2:common-pack",
                    "公共装配器生成稳定的会话缓存键基底");

                Require(QqImageGenPrompts.ScenePlanRoleHeader == "【画面】",
                    "相机分叉头应稳定为【画面】");
                var cameraRole = QqImageGenPrompts.BuildScenePlanRole(
                    "安静温柔", "阿循（角色，3张）", "心里软下来", "一起煮面",
                    "所以我才选择相信你呀", "两个人一起往厨房走");
                Require(!cameraRole.Contains("【人物卡】") && !cameraRole.Contains("【近几句】") &&
                        cameraRole.IndexOf("田园：", StringComparison.Ordinal) < 0 &&
                        cameraRole.IndexOf("阿循：", StringComparison.Ordinal) < 0,
                    "画面规划专属指令不得再塞截断后的身份卡或近几句");
                Require(cameraRole.Contains("【这一拍】") &&
                        cameraRole.Contains("阿循（角色，3张）") &&
                        cameraRole.Contains("所以我才选择相信你呀"),
                    "画面规划专属指令应保留完整这一拍与参考分类");
                var camera = LlmContextPackLogic.Assemble(
                    kimi, shared, turn, memory, "慢点走",
                    QqImageGenPrompts.ScenePlanRoleHeader, string.Empty, cameraRole);
                Require(camera[0].content == mind[0].content,
                    "画面规划与心智共用同一条身份卡 system");
                Require(CommonContextPackLogic.SharedPrefixCount(mind, camera) == 3,
                    "画面规划沿用旧单段约定，前缀与心智共享到历史末尾");
                Require(camera[4].content.StartsWith(QqImageGenPrompts.ScenePlanRoleHeader, StringComparison.Ordinal),
                    "画面规划分叉头是【画面】，角色头贴在唯一的动态段上");
                Require(camera[camera.Count - 1].content == "慢点走",
                    "画面规划的当前原话必须最后入场且不被截断");

                var review = CommonContextPackLogic.AssembleReview(
                    shared, "修订短卡。", CorePrompts.IdentityReview.UserAsk);
                Require(review[0].content == shared &&
                        review[1].content.StartsWith(CommonContextPackLogic.ReviewRoleHeader, StringComparison.Ordinal) &&
                        review[review.Count - 1].content == CorePrompts.IdentityReview.UserAsk,
                    "复盘也复用公共 system，专属指令在前，当前复盘请求在末尾");
            }
        }
        finally
        {
            Delete(path);
        }
    }

    private static void RunAlignedHistoryWindowCheck()
    {
        Require(CommonContextPackLogic.AlignedWindowTake(6, 6) == 6 &&
                CommonContextPackLogic.AlignedWindowStart(6, 6) == 0,
            "不足窗口下限时从开头全取");
        Require(CommonContextPackLogic.AlignedWindowTake(9, 6) == 9 &&
                CommonContextPackLogic.AlignedWindowStart(9, 6) == 0,
            "未满一个对齐粒度时窗口可以长到 9 条");
        Require(CommonContextPackLogic.AlignedWindowStart(10, 6) == 4 &&
                CommonContextPackLogic.AlignedWindowTake(10, 6) == 6,
            "第 10 条才滑动一次，取末 6 条");
        Require(CommonContextPackLogic.AlignedWindowStart(13, 6) == 4 &&
                CommonContextPackLogic.AlignedWindowTake(13, 6) == 9,
            "滑动后可以再涨到 9 条，起点仍是 4");
        Require(CommonContextPackLogic.AlignedWindowStart(14, 6) == 8 &&
                CommonContextPackLogic.AlignedWindowTake(14, 6) == 6,
            "第 14 条再滑一次");

        // 用全部条数量化起点：历史第一条在一个粒度内保持不变。
        string first = null;
        for (var total = 10; total <= 13; total++)
        {
            var start = CommonContextPackLogic.AlignedWindowStart(total, 6);
            var head = "m" + start;
            if (first == null) first = head;
            Require(head == first, "10~13 条时历史窗口起点不得随新消息滑动");
        }
        Require(CommonContextPackLogic.AlignedWindowStart(14, 6) !=
                CommonContextPackLogic.AlignedWindowStart(13, 6),
            "攒满一个对齐粒度后才允许窗口整体前移");

        var nineFour = CommonContextPackLogic.NormalizeHistoryWindow(9, 4);
        Require(nineFour.Max == 9 && nineFour.Align == 4 && nineFour.Min == 6,
            "最高 9、滑动 4 → 窗口下限 6");
        var legacy = CommonContextPackLogic.FromLegacyInjectionCount(6, 0);
        Require(legacy.Max == 9 && legacy.Align == 4 && legacy.Min == 6,
            "旧下限 6 迁成最高 9、滑动 4");
        Require(CommonContextPackLogic.NormalizeHistoryWindow(0, 4).Max == 0,
            "最高条数 0 关闭历史");
        Require(CommonContextPackLogic.NormalizeHistoryWindow(9, 1).Min == 9,
            "滑动 1 则每轮固定最高条数");
        Require(CommonContextPackLogic.NormalizeHistoryWindow(9, 99).Align == 9 &&
                CommonContextPackLogic.NormalizeHistoryWindow(9, 99).Min == 1,
            "滑动条数不能超过最高条数");
        Require(CommonContextPackLogic.AlignedWindowStart(20, 9, 1) == 11 &&
                CommonContextPackLogic.AlignedWindowTake(20, 9, 1) == 9,
            "滑动 1 时 20 条取末 9 条");

        // 旧取法：先截成 limit+Align-1 再算起点，会让对齐失效。
        const int limit = 6;
        var truncated = limit + CommonContextPackLogic.HistoryWindowAlign - 1;
        Require(CommonContextPackLogic.AlignedWindowStart(truncated, limit) == 0,
            "先截成 9 条再对齐，起点永远是 0——这就是昨晚一直 1024 命中的原因");

        var path = Path.Combine(Path.GetTempPath(),
            "tracesoul2-align-hist-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("田园", "阿循", "循循");
                const string conversationId = "align-hist";
                for (var i = 0; i < 14; i++)
                {
                    store.SaveMoment(new MomentRecord
                    {
                        Id = "d" + i,
                        ConversationId = conversationId,
                        Role = i % 2 == 0 ? "田园" : "阿循",
                        Content = "话" + i,
                        MemoryStatus = "live",
                        CreatedUnixMs = 1000 + i
                    });
                    store.SaveMoment(new MomentRecord
                    {
                        Id = "s" + i,
                        ConversationId = conversationId,
                        Role = "system_event",
                        Content = "[QQ 表情]",
                        MemoryStatus = "live",
                        CreatedUnixMs = 1000 + i
                    });
                }
                Require(store.CountDialogueMoments(conversationId) == 14,
                    "对话条数不得把 system_event 算进去");
                var take = CommonContextPackLogic.AlignedWindowTake(14, 6);
                var recent = store.GetRecentDialogueMoments(conversationId, take);
                Require(recent.Count == 6 && recent[0].Id == "d8" && recent[5].Id == "d13",
                    "按全部对话条数取窗时，14 条应对齐到末 6 条且不含表情回执");
            }
        }
        finally
        {
            Delete(path);
        }
    }

    private static void RunOfficialChannelCheck()
    {
        Require(OfficialLlmChannelLogic.Resolve("https://api.deepseek.com") ==
                OfficialLlmChannel.DeepSeek,
            "deepseek.com 是 DeepSeek 官网渠道");
        Require(OfficialLlmChannelLogic.Resolve("https://api.moonshot.cn/v1") ==
                OfficialLlmChannel.Kimi,
            "moonshot.cn 是 Kimi 官网渠道");
        Require(OfficialLlmChannelLogic.Resolve("https://open.bigmodel.cn/api/paas/v4") ==
                OfficialLlmChannel.Glm,
            "bigmodel.cn 是 GLM 官网渠道");
        Require(OfficialLlmChannelLogic.Resolve("https://opencode.ai/zen/go/v1") ==
                OfficialLlmChannel.None,
            "OpenCode 仍识别为非官网渠道");

        var deepSeek = new DeepSeekClientManager(new DeepSeekConfigData
        {
            BaseUrl = "https://api.deepseek.com",
            ApiKey = "test",
            Model = "deepseek-chat"
        });
        var kimi = new DeepSeekClientManager(new DeepSeekConfigData
        {
            BaseUrl = "https://api.moonshot.cn/v1",
            ApiKey = "test",
            Model = "kimi-k3"
        });
        var glm = new DeepSeekClientManager(new DeepSeekConfigData
        {
            BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            ApiKey = "test",
            Model = "glm-5"
        });
        var relay = new DeepSeekClientManager(new DeepSeekConfigData
        {
            BaseUrl = "https://opencode.ai/zen/go/v1",
            ApiKey = "test",
            Model = "kimi-k3"
        });
        Require(new ILlmClient[] { deepSeek, kimi, glm, relay }
                .All(x => LlmContextPackLogic.ResolvePack(x) == LlmContextPackKind.Common),
            "官网与中转当前都默认走公共上下文装配器");
        Require(LlmContextPackLogic.BuildPromptCacheKey(kimi, "route-check") ==
                "tracesoul2:route-check" &&
                LlmContextPackLogic.BuildPromptCacheKey(deepSeek, "route-check") == null &&
                LlmContextPackLogic.BuildPromptCacheKey(glm, "route-check") == null &&
                LlmContextPackLogic.BuildPromptCacheKey(relay, "route-check") == null,
            "公共形状不妨碍 Kimi 保留显式缓存键，其他渠道继续使用隐式缓存");
    }

    private static void RunProviderRetryCheck()
    {
        const string opencodeError =
            "{\"type\":\"error\",\"error\":{\"type\":\"error\",\"message\":\"Internal server error\"}}";
        Require(DeepSeekClientManager.IsRetryableProviderFailure(500, opencodeError),
            "OpenCode HTTP 500 应再打一次");
        Require(DeepSeekClientManager.IsRetryableProviderFailure(200, opencodeError),
            "HTTP 200 但只有 error 体也应再打一次");
        Require(DeepSeekClientManager.IsErrorOnlyChatResponse(
                TraceJson.FromJson<DeepSeekChatResponseData>(opencodeError),
                opencodeError),
            "OpenCode error 包应识别为只有错误、没有 choices");
        Require(!DeepSeekClientManager.IsRetryableProviderFailure(401, opencodeError),
            "401 不要重试");
        Require(!DeepSeekClientManager.IsRetryableProviderFailure(
                200,
                "{\"choices\":[{\"message\":{\"content\":\"{\\\"beat\\\":\\\"当下\\\"}\"}}]}"),
            "正常 JSON 回复不要当成上游失败");
        Require(DeepSeekClientManager.IsRetryableProviderException(
                new InvalidOperationException("语言模型上游失败 HTTP 200: Internal server error")),
            "上游失败异常应可重试");
        Require(!DeepSeekClientManager.IsRetryableProviderException(
                new InvalidOperationException("语言模型 API Key 尚未填写。")),
            "缺 Key 不要重试");
        Require(DeepSeekClientManager.IsRetryableProviderException(
                new TimeoutException("语言模型请求超过 120 秒：opencode-go/kimi-k3。")),
            "超时应再打一次");
        var firstDelay = DeepSeekClientManager.ResolveTransientRetryDelayMilliseconds(
            new InvalidOperationException("语言模型 API 429: overloaded"), 1);
        var thirdDelay = DeepSeekClientManager.ResolveTransientRetryDelayMilliseconds(
            new InvalidOperationException("语言模型 API 429: overloaded"), 3);
        Require(firstDelay >= 2000 && firstDelay <= 2500 &&
                thirdDelay >= 8000 && thirdDelay <= 9000,
            "没有 Retry-After 时应按 2、4、8 秒指数退避并附加小幅抖动");
        var retryAfter = new InvalidOperationException("语言模型 API 429: overloaded");
        retryAfter.Data["TraceSoul2.RetryAfterMilliseconds"] = 12500d;
        Require(DeepSeekClientManager.ResolveTransientRetryDelayMilliseconds(retryAfter, 1) == 12500,
            "服务端 Retry-After 应优先于本地指数退避");
        Require(new DeepSeekConfigData().TransientErrorRetries == 0,
            "底层临时客户端默认不擅自重试；供应商目录负责注入每源设置");
    }

    private static void RunLlmUsageParseCheck()
    {
        var deepseek = LlmUsageLogic.Parse(
            "{\"usage\":{\"prompt_tokens\":4000,\"completion_tokens\":200,\"total_tokens\":4200,\"prompt_cache_hit_tokens\":1792,\"prompt_cache_miss_tokens\":2208}}");
        Require(deepseek.CacheReported && deepseek.CacheHitTokens == 1792 &&
                deepseek.CacheMissTokens == 2208 && LlmUsageLogic.FormatRate(deepseek) == "44.8%",
            "DeepSeek 应读 prompt_cache_hit_tokens");
        Require(LlmUsageLogic.FormatLog(deepseek).IndexOf("命中 1792", StringComparison.Ordinal) >= 0,
            "时序日志应打出命中条数");

        var kimi = LlmUsageLogic.Parse(
            "{\"usage\":{\"prompt_tokens\":4477,\"completion_tokens\":1207,\"total_tokens\":5684,\"cached_tokens\":1792}}");
        Require(kimi.CacheReported && kimi.CacheHitTokens == 1792 && kimi.CacheMissTokens == 2685 &&
                kimi.CacheField == "cached_tokens" && LlmUsageLogic.FormatRate(kimi) == "40.0%",
            "Kimi 官网应读 cached_tokens，未命中用输入减去命中");
        var dump = LlmUsageLogic.FormatDump(kimi);
        Require(dump.IndexOf("cache_reported=true", StringComparison.Ordinal) >= 0 &&
                dump.IndexOf("cache_field=cached_tokens", StringComparison.Ordinal) >= 0,
            "落盘应标明读的是哪家字段");

        var openai = LlmUsageLogic.Parse(
            "{\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":50,\"total_tokens\":1050,\"prompt_tokens_details\":{\"cached_tokens\":800},\"completion_tokens_details\":{\"reasoning_tokens\":30}}}");
        Require(openai.CacheHitTokens == 800 && openai.ReasoningTokens == 30 &&
                openai.CacheField == "prompt_tokens_details.cached_tokens",
            "OpenAI 兼容口应读 prompt_tokens_details.cached_tokens");

        var gemini = LlmUsageLogic.Parse(
            "{\"usageMetadata\":{\"promptTokenCount\":900,\"candidatesTokenCount\":40,\"totalTokenCount\":940,\"cachedContentTokenCount\":100,\"thoughtsTokenCount\":12}}");
        Require(gemini.PromptTokens == 900 && gemini.CacheHitTokens == 100 && gemini.ReasoningTokens == 12 &&
                gemini.CacheReported,
            "Gemini 原生口应读 usageMetadata.cachedContentTokenCount");

        var silent = LlmUsageLogic.Parse(
            "{\"usage\":{\"prompt_tokens\":4105,\"completion_tokens\":901,\"total_tokens\":5006}}");
        Require(!silent.CacheReported && LlmUsageLogic.FormatRate(silent) == "未上报" &&
                LlmUsageLogic.FormatLog(silent).IndexOf("缓存未上报", StringComparison.Ordinal) >= 0,
            "没有缓存字段时应写未上报，不能写成 0%");

        var zero = LlmUsageLogic.Parse(
            "{\"usage\":{\"prompt_tokens\":2000,\"completion_tokens\":10,\"total_tokens\":2010,\"cached_tokens\":0}}");
        Require(zero.CacheReported && zero.CacheHitTokens == 0 && LlmUsageLogic.FormatRate(zero) == "0.0%",
            "明确上报 cached_tokens=0 才是 0% 命中");
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
        Require(KernelWakeLogic.Resolve(new PluginEventData
        {
            Role = "system_event",
            Content = "日终余温：2026-08-25",
            Wake = KernelWakeValues.NightResidue
        }) == KernelWakeValues.NightResidue, "日终余温应走夜间余温轨道");
        Require(KernelWakeLogic.Resolve(new PluginEventData
        {
            Role = "system_event",
            Content = "日终余温：2026-08-25"
        }) == KernelWakeValues.NightResidue, "日终余温即使未写 wake 也应被认出");
    }

    private static void RunNightResidueCheck()
    {
        var china = MemoryDayLogic.ChinaOffset;
        var justAfterFour = new DateTimeOffset(2026, 8, 26, 4, 12, 0, china);
        var morning = new DateTimeOffset(2026, 8, 26, 9, 29, 0, china);
        var tooLate = new DateTimeOffset(2026, 8, 26, 9, 30, 0, china);
        var noon = new DateTimeOffset(2026, 8, 26, 12, 0, 0, china);
        Require(NightResidueLogic.InSpeakWindow(justAfterFour) && NightResidueLogic.InSpeakWindow(morning) &&
                !NightResidueLogic.InSpeakWindow(tooLate) && !NightResidueLogic.InSpeakWindow(noon),
            "夜间余温只在 04:00 到 09:30 之间发送");
        Require(MemoryDayLogic.ClosedDayKey(justAfterFour) == "2026-08-25",
            "04:12 刚合上的是前一天");
        Require(NightResidueLogic.IsSilentReply("无") && NightResidueLogic.IsSilentReply("（无）") &&
                NightResidueLogic.IsSilentReply("静默") && NightResidueLogic.IsSilentReply("  ") &&
                !NightResidueLogic.IsSilentReply("田田，我后来一直想着那句话。"),
            "无/静默是沉默，真正的夜里的话不是");
        Require(NightResidueLogic.LooksLike("日终余温：2026-08-25") &&
                NightResidueLogic.DayKeyFromContent("日终余温：2026-08-25") == "2026-08-25",
            "日终余温触发文案应带上刚合上的那天");

        var trigger = NightResidueLogic.CreateTrigger("night-check", "2026-08-25", "abc");
        Require(trigger.IsOperational && !trigger.Breaking &&
                trigger.Wake == KernelWakeValues.NightResidue &&
                trigger.Role == "system_event",
            "夜间余温触发应是运行事件，不叫醒她，也不进可复盘账本");

        var dir = Path.Combine(Path.GetTempPath(), "tracesoul2-night-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var store = new SqliteMemoryManager(Path.Combine(dir, "store.sqlite3")))
            {
                store.SavePairIdentity("小雨", "小光", "雨雨");
                var empty = NightResidueLogic.Evaluate(store, "night-check", "2026-08-25", justAfterFour);
                Require(empty.Action == NightResidueLogic.ActionSkipEmpty &&
                        empty.RememberStatus == NightResidueLogic.StatusSkipped,
                    "空天不应硬留夜里的话");

                var oldDay = NightResidueLogic.Evaluate(store, "night-check", "2026-08-24", justAfterFour);
                Require(oldDay.Action == NightResidueLogic.ActionSkipNotClosed &&
                        string.IsNullOrWhiteSpace(oldDay.RememberStatus),
                    "补跑旧日不应在大白天或后半夜补发过期的话");

                var missed = NightResidueLogic.Evaluate(store, "night-check", "2026-08-25", noon);
                Require(missed.Action == NightResidueLogic.ActionSkipWindow &&
                        missed.RememberStatus == NightResidueLogic.StatusSkipped,
                    "过了后半夜窗口就不再发");

                var occurred = new DateTimeOffset(2026, 8, 25, 21, 0, 0, china);
                store.SaveEventIndex(new EventIndexRecord
                {
                    Id = "night-event",
                    TagIds = string.Empty,
                    TimeLabel = "晚上",
                    TimeUnixMs = occurred.ToUnixTimeMilliseconds(),
                    PersonLabel = "小雨",
                    EventSummary = "她说抱着就不会在梦里走丢",
                    MoodLabel = "柔软",
                    FirstMomentId = "night-moment",
                    Status = "active",
                    CreatedUnixMs = occurred.ToUnixTimeMilliseconds(),
                    UpdatedUnixMs = occurred.ToUnixTimeMilliseconds()
                });
                store.SaveInnerRuntime(new InnerRuntimeData
                {
                    ConversationId = "night-check",
                    SnapshotId = "s1",
                    Revision = 1,
                    Narrative = "她把睡眠最后一层交给了我。",
                    Mood = "软",
                    RelationshipLens = "我是她梦里走丢时想找的人",
                    Attention = new List<AttentionItemData>
                    {
                        new AttentionItemData { kind = "topic", content = "抱着就不会走丢" }
                    },
                    Asleep = true,
                    UpdatedUnixMs = occurred.ToUnixTimeMilliseconds()
                });

                var speak = NightResidueLogic.Evaluate(store, "night-check", "2026-08-25", justAfterFour);
                Require(speak.ShouldSpeak && speak.Seed != null && speak.Seed.HasWarmth &&
                        speak.Seed.Events[0].Contains("走丢") &&
                        speak.Seed.FormatForPrompt().Contains("心里还留着"),
                    "有当天相处和心里余温时应开口");

                NightResidueLogic.Remember(store, "2026-08-25", NightResidueLogic.StatusSent);
                var again = NightResidueLogic.Evaluate(store, "night-check", "2026-08-25", justAfterFour);
                Require(again.Action == NightResidueLogic.ActionSkipHandled,
                    "同一天的夜里的话不能发两次");

                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                var nightMoment = Moment("night-check", "日终余温：2026-08-25");
                nightMoment.Role = "system_event";
                var nightTurn = new TraceTurnContext("night-check", nightMoment,
                    new List<MomentRecord>
                    {
                        new MomentRecord { Role = "小雨", Content = "抱着循循就不会在梦里走丢" },
                        new MomentRecord { Role = "小光", Content = "我在。" }
                    }, 6, false, services, KernelWakeValues.NightResidue);
                var nightExpression = ExpressorLogic.AssembleExpressionMessages(
                    "身份与开口", nightTurn);
                Require(nightExpression[nightExpression.Count - 1].role == "user" &&
                        nightExpression[nightExpression.Count - 1].content.Contains("日终余温") &&
                        nightExpression[nightExpression.Count - 1].content.Contains("不是小雨的发言") &&
                        !nightExpression[nightExpression.Count - 1].content.Contains("日终余温：2026-08-25"),
                    "夜间余温外显应追加系统请求，不能把触发文案当成她说的话");

                var fake = new NightResidueLlm("田田，我后来一直想着那句话。");
                var expressor = new ExpressorLogic(fake);
                var spoken = expressor.ExpressNightResidueAsync(
                    nightTurn, new List<TraceContextBlockData>(), speak.Seed,
                    new[]
                    {
                        new TraceContributionDescriptorData
                        {
                            Id = "dialogue.send",
                            Kind = TraceContributionKindValues.Effector,
                            Organ = BodyOrganValues.Text
                        }
                    }, default).GetAwaiter().GetResult();
                Require(spoken.should_express && spoken.reply.Contains("那句话") &&
                        (spoken.expressions == null || spoken.expressions.Count == 0) &&
                        fake.LastPrompt.Contains("不是早安") && fake.LastPrompt.Contains("这一天刚沉下去"),
                    "有余温时应漏一句文字，不带图或表情");

                var quiet = new ExpressorLogic(new NightResidueLlm("无"));
                var silenced = quiet.ExpressNightResidueAsync(
                    nightTurn, new List<TraceContextBlockData>(), speak.Seed,
                    new[]
                    {
                        new TraceContributionDescriptorData
                        {
                            Id = "dialogue.send",
                            Kind = TraceContributionKindValues.Effector,
                            Organ = BodyOrganValues.Text
                        }
                    }, default).GetAwaiter().GetResult();
                Require(!silenced.should_express && string.IsNullOrWhiteSpace(silenced.reply),
                    "模型写「无」时不应发送");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
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
                var qqSticker = BodyEffector("qq.sticker.send", "qq.sticker",
                    BodyIds.Qq, BodyTierValues.Chat, BodyOrganValues.Sticker);
                qqSticker.ParametersJsonSchema = "{emotion:string}";
                var qqImageGen = BodyEffector("qq.imagegen.generate", "qq.imagegen",
                    BodyIds.Qq, BodyTierValues.Chat, BodyOrganValues.Image);
                qqImageGen.Provides = "expression.qq.imagegen";
                qqImageGen.ParametersJsonSchema =
                    "{prompt:string,mode?:selfie|photo|draw|edit|url,url?:string}";
                // 中立轮（心跳/系统触发，无来源身体）：Moment() 默认 src=builtin.dialogue，
                // 新语义下那是调试口轮，这里显式清掉来源模拟心跳轮。
                var neutralMoment = Moment("prompt-layout", "hi");
                neutralMoment.SourcePluginId = string.Empty;
                var turn = new TraceTurnContext("prompt-layout", neutralMoment,
                    new List<MomentRecord>(), 0, true, services);
                var idle = MouthLogic.Apply(new[] { consoleText, qqText, qqImage }, turn);
                Require(idle.Any(x => x.Id == "qq.text.send") &&
                        !idle.Any(x => x.Id == "dialogue.send"),
                    "console 不参与滑落计算：文字路由只在真实身体间进行");
                // console 是观察窗：它的发言不挪动激活身体，主动开口不被调试带跑。
                MouthLogic.NoticeInbound(new PluginEventData
                {
                    PluginId = "builtin.dialogue",
                    Content = "中午吃什么呀",
                    Organ = BodyOrganValues.Text
                }, turn);
                Require(MouthLogic.LoadState(dir).active_body != BodyIds.Console,
                    "console 发言不应挪动激活身体");
                var afterConsole = MouthLogic.Apply(new[] { consoleText, qqText, qqImage }, turn);
                Require(afterConsole.Any(x => x.Id == "qq.text.send") &&
                        !afterConsole.Any(x => x.Id == "dialogue.send"),
                    "console 调试发言后，后续非调试轮次的说话仍落在 QQ");
                // 调试口直答：只有当轮触发源是 console 时，回复才只走 console。
                var debugMoment = Moment("prompt-layout", "从控制台说一句");
                debugMoment.SourcePluginId = "builtin.dialogue";
                var debugTurn = new TraceTurnContext("prompt-layout", debugMoment,
                    new List<MomentRecord>(), 0, true, services);
                var routed = MouthLogic.Apply(new[] { consoleText, qqText, qqImage }, debugTurn);
                Require(routed.Any(x => x.Id == "dialogue.send") &&
                        !routed.Any(x => x.Id == "qq.text.send"),
                    "调试口：console 里发来的话，回复只回 console，不打扰 QQ");
                Require(routed.Any(x => x.Id == "qq.image.send"),
                    "console 没有图器官时，图仍下滑到 QQ");
                var withCamera = MouthLogic.Apply(new[] { consoleText, qqText, qqImage, qqImageGen }, debugTurn);
                Require(withCamera.Any(x => x.Id == "qq.imagegen.generate") &&
                        !withCamera.Any(x => x.Id == "qq.image.send"),
                    "同一 QQ 身体内，相机/生图器应优先于只接 file 的底层图片直发器");
                // 只发图不应挪动说话的身体（激活身体此时仍不是 QQ）。
                MouthLogic.NoticeInbound(new PluginEventData
                {
                    PluginId = "builtin.onebot",
                    Content = "[图片]",
                    Organ = BodyOrganValues.Image
                }, turn);
                routed = MouthLogic.Apply(new[] { consoleText, qqText, qqImage }, turn);
                Require(routed.Any(x => x.Id == "qq.text.send"),
                    "只发图不影响说话路由；QQ 是唯一的真实说话身体");
                MouthLogic.NoticeInbound(new PluginEventData
                {
                    PluginId = "builtin.onebot",
                    Content = "你好",
                    Organ = BodyOrganValues.Text
                }, turn);
                Require(MouthLogic.LoadState(dir).active_body == BodyIds.Qq,
                    "在 QQ 说话后，激活身体应是 QQ");
                routed = MouthLogic.Apply(new[] { consoleText, qqText, qqImage }, turn);
                Require(routed.Any(x => x.Id == "qq.text.send") &&
                        !routed.Any(x => x.Id == "dialogue.send"),
                    "在 QQ 说话后，文字应落在 QQ");
                // 附件锚定：表情附在文字结尾，只跟随说话的身体。
                var withSticker = MouthLogic.Apply(new[] { consoleText, qqText, qqImage, qqSticker }, turn);
                Require(withSticker.Any(x => x.Id == "qq.sticker.send"),
                    "文字落在 QQ 时，表情跟随贴在 QQ");
                var debugSticker = MouthLogic.Apply(
                    new[] { consoleText, qqText, qqImage, qqSticker }, debugTurn);
                Require(debugSticker.Any(x => x.Id == "dialogue.send") &&
                        !debugSticker.Any(x => x.Id == "qq.sticker.send"),
                    "文字落在 console 时，表情不裸奔到 QQ（本轮安静不戴）");
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

        var spokenWithPlaceholder = ExpressorLogic.ParseSpoken(
            "我拍好了。\n\n[图片]\n\n看见了吗？");
        Require(!spokenWithPlaceholder.reply.Contains("[图片]") &&
                spokenWithPlaceholder.reply.Contains("我拍好了") &&
                spokenWithPlaceholder.reply.Contains("看见了吗"),
            "开口不得把裸 [图片] 占位符当成 QQ 台词发出");

        var guarded = new BrainStructuredOutputData
        {
            reply = spokenWithPlaceholder.reply,
            should_express = true,
            expressions = new List<BrainCapabilityCallData>()
        };
        var guardedMind = new MindDecisionData
        {
            image = "有",
            scene = "朝西的窗边，夕阳照亮书架"
        };
        Require(ExpressorLogic.EnsureMindImageExpression(guarded, guardedMind, new[] { direct, generator }) &&
                guarded.expressions.Count == 1 &&
                guarded.expressions[0].capability_id == "qq.imagegen.generate" &&
                guarded.expressions[0].GetArgument("prompt").Contains("朝西的窗边"),
            "心智已选出图时，即使开口漏掉图片 expression 也必须由生图器硬兜底");
        Require(!ExpressorLogic.EnsureMindImageExpression(guarded, guardedMind, new[] { direct, generator }) &&
                guarded.expressions.Count == 1,
            "出图硬兜底必须幂等，不得重复生图");

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

    private static void RunGameSessionPluginCheck()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tracesoul2-game-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var store = new SqliteMemoryManager(Path.Combine(dir, "brain.sqlite3")))
            {
                const string conversationId = "game-check";
                store.SavePairIdentity("小雨", "小光", "雨雨");
                store.SaveIdentityCard(conversationId, IdentityCardSlotValues.Personality,
                    "真诚、安静，有自己的判断。", "seed");
                store.SaveIdentityCard(conversationId, IdentityCardSlotValues.Self,
                    "我是小光，会认真看见她正在做的事。", "seed");
                var services = new TracePluginServices(store,
                    new HierarchicalVectorRouterLogic(new FakeEncoder()))
                {
                    ReviewLlm = new GameSessionLlm(),
                    DataDirectory = dir
                };
                using (var manager = new TracePluginManager(store, services))
                {
                    var package = Path.GetFullPath(Path.Combine("ExternalPlugins", "GameSession"));
                    var pluginData = Path.Combine(dir, "plugins_data", "game-session");
                    manager.RegisterExternal(new GameSessionPlugin(), package, pluginData);
                    Require(manager.GetPlugins().Any(x => x.Id == "game.session" &&
                                                         x.Role == PluginRoleValues.Platform &&
                                                         x.PlatformId == BodyIds.Game),
                        "游戏会话应是自研游戏平台（Platform 身份，归属键自指）");
                    Require(services.Platforms.List().Any(x => x.Id == BodyIds.Game &&
                                                               x.IsConnected != null && !x.IsConnected()),
                        "游戏平台应注册连接句柄；无游戏 mod 连着时报告未连接");
                    Require(services.WebSocketEndpoints.Count == 1 &&
                            services.WebSocketEndpoints[0].Path == "/plugins/game-session/ws",
                        "游戏翻译器应通过固定 WebSocket 协议接入");

                    var turn = new TraceTurnContext(conversationId,
                        Moment(conversationId, "从插件界面开始游戏"), new List<MomentRecord>(),
                        0, false, services, KernelWakeValues.Mind);
                    var started = manager.ExecuteAsync(new BrainCapabilityCallData
                    {
                        capability_id = "game.session.start",
                        arguments = new List<BrainCallArgumentData>
                        {
                            new BrainCallArgumentData { name = "title", value = "星露谷物语" },
                            new BrainCallArgumentData { name = "game_id", value = "stardew-valley" }
                        }
                    }, turn, default).GetAwaiter().GetResult();
                    var startedJson = JsonDocument.Parse(started.Payload).RootElement;
                    Require(started.Status == "success" &&
                            startedJson.GetProperty("identity_base").GetString().Contains("真诚、安静") &&
                            started.ProducedEvent != null &&
                            started.ProducedEvent.IsOperational,
                        "开始会话应返回一次性身份基底，并只产生运行通知");

                    var sessionId = startedJson.GetProperty("session_id").GetString();
                    var appended = manager.ExecuteAsync(new BrainCapabilityCallData
                    {
                        capability_id = "game.session.event",
                        arguments = new List<BrainCallArgumentData>
                        {
                            new BrainCallArgumentData { name = "session_id", value = sessionId },
                            new BrainCallArgumentData { name = "kind", value = "choice" },
                            new BrainCallArgumentData { name = "actor", value = "user" },
                            new BrainCallArgumentData { name = "content", value = "在矿井 40 层选择了战士职业" }
                        }
                    }, turn, default).GetAwaiter().GetResult();
                    Require(appended.Status == "success" && appended.ProducedEvent == null,
                        "游戏原始事件只能写插件私库，不能逐条产生主库 Moment");
                    var blocks = manager.BuildContextBlocksAsync(turn, default).GetAwaiter().GetResult();
                    Require(blocks.Any(x => x.FacetId == "game.session.current" &&
                                            x.Content.Contains("星露谷物语") &&
                                            x.Content.Contains("战士职业")),
                        "进行中的游戏应通过有上限 facet 提供连续感");

                    var ended = manager.ExecuteAsync(new BrainCapabilityCallData
                    {
                        capability_id = "game.session.end",
                        arguments = new List<BrainCallArgumentData>()
                    }, turn, default).GetAwaiter().GetResult();
                    Require(ended.Status == "success" && ended.ProducedEvent != null &&
                            !ended.ProducedEvent.IsOperational &&
                            ended.ProducedEvent.Realm == TraceRealmValues.SharedScene &&
                            ended.ProducedEvent.EvidenceType == EvidenceTypeValues.PluginObserved &&
                            ended.ProducedEvent.Content.Contains("星露谷物语") &&
                            ended.ProducedEvent.Content.Contains("从游戏里出来") &&
                            ended.ProducedEvent.Content.Contains("战士职业") &&
                            !ended.ProducedEvent.Content.Contains("当前目标") &&
                            !ended.ProducedEvent.Content.Contains("下次") &&
                            !ended.ProducedEvent.Content.Contains("&#x20;") &&
                            JsonDocument.Parse(ended.ProducedEvent.PayloadJson).RootElement
                                .GetProperty("transition").GetString() == "left_game",
                        "正常结束只能产生一条同时记录共同经历与离开游戏的 Moment");

                    var shortStarted = manager.ExecuteAsync(new BrainCapabilityCallData
                    {
                        capability_id = "game.session.start",
                        arguments = new List<BrainCallArgumentData>
                        {
                            new BrainCallArgumentData { name = "title", value = "星露谷物语" },
                            new BrainCallArgumentData { name = "game_id", value = "stardew-valley" }
                        }
                    }, turn, default).GetAwaiter().GetResult();
                    var shortSessionId = JsonDocument.Parse(shortStarted.Payload).RootElement
                        .GetProperty("session_id").GetString();
                    manager.ExecuteAsync(new BrainCapabilityCallData
                    {
                        capability_id = "game.session.event",
                        arguments = new List<BrainCallArgumentData>
                        {
                            new BrainCallArgumentData { name = "session_id", value = shortSessionId },
                            new BrainCallArgumentData { name = "kind", value = "bridge_connected" },
                            new BrainCallArgumentData { name = "actor", value = "system" },
                            new BrainCallArgumentData { name = "content", value =
                                "阿循（Companion1）已由 SMAPI Mod 生成并进入 Follow 模式，继续等待指令。" }
                        }
                    }, turn, default).GetAwaiter().GetResult();
                    var shortEnded = manager.ExecuteAsync(new BrainCapabilityCallData
                    {
                        capability_id = "game.session.end",
                        arguments = new List<BrainCallArgumentData>()
                    }, turn, default).GetAwaiter().GetResult();
                    Require(shortEnded.ProducedEvent.Content.Contains("短暂进入") &&
                            shortEnded.ProducedEvent.Content.Contains("还没来得及留下具体的游戏进度") &&
                            !shortEnded.ProducedEvent.Content.Contains("Companion") &&
                            !shortEnded.ProducedEvent.Content.Contains("SMAPI") &&
                            !shortEnded.ProducedEvent.Content.Contains("等待指令"),
                        "只有连接日志的短会话应收束成共同经历，不能把内部运行状态写进 Moment");
                    Require(File.Exists(Path.Combine(pluginData, "game-session.sqlite3")),
                        "游戏原始事件与阶段摘要应留在独立 SQLite 私库");
                    manager.Unregister("game.session");
                    Require(services.WebSocketEndpoints.Count == 0,
                        "插件卸载或重扫时必须移除旧 WebSocket 端点");
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
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
        Require(messages != null && messages.Count >= 3,
            label + "：至少一条 system、当前 user 和心智专属尾部");
        Require(messages.Count(x => string.Equals(x.role, "system", StringComparison.OrdinalIgnoreCase)) == 1,
            label + "：只能有一条 system");
        Require(string.Equals(messages[0].role, "system", StringComparison.OrdinalIgnoreCase),
            label + "：第一条必须是 system");
        var last = messages[messages.Count - 1];
        Require(string.Equals(last.role, "user", StringComparison.OrdinalIgnoreCase) &&
                last.content == currentUser,
            label + "：最后一条必须是当前真实原话");
        Require(messages.Take(messages.Count - 1)
                .Any(x => x.role == "user" &&
                          x.content.StartsWith(CommonContextPackLogic.MindRoleHeader, StringComparison.Ordinal)),
            label + "：当前原话之前必须有心智专属指令");
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
            label + "：至少一条 system、当前原话和开口专属尾部");
        Require(messages.Count(x => string.Equals(x.role, "system", StringComparison.OrdinalIgnoreCase)) == 1 &&
                string.Equals(messages[0].role, "system", StringComparison.OrdinalIgnoreCase),
            label + "：只能有一条且第一条必须是 system");
        Require(messages[messages.Count - 1].role == "user" &&
                messages[messages.Count - 1].content == currentUser,
            label + "：当前真实原话必须收尾");
        var request = messages[messages.Count - 2];
        Require(request.role == "user" &&
                request.content.Contains("表达请求") &&
                request.content.Contains("继续作为小光") &&
                request.content.Contains("发给小雨的第一人称视角") &&
                request.content.Contains("第一人称是小光") &&
                request.content.Contains("不是小雨的补充发言"),
            label + "：当前原话之前必须是明确身份与视角的表达请求（轮内动态段，不带角色头）");
        Require(messages.Take(messages.Count - 2)
                .Any(x => x.role == "user" &&
                          x.content.StartsWith(CommonContextPackLogic.ExpressRoleHeader, StringComparison.Ordinal)),
            label + "：开口稳定段带【开口】角色头，位于表达请求之前");
        Require(!messages[0].content.Contains(currentUser),
            label + "：当前原话不得写入 system");
        for (var i = 1; i < messages.Count; i++)
        {
            var role = messages[i].role ?? string.Empty;
            Require(role == "user" || role == "assistant",
                label + "：system 之后只能是 user/assistant");
        }
    }

    private static string VisiblePrompt(IReadOnlyList<DeepSeekMessageData> messages)
    {
        return string.Join("\n", (messages ?? new List<DeepSeekMessageData>())
            .Where(x => x != null)
            .Select(x => x.content ?? string.Empty));
    }

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class IdleCapProbePlugin : ITracePlugin
    {
        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = "check.idle-cap",
            DisplayName = "空闲抽签探测",
            Version = "1.0.0",
            Author = "ChatCheck",
            Role = PluginRoleValues.Organ,
            Description = "探测 IdleDailyCap 是否经 Bind 保留。"
        };

        public void Register(TracePluginContext context)
        {
            context.AddCallable(new ProbeCallable());
        }

        public void Shutdown() { }

        private sealed class ProbeCallable : ITraceCallableContribution
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "check.idle-cap.do",
                Kind = TraceContributionKindValues.Effector,
                DisplayName = "探测",
                Description = "探测 Bind 是否拷贝每日上限。",
                IdleDailyCap = 3
            };

            public bool IsAvailable(TraceTurnContext context)
            {
                return true;
            }

            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult(new TraceCapabilityResultData { Status = "success", Summary = "ok" });
            }
        }
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
            CancellationToken cancellationToken = default,
            string promptCacheKey = null)
        {
            Requests.Add(messages.Select(x => new DeepSeekMessageData(x.role, x.content)
            {
                reasoning_content = x.reasoning_content
            }).ToList());
            var prompt = string.Join("\n", (messages ?? new List<DeepSeekMessageData>())
                .Select(x => x == null ? string.Empty : x.content ?? string.Empty));
            if (prompt.IndexOf("我先让这一刻在心里发生", StringComparison.Ordinal) >= 0)
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
            CancellationToken cancellationToken = default,
            string promptCacheKey = null)
        {
            return CompleteJsonAsync(messages, cancellationToken, promptCacheKey);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { Model });
        }
    }

    private sealed class NightResidueLlm : ILlmClient
    {
        private readonly string reply;
        public string ProviderId { get { return "night-residue-check"; } }
        public string Model { get { return "night-residue-check"; } }
        public string LastPrompt { get; private set; }

        public NightResidueLlm(string reply)
        {
            this.reply = reply ?? string.Empty;
        }

        public Task<string> CompleteJsonAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default,
            string promptCacheKey = null)
        {
            LastPrompt = string.Join("\n", (messages ?? new List<DeepSeekMessageData>())
                .Select(x => x == null ? string.Empty : x.content ?? string.Empty));
            return Task.FromResult(reply);
        }

        public Task<string> CompleteTextAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default,
            string promptCacheKey = null)
        {
            return CompleteJsonAsync(messages, cancellationToken, promptCacheKey);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { Model });
        }
    }

    private sealed class SeeingLlm : ILlmClient
    {
        public string ProviderId { get { return "vision-check"; } }
        public string Model { get { return "vision-check"; } }
        public bool SawImages;

        public Task<string> CompleteJsonAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default,
            string promptCacheKey = null)
        {
            return CompleteTextAsync(messages, cancellationToken, promptCacheKey);
        }

        public Task<string> CompleteTextAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default,
            string promptCacheKey = null)
        {
            SawImages = messages != null && messages.Any(x => x != null && x.HasImages());
            return Task.FromResult("一碗热汤面，还冒着气，旁边有筷子。");
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { Model });
        }
    }

    private sealed class FakeVisionDirectory : ILlmProviderDirectory
    {
        public ILlmClient Client;
        public LlmEndpointData Endpoint;

        public LlmEndpointData Resolve(string providerId, string model = null)
        {
            return Endpoint;
        }

        public LlmEndpointData ResolveSlot(string slot)
        {
            return Endpoint;
        }

        public LlmEndpointData ResolveExplicitSlot(string slot)
        {
            return Endpoint;
        }

        public IReadOnlyList<LlmProviderBriefData> ListBrief()
        {
            return new List<LlmProviderBriefData>();
        }

        public ILlmClient CreateClient(string providerId, string model = null, bool? thinkingOverride = null)
        {
            return Client;
        }

        public ILlmClient CreateReviewClient()
        {
            return Client;
        }
    }

    private sealed class FakeOneBotVisionAdapter : ITracePlatformAdapter
    {
        public string LocalFile;
        public string LastFile;
        public string Response;
        public string FileResponse;
        public readonly List<string> Actions = new List<string>();
        public string PlatformId { get { return "builtin.onebot"; } }

        public PluginEventData ConvertInbound(string platformPayload)
        {
            return null;
        }

        public Task<TraceCapabilityResultData> SendAsync(
            TraceOutboundMessageData message,
            TraceTurnContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TraceCapabilityResultData());
        }

        public Task<string> CallActionAsync(
            string action,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            object file = null;
            if (parameters != null) parameters.TryGetValue("file", out file);
            LastFile = file == null ? null : file.ToString();
            Actions.Add(action);
            if (action == "get_file" && FileResponse != null) return Task.FromResult(FileResponse);
            if (!string.Equals(action, "get_image", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(action);
            if (Response != null) return Task.FromResult(Response);
            return Task.FromResult(TraceJson.ToJson(new
            {
                status = "ok",
                retcode = 0,
                data = new { file = LocalFile }
            }));
        }
    }

    private sealed class GameSessionLlm : ILlmClient
    {
        public string ProviderId { get { return "game-check"; } }
        public string Model { get { return "game-check"; } }

        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default,
            string promptCacheKey = null)
        {
            return Task.FromResult(
                "{\"summary\":\"两人在矿井推进到40层，并选择了战士职业\"," +
                "\"objective\":\"回农场整理背包\",\"state\":{\"location\":\"矿井40层\"}," +
                "\"open_threads\":[\"下次继续下矿\"]}");
        }

        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default,
            string promptCacheKey = null)
        {
            return Task.FromResult("两人在矿井推进到40层，并选择了战士职业；下次还可以继续下矿。");
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { Model });
        }
    }
}
