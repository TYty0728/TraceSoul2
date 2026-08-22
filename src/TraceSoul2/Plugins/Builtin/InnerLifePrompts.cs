namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>心里状态插件提示词与注入片段。</summary>
    public static class InnerLifePrompts
    {
        public const string SnapshotTitle = "我此刻";
        public const string SnapshotPrefix = "我此刻：";
        public const string MoodWrapPrefix = "（情绪：";
        public const string UnfinishedPrefix = "；未完成：";
        public const string HoldPrefix = "；手上：";
        public const string InspectWhenToUse =
            "关系冲突、强烈感受、身份连续性或未完成意图会影响当前判断时。";
        public const string InspectWhenNotToUse = "一句话心里状态已经足够的普通对话。";
        public const string InspectDescription = "读取情绪、关系视角、进行中活动、未完成意图和注意项。";
        public const string FacetDescription =
            "每轮固定陪伴同伴（Brain＝正在思考的本人）的一句话自我状态；只有确实变化时才写回。";
    }
}
