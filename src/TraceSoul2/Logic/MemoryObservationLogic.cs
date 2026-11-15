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
    /// 旧观察记录的纯数据归一化工具，不执行模型观察或写入。
    /// 它没有第一人称人格，也没有认知写入权限。
    /// </summary>
    public static class MemoryObservationLogic
    {
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
