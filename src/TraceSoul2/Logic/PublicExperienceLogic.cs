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
            CancellationToken token = default)
        {
            var count = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var pending = store.GetPublicMomentsInRange(startMs, endMs);
                if (pending.Count == 0) return count;
                var group = pending.GroupBy(x => x.ConversationId).First().ToList();
                var evidence = RuntimeSliceLogic.EvidenceBatches(store, group).FirstOrDefault();
                if (evidence == null) throw new InvalidOperationException("公开经历没有可处理的完整证据，原件保留。");
                var scope = group[0].ConversationId;
                var existing = store.GetCognitionNodes(5000).Where(x => x.ContextConversationId == scope && PuzzleDomains.Live(x.Status))
                    .Take(40).ToList();
                var prompt = "我是" + store.LoadPairIdentity().Assname + "。整理本环境的实际经历，形成同一主体的长期理解。\n" +
                    "当前环境不是两人私密相处。user 指已绑定的专属用户；其他人有各自账号，放 world 或明确对象的 relation，不能冒认为用户。\n" +
                    CorePrompts.Migration.CognitionBody + "\n" +
                    "现有本环境认知：\n" + TraceJson.ToJson(existing) + "\n完整证据（id、真实发言者、内容）：\n" +
                    string.Join("\n", evidence.Select(x => x.Id + "｜" + PuzzleViewLogic.SubjectKey(x) + "｜" +
                        EnvironmentLogic.PublicDialogue(x))) + RuntimeSliceLogic.EvidenceContext(store, evidence) +
                    "\n" + CorePrompts.Migration.CognitionJsonSchema;
                var output = await DeepSeekStructuredOutputLogic.CompleteAsync<Output>(llm,
                    new List<DeepSeekMessageData> { new("system", prompt), new("user", "只根据这些经历输出认知变更，没有变化时给空数组。") },
                    x => x != null && CognitionFormationLogic.Valid(x.cognitions, evidence, existing, new List<LifeTagRecord>()) &&
                        (evidence.Any(m => EnvironmentLogic.FromMoment(m)?.SpeakerIsOwner == true) ||
                         x.cognitions.All(w => !(w.domains ?? new()).Contains("user"))),
                    "认知变更缺少有效证据或对象不符。", token);
                store.CommitPublicExperience(evidence.Select(x => x.Id), output.cognitions);
                count += evidence.Count;
            }
        }
    }
}
