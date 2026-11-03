using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TraceSoul2.Util;

/// <summary>运行日志的可读文本协议；保留轮次和技术编号，不记录请求/响应正文。</summary>
public static class TraceLogFormat
{
    public static string Format(string traceId, string stage, long? elapsedMs = null, string detail = null, string level = null)
    {
        var message = "[" + (level ?? Level(stage)) + "]";
        if (!string.IsNullOrWhiteSpace(traceId)) message += " [轮次 " + SingleLine(traceId) + "]";
        message += " " + SingleLine(stage);
        if (elapsedMs.HasValue) message += " ｜ 耗时 " + Duration(elapsedMs.Value);
        if (!string.IsNullOrWhiteSpace(detail)) message += " ｜ " + Detail(detail);
        return SingleLine(message);
    }

    public static string Normalize(string message)
    {
        var value = SingleLine(message);
        if (Regex.IsMatch(value, @"^\[(信息|调试|警告|错误)\]")) return value;
        var legacy = Regex.Match(value, @"^\[链路 ([^\]]+)\]\s*(.*)$");
        if (legacy.Success)
            return Format(legacy.Groups[1].Value == "--------" ? null : legacy.Groups[1].Value, legacy.Groups[2].Value);
        return Format(null, value);
    }

    // 仅给未声明级别的旧日志提供兼容分类；新入口可显式指定级别。
    public static string Level(string stage) => Regex.IsMatch(stage ?? "", "失败|异常|错误|拦截|未完成|未成功|未就绪|未安排") ? "警告" : "信息";
    public static string SingleLine(string value)
    {
        var text = Regex.Replace(value ?? "", @"\x1b\[[0-?]*[ -/]*[@-~]", "");
        text = Regex.Replace(text, @"[\s\p{Cc}]+", " ").Trim();
        return Regex.Replace(text, @"\s*｜\s*", " ｜ ");
    }
    public static string Duration(long milliseconds) => milliseconds < 1000
        ? Math.Max(0, milliseconds).ToString(CultureInfo.InvariantCulture) + " ms"
        : (milliseconds / 1000d).ToString("0.00", CultureInfo.InvariantCulture) + " s";

    public static string Status(string value) => value switch {
        "success" or "completed" => "成功", "failed" => "失败", "empty" => "无结果", "skipped" => "已跳过",
        "running" => "进行中", "accepted" => "已受理", "cancelled" => "已取消", _ => value ?? "未知"
    };

    private static string Detail(string value) => Regex.Replace(SingleLine(value), @"(^|[｜|])\s*([a-z_]+)=([^｜|]*)", match => {
        var key = match.Groups[2].Value;
        var data = match.Groups[3].Value.Trim();
        var label = key switch {
            "status" => "状态", "source" => "来源", "results" => "结果数", "chars" => "字符数",
            "messages" => "消息数", "round" => "步骤", "events" => "事件数", "queue" => "排队",
            "lock" => "等待运行锁", "history" => "历史条数", "blocks" => "上下文块", "cached" => "缓存", "top_k" => "最多召回条数",
            _ => null
        };
        return (match.Groups[1].Length == 0 ? "" : " ｜ ") + (label == null ? key + "=" + data : label + "：" + (key == "status" ? Status(data) : data));
    });
}
