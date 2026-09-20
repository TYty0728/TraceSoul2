using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TraceSoul2.Host;

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
        log.Dispose();
        Require(second.Reader.Completion.IsCompleted, "宿主关闭应结束订阅");
    }

    private static List<string> Drain(RecentEventLog.Subscription subscription)
    {
        var lines = new List<string>();
        while (subscription.Reader.TryRead(out var line)) lines.Add(line);
        return lines;
    }
}
