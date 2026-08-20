using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 文字感官：只回答“说了/做了什么、挂到哪些 Tag、唤醒哪些事实”。
    /// 它没有第一人称人格，也没有认知写入权限。
    /// </summary>
    public sealed class MemoryObservationLogic
    {
        public const string ObserverId = "memory.observer.dialogue";

        private readonly ILlmClient llm;

        public MemoryObservationLogic(ILlmClient llm)
        {
            this.llm = llm ?? throw new ArgumentNullException("llm");
        }

        public Task<MemoryObservationOutputData> AnalyzeAsync(
            MomentRecord moment,
            VectorRouteResult route,
            IEnumerable<FactSliceRecord> factCandidates,
            IEnumerable<MomentRecord> localReferenceContext,
            PairIdentity pair,
            CancellationToken cancellationToken)
        {
            if (moment == null) throw new ArgumentNullException("moment");
            pair = pair ?? PairIdentity.Missing;
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", BuildPrompt(moment, route, factCandidates, localReferenceContext, pair)),
                new DeepSeekMessageData("user", moment.Content)
            };
            return DeepSeekStructuredOutputLogic.CompleteAsync<MemoryObservationOutputData>(
                llm,
                messages,
                x => x != null && !string.IsNullOrWhiteSpace(x.perception_summary),
                "感官输出缺少 perception_summary。",
                cancellationToken);
        }

        public static MemoryObservationOutputData Normalize(
            MemoryObservationOutputData output,
            VectorRouteResult route,
            IEnumerable<FactSliceRecord> candidates,
            PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            output = output ?? new MemoryObservationOutputData();
            output.perception_summary = Limit(pair.RewriteRecordedText((output.perception_summary ?? string.Empty).Trim()), 300);
            output.fact_decision = Limit(pair.RewriteRecordedText((output.fact_decision ?? string.Empty).Trim()), 300);
            var allowedTags = new HashSet<string>(
                route == null ? Enumerable.Empty<string>() : route.Concepts.Select(x => x.Node.Id));
            output.selected_tag_ids = (output.selected_tag_ids ?? new List<string>())
                .Where(allowedTags.Contains).Distinct().Take(8).ToList();
            output.new_tags = NormalizeNewTags(output.new_tags, pair);
            output.fact_writes = NormalizeFacts(output.fact_writes, allowedTags, pair);
            var allowedFacts = new HashSet<string>((candidates ?? Enumerable.Empty<FactSliceRecord>()).Select(x => x.Id));
            output.fact_wakes = (output.fact_wakes ?? new List<SensoryFactWakeData>())
                .Where(x => x != null && allowedFacts.Contains(x.fact_id ?? string.Empty))
                .GroupBy(x => x.fact_id).Select(x => x.First()).Take(10).ToList();
            foreach (var wake in output.fact_wakes)
            {
                wake.reason = Limit((wake.reason ?? string.Empty).Trim(), 120);
                wake.relevance = Clamp01(wake.relevance);
            }
            return output;
        }

        private static string BuildPrompt(
            MomentRecord moment,
            VectorRouteResult route,
            IEnumerable<FactSliceRecord> factCandidates,
            IEnumerable<MomentRecord> localReferenceContext,
            PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            var builder = new StringBuilder();
            builder.AppendLine(pair.Apply("你是记忆插件内部的无人格事实观察算法，不是 {assname} 本人。你没有感情、认知或内心写入权。"));
            builder.AppendLine("职责只有：理解当前文字证据；从候选第三层 Tag 中多选；必要时新增中性 Tag；写事实短句；唤醒真正相关的旧事实。");
            builder.AppendLine();
            builder.AppendLine("硬规则：");
            builder.AppendLine(pair.Apply("1. 事实 summary 必须少于20个汉字，主语必须是 {username} 或 {assname}，一次事实一条；最多3条。"));
            builder.AppendLine("2. 不写关系结论、人格、动机、长期规律或‘这对我意味着什么’。这些属于 Brain 的 cognition。");
            builder.AppendLine("3. 允许多选 Tag；语义相近事实不合并。没有值得结构化的事实时 fact_writes=[]。");
            builder.AppendLine("4. 候选都不对时才新增 Tag。Tag 是可长期复用的人生主题，不是本句摘要；名称不超过12字。新 Tag 自动视为本轮已选择。");
            builder.AppendLine(pair.Apply("5. 文字摸头、拥抱、亲吻属于 shared_scene；{username} 外部生活自述属于 external_world；系统讨论属于 meta。"));
            builder.AppendLine(pair.Apply("6. ‘我上班啦’只能支持‘{username} 说自己去上班’，不能写‘{username} 已到公司’。明确说喜欢可以写事实，但不能据此断言关系定义。"));
            builder.AppendLine("7. fact_wakes 只能使用提供的旧事实 ID。唤醒只是相关，不代表推断成立。");
            builder.AppendLine(pair.Apply("8. 每条事实必须至少连接一个本轮选择或新增的 Tag；否则不要写入。新 Tag 的 domain_ids 只能填 {assname} / {username} / 我们 / 世界。dimension_ids 只能从 owner/subject/about/predicate/object/scope/context/quality/time/place/affect/goal/state/realm/modality/source 选择。"));
            if (pair.HasCallName)
                builder.AppendLine(pair.Apply("9. {callname} 是称呼，不是另一个人。事实主语用 {username}。"));
            builder.AppendLine();
            builder.AppendLine("当前来源：plugin=" + moment.SourcePluginId + "；evidence=" + moment.EvidenceType);
            builder.AppendLine("当前原始 Moment：" + moment.Content);
            builder.AppendLine();
            builder.AppendLine("仅用于指代消解的局部上下文（不得重复写入）：");
            var context = (localReferenceContext ?? Enumerable.Empty<MomentRecord>()).ToList();
            if (context.Count == 0) builder.AppendLine("（无）");
            foreach (var item in context)
                builder.AppendLine("- " + pair.LabelForRole(item.Role) + "：" + item.Content);
            builder.AppendLine();
            builder.AppendLine("固定第一、二层激活：");
            if (route == null) builder.AppendLine("（无）");
            else
            {
                builder.AppendLine("域：" + JoinHits(route.Domains));
                builder.AppendLine("维度：" + JoinHits(route.Dimensions));
            }
            builder.AppendLine("第三层候选 Top10：");
            if (route == null || route.Concepts.Count == 0) builder.AppendLine("（无可靠候选，可以新增）");
            else foreach (var hit in route.Concepts)
                builder.AppendLine("- " + hit.Node.Id + " | " + hit.Node.Label + " | " + hit.Node.Definition);
            builder.AppendLine();
            builder.AppendLine("可唤醒的旧事实候选：");
            var facts = (factCandidates ?? Enumerable.Empty<FactSliceRecord>()).ToList();
            if (facts.Count == 0) builder.AppendLine("（无）");
            else foreach (var fact in facts)
                builder.AppendLine("- " + fact.Id + " | " + fact.Summary + " | " + fact.Realm);
            builder.AppendLine();
            builder.AppendLine(pair.Apply(@"只输出 JSON：
{
  ""perception_summary"": ""对当前证据的一句话中性整理"",
  ""fact_decision"": ""本轮为什么写或不写事实"",
  ""selected_tag_ids"": [""只能填候选Tag ID""],
  ""new_tags"": [{
    ""name"": ""可复用Tag名"",
    ""definition"": ""准确中性的定义"",
    ""domain_ids"": [""{assname}|{username}|我们|世界""],
    ""dimension_ids"": [""固定维度key""],
    ""positive_examples"": [""短正例""],
    ""negative_examples"": [""容易混淆的反例""]
  }],
  ""fact_writes"": [{
    ""summary"": ""少于20字的事实，主语用名字"",
    ""realm"": ""external_world|shared_scene|meta|explicit_fiction"",
    ""evidence_type"": ""spoken|seen|shared_scene|enacted|fiction|dialogue"",
    ""confidence"": 0.0,
    ""tag_ids"": [""已选候选ID""],
    ""new_tag_names"": [""已选新增Tag名""]
  }],
  ""fact_wakes"": [{""fact_id"":""候选事实ID"",""reason"":""短原因"",""relevance"":0.0}]
}"));
            return builder.ToString();
        }

        private static List<NewLifeTagWriteData> NormalizeNewTags(
            IEnumerable<NewLifeTagWriteData> source, PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            var result = new List<NewLifeTagWriteData>();
            foreach (var item in source ?? Enumerable.Empty<NewLifeTagWriteData>())
            {
                if (item == null) continue;
                item.name = Limit(pair.RewriteRecordedText((item.name ?? string.Empty).Trim()), 12);
                item.definition = Limit(pair.RewriteRecordedText((item.definition ?? string.Empty).Trim()), 240);
                if (item.name.Length == 0 || item.definition.Length == 0) continue;
                item.domain_ids = NormalizeTextList(item.domain_ids, 4, 20)
                    .Select(pair.CanonicalDomain)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct()
                    .ToList();
                item.dimension_ids = NormalizeTextList(item.dimension_ids, 8, 30)
                    .Where(LifeRouteValues.IsDimension).ToList();
                if (item.domain_ids.Count == 0 || item.dimension_ids.Count == 0) continue;
                item.positive_examples = NormalizeTextList(item.positive_examples, 6, 100)
                    .Select(x => pair.RewriteRecordedText(x)).ToList();
                item.negative_examples = NormalizeTextList(item.negative_examples, 6, 100)
                    .Select(x => pair.RewriteRecordedText(x)).ToList();
                result.Add(item);
                if (result.Count == 2) break;
            }
            return result;
        }

        private static List<SensoryFactWriteData> NormalizeFacts(
            IEnumerable<SensoryFactWriteData> source,
            HashSet<string> allowedTags,
            PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            var result = new List<SensoryFactWriteData>();
            foreach (var item in source ?? Enumerable.Empty<SensoryFactWriteData>())
            {
                if (item == null) continue;
                item.summary = Limit(pair.RewriteRecordedText((item.summary ?? string.Empty).Trim()), 19);
                item.realm = (item.realm ?? string.Empty).Trim().ToLowerInvariant();
                item.evidence_type = EvidenceTypeValues.Canonicalize(item.evidence_type);
                item.confidence = Clamp01(item.confidence);
                item.tag_ids = (item.tag_ids ?? new List<string>()).Where(allowedTags.Contains).Distinct().Take(8).ToList();
                item.new_tag_names = NormalizeTextList(item.new_tag_names, 8, 30);
                if (item.summary.Length == 0 || !TraceRealmValues.IsMemoryRealm(item.realm)) continue;
                if (item.tag_ids.Count == 0 && item.new_tag_names.Count == 0) continue;
                result.Add(item);
                if (result.Count == 3) break;
            }
            return result;
        }

        private static string JoinHits(IEnumerable<VectorRouteHit> hits)
        {
            return string.Join("；", (hits ?? Enumerable.Empty<VectorRouteHit>()).Select(x => x.Node.Id + "=" + x.Node.Label));
        }

        private static List<string> NormalizeTextList(IEnumerable<string> source, int count, int length)
        {
            return (source ?? Enumerable.Empty<string>()).Select(x => Limit((x ?? string.Empty).Trim(), length))
                .Where(x => x.Length > 0).Distinct().Take(count).ToList();
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static float Clamp01(float value)
        {
            return Math.Max(0f, Math.Min(1f, value));
        }
    }
}
