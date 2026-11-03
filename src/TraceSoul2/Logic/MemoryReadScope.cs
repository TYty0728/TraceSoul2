using System;
using System.Globalization;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic;

/// <summary>模型选择语义范围，程序把日期规范化为北京时间的左闭右开区间。</summary>
public sealed class MemoryReadScope
{
    public string Kind { get; private set; }
    public string DayBasis { get; private set; }
    public DateTimeOffset? Start { get; private set; }
    public DateTimeOffset? End { get; private set; }
    public bool HasDate => Start.HasValue;
    public long StartMs => Start?.ToUnixTimeMilliseconds() ?? 0;
    public long EndMs => End?.ToUnixTimeMilliseconds() ?? long.MaxValue;
    public bool Contains(long timestamp) => timestamp > 0 && timestamp >= StartMs && timestamp < EndMs;

    public static DateTimeOffset ReferenceTime(TraceTurnContext turn) => turn.Moment?.CreatedUnixMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(turn.Moment.CreatedUnixMs).ToOffset(MemoryDayLogic.ChinaOffset)
        : DateTimeOffset.UtcNow.ToOffset(MemoryDayLogic.ChinaOffset);

    public static MemoryReadScope Parse(BrainCapabilityCallData call, DateTimeOffset reference)
    {
        string Arg(string name) => (call.GetArgument(name) ?? "").Trim();
        var kind=Arg("kind"); if (kind.Length==0) kind="auto";
        if (kind is not ("auto" or "events" or "cognitions" or "sources"))
            throw new ArgumentException("kind须为auto/events/cognitions/sources。");
        var basis=Arg("day_basis"); if (basis.Length==0) basis="calendar";
        if (basis is not ("calendar" or "memory")) throw new ArgumentException("day_basis须为calendar或memory。");
        var period=Arg("period"); var from=Arg("date_start"); var to=Arg("date_end");
        if (period.Length>0 && (from.Length>0 || to.Length>0)) throw new ArgumentException("period和显式日期不能同时使用。");
        if ((from.Length==0)!=(to.Length==0)) throw new ArgumentException("date_start/date_end须同时提供，单日请填相同日期。");
        var scope=new MemoryReadScope {Kind=kind,DayBasis=basis};
        var local=reference.ToOffset(MemoryDayLogic.ChinaOffset);
        var today=basis=="memory" ? MemoryDayLogic.CurrentStart(local).Date : local.Date;
        DateTime start=default, end=default;
        if (from.Length>0)
        {
            if (!DateTime.TryParseExact(from,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out start) ||
                !DateTime.TryParseExact(to,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out end) || end<start)
                throw new ArgumentException("日期须为有效的yyyy-MM-dd，结束日不能早于开始日。");
            if (end.Year>=9999) throw new ArgumentException("日期超出支持范围。");
            end=end.AddDays(1);
        }
        else if (period.Length>0)
        {
            var monday=today.AddDays(-(((int)today.DayOfWeek+6)%7));
            var month=new DateTime(today.Year,today.Month,1);
            switch (period)
            {
                case "today": start=today;end=today.AddDays(1);break;
                case "yesterday": start=today.AddDays(-1);end=today;break;
                case "day_before_yesterday": start=today.AddDays(-2);end=today.AddDays(-1);break;
                case "this_week": start=monday;end=monday.AddDays(7);break;
                case "last_week": start=monday.AddDays(-7);end=monday;break;
                case "this_month": start=month;end=month.AddMonths(1);break;
                case "last_month": start=month.AddMonths(-1);end=month;break;
                case "this_year": start=new DateTime(today.Year,1,1);end=start.AddYears(1);break;
                case "last_year": end=new DateTime(today.Year,1,1);start=end.AddYears(-1);break;
                default: throw new ArgumentException("未知period；可用today/yesterday/day_before_yesterday/this_week/last_week/this_month/last_month/this_year/last_year，或明确日期。");
            }
        }
        if (period.Length>0 || from.Length>0)
        {
            var hour=basis=="memory" ? MemoryDayLogic.BoundaryHour : 0;
            scope.Start=new DateTimeOffset(start.AddHours(hour),MemoryDayLogic.ChinaOffset);
            scope.End=new DateTimeOffset(end.AddHours(hour),MemoryDayLogic.ChinaOffset);
            if (scope.StartMs<0) throw new ArgumentException("暂不支持1970年以前的日期。");
        }
        if (!scope.HasDate && Arg("day_basis").Length>0) throw new ArgumentException("day_basis须与日期范围一起使用。");
        if (kind=="cognitions" && scope.HasDate)
            throw new ArgumentException("当前认知不是过去某天的版本。按日期查证请用kind=events或sources，不能把认知写入日期当作事实发生日期。");
        if (kind=="auto" && scope.HasDate) scope.Kind="events";
        return scope;
    }

    public string Description => "读取类型：" + (Kind switch {"events"=>"事件与同期原文","sources"=>"原始记录","cognitions"=>"当前认知",_=>"综合"}) + "；" + (HasDate
        ? "范围：["+Start.Value.ToString("yyyy-MM-dd HH:mm zzz")+", "+End.Value.ToString("yyyy-MM-dd HH:mm zzz")+")；"+
          (DayBasis=="memory" ? "记忆日（04:00切日）" : "自然日（00:00切日）")
        : "未限定日期");

    public static string Instructions(TraceTurnContext turn) => "\n【记忆读取范围】\n日期基准（本轮输入时刻）："+
        ReferenceTime(turn).ToString("yyyy-MM-dd HH:mm zzz")+"。先确定问题范围，再调用memory.recall。\n"+
        "具体哪天发生什么：kind=events，填写period或date_start/date_end；昨天用period=yesterday，query写要找的事情如洗头。"+
        "日常日期默认北京时间自然日；明确问归档的记忆日才用day_basis=memory。含糊日期先澄清，不猜日期。\n"+
        "稳定喜好或认识：先从主题候选或memory.topics选择如口味偏好的主题，kind=cognitions并传tags，不必限定日期。"+
        "日期和主题可组合，按交集读取；要查原话可用kind=sources。空结果不等于没发生，不擅自放宽范围。"+
        "上下文中的榜单、当前状态、近期观察只是背景，不能替代所问日期的查证；若已有明确原话足够回答可直接引用。\n";
}
