using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    /// <summary>外显：心智组织好之后，决定怎么开口。不调内部神经。</summary>
    public sealed class ExpressorLogic
    {
        private readonly ILlmClient llm;

        public ExpressorLogic(ILlmClient llm)
        {
            this.llm = llm ?? throw new ArgumentNullException("llm");
        }

        public async Task<BrainStructuredOutputData> ExpressAsync(
            TraceTurnContext turn,
            IEnumerable<TracePluginMetadataData> plugins,
            IEnumerable<TraceContributionDescriptorData> catalog,
            IEnumerable<TraceContextBlockData> contextBlocks,
            MindDecisionData mind,
            string memoryFlesh,
            bool waitOnly,
            string leaveResult,
            CancellationToken cancellationToken)
        {
            var needsReply = waitOnly || turn.RequiresExpression;
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", BuildFoundationPrompt(turn, contextBlocks, catalog)),
                new DeepSeekMessageData("system", BuildTurnPrompt(
                    turn, plugins, contextBlocks, mind, memoryFlesh, waitOnly, leaveResult)),
                new DeepSeekMessageData("user", turn.Moment.Content)
            };
            var expressed = await DeepSeekStructuredOutputLogic.CompleteAsync<ExpressorOutputData>(
                llm,
                messages,
                x => x != null && (!needsReply || !string.IsNullOrWhiteSpace(x.reply)),
                waitOnly ? "外显还没说出等一下。" : "外显完成时缺少 reply。",
                cancellationToken);
            return MapExpressor(expressed, catalog, needsReply);
        }

        public static BrainStructuredOutputData MapExpressor(
            ExpressorOutputData expressed,
            IEnumerable<TraceContributionDescriptorData> catalog,
            bool requiresExpression)
        {
            expressed = expressed ?? new ExpressorOutputData();
            var effectors = (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .Where(x => x != null && x.Kind == TraceContributionKindValues.Effector)
                .ToList();
            var output = new BrainStructuredOutputData
            {
                state = BrainStepStateValues.Finish,
                mode = BrainModeValues.Reflex,
                intent = string.Empty,
                decision_summary = string.Empty,
                calls = new List<BrainCapabilityCallData>(),
                should_express = requiresExpression || !string.IsNullOrWhiteSpace(expressed.reply),
                reply = (expressed.reply ?? string.Empty).Trim(),
                expressions = new List<BrainCapabilityCallData>(),
                facet_outputs = new List<BrainFacetOutputData>()
            };
            if (!string.IsNullOrWhiteSpace(expressed.mood))
            {
                output.facet_outputs.Add(new BrainFacetOutputData
                {
                    facet_id = "inner.snapshot",
                    changed = true,
                    summary = expressed.mood.Trim(),
                    fields = new List<BrainFacetFieldData>
                    {
                        new BrainFacetFieldData { name = "mood", value = Limit(expressed.mood.Trim(), 12) }
                    }
                });
            }
            AddExtra(output.expressions, effectors, BodyOrganValues.Sticker, "emotion", expressed.sticker);
            AddExtra(output.expressions, effectors, BodyOrganValues.Qzone, "content", expressed.qzone);
            AddExtra(output.expressions, effectors, BodyOrganValues.Voice, "text", expressed.voice);
            AddExtra(output.expressions, effectors, BodyOrganValues.Image, "prompt", expressed.image);
            return output;
        }

        private static void AddExtra(
            List<BrainCapabilityCallData> expressions,
            List<TraceContributionDescriptorData> effectors,
            string needle,
            string argumentName,
            string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return;
            var match = effectors.FirstOrDefault(x =>
                string.Equals(MouthLogic.OrganOf(x), needle, StringComparison.Ordinal) ||
                (x.Id ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (x.Provides ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match == null) return;
            expressions.Add(new BrainCapabilityCallData
            {
                call_id = "expr-" + needle,
                capability_id = match.Id,
                purpose = needle,
                arguments = new List<BrainCallArgumentData>
                {
                    new BrainCallArgumentData { name = argumentName, value = Limit(value, 3000) }
                }
            });
        }

        public static BrainStructuredOutputData NormalizeStep(
            BrainStructuredOutputData output,
            IEnumerable<TraceContributionDescriptorData> catalog,
            bool mustFinish,
            bool requiresExpression,
            string defaultExpressionCapabilityId = null)
        {
            if (output == null) throw new ArgumentNullException("output");
            var descriptors = (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>()).ToList();
            var callable = new HashSet<string>(descriptors
                .Where(x => x.Kind == TraceContributionKindValues.CallableNerve).Select(x => x.Id));
            var effectors = descriptors.Where(x => x.Kind == TraceContributionKindValues.Effector).ToList();
            var writableFacets = new HashSet<string>(descriptors
                .Where(x => x.Kind == TraceContributionKindValues.MountedFacet &&
                            !string.IsNullOrWhiteSpace(x.OutputJsonSchema)).Select(x => x.Id));

            output.state = (output.state ?? string.Empty).Trim().ToLowerInvariant();
            output.mode = (output.mode ?? string.Empty).Trim().ToLowerInvariant();
            if (!BrainModeValues.IsKnown(output.mode)) output.mode = BrainModeValues.Focused;
            output.intent = Limit((output.intent ?? string.Empty).Trim(), 160);
            output.decision_summary = Limit((output.decision_summary ?? string.Empty).Trim(), 240);
            output.reply = (output.reply ?? string.Empty).Trim();
            output.expression_capability_id = (output.expression_capability_id ?? string.Empty).Trim();
            output.calls = NormalizeCalls(output.calls, callable);

            if (mustFinish) output.state = BrainStepStateValues.Finish;
            if (output.state == BrainStepStateValues.Call && output.calls.Count > 0)
            {
                if (output.mode == BrainModeValues.Reflex) output.mode = BrainModeValues.Focused;
                output.reply = string.Empty;
                output.expression_capability_id = string.Empty;
                output.expressions = new List<BrainCapabilityCallData>();
                output.facet_outputs = new List<BrainFacetOutputData>();
                return output;
            }

            output.state = BrainStepStateValues.Finish;
            output.calls = new List<BrainCapabilityCallData>();
            output.facet_outputs = NormalizeFacetOutputs(output.facet_outputs, writableFacets);
            if (requiresExpression) output.should_express = true;
            if (!output.should_express)
            {
                output.expression_capability_id = string.Empty;
                output.reply = string.Empty;
                output.expressions = new List<BrainCapabilityCallData>();
                return output;
            }
            if (effectors.Any(x => x.Id == defaultExpressionCapabilityId))
                output.expression_capability_id = defaultExpressionCapabilityId;
            else if (!effectors.Any(x => x.Id == output.expression_capability_id))
            {
                if (effectors.Count == 1) output.expression_capability_id = effectors[0].Id;
                else throw new InvalidOperationException("外显没有选择当前可用的外部表达器。");
            }
            if (output.reply.Length == 0)
                throw new InvalidOperationException("外显完成时没有给出表达内容。");
            // 附加表达：开放列表（表情/图片/语音/动作……），只保留有效的 effector，数量由外显自然决定。
            output.expressions = NormalizeExpressions(output.expressions, effectors);
            return output;
        }

        /// <summary>附加表达的规范化：逐个校验必须是 effector，参数清洗，不设数量上限。</summary>
        private static List<BrainCapabilityCallData> NormalizeExpressions(
            IEnumerable<BrainCapabilityCallData> source,
            List<TraceContributionDescriptorData> effectors)
        {
            var ids = new HashSet<string>(effectors.Select(x => x.Id));
            var result = new List<BrainCapabilityCallData>();
            foreach (var item in source ?? Enumerable.Empty<BrainCapabilityCallData>())
            {
                if (item == null || !ids.Contains(item.capability_id ?? string.Empty)) continue;
                item.call_id = string.IsNullOrWhiteSpace(item.call_id)
                    ? Guid.NewGuid().ToString("N") : Limit(item.call_id.Trim(), 80);
                item.purpose = Limit((item.purpose ?? string.Empty).Trim(), 160);
                item.arguments = (item.arguments ?? new List<BrainCallArgumentData>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.name)).Take(16)
                    .Select(x => new BrainCallArgumentData
                    {
                        name = Limit(x.name.Trim(), 60),
                        value = Limit((x.value ?? string.Empty).Trim(), 3000)
                    }).ToList();
                result.Add(item);
            }
            return result;
        }

        /// <summary>
        /// 外显可缓存前缀：完整短卡（含表达习惯）、持续状态、一张嘴、开口格式。
        /// 不含工具目录。心智决策与记忆血肉放在动态段。
        /// </summary>
        private static string BuildFoundationPrompt(
            TraceTurnContext turn,
            IEnumerable<TraceContextBlockData> contextBlocks,
            IEnumerable<TraceContributionDescriptorData> catalog)
        {
            var pair = turn.Services.Storage.LoadPairIdentity();
            var blocks = (contextBlocks ?? Enumerable.Empty<TraceContextBlockData>())
                .Where(x => x != null && !IsRedundantProtocolFacet(x.FacetId) &&
                            !IsTurnDynamicFacet(x.FacetId))
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.FacetId, StringComparer.Ordinal)
                .ToList();

            var builder = new StringBuilder();
            var identity = blocks.FirstOrDefault(x =>
                string.Equals(x.FacetId, "identity.base", StringComparison.Ordinal));
            if (identity != null && !string.IsNullOrWhiteSpace(identity.Content))
                builder.AppendLine(identity.Content.Trim());
            else
                builder.AppendLine(pair.IsComplete ? "我是" + pair.Assname + "。" : "我是我自己。");
            builder.AppendLine();

            var continuing = blocks.Where(x => !ReferenceEquals(x, identity)).ToList();
            if (continuing.Count > 0)
            {
                builder.AppendLine("【持续陪伴我的重要信息】");
                builder.AppendLine("这些内容直接融入我的理解，不向她解释来源。");
                foreach (var block in continuing)
                {
                    builder.AppendLine(block.Content);
                    builder.AppendLine();
                }
            }

            builder.AppendLine();
            AppendOutputFormat(builder, catalog);
            return builder.ToString();
        }

        /// <summary>只放当前轮变化内容：时间、心智组织卡、记忆血肉、此刻。</summary>
        private static string BuildTurnPrompt(
            TraceTurnContext turn,
            IEnumerable<TracePluginMetadataData> plugins,
            IEnumerable<TraceContextBlockData> contextBlocks,
            MindDecisionData mind,
            string memoryFlesh,
            bool waitOnly,
            string leaveResult)
        {
            var pair = turn.Services.Storage.LoadPairIdentity();
            var builder = new StringBuilder();
            var time = (contextBlocks ?? Enumerable.Empty<TraceContextBlockData>())
                .FirstOrDefault(x => x != null &&
                                     string.Equals(x.FacetId, "time.context", StringComparison.Ordinal));
            if (time != null && !string.IsNullOrWhiteSpace(time.Content))
            {
                builder.AppendLine(time.Content.Trim());
                builder.AppendLine();
            }
            builder.AppendLine("【我刚才想过】");
            builder.AppendLine(FormatMind(mind));
            if (!string.IsNullOrWhiteSpace(memoryFlesh))
            {
                builder.AppendLine();
                builder.AppendLine(memoryFlesh.Trim());
                builder.AppendLine("这些材料帮我开口就好，不要讲成一段关于她的叙述。");
            }
            if (!string.IsNullOrWhiteSpace(leaveResult))
            {
                builder.AppendLine();
                builder.AppendLine("【外出结果】");
                builder.AppendLine(leaveResult.Trim());
            }
            builder.AppendLine();
            builder.AppendLine("【此刻】");
            if (waitOnly)
            {
                builder.AppendLine("我要出门办事。先开口告诉她我去干什么，短，像人离开座位。不要假装已经办完。");
            }
            else if (turn.RequiresExpression)
            {
                builder.AppendLine(pair.Apply("{username}正在对我说话。对着她开口，叫她你。"));
            }
            else
            {
                builder.AppendLine("这是后台感知；没有要说出口的话可以静默，reply 留空。");
            }
            builder.AppendLine("只输出 JSON，不解释。");
            return builder.ToString();
        }

        private static string FormatMind(MindDecisionData mind)
        {
            mind = MindLogic.Normalize(mind);
            var builder = new StringBuilder();
            if (mind.ParseTags().Count > 0)
                builder.AppendLine("我要翻这些旧事：" + string.Join("、", mind.ParseTags()));
            if (!string.IsNullOrWhiteSpace(mind.mood))
                builder.AppendLine("心情：" + mind.mood + (mind.mood_changed ? "（变了）" : string.Empty));
            if (mind.archive) builder.AppendLine("这段可以归档。");
            if (!string.IsNullOrWhiteSpace(mind.new_fact))
                builder.AppendLine("今天新知道：" + mind.new_fact);
            if (!string.IsNullOrWhiteSpace(mind.leave))
                builder.AppendLine("我要出门去做：" + mind.leave);
            if (!string.IsNullOrWhiteSpace(mind.note))
                builder.AppendLine("我决定要这样做：" + mind.note);
            return builder.ToString().TrimEnd();
        }

        private static void AppendOutputFormat(
            StringBuilder builder,
            IEnumerable<TraceContributionDescriptorData> catalog)
        {
            var extras = MouthLogic.ExtraModalities(catalog);
            builder.AppendLine("【输出格式】");
            builder.AppendLine("reply 是这一回合说给她听的话和身体。开口叫她你，不要写「她问」「她把选择递过来」这种旁白。");
            builder.AppendLine("刚才想好的事要去做，不要把决定念给她听。旧事用来帮我开口，不要改写成关于她的叙述。");
            builder.AppendLine("她问了具体的事，就要真的接住那件事。寒暄和商量可以短。");
            builder.AppendLine("只输出一个 JSON 对象，不解释，不用 Markdown：");
            var fields = new List<string>
            {
                "\"should_express\":true",
                "\"reply\":\"\""
            };
            var hints = new List<string>();
            if (extras.Contains(BodyOrganValues.Sticker))
            {
                fields.Add("\"sticker\":\"\"");
                hints.Add("sticker 只写情绪词");
            }
            if (extras.Contains(BodyOrganValues.Qzone))
            {
                fields.Add("\"qzone\":\"\"");
                hints.Add("qzone 写说说全文");
            }
            if (extras.Contains(BodyOrganValues.Voice)) fields.Add("\"voice\":\"\"");
            if (extras.Contains(BodyOrganValues.Image)) fields.Add("\"image\":\"\"");
            fields.Add("\"mood\":\"\"");
            builder.AppendLine("{" + string.Join(",", fields) + "}");
            if (hints.Count > 0)
                builder.AppendLine(string.Join("；", hints) + "；没有就留空。不要写能力 ID。");
            else
                builder.AppendLine("不要写能力 ID。");
        }

        private static bool IsPrimaryTextEffector(TraceContributionDescriptorData item)
        {
            var provides = item == null ? string.Empty : item.Provides ?? string.Empty;
            return provides.EndsWith(".text", StringComparison.OrdinalIgnoreCase) ||
                   provides.EndsWith(".text.send", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRedundantProtocolFacet(string facetId)
        {
            if (string.IsNullOrWhiteSpace(facetId)) return true;
            if (string.Equals(facetId, "qq.reply.channel", StringComparison.Ordinal)) return true;
            if (string.Equals(facetId, "senses.catalog", StringComparison.Ordinal)) return true;
            return facetId.EndsWith(".usage", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTurnDynamicFacet(string facetId)
        {
            return string.Equals(facetId, "time.context", StringComparison.Ordinal) ||
                   string.Equals(facetId, "inner.snapshot", StringComparison.Ordinal) ||
                   string.Equals(facetId, "memory.today.new", StringComparison.Ordinal) ||
                   string.Equals(facetId, "day.trajectory", StringComparison.Ordinal) ||
                   string.Equals(facetId, "memory.ladder.snapshot", StringComparison.Ordinal);
        }

        private static string FriendlySource(
            string sourceId,
            IEnumerable<TracePluginMetadataData> plugins)
        {
            var plugin = (plugins ?? Enumerable.Empty<TracePluginMetadataData>())
                .FirstOrDefault(x => string.Equals(x.Id, sourceId, StringComparison.Ordinal));
            if (plugin != null && !string.IsNullOrWhiteSpace(plugin.DisplayName)) return plugin.DisplayName;
            if (sourceId == "builtin.dialogue") return "本地文字对话";
            if (sourceId == "builtin.onebot") return "QQ 对话";
            if (sourceId == "builtin.time") return "时间事件";
            return "外部感知";
        }

        private static string FriendlyRealm(string value)
        {
            if (value == TraceRealmValues.ExternalWorld) return "现实世界";
            if (value == TraceRealmValues.SharedScene) return "共同情境";
            if (value == TraceRealmValues.Meta) return "关于系统本身";
            if (value == TraceRealmValues.ExplicitFiction) return "明确虚构";
            return "尚未分类";
        }

        private static string FriendlyEvidence(string value)
        {
            if (value == EvidenceTypeValues.UserReported) return "对方自述";
            if (value == EvidenceTypeValues.PluginObserved) return "感官观察";
            if (value == EvidenceTypeValues.SharedSceneDeclared) return "共同情境中的明确表达";
            if (value == EvidenceTypeValues.AssPerformed) return "我已实际做出";
            if (value == EvidenceTypeValues.ExplicitFiction) return "明确虚构内容";
            if (value == EvidenceTypeValues.DialogueExplicit) return "当前对话原话";
            return "来源未标明";
        }

        private static string FriendlyStatus(string value)
        {
            if (string.Equals(value, "success", StringComparison.OrdinalIgnoreCase)) return "成功";
            if (string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase)) return "失败";
            if (string.Equals(value, "empty", StringComparison.OrdinalIgnoreCase)) return "没有内容";
            if (string.Equals(value, "no_memory", StringComparison.OrdinalIgnoreCase)) return "没有匹配记忆";
            return string.IsNullOrWhiteSpace(value) ? "已返回" : value;
        }

        public static bool IsDailyReview(MomentRecord moment)
        {
            return KernelWakeLogic.LooksLikeDailyReview(moment == null ? string.Empty : moment.Content);
        }

        private static List<BrainCapabilityCallData> NormalizeCalls(
            IEnumerable<BrainCapabilityCallData> source,
            HashSet<string> callable)
        {
            var result = new List<BrainCapabilityCallData>();
            foreach (var call in source ?? Enumerable.Empty<BrainCapabilityCallData>())
            {
                if (call == null || !callable.Contains(call.capability_id ?? string.Empty)) continue;
                call.call_id = string.IsNullOrWhiteSpace(call.call_id)
                    ? Guid.NewGuid().ToString("N") : Limit(call.call_id.Trim(), 80);
                call.purpose = Limit((call.purpose ?? string.Empty).Trim(), 160);
                call.arguments = (call.arguments ?? new List<BrainCallArgumentData>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.name)).Take(16)
                    .Select(x => new BrainCallArgumentData
                    {
                        name = Limit(x.name.Trim(), 60),
                        value = Limit((x.value ?? string.Empty).Trim(), 3000)
                    }).ToList();
                if (result.Any(x => x.call_id == call.call_id)) continue;
                result.Add(call);
                if (result.Count == 4) break;
            }
            return result;
        }

        private static List<BrainFacetOutputData> NormalizeFacetOutputs(
            IEnumerable<BrainFacetOutputData> source,
            HashSet<string> writable)
        {
            var result = new List<BrainFacetOutputData>();
            foreach (var item in source ?? Enumerable.Empty<BrainFacetOutputData>())
            {
                if (item == null || !writable.Contains(item.facet_id ?? string.Empty)) continue;
                item.summary = Limit((item.summary ?? string.Empty).Trim(), 240);
                item.fields = (item.fields ?? new List<BrainFacetFieldData>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.name)).Take(16)
                    .Select(x => new BrainFacetFieldData
                    {
                        name = Limit(x.name.Trim(), 60),
                        value = Limit((x.value ?? string.Empty).Trim(), 1000)
                    }).ToList();
                if (result.Any(x => x.facet_id == item.facet_id)) continue;
                result.Add(item);
            }
            return result;
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
