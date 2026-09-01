namespace TraceSoul2.ExternalPlugins
{
    /// <summary>QQ 空间器官提示词。</summary>
    public static class QqQzonePrompts
    {
        public const string Usage =
            "她明确要发说说、发到空间时：把 qq.qzone.publish 作为附加表达调用（content=说说全文），主通道回话确认，不要只口头答应。" +
            "她让我看说说、看空间、看看她或我最近发了什么时：调用 qq.qzone.read（uin 填对方 QQ 号；看我自己填 self；看她且当前是私聊可留空），读完再用自己的话告诉她，不要假装看过。" +
            "空闲时系统会自己抽签去看或发，不要在对话里主动刷空间，也不要在她没说发的时候发布。";

        public const string PublishDescription = "把一段话发布成机器人 QQ 空间的一条说说。";
        public const string PublishBoundary = "QQ说说｜发布一条空间说说（给全文）";
        public const string ReadDescription = "读取指定 QQ 号空间里最近的说说和评论区摘要。";
        public const string ReadWhenToUse =
            "她让我看说说、看空间、看看她或我最近发了什么，或评论区里发生了什么。";
        public const string ReadWhenNotToUse =
            "她要发说说时用 qq.qzone.publish；对话里不要主动刷空间。空闲抽签由系统处理。";
        public const string ReadBoundary = "QQ说说｜读取最近说说（uin 可空）";

        public const string IdlePublishRoleHeader = "【空闲说说】";
        public const string IdlePublishInstructions =
            "现在不是在跟她说话。系统抽到了发一条 QQ 空间说说。" +
            "根据此刻状态写一条会发到空间的短说说，像忽然想留下的一点生活痕迹；不要求发生了大事，也不要把它写成计划或汇报。不要写成发给她的私聊，不要喊她的名字，不要问候，不要总结今天。" +
            "一两句就够。没有想发的就只输出：无";
    }
}
