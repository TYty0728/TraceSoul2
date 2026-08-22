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
    /// 记忆写入活体轨：默认只把 Mind 明确给出的 new_fact 当场写入。
    /// 无人格观察器保留给迁移/显式工具，不再由普通对话逐句调用。
    /// </summary>
    public static class MemoryLiveWriteLogic
    {
        public const string StateKey = "memory.live";

        public sealed class State
        {
            public MemoryObservationCommitData Commit;
        }

        /// <summary>
        /// 在对话锁内取得的只读快照。后续 LLM 分析只使用这些数据，可以安全地在后台队列运行。
        /// 真正的 SQLite 写入仍由宿主重新取得对话锁后执行。
        /// </summary>
        public sealed class PreparedObservation
        {
            internal TraceTurnContext Turn;
            internal IMemoryStore Storage;
            internal PairIdentity Pair;
            internal VectorRouteResult Route;
            internal List<string> Allowed;
            internal List<FactSliceRecord> Facts;
            internal List<MomentRecord> Local;
            internal ILlmClient Llm;
        }

        public sealed class ObservationAnalysis
        {
            internal MemoryObservationOutputData Output;
            internal Exception Error;
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
            var prepared = PrepareObservation(turn);
            if (prepared == null) return null;
            var analysis = await AnalyzePreparedAsync(prepared, cancellationToken);
            return CommitPrepared(prepared, analysis);
        }

        public static PreparedObservation PrepareObservation(TraceTurnContext turn)
        {
            if (turn == null || turn.Services == null || turn.Services.Storage == null)
                return null;
            var storage = turn.Services.Storage;
            var pair = storage.LoadPairIdentity();
            var llm = turn.Services.Llm;
            if (!ShouldObserve(turn.Moment, turn.Wake, pair) || llm == null)
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
            return new PreparedObservation
            {
                Turn = turn,
                Storage = storage,
                Pair = pair,
                Route = route,
                Allowed = allowed,
                Facts = storage.GetFactCandidates(allowed, 12),
                Local = (turn.RecentMoments ?? new List<MomentRecord>()).Take(6).ToList(),
                Llm = llm
            };
        }

        /// <summary>纯 LLM 阶段：不访问共享 SQLite，不修改轮次工作区。</summary>
        public static async Task<ObservationAnalysis> AnalyzePreparedAsync(
            PreparedObservation prepared,
            CancellationToken cancellationToken)
        {
            if (prepared == null) return new ObservationAnalysis();
            try
            {
                var observer = new MemoryObservationLogic(prepared.Llm);
                var output = await observer.AnalyzeAsync(
                    prepared.Turn.Moment,
                    prepared.Route,
                    prepared.Facts,
                    prepared.Local,
                    prepared.Pair,
                    cancellationToken);
                return new ObservationAnalysis
                {
                    Output = MemoryObservationLogic.Normalize(
                        output, prepared.Route, prepared.Facts, prepared.Pair)
                };
            }
            catch (Exception exception)
            {
                return new ObservationAnalysis { Error = exception };
            }
        }

        /// <summary>快速提交阶段：宿主必须在重新取得对话锁后调用。</summary>
        public static MemoryObservationCommitData CommitPrepared(
            PreparedObservation prepared,
            ObservationAnalysis analysis)
        {
            if (prepared == null) return null;
            var turn = prepared.Turn;
            if (analysis == null || analysis.Error != null || analysis.Output == null)
            {
                turn.Workspace.Results.Add(new TraceCapabilityResultData
                {
                    CallId = "memory-observe",
                    CapabilityId = "memory.observe",
                    Status = "failed",
                    Summary = "当场观察失败：" +
                              (analysis == null || analysis.Error == null
                                  ? "没有返回观察结果。" : analysis.Error.Message),
                    Payload = string.Empty
                });
                return null;
            }

            var state = turn.Workspace.GetOrCreateState(StateKey, () => new State());
            var output = analysis.Output;
            var commit = prepared.Storage.CommitMemoryObservation(
                turn.Moment, MemoryObservationLogic.ObserverId, output, prepared.Allowed);
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
