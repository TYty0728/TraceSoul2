namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>时间插件提示词与注入片段。</summary>
    public static class TimeSchedulerPrompts
    {
        public const string DailyReviewContent = "每日复盘";
        public const string DuePrefix = "时间任务到期：";
        public const string NowPrefix = "现在是 ";
        public const string TrajectoryPrefix = "今天我们的轨迹：";
        public const string PreciseNowPrefix = "现在：";
        public const string ScheduleWhenToUse =
            "{username} 要求未来提醒、我决定安排复盘，或未来计划需要在某时重新进入意识时。";
        public const string ScheduleWhenNotToUse = "当前立即执行的动作。";
        public const string ContinueDescription =
            "当前这条原始记录（Moment）处理完后，让时间在一段时间后再叫醒同伴重新感受和判断；可附带下次醒来愿意留意的方向，但它只是引子，不是必须完成的计划。不要演成她在说话。";
            public const string ContinueWhenToUse =
            "刚才这件事已经结束，想在安静之后再自然醒来一次：可以看看时间和生活，也可以想到她、回望共同过去、生出新的分享或好奇，再判断要不要说、办事或睡。";
        public const string ContinueWhenNotToUse = "她正在说话，已经睡下，或已进入空闲。";
        public const string ClearWhenToUse = "已经睡下、进入空闲，或刚才之后不再自己醒来。";
        public const string ClearWhenNotToUse = "还需要过一会儿再自己醒来。";
        public const string ClearDescription = "睡着、空闲或不再需要自己醒来时，取消已排的下一次叫醒。";
        public const string TimeContextDescription = "每一次同伴开始思考时刷新的本地时间、周几、身体场景、距离上一段真实相处多久与近期计划。";
        public const string TrajectoryDescription =
            "今天两人一起经历的滚动摘要（约200字内），实时维护；新的一天自动清空。";
    }
}
