using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TraceSoul2.Host;
using TraceSoul2.Util;
using TraceSoul2.Plugins;
using TraceSoul2.Manager;
using TraceSoul2.Data;
using System.Threading;

internal static partial class Program
{
    private static void RunRecentEventLogCheck()
    {
        using var log = new RecentEventLog();
        for (var i = 0; i < 350; i++) log.Emit("event=" + i);
        using var first = log.Subscribe();
        var replay = Drain(first);
        Require(replay.Count == 300 && replay[0].EndsWith("event=50") && replay.Last().EndsWith("event=349"),
            "打开网页前的日志也应保留最近 300 条，按发生顺序回放");
        log.Emit("live");
        Require(Drain(first).Single().EndsWith("live"), "回放后应继续收到实时事件");
        using var second = log.Subscribe();
        var reconnect = Drain(second);
        Require(reconnect.Count == 300 && reconnect.Last().EndsWith("live"), "刷新或第二个页面应得到同一份最近日志");
        Parallel.For(0, 400, i => log.Emit("parallel=" + i));
        Require(Drain(first).Count == 300 && Drain(second).Count == 300, "慢订阅者也只保留最新 300 条，不能阻塞运行");
        first.Dispose();
        log.Emit("after-dispose");
        Require(Drain(second).Single().EndsWith("after-dispose"), "关闭一个页面不能影响另一个页面");

        foreach (var encoded in new[] { "data:image/png;base64," + new string('A', 10000),
                     "base64://" + new string('B', 10000), new string('C', 10000),
                     string.Concat(Enumerable.Repeat(@"AB\u002BCD", 1000)) })
        {
            var clean = RecentEventLog.Sanitize("相机失败｜" + encoded + "｜status=400\n结束");
            Require(clean.Length < 100 && clean.Contains("status=400") && !clean.Contains('\n'),
                "图片编码应整个移除、错误信息保留，一条事件不产生大量换行");
        }
        Require(RecentEventLog.Sanitize("识图失败 HTTP 400 Unsupported MIME").Contains("Unsupported MIME"),
            "正常诊断文本不能被隐藏");
        var timing = new List<string>();
        using var store = new SqliteMemoryManager(":memory:");
        var services = new TracePluginServices(store, new TraceSoul2.Logic.HierarchicalVectorRouterLogic(new BagOfCharsVectorEncoder())) { TimingLog = timing.Add };
        services.LogTiming("abc123", "执行返回：网页读取", 1250, "status=running｜chars=50");
        services.LogDiagnostic("abc123", "上下文已读取", 0, "identity.base");
        services.LogTiming("abc123", "上下文读取失败", 42, "模拟错误");
        Require(timing[0].Contains("[轮次 abc123]") && timing[0].Contains("1.25 s") && timing[0].Contains("状态：进行中") &&
            !timing[0].Contains("status="), "正常运行日志关联轮次并显示真实状态与可读耗时");
        Require(timing[1].StartsWith("[调试]") && timing[2].StartsWith("[警告]"), "内部过程与异常分级，不将失败隐藏为调试");
        Require(!TraceLogFormat.Format(null,"宿主已启动").Contains("--------") &&
            TraceLogFormat.Normalize("[链路 abc123] 旧插件阶段").Contains("[轮次 abc123]"), "缺少轮次不伪造编号，旧插件前缀仍兼容");
        log.Emit(timing[0]);
        var formatted=Drain(second).Single();
        Require(formatted.Contains(DateTimeOffset.Now.ToString("yyyy-MM-dd")) && formatted.Split("[信息]").Length==2,
            "SSE回放有日期且已格式化日志不重复套级别");
        Require(!TraceLogFormat.Format("id\n伪造", "阶段\t继续", detail:"内容\r\n第二行").Contains('\n'),
            "换行与控制字符不能破坏单行日志");
        timing.Clear();
        using (var plugins=new TracePluginManager(store,services))
        {
            plugins.RegisterExternal(new LogFaultFacet(new InvalidOperationException("模拟读取失败")));
            var turn=new TraceTurnContext("log-test",new MomentRecord{Id="log-source",Content="测试"},new(),0,false,services);
            try { plugins.BuildContextBlocksAsync(turn,CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("应抛出上下文错误"); }
            catch(InvalidOperationException) { }
            Require(timing.Count==1 && timing[0].Contains("上下文读取失败") && timing[0].Contains("log.fault") &&
                !timing[0].Contains("完成"),"实际上下文读取异常只能报告失败，不能在finally报告完成");
            timing.Clear();
            plugins.ApplyFacetOutputsAsync(new List<BrainFacetOutputData>(),turn,CancellationToken.None).GetAwaiter().GetResult();
            Require(timing.Count==1 && timing[0].StartsWith("[警告]") && timing[0].Contains("模拟写回失败"),
                "返回failed但不抛异常的状态更新仍须显示警告，不能只记录调试完成");
        }
        RunTimedLogChecksAsync().GetAwaiter().GetResult();
        log.Dispose();
        Require(second.Reader.Completion.IsCompleted, "宿主关闭应结束订阅");
    }

    private static async Task RunTimedLogChecksAsync()
    {
        var rows=new List<string>();
        var mock=new LogProbeLlm();
        var client=new TimedLlmClient(mock,"log-turn",rows.Add);
        var input=new List<DeepSeekMessageData>{new(){role="user",content="私密输入标记"}};
        Require(await client.CompleteJsonAsync(input)=="私密输出标记", "计时包装不更改返回内容");
        Require(rows.Count==2 && rows.All(x=>x.Contains("[轮次 log-turn]")) &&
            !rows.Any(x=>x.Contains("私密")), "模型日志保留开始与完成但不抄写输入输出正文");
        rows.Clear(); mock.Error=new InvalidOperationException("模拟HTTP 503");
        try { await client.CompleteTextAsync(input); throw new Exception("应抛出失败"); }
        catch(InvalidOperationException) { }
        Require(rows.Count==2 && rows.Last().StartsWith("[错误]") && rows.Last().Contains("HTTP 503") &&
            !rows.Any(x=>x.Contains(" 完成")), "模型错误保留诊断和轮次，不额外报告完成");
        rows.Clear(); using var cancel=new CancellationTokenSource(); cancel.Cancel();
        mock.Error=new OperationCanceledException(cancel.Token);
        try { await client.CompleteJsonAsync(input,cancel.Token); throw new Exception("应抛出取消"); }
        catch(OperationCanceledException) { }
        Require(rows.Last().Contains("已取消") && !rows.Last().Contains("[错误]"), "调用方取消与模型失败区分");
    }

    private sealed class LogFaultFacet : ITracePlugin,ITraceMountedFacet
    {
        private readonly Exception failure;
        // 无默认构造器，避免其它回归的插件自动发现误加载故障夹具。
        public LogFaultFacet(Exception failure) { this.failure=failure; }
        public TracePluginMetadataData Metadata {get;}=new(){Id="log.fault",DisplayName="模拟故障",Version="1.0.0"};
        public TraceContributionDescriptorData Descriptor {get;}=new(){Id="log.fault",Kind=TraceContributionKindValues.MountedFacet};
        public bool IsAvailable(TraceTurnContext turn)=>true;
        public void Register(TracePluginContext context)=>context.AddMountedFacet(this);
        public void Shutdown() { }
        public Task<TraceContextBlockData> BuildContextAsync(TraceTurnContext turn,CancellationToken token)=>throw failure;
        public Task<TraceCapabilityResultData> ApplyOutputAsync(BrainFacetOutputData output,TraceTurnContext turn,CancellationToken token)=>Task.FromResult(new TraceCapabilityResultData{Status="failed",Summary="模拟写回失败"});
    }

    private sealed class LogProbeLlm : ILlmClient
    {
        public Exception Error;
        public string ProviderId=>"probe";
        public string Model=>"probe";
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages,CancellationToken cancellationToken=default,string promptCacheKey=null)
            => Error==null ? Task.FromResult("私密输出标记") : Task.FromException<string>(Error);
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages,CancellationToken cancellationToken=default,string promptCacheKey=null)
            =>CompleteJsonAsync(messages,cancellationToken,promptCacheKey);
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<string>>(new[]{Model});
    }

    private static List<string> Drain(RecentEventLog.Subscription subscription)
    {
        var lines = new List<string>();
        while (subscription.Reader.TryRead(out var line)) lines.Add(line);
        return lines;
    }
}
