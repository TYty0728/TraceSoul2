using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Prompts;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 把本轮真正点亮的 Tag / 事实 / 认知收成人生切片。
    /// 未选中的导航候选保持沉寂，不作为清单注入 Brain。
    /// </summary>
    public sealed class MemoryNeighborhoodData
    {
        public string PerceptionSummary { get; set; }
        public string FactDecision { get; set; }
        public List<LifeTagRecord> Anchors { get; set; } = new List<LifeTagRecord>();
        public List<FactSliceRecord> NewFacts { get; set; } = new List<FactSliceRecord>();
        public List<FactSliceRecord> AwakenedFacts { get; set; } = new List<FactSliceRecord>();
        public List<CognitionSliceRecord> Cognitions { get; set; } = new List<CognitionSliceRecord>();
        public Dictionary<string, List<string>> FactTagIds { get; set; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> CognitionTagIds { get; set; } =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public List<CognitionCueRecallData> TraceHits { get; set; } = new List<CognitionCueRecallData>();
        public int SilentCandidateCount { get; set; }
    }

    public static class MemoryNeighborhoodLogic
    {
        public static MemoryNeighborhoodData Collect(
            IMemoryStore store,
            MemoryObservationOutputData sensory,
            MemoryObservationCommitData commit,
            IEnumerable<CognitionSliceRecord> cognitions,
            IEnumerable<CognitionCueRecallData> traceHits,
            int silentCandidateCount)
        {
            var data = new MemoryNeighborhoodData
            {
                PerceptionSummary = sensory == null ? string.Empty : sensory.perception_summary,
                FactDecision = sensory == null ? string.Empty : sensory.fact_decision,
                SilentCandidateCount = Math.Max(0, silentCandidateCount)
            };
            if (commit != null)
            {
                data.Anchors = (commit.SelectedTags ?? new List<LifeTagRecord>()).ToList();
                data.NewFacts = (commit.WrittenFacts ?? new List<FactSliceRecord>()).ToList();
                data.AwakenedFacts = (commit.AwakenedFacts ?? new List<FactSliceRecord>()).ToList();
            }
            data.Cognitions = (cognitions ?? Enumerable.Empty<CognitionSliceRecord>()).ToList();
            data.TraceHits = (traceHits ?? Enumerable.Empty<CognitionCueRecallData>()).ToList();

            var factIds = data.NewFacts.Concat(data.AwakenedFacts).Select(x => x.Id).Distinct();
            var cognitionIds = data.Cognitions.Select(x => x.Id)
                .Concat(data.TraceHits.Where(x => x != null && x.Cognition != null).Select(x => x.Cognition.Id))
                .Distinct();
            if (store != null)
            {
                data.FactTagIds = store.GetFactTagIds(factIds);
                data.CognitionTagIds = store.GetCognitionTagIds(cognitionIds);
            }
            return data;
        }

        public static string FormatForExpressor(MemoryNeighborhoodData data)
        {
            var builder = new StringBuilder();
            if (data == null || !HasLitNodes(data))
            {
                builder.AppendLine(CorePrompts.MemoryNeighborhood.EmptyNodes);
                builder.AppendLine(CorePrompts.MemoryNeighborhood.EmptyHint);
                AppendObservation(builder, data);
                return builder.ToString().TrimEnd();
            }

            builder.AppendLine(CorePrompts.MemoryNeighborhood.LitHeader);
            var newIds = new HashSet<string>((data.NewFacts ?? new List<FactSliceRecord>()).Select(x => x.Id));
            var awakenedIds = new HashSet<string>((data.AwakenedFacts ?? new List<FactSliceRecord>()).Select(x => x.Id));
            var placedFacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var placedCognitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cueByCognition = (data.TraceHits ?? new List<CognitionCueRecallData>())
                .Where(x => x != null && x.Cognition != null)
                .GroupBy(x => x.Cognition.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var tag in data.Anchors ?? new List<LifeTagRecord>())
            {
                if (tag == null) continue;
                builder.Append("- 锚点 ").Append(tag.Id).Append(" ").Append(tag.Label);
                if (tag.ActivationCount > 0)
                    builder.Append("（已点亮 ").Append(tag.ActivationCount).Append(" 次）");
                builder.AppendLine();

                foreach (var fact in data.NewFacts.Concat(data.AwakenedFacts))
                {
                    if (fact == null || !LinkedTo(data.FactTagIds, fact.Id, tag.Id)) continue;
                    placedFacts.Add(fact.Id);
                    builder.Append("  事实 ").Append(fact.Id).Append("：").Append(fact.Summary);
                    if (newIds.Contains(fact.Id)) builder.Append(" ［新写入］");
                    else if (awakenedIds.Contains(fact.Id)) builder.Append(" ［唤醒］");
                    builder.AppendLine();
                }

                foreach (var cognition in data.Cognitions ?? new List<CognitionSliceRecord>())
                {
                    if (cognition == null || !LinkedTo(data.CognitionTagIds, cognition.Id, tag.Id)) continue;
                    placedCognitions.Add(cognition.Id);
                    builder.Append("  认知 ").Append(cognition.Id).Append("：").Append(cognition.Summary);
                    CognitionCueRecallData cueHit;
                    if (cueByCognition.TryGetValue(cognition.Id, out cueHit))
                        builder.Append(" ← cue「").Append(cueHit.Cue).Append("」");
                    builder.AppendLine();
                }
            }

            var hangingFacts = data.NewFacts.Concat(data.AwakenedFacts)
                .Where(x => x != null && placedFacts.Add(x.Id)).ToList();
            foreach (var fact in hangingFacts)
            {
                builder.Append("- 事实 ").Append(fact.Id).Append("：").Append(fact.Summary);
                if (newIds.Contains(fact.Id)) builder.Append(" ［新写入］");
                else if (awakenedIds.Contains(fact.Id)) builder.Append(" ［唤醒］");
                builder.AppendLine();
            }

            var traceOnly = (data.TraceHits ?? new List<CognitionCueRecallData>())
                .Where(x => x != null && x.Cognition != null && placedCognitions.Add(x.Cognition.Id))
                .ToList();
            if (traceOnly.Count > 0)
            {
                builder.AppendLine(CorePrompts.MemoryNeighborhood.TraceWakeHeader);
                foreach (var hit in traceOnly)
                    builder.Append("- 认知 ").Append(hit.Cognition.Id).Append("：")
                        .Append(hit.Cognition.Summary)
                        .Append(" ← cue「").Append(hit.Cue).Append("」")
                        .AppendLine();
            }

            if (data.SilentCandidateCount > 0)
                builder.Append("另有 ").Append(data.SilentCandidateCount)
                    .AppendLine(" 个导航候选未被点亮，保持沉寂。");
            AppendObservation(builder, data);
            return builder.ToString().TrimEnd();
        }

        private static bool HasLitNodes(MemoryNeighborhoodData data)
        {
            return (data.Anchors != null && data.Anchors.Count > 0) ||
                   (data.NewFacts != null && data.NewFacts.Count > 0) ||
                   (data.AwakenedFacts != null && data.AwakenedFacts.Count > 0) ||
                   (data.Cognitions != null && data.Cognitions.Count > 0) ||
                   (data.TraceHits != null && data.TraceHits.Count > 0);
        }

        private static bool LinkedTo(Dictionary<string, List<string>> map, string nodeId, string tagId)
        {
            List<string> tags;
            return map != null && map.TryGetValue(nodeId, out tags) && tags != null && tags.Contains(tagId);
        }

        private static void AppendObservation(StringBuilder builder, MemoryNeighborhoodData data)
        {
            if (data == null) return;
            if (!string.IsNullOrWhiteSpace(data.PerceptionSummary))
                builder.Append("观察：").AppendLine(data.PerceptionSummary.Trim());
            if (!string.IsNullOrWhiteSpace(data.FactDecision))
                builder.Append("事实决策：").AppendLine(data.FactDecision.Trim());
        }
    }
}
