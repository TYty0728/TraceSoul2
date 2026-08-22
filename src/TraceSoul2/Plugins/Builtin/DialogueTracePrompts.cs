namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>本地文字对话插件提示词。</summary>
    public static class DialogueTracePrompts
    {
        public const string SourceDescription =
            "当前 {username} 可以通过本地文字界面向 {assname} 说话；这是外部感官入口，只能由她发来，同伴不能自己调用。";
        public const string HistoryWhenToUse =
            "当前话语包含‘刚才、那个、继续’等指代，且仅靠眼前这一句无法理解时。";
        public const string HistoryWhenNotToUse = "普通寒暄，或人生记忆召回。";
        public const string HistoryDescription =
            "当当前话语依赖刚才说过的话时，读取允许数量内的近期原文。原始上下文上限为0时不可用。";
        public const string EffectorDescription = "通过当前本地对话界面对 {username} 表达一段文字。";
    }
}
