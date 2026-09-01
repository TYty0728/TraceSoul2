namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>心里状态插件提示词与注入片段。</summary>
    public static class InnerLifePrompts
    {
        public const string SnapshotTitle = "我此刻";
        public const string SnapshotPrefix = "我此刻和她共同处在：";
        public const string MoodWrapPrefix = "（情绪：";
        public const string HoldPrefix = "；此刻还留着一点余波：";
        public const string InspectWhenToUse =
            "关系冲突、强烈感受、身份连续性或心里反复浮起的碎片会影响当前判断时。";
        public const string InspectWhenNotToUse = "一句话心里状态已经足够的普通对话。";
        public const string InspectDescription = "读取情绪、关系视角、共享场景和此刻留在自己身上的一点余波；余波不是待办，也不是她欠下的回答。";
        public const string FacetDescription =
            "每轮固定陪伴同伴（Brain＝正在思考的本人）的一句话自我状态；记录这一刻确实发生的感受，也容得下没由来的想念、好奇和忽然浮起的过去。旧余波会自然沉下去，不会变成待办。";
    }
}
