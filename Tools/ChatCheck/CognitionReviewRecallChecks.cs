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
    // 故意使用无共同汉字的同义表达，检查检索走注入的语义向量，不依赖字面匹配或最新顺序。
    private sealed class ReviewSemanticEncoder : IVectorEncoder
    {
        public string ModelId => "review-semantic-test";
        public int Dimensions => 4;
        public int IndexCalls;
        public float[] Encode(string text, VectorTextPurpose purpose)
        {
            if (purpose == VectorTextPurpose.Index) IndexCalls++;
            if (text.Contains("面包") || text.Contains("烘焙")) return new float[] { 1, 0, 0, 0 };
            if (text.Contains("冷饮") || text.Contains("冰水")) return new float[] { 0, 1, 0, 0 };
            return purpose == VectorTextPurpose.Index ? new float[] { 0, 0, 1, 0 } : new float[] { 0, 0, 0, 1 };
        }
    }

    private static void RunCognitionReviewRecallChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cognition-recall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var brain = Path.Combine(dir, "brain.db");
            using var store = new SqliteMemoryManager(brain);
            using var cache = new SqliteVectorManager(Path.Combine(dir, "vectors.db"));
            using var sql = new SQLite.SQLiteConnection(brain);
            var encoder = new ReviewSemanticEncoder();
            CognitionSliceRecord Node(string id, string summary, string scope = "private", string visibility = "private", string status = "active") =>
                new() { Id = id, Summary = summary, About = "偏好", Scope = "平日", Exceptions = "特殊情况另问", Domains = "user",
                    ContextConversationId = scope, MemoryVisibility = visibility, Status = status, UpdatedUnixMs = 1 };
            var old = Node("old", "他喜欢烘焙"); sql.Insert(old);
            var weak = Node("weak", "他喜欢冷饮", status: "weakened"); sql.Insert(weak);
            foreach (var n in new[] { Node("foreign", "他喜欢烘焙", "other"), Node("public", "他喜欢烘焙", "room", "public"),
                Node("retired", "他喜欢烘焙", status: "retired"), Node("superseded", "他喜欢烘焙", status: "superseded") }) sql.Insert(n);
            for (var i = 0; i < 65; i++) { var n = Node("new-" + i, "无关的认识"); n.UpdatedUnixMs = 100 + i; sql.Insert(n); }
            List<MomentRecord> Evidence(string text, string scope = "private", string visibility = "private") => new() {
                new() { Id = "evidence", ConversationId = scope, MemoryVisibility = visibility, Content = text } };
            var evidence = Evidence("今天想吃面包");
            var selected = CognitionReviewRecallLogic.Select(store, evidence, () => encoder, cache);
            Require(selected.Select(x => x.Id).SequenceEqual(new[] { "old" }) && store.GetCognitionNodes(40).All(x => x.Id != "old"),
                "语义检索找回最近40条以外的同义旧认知，不混入其他环境、已替代或退役节点");
            var count = encoder.IndexCalls;
            CognitionReviewRecallLogic.Select(store, evidence, () => encoder, cache);
            Require(encoder.IndexCalls == count, "未变正文复用本地持久向量缓存");
            old.Summary = "他喜欢冷饮"; sql.Update(old);
            Require(CognitionReviewRecallLogic.Select(store, evidence, () => encoder, cache).Count == 0 && encoder.IndexCalls > count,
                "同ID正文变动使缓存失效，无相关项不保底塞入最新认知");
            old.Summary = "他喜欢烘焙"; sql.Update(old);
            evidence = Evidence(new string('甲', 250) + "末尾说想喝冰水");
            evidence.AddRange(Evidence("今天想吃面包"));
            selected = CognitionReviewRecallLogic.Select(store, evidence, () => encoder, cache);
            Require(selected.Any(x => x.Id == "weak") && selected.Any(x => x.Id == "old"),
                "查询覆盖长原文末尾、多个主题和已削弱理解，不只嵌入开头");
            var longNode = Node("late", new string('乙', 280) + "他喜欢烘焙"); sql.Insert(longNode);
            Require(CognitionReviewRecallLogic.Select(store, Evidence("面包"), () => encoder, cache).Any(x => x.Id == "late"),
                "长旧认知末尾也参与语义检索，返回完整原文");
            var publicSelected = CognitionReviewRecallLogic.Select(store, Evidence("面包", "room", "public"), () => encoder, cache);
            Require(publicSelected.Select(x => x.Id).SequenceEqual(new[] { "public" }), "公开检索只读取本公开环境");
            var publicMoment = Evidence("面包", "room", "public").Single();
            publicMoment.Id = "public-proof"; publicMoment.CreatedUnixMs = 1; publicMoment.Role = "user";
            store.SaveMoment(publicMoment);
            var publicLlm = new AgentSequenceLlm(TraceJson.ToJson(new { cognitions = new[] { new BrainCognitionWriteData {
                operation = "reinforce", target_id = "public", evidence_moment_ids = new() { publicMoment.Id }, confidence = 0.7f } } }));
            Require(PublicExperienceLogic.BuildAsync(store, publicLlm, 0, 100, encoderFactory: () => encoder, cache: cache)
                .GetAwaiter().GetResult() == 1 && publicLlm.Requests.Single().Contains("他喜欢烘焙") &&
                store.GetCognitionEvidence(new[] { "public" }).Any(x => x.MomentId == publicMoment.Id),
                "公开复盘也在生成前召回旧理解并按真实依据补强原节点");
            var mixed = Evidence("面包"); mixed.AddRange(Evidence("面包", "room", "public"));
            var rejected = false;
            try { CognitionReviewRecallLogic.Select(store, mixed, () => encoder, cache); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "混合环境证据在编码前拒绝");
            for (var i = 0; i < 20; i++) sql.Insert(Node("large-" + i, "烘焙" + new string('长', 570)));
            selected = CognitionReviewRecallLogic.Select(store, Evidence("面包"), () => encoder, cache);
            Require(selected.Count <= CognitionReviewRecallLogic.CandidateLimit &&
                selected.Sum(x => CognitionReviewRecallLogic.Describe(x).Length + 1) + 2 <= CognitionReviewRecallLogic.TextBudget &&
                selected.All(x => x.Summary == sql.Find<CognitionSliceRecord>(x.Id).Summary),
                "候选数量和完整正文预算受控，不截断旧理解");
            rejected = false;
            try { CognitionReviewRecallLogic.Select(store, evidence, () => encoder, cache, token: new CancellationToken(true)); }
            catch (OperationCanceledException) { rejected = true; }
            Require(rejected, "取消检索不会开始复盘写入");
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("Cognition review recall checks passed: semantic candidates, scope, cache invalidation, complete text and budgets.");
    }

    private static void RunCognitionReviewBgeChecks(string migrationDirectory)
    {
        using var encoder = new TraceSoul2.Tools.Memory.OnnxBgeEncoder(
            Path.Combine(migrationDirectory, "Models", "BgeSmallZh", "bge-small-zh-v1.5.onnx"),
            Path.Combine(migrationDirectory, "Models", "BgeSmallZh", "vocab.txt"));
        var query = encoder.Encode("情绪低落的时候，陪伴和理解比马上解决问题更重要。", VectorTextPurpose.Query);
        var index = encoder.Encode("她难过时希望我先听她说完，不急着提建议。", VectorTextPurpose.Index);
        Require(VectorMathUtil.Cosine(query, index) >= 0.45f, "实际打包BGE模型可命中语义相近的旧理解，不使用远端API");
        Console.WriteLine("Cognition BGE check passed: packaged local model and semantic paraphrase.");
    }

}
