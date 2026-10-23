using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

internal static partial class Program
{
    private static string ReorganizationAssessment(params string[] ids) => TraceJson.ToJson(new {
        understandings = new[] { new { meaning = "能够在分开后恢复联系", scope = "双方可回应时", exceptions = "忙碌时仍需沟通", certainty = "反复印证",
            cognition_ids = ids } }, details = new object[0] });
    private static string ReorganizationProposal(params string[] ids) => TraceJson.ToJson(new {
        body = "我们逐渐信任彼此能够在短暂分开后恢复连接，忙碌时仍需沟通。", change = "把重复事例归为恢复连接的信任，保留忙碌时的边界。",
        understanding_indices = new[] { 0 }, cognition_ids = ids });
    private static async Task RunIdentityReorganizationChecksAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "identity-reorganization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "brain.db");
            using var store = new SqliteMemoryManager(path);
            const string root = "tracesoul2";
            store.SavePairIdentity("本人", "同伴", "称呼");
            var original = store.LoadIdentityCards(root).Single(x => x.Slot == "relation").Body;
            store.SaveMoment(new MomentRecord { Id = "m1", ConversationId = root, Role = "user", Content = "合成来源", CreatedUnixMs = 1 });
            using (var db = new SQLite.SQLiteConnection(path))
                foreach (var id in new[] { "r1", "r2", "public", "foreign" })
                {
                    db.Insert(new CognitionSliceRecord { Id = id, Status = "active", IdentitySlot = "relation", Summary = "重复的合成认识",
                        ContextConversationId = id == "foreign" ? "another" : root, MemoryVisibility = id == "public" ? "public" : "private" });
                    db.Insert(new CognitionEvidenceRecord { Id = "e-" + id, CognitionId = id, MomentId = "m1", Relation = "support" });
                }
            var llm = new AgentSequenceLlm(ReorganizationAssessment("r1", "r2"), ReorganizationProposal("r1", "r2"));
            var draft = await IdentityReorganizationLogic.GenerateAsync(store, root, "relation", "", llm, CancellationToken.None);
            Require(draft.status == "ready" && llm.Requests.Count == 2 &&
                store.LoadIdentityCards(root).Single(x => x.Slot == "relation").Body == original &&
                !llm.Requests[0].Contains("public") && !llm.Requests[0].Contains("foreign"), "候选归并只读同主体私密认知，不提前写卡");
            await IdentityReorganizationLogic.GenerateAsync(store, root, "relation", "", new AgentSequenceLlm(), CancellationToken.None);
            IdentityReorganizationLogic.Apply(store, root, "relation", draft.id);
            var applied = store.LoadIdentityCards(root).Single(x => x.Slot == "relation");
            IdentityReorganizationLogic.Apply(store, root, "relation", draft.id);
            Require(applied.Origin == "derived" && !applied.Pinned && IdentityProjectionLogic.Current(applied, store.GetCognitionNodes(5000)) &&
                store.LoadIdentityCards(root).Single(x => x.Slot == "relation").Revision == applied.Revision &&
                IdentityReorganizationLogic.Read(store, root, "relation").status == "applied", "写回为有来源的同主体派生卡，重复采用不重复修改");
            Require(store.GetCognitionNodes(5000).Count == 4 && store.GetEvidenceMoments(new[] { "m1" }).Single().Content == "合成来源", "原始认知及经历完整保留");

            var failed = false;
            try { await IdentityReorganizationLogic.GenerateAsync(store, root, "relation", "", new AgentSequenceLlm(
                ReorganizationAssessment("r1", "r2")), CancellationToken.None, true); }
            catch (InvalidOperationException) { failed = true; }
            Require(failed && IdentityReorganizationLogic.Read(store, root, "relation").batchesDone == 1, "成稿中断仍保存归并检查点");
            var resume = new AgentSequenceLlm(ReorganizationProposal("r1", "r2"));
            draft = await IdentityReorganizationLogic.GenerateAsync(store, root, "relation", "", resume, CancellationToken.None);
            Require(resume.Requests.Count == 1 && draft.status == "ready", "断线续做只请求成稿，不重复归并");
            using (var db = new SQLite.SQLiteConnection(path)) db.Execute("UPDATE cognition_slices SET Summary='已修订' WHERE Id='r2'");
            failed = false;
            try { IdentityReorganizationLogic.Apply(store, root, "relation", draft.id); } catch (InvalidOperationException) { failed = true; }
            Require(failed && store.LoadIdentityCards(root).Single(x => x.Slot == "relation").Revision == applied.Revision, "资料修订后拒绝旧候选");

            foreach (var invalid in new[] { ReorganizationAssessment("r1"), ReorganizationAssessment("r1", "r1"), ReorganizationAssessment("r1", "unknown") })
            {
                failed = false;
                try { await IdentityReorganizationLogic.GenerateAsync(store, root, "relation", "", new AgentSequenceLlm(invalid), CancellationToken.None, true); }
                catch (InvalidOperationException) { failed = true; }
                Require(failed, "遗漏、重复、未知认知均拒绝");
            }
            var detailsAssessment = TraceJson.ToJson(new {
                understandings = new[] { new { meaning = "形成认识", scope = "特定条件", exceptions = "有例外", certainty = "初步", cognition_ids = new[] { "r1" } } },
                details = new[] { new { id = "r2", reason = "目前只表达一次经历" } } });
            var detailDraft = await IdentityReorganizationLogic.GenerateAsync(store, root, "relation", "",
                new AgentSequenceLlm(detailsAssessment, ReorganizationProposal("r1")), CancellationToken.None, true);
            Require(detailDraft.details.Single().id == "r2", "经历细节留在来源层，正文由理解形成");
            detailDraft.proposal.cognition_ids = new List<string> { "r2" };
            failed = false;
            try { IdentityReorganizationLogic.ValidateProposal(detailDraft); } catch (InvalidOperationException) { failed = true; }
            Require(failed, "不能绕过理解归并把细节当作正文依据");
            store.SetIdentityPinned(root, "relation", true);
            failed = false;
            try { await IdentityReorganizationLogic.GenerateAsync(store, root, "relation", "", new AgentSequenceLlm(), CancellationToken.None); }
            catch (InvalidOperationException) { failed = true; }
            Require(failed, "固定卡在模型调用前拦截");
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("Identity reorganization checks passed: full source coverage, private scope, preview, provenance, resumable generation, stale rejection and idempotent apply.");
    }
}
