using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Prompts;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>公开经历独立分环境整理，使用同一认知写入契约；不进入私密事件/关系卡复盘。</summary>
    public static class PublicExperienceLogic
    {
        public sealed class Output { public List<BrainCognitionWriteData> cognitions; }
        public static async Task<int> BuildAsync(SqliteMemoryManager store, ILlmClient llm, long startMs, long endMs,
            CancellationToken token = default, Func<IVectorEncoder> encoderFactory = null, IVectorCacheStore cache = null)
        {
            var count = 0;
            var deferred = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var pending = store.GetPublicMomentsInRange(startMs, endMs).Where(x => !deferred.Contains(x.Id)).ToList();
                if (pending.Count == 0)
                {
                    if (deferred.Count > 0) throw new ReviewProsePendingException("公开经历仍有待整理项，其他环境和批次已保存。");
                    return count;
                }
                var group = pending.GroupBy(x => x.ConversationId).First().ToList();
                var evidence = RuntimeSliceLogic.EvidenceBatches(store, group).FirstOrDefault();
                if (evidence == null) throw new InvalidOperationException("公开经历没有可处理的完整证据，原件保留。");
                var evidenceContext = RuntimeSliceLogic.EvidenceContext(store, evidence);
                var existing = CognitionReviewRecallLogic.Select(store, evidence, encoderFactory, cache, evidenceContext, token);
                var prompt = "我是" + store.LoadPairIdentity().Assname + "。整理本环境的实际经历，形成同一主体的长期理解。\n" +
                    "当前环境不是两人私密相处。user 指已绑定的专属用户；其他人有各自账号，放 world 或明确对象的 relation，不能冒认为用户。\n" +
                    CorePrompts.Migration.CognitionBody + "\n" +
                    CorePrompts.Migration.CognitionExistingHeader + "\n" + string.Join("\n", existing.Select(CognitionReviewRecallLogic.Describe)) +
                    "\n完整证据（id、真实发言者、内容）：\n" +
                    string.Join("\n", evidence.Select(x => x.Id + "｜" + PuzzleViewLogic.SubjectKey(x) + "｜" +
                        EnvironmentLogic.PublicDialogue(x))) + evidenceContext +
                    "\n" + CorePrompts.Migration.CognitionJsonSchema;
                Output output;
                try { output = await ReviewWorkLogic.CompleteAsync<Output>(store, "public-cognition", llm,
                    new List<DeepSeekMessageData> { new("system", prompt), new("user", "只根据这些经历输出认知变更，没有变化时给空数组。") },
                    x => CognitionFormationLogic.ValidationError(x.cognitions, evidence, existing, new List<LifeTagRecord>(), false) ??
                        (!evidence.Any(m => EnvironmentLogic.FromMoment(m)?.SpeakerIsOwner == true) &&
                         x.cognitions.Any(w => (w.domains ?? new()).Contains("user")) ? "公开认知不能冒认专属用户。" : null),
                    x => CognitionFormationLogic.TextParts(x.cognitions), token); }
                catch (ReviewProsePendingException) { foreach (var item in evidence) deferred.Add(item.Id); continue; }
                var resolution = await CognitionConsolidationLogic.ResolveAsync(store, llm, evidence, existing,
                    output.cognitions, encoderFactory, cache, token);
                if (!CognitionFormationLogic.Valid(resolution.Writes, evidence, resolution.Candidates, new List<LifeTagRecord>()) ||
                    (!evidence.Any(m => EnvironmentLogic.FromMoment(m)?.SpeakerIsOwner == true) && resolution.Writes.Any(w => (w.domains ?? new()).Contains("user"))))
                    throw new InvalidOperationException("公开认知比较后对象或依据无效。");
                var candidateStamp = IdentityReorganizationLogic.Digest(resolution.Candidates.OrderBy(x => x.Id, StringComparer.Ordinal).ToList());
                var sourceStamp = IdentityReorganizationLogic.Digest(evidence.OrderBy(x => x.Id, StringComparer.Ordinal).ToList());
                store.CommitPublicExperienceChecked(evidence.Select(x => x.Id), resolution.Writes, () => {
                    var ids = resolution.Candidates.Select(x => x.Id).ToHashSet();
                    if (candidateStamp != IdentityReorganizationLogic.Digest(store.GetCognitionReviewCandidates(evidence[0].ConversationId, true)
                        .Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id, StringComparer.Ordinal).ToList()) ||
                        sourceStamp != IdentityReorganizationLogic.Digest(evidence.Select(x => x.Id).Chunk(256).SelectMany(store.GetEvidenceMoments)
                            .OrderBy(x => x.Id, StringComparer.Ordinal).ToList()))
                        throw new InvalidOperationException("公开认知生成期间来源或认识已变化，未提交旧结果。");
                });
                count += evidence.Count;
            }
        }
    }
}
