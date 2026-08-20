using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 记忆写入活体轨：无人格观察当场选标签、写事实；日构建只做浸染和阶梯。
    /// </summary>
    public static class MemoryLiveWriteLogic
    {
        public const string StateKey = "memory.live";

        public sealed class State
        {
            public MemoryObservationCommitData Commit;
        }

        public static bool ShouldObserve(MomentRecord moment, string wake, PairIdentity pair)
        {
            if (moment == null || string.IsNullOrWhiteSpace(moment.Content)) return false;
            if (KernelWakeLogic.IsSubconscious(wake)) return false;
            if (InnerLifeLogic.IsContinuationContent(moment.Content)) return false;
            var role = moment.Role ?? string.Empty;
            if (role.IndexOf("system", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            pair = pair ?? PairIdentity.Missing;
            if (pair.IsCompanionMoment(role)) return false;
            return pair.IsHumanMoment(role) ||
                   string.Equals(role.Trim(), "user", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<MemoryObservationCommitData> ObserveAndCommitAsync(
            TraceTurnContext turn,
            CancellationToken cancellationToken)
        {
            if (turn == null || turn.Services == null || turn.Services.Storage == null)
                return null;
            var storage = turn.Services.Storage;
            var pair = storage.LoadPairIdentity();
            var state = turn.Workspace.GetOrCreateState(StateKey, () => new State());
            if (!ShouldObserve(turn.Moment, turn.Wake, pair) || turn.Services.Llm == null)
                return null;

            VectorRouteResult route = null;
            try
            {
                if (turn.Services.Router != null)
                    route = turn.Services.Router.Route(turn.Moment.Content);
            }
            catch
            {
                route = null;
            }

            var allowed = route == null || route.Concepts == null
                ? new List<string>()
                : route.Concepts.Where(x => x != null && x.Node != null).Select(x => x.Node.Id).ToList();
            var facts = storage.GetFactCandidates(allowed, 12);
            var local = (turn.RecentMoments ?? new List<MomentRecord>()).Take(6).ToList();
            MemoryObservationOutputData output;
            try
            {
                var observer = new MemoryObservationLogic(turn.Services.Llm);
                output = await observer.AnalyzeAsync(
                    turn.Moment, route, facts, local, pair, cancellationToken);
            }
            catch (Exception exception)
            {
                turn.Workspace.Results.Add(new TraceCapabilityResultData
                {
                    CallId = "memory-observe",
                    CapabilityId = "memory.observe",
                    Status = "failed",
                    Summary = "当场观察失败：" + exception.Message,
                    Payload = string.Empty
                });
                return null;
            }

            output = MemoryObservationLogic.Normalize(output, route, facts, pair);
            var commit = storage.CommitMemoryObservation(
                turn.Moment, MemoryObservationLogic.ObserverId, output, allowed);
            state.Commit = commit;
            if (commit != null && commit.OntologyChanged)
                RebuildRouter(turn);

            var written = commit == null ? 0 : commit.WrittenFacts.Count;
            var tags = commit == null ? 0 : commit.SelectedTags.Count;
            var created = commit != null && commit.OntologyChanged;
            turn.Workspace.Results.Add(new TraceCapabilityResultData
            {
                CallId = "memory-observe",
                CapabilityId = "memory.observe",
                Status = "success",
                Summary = created
                    ? "当场观察：新标签进入生命网，事实 +" + written + "。"
                    : "当场观察：点亮 " + tags + " 个标签，事实 +" + written + "。",
                Payload = output.perception_summary ?? string.Empty
            });
            return commit;
        }

        public static MemoryObservationCommitData TryCommitNewFact(
            TraceTurnContext turn,
            MindDecisionData mind)
        {
            if (turn == null || turn.Services == null || turn.Services.Storage == null) return null;
            mind = MindLogic.Normalize(mind);
            var fact = (mind.new_fact ?? string.Empty).Trim();
            if (fact.Length == 0) return null;
            var live = turn.Workspace.GetOrCreateState(StateKey, () => new State());
            if (live.Commit != null && live.Commit.WrittenFacts != null && live.Commit.WrittenFacts.Count > 0)
                return null;

            var tagIds = ResolveTagIds(turn, mind);
            if (tagIds.Count == 0 && live.Commit != null && live.Commit.SelectedTags != null)
                tagIds = live.Commit.SelectedTags.Where(x => x != null).Select(x => x.Id).ToList();
            if (tagIds.Count == 0) return null;

            var output = new MemoryObservationOutputData
            {
                perception_summary = "心智记下新知道的事。",
                fact_decision = "new_fact",
                selected_tag_ids = tagIds,
                fact_writes = new List<SensoryFactWriteData>
                {
                    new SensoryFactWriteData
                    {
                        summary = Limit(fact, 19),
                        realm = TraceRealmValues.SharedScene,
                        evidence_type = EvidenceTypeValues.DialogueExplicit,
                        confidence = 0.8f,
                        tag_ids = tagIds
                    }
                }
            };
            var commit = turn.Services.Storage.CommitMemoryObservation(
                turn.Moment, MemoryObservationLogic.ObserverId, output, tagIds);
            if (live.Commit == null) live.Commit = commit;
            if (commit != null && commit.WrittenFacts != null && commit.WrittenFacts.Count > 0)
            {
                turn.Workspace.Results.Add(new TraceCapabilityResultData
                {
                    CallId = "memory-new-fact",
                    CapabilityId = "memory.observe",
                    Status = "success",
                    Summary = "新事实已进网：" + commit.WrittenFacts[0].Summary,
                    Payload = commit.WrittenFacts[0].Summary
                });
            }
            return commit;
        }

        public static void RebuildRouter(TraceTurnContext turn)
        {
            if (turn == null || turn.Services == null) return;
            var logic = turn.Services.Router as HierarchicalVectorRouterLogic;
            if (logic == null || turn.Services.Storage == null) return;
            var pair = turn.Services.Storage.LoadPairIdentity();
            logic.Build(LifeTagVectorLogic.BuildOntology(
                turn.Services.Storage, CoreVectorOntologyFactory.Create(pair)));
        }

        public static List<string> ResolveTagIds(TraceTurnContext turn, MindDecisionData mind)
        {
            var labels = mind == null ? new List<string>() : mind.ParseTags();
            if (labels.Count == 0 || turn == null || turn.Services == null || turn.Services.Storage == null)
                return new List<string>();
            var byLabel = (turn.Services.Storage.GetActiveLifeTags() ?? new List<LifeTagRecord>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Label))
                .GroupBy(x => x.Label, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.Ordinal);
            return labels.Where(byLabel.ContainsKey).Select(x => byLabel[x]).Distinct().ToList();
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
