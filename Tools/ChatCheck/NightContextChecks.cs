using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

internal static partial class Program
{
    private static async Task RunNightContextChecksAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "night-context-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using var store = new SqliteMemoryManager(path);
            store.SavePairIdentity("小雨", "小光", "雨雨");
            var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
            var trigger = Moment("night-projection", "日终余温：2026-11-10");
            trigger.Role = "system_event";
            var turn = new TraceTurnContext("night-projection", trigger, new List<MomentRecord> {
                new() { Role = "小光", Content = "不应作为梦话示范的历史长回复" }
            }, 6, false, services, KernelWakeValues.NightResidue);
            store.SaveMoment(new MomentRecord { Id = "old-night-evidence", ConversationId = turn.ConversationId,
                Content = "历史理解依据", Role = "user", MemoryVisibility = "private" });
            store.CommitCognitions("old-night-evidence", new[] { new BrainCognitionWriteData {
                operation = "create", summary = "不应自动追加到梦话的旧认知", domains = new() { "ass" }, identity_slot = "self",
                evidence_moment_ids = new() { "old-night-evidence" }, confidence = .7f, strength = .5f } });
            Require(IdentityProjectionLogic.Build(turn).Contains("不应自动追加到梦话的旧认知"),
                "探针旧认知确实会进入普通身份补充视图");
            var doc = new GoalMemoryLogic.GoalDocument();
            for (var i = 0; i < 32; i++) doc.Items.Add(new() {
                Id = "stored-id-" + i, Kind = i == 0 ? "preference" : "goal",
                Content = i == 0 ? "不要用追问结束心里话" : "候选计划" + i,
                AppliesWhen = i == 0 ? "表达感想时" : "对应项目安排时",
                Horizon = "ongoing", Status = "active", Origin = "user",
                SourceMomentId = "evidence-" + i, EvidenceQuote = "不应进入梦话的原始私密依据",
                UpdatedUnixMs = DateTimeOffset.Parse("2026-10-30T12:00:00+08:00").ToUnixTimeMilliseconds()
            });
            var saved = JsonSerializer.Serialize(doc, new JsonSerializerOptions { IncludeFields = true });
            store.SavePluginDocument("kernel.goals", turn.ConversationId, saved);
            var seed = new NightResidueSeed { DayKey = "2026-11-10", Events = new() { "她和我商量项目安排" } };
            var llm = new NightProjectionLlm(new[] { "{\"selected_refs\":[1,3]}" }, new[] { "我后来才明白，能一起商量就让我安心。" });
            var result = await new ExpressorLogic(llm).ExpressNightResidueAsync(turn, new List<TraceContextBlockData>(), seed,
                Array.Empty<TraceContributionDescriptorData>(), default);
            var prompt = llm.TextPrompts.Single();
            Require(result.should_express && llm.JsonPrompts.Count == 1 && prompt.Contains("不要用追问") &&
                prompt.Contains("候选计划2") && !prompt.Contains("候选计划1\"") &&
                !prompt.Contains("stored-id-") && !prompt.Contains("原始私密依据") &&
                !prompt.Contains("历史长回复") && !prompt.Contains("不应自动追加到梦话的旧认知") && prompt.Contains("recorded_at") &&
                llm.JsonPrompts[0].Contains("2026-10-30") && llm.JsonPrompts[0].Contains("候选计划31") &&
                !llm.JsonPrompts[0].Contains("原始私密依据"), "夜间选择覆盖完整目录，表达仅带选中项及日期，不带管理证据和近期长回复");
            Require(GoalMemoryLogic.BuildContext(turn).Contains("候选计划31") &&
                GoalMemoryLogic.BuildContext(turn).Contains("recorded_at") &&
                store.LoadPluginDocument("kernel.goals", turn.ConversationId) == saved,
                "夜间读取不得改变目标状态，普通Agent仍读取完整有效目录和时间锚");

            var all = new NightProjectionLlm(new[] { JsonSerializer.Serialize(new { selected_refs = Enumerable.Range(1, 32) }) });
            var allContext = await GoalMemoryLogic.BuildNightContextAsync(turn, "当日材料", all, default);
            Require(allContext.Contains("候选计划31") && allContext.Contains("不要用追问"), "不得用top-N裁掉模型判断适用的边界");
            var repair = new NightProjectionLlm(new[] { "{\"selected_refs\":[33]}", "{\"selected_refs\":[1]}" });
            Require((await GoalMemoryLogic.BuildNightContextAsync(turn, "当日材料", repair, default)).Contains("不要用追问") &&
                repair.JsonPrompts.Count == 2, "越界选择仅一次纠正并保留原输出");
            var bad = new NightProjectionLlm(new[] { "{\"selected_refs\":[1,1]}", "{\"selected_refs\":[0]}" });
            try { await GoalMemoryLogic.BuildNightContextAsync(turn, "当日材料", bad, default); throw new Exception("应拒绝无效选择"); }
            catch (InvalidOperationException) { }
            Require(bad.JsonPrompts.Count == 2 && store.LoadPluginDocument("kernel.goals", turn.ConversationId) == saved,
                "选择失败不能回退全量注入或删除有效目标");

            // No records means no selector call. Long prose gets one complete rewrite, never substring.
            var empty = new TraceTurnContext("night-empty", trigger, new List<MomentRecord>(), 0, false, services, KernelWakeValues.NightResidue);
            var longText = new string('想', 656) + "。";
            var shorter = "这件事让我重新理解了陪伴。";
            var compact = new NightProjectionLlm(text: new[] { longText, shorter });
            var spoken = await new ExpressorLogic(compact).ExpressNightResidueAsync(empty, new List<TraceContextBlockData>(), seed,
                Array.Empty<TraceContributionDescriptorData>(), default);
            Require(spoken.reply == shorter && compact.JsonPrompts.Count == 0 && compact.TextPrompts.Count == 2 &&
                compact.TextPrompts[1].Contains(longText) && compact.TextPrompts[1].Contains("657字符"),
                "657字符原稿仅压缩一次，保留原稿和实测长度，不发送500字符切片");
            foreach (var failure in new[] { longText, "无", "" })
            {
                var failing = new NightProjectionLlm(text: new[] { longText, failure });
                try {
                    await new ExpressorLogic(failing).ExpressNightResidueAsync(empty, new List<TraceContextBlockData>(), seed,
                        Array.Empty<TraceContributionDescriptorData>(), default);
                    throw new Exception("不能接受超长或空压缩");
                } catch (InvalidOperationException) { }
                Require(failing.TextPrompts.Count == 2, "压缩失败有界停止，不无限重试");
            }
            var quiet = new NightProjectionLlm(text: new[] { "无" });
            Require(!(await new ExpressorLogic(quiet).ExpressNightResidueAsync(empty, new List<TraceContextBlockData>(), seed,
                Array.Empty<TraceContributionDescriptorData>(), default)).should_express, "初次自然沉默仍有效");

            doc.Items[0].ExpiresUnixMs = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
            doc.Items[1].Horizon = "turn";
            doc.Items[2].Status = "completed";
            store.SavePluginDocument("kernel.goals", turn.ConversationId, JsonSerializer.Serialize(doc, new JsonSerializerOptions { IncludeFields = true }));
            var filtered = new NightProjectionLlm(new[] { "{\"selected_refs\":[]}" });
            Require(await GoalMemoryLogic.BuildNightContextAsync(turn, "材料", filtered, default) == "" &&
                !filtered.JsonPrompts[0].Contains("不要用追问") && !filtered.JsonPrompts[0].Contains("候选计划1\"") &&
                !filtered.JsonPrompts[0].Contains("候选计划2\""), "过期、本轮外和已完成记录不进入选择；允许全部无关");
        }
        finally { Delete(path); Delete(path + "-wal"); Delete(path + "-shm"); }
        Console.WriteLine("Night context checks passed.");
    }

    private sealed class NightProjectionLlm : ILlmClient
    {
        private readonly Queue<string> json, text;
        public readonly List<string> JsonPrompts = new(), TextPrompts = new();
        public string ProviderId => "night-projection-check";
        public string Model => "night-projection-check";
        public NightProjectionLlm(IEnumerable<string> json = null, IEnumerable<string> text = null)
        { this.json = new(json ?? Array.Empty<string>()); this.text = new(text ?? Array.Empty<string>()); }
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
        { cancellationToken.ThrowIfCancellationRequested(); JsonPrompts.Add(string.Join("\n", messages.Select(m => m.content))); return Task.FromResult(json.Dequeue()); }
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
        { cancellationToken.ThrowIfCancellationRequested(); TextPrompts.Add(string.Join("\n", messages.Select(m => m.content))); return Task.FromResult(text.Dequeue()); }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { Model });
    }
}
