namespace TraceSoul2.ExternalPlugins
{
    /// <summary>QQ 签名/在线状态器官提示词。</summary>
    public static class QqStatusPrompts
    {
        public const string Usage =
            "她明确要改 QQ 签名、在线状态或 QQ 心情时：调用 qq.status.mood（signature=签名全文，status=状态名），主通道回话确认。" +
            "空闲时系统会自己抽签改心情，不要在对话里主动改。";

        public const string Description = "改机器人 QQ 的个性签名和/或在线状态。";
        public const string Boundary = "QQ心情｜签名和/或在线状态";
        public const string WhenToUse = "她让我改签名、换在线状态、改 QQ 心情。";
        public const string WhenNotToUse = "对话里不要主动改。空闲抽签由系统处理。";

        public const string IdleRoleHeader = "【空闲心情】";
        public const string IdleInstructions =
            "现在不是在跟她说话。系统抽到了改 QQ 签名或在线状态。" +
            "根据此刻状态决定要不要改；一句没来由浮起的话、一点心情或忽然想留下的生活痕迹也可以，不要求先发生事情。真的没有浮起才写无。" +
            "签名要短，像随手写的一句，不要写成计划、汇报或发给她的私聊，不要喊她的名字。" +
            "状态必须从给定名单里原样挑一个；不想改就写无。" +
            "只输出两行：\n签名：……\n状态：……";
        public const string StatusNamesHeader = "可选状态：";
    }
}
