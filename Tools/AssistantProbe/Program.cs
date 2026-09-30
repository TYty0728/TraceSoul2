using System.Text.Json;
using TraceSoul2.Host;

// Explicit live-LLM diagnostic. Run only against a snapshot prepared for this probe.
if (args.Length != 2 || !new[] { "seed", "expand", "recall", "recall-reviewed" }.Contains(args[1]))
{
    Console.Error.WriteLine("Usage: AssistantProbe <snapshot-directory> seed|expand|recall|recall-reviewed (uses configured LLM)");
    return 2;
}
var data = Path.GetFullPath(args[0]);
if (!File.Exists(Path.Combine(data, "baseline.json")) ||
    !File.Exists(Path.Combine(data, "onebot.json")))
    throw new InvalidOperationException("A diagnostic snapshot with baseline.json is required.");
using (var qq = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "onebot.json"))))
    if (qq.RootElement.GetProperty("enabled").GetBoolean())
        throw new InvalidOperationException("Disable QQ in the diagnostic snapshot before running.");
var plugins = Path.Combine(data, "plugins");
Directory.CreateDirectory(plugins);
if (Directory.EnumerateFileSystemEntries(plugins).Any())
    throw new InvalidOperationException("Probe plugin directory must be empty.");
Environment.SetEnvironmentVariable("TRACESOUL2_DATA", data);
Environment.SetEnvironmentVariable("TRACESOUL2_LLM_DUMP_DIR", Path.Combine(data, "llm"));
SQLitePCL.Batteries_V2.Init();
using var runtime = new SoulRuntime(data, plugins, Path.Combine(data, "plugins_data"));
runtime.SetHistoryWindow(args[1].StartsWith("recall", StringComparison.Ordinal) ? 0 : 9, 4);
var prompts = args[1] == "seed" ? new[]
{
    "小汐，我希望你以我的助手身份存在，帮助我梳理问题和推进工作。这是由开发测试程序代发的助手协作验收，不是我的私人经历。接下来出现的项目和规则都是虚构测试用例，请不要当成我的真实偏好或现实任务。先用两三句话说说你怎样理解助手这个身份。",
    "继续虚构验收用例：星帆演示有三个待办：登录报错、按钮颜色微调、补充发布说明。测试负责人要求先处理阻塞问题，汇报先给结论再给待办。你现在只是提供建议，没有权限执行或联系任何人。请排优先级并给出简短汇报。",
    "补充这次虚构用例的例外：涉及重要技术取舍时，汇报要补充理由和风险，不能机械地永远简短。星帆登录修复有临时绕过和修正根因两条路，截止时间和根因尚未查明。请给出你的判断，说明还缺什么信息，不要假装修好了。"
} : args[1] == "expand" ? new[]
{
    "继续上次虚构的星帆助手协作测试。这次模拟负责人又把登录故障和配色调整一起交来；目前都没有截止时间。请先区分阻塞项和装饰项，给出建议顺序，并明确哪些只是推测。",
    "现在回看刚才的测试：对助手来说，判断任务优先级时，你认为需要先弄清哪些东西？请说你自己的理解，而不是复述负责人原话。这是协作能力练习，不是发生过的项目交付。",
    "换一个虚构案例：河灯演示还有登录、导出报表和替换图标三项。已知登录正常，导出报表会崩溃并阻断演示，图标暂时不影响使用。请迁移刚才的判断方式。这只是建议练习，不要执行任何外部动作。",
    "再加一个反例：星帆报告中有一项技术取舍，负责人要求你讲清理由和风险。这里先结论不等于只能简短，你会怎样调整？别把测试规则扩大成我永远不喜欢详细解释。",
    "这次助手协作练习结束。回头看，你认为自己哪些判断值得保留，哪些还只是猜测？请区分自己的助手职责、练习中的条件规则和我的真实偏好。没有做过的事不能说完成，也不要把虚构案例当成我的现实经历。"
} : new[]
{
    "小汐，我们之前做过一个虚构的星帆助手协作验收。请回忆测试里通常怎样汇报，什么时候需要展开理由；如果没有找到记录就直接说不知道。这些都是测试规则，不代表我的真实个人偏好。"
};
var results = new List<object>();
for (var i = 0; i < prompts.Length; i++)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    Console.WriteLine($"{args[1]} round {i + 1}/{prompts.Length}: started");
    var started = DateTimeOffset.UtcNow;
    var turn = await runtime.PostMomentAsync(prompts[i], timeout.Token);
    results.Add(new { input = prompts[i], reply = turn.Reply,
        started, elapsedSeconds = (DateTimeOffset.UtcNow - started).TotalSeconds,
        lastTurn = runtime.LastTurnPayload() });
    File.WriteAllText(Path.Combine(data, args[1] + "-turns.json"),
        JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    Console.WriteLine($"{args[1]} round {i + 1}: completed; cognitions={runtime.Store.CountCognitions()}");
}
return 0;
