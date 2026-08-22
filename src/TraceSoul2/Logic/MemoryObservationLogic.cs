using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Prompts;

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
                CorePrompts.MemoryObservation.MissingSummary,
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
            builder.AppendLine(pair.Apply(CorePrompts.MemoryObservation.Role));
            builder.AppendLine(CorePrompts.MemoryObservation.Duty);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.MemoryObservation.HardRulesHeader);
            builder.AppendLine(pair.Apply(CorePrompts.MemoryObservation.Rule1));
            builder.AppendLine(CorePrompts.MemoryObservation.Rule2);
            builder.AppendLine(CorePrompts.MemoryObservation.Rule3);
            builder.AppendLine(CorePrompts.MemoryObservation.Rule4);
            builder.AppendLine(pair.Apply(CorePrompts.MemoryObservation.Rule5));
            builder.AppendLine(pair.Apply(CorePrompts.MemoryObservation.Rule6));
            builder.AppendLine(CorePrompts.MemoryObservation.Rule7);
            builder.AppendLine(pair.Apply(CorePrompts.MemoryObservation.Rule8));
            if (pair.HasCallName)
                builder.AppendLine(pair.Apply(CorePrompts.MemoryObservation.Rule9));
            builder.AppendLine();
            builder.AppendLine(CorePrompts.MemoryObservation.CurrentSourcePrefix + moment.SourcePluginId + "；evidence=" + moment.EvidenceType);
            builder.AppendLine(CorePrompts.MemoryObservation.CurrentMomentPrefix + moment.Content);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.MemoryObservation.LocalContextHeader);
            var context = (localReferenceContext ?? Enumerable.Empty<MomentRecord>()).ToList();
            if (context.Count == 0) builder.AppendLine(CorePrompts.MemoryObservation.Empty);
            foreach (var item in context)
                builder.AppendLine("- " + pair.LabelForRole(item.Role) + "：" + item.Content);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.MemoryObservation.Layer12Header);
            if (route == null) builder.AppendLine(CorePrompts.MemoryObservation.Empty);
            else
            {
                builder.AppendLine(CorePrompts.MemoryObservation.DomainPrefix + JoinHits(route.Domains));
                builder.AppendLine(CorePrompts.MemoryObservation.DimensionPrefix + JoinHits(route.Dimensions));
            }
            builder.AppendLine(CorePrompts.MemoryObservation.Layer3Header);
            if (route == null || route.Concepts.Count == 0) builder.AppendLine(CorePrompts.MemoryObservation.NoReliableTags);
            else foreach (var hit in route.Concepts)
                builder.AppendLine("- " + hit.Node.Id + " | " + hit.Node.Label + " | " + hit.Node.Definition);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.MemoryObservation.FactCandidatesHeader);
            var facts = (factCandidates ?? Enumerable.Empty<FactSliceRecord>()).ToList();
            if (facts.Count == 0) builder.AppendLine(CorePrompts.MemoryObservation.Empty);
            else foreach (var fact in facts)
                builder.AppendLine("- " + fact.Id + " | " + fact.Summary + " | " + fact.Realm);
            builder.AppendLine();
            CorePrompts.Write(builder, pair.Apply(CorePrompts.MemoryObservation.JsonSchema));
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
