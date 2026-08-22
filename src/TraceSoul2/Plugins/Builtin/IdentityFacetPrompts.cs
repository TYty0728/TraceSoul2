namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>身份插件提示词。复盘正文在 CorePrompts.IdentityReview。</summary>
    public static class IdentityFacetPrompts
    {
        public const string ReviewWhenToUse =
            "每日复盘到期，或今天的相处明显改变了自我理解、对 {username} 的理解、或关系定义时。";
        public const string ReviewWhenNotToUse =
            "普通对话、寒暄、只是一件生活事实。身份短卡不是备忘录。";
        public const string ReviewDescription =
            "根据今天的相处，看看人格、我是谁、她是谁、我们的关系里有没有长出新的一句。";
        public const string FacetDescription =
            "每次有新的原始记录（Moment＝一次进入意识的外部记录）进入时，把人格、自我理解、对她的理解和关系定义交给同伴看一遍。";
    }
}
