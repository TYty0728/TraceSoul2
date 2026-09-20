namespace TraceSoul2.ExternalPlugins
{
    /// <summary>QQ 空间器官提示词。</summary>
    public static class QqQzonePrompts
    {
        public const string Usage =
            "她明确要发说说、发到空间时：把 qq.qzone.publish 作为附加表达调用（content=说说全文），主通道回话确认，不要只口头答应。" +
            "发布前可以自行决定是否配图，想配图就与正文一起发到空间；用户明确要求纯文字时遵守。" +
            "她让我看说说、看空间、看看她或我最近发了什么时：调用 qq.qzone.read（uin 填对方 QQ 号；看我自己填 self；看她且当前是私聊可留空），读完再用自己的话告诉她，不要假装看过。" +
            "空闲时系统会自己抽签去看或发，不要在对话里主动刷空间，也不要在她没说发的时候发布。";

        public const string PublishDescription = "发布一条 QQ 空间说说；愿意配图时由相机生成，与正文一起发布。";
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
            "从提供的相处、今日经历或此刻心事里，选一件自己确实在意的事，写出由它生出的感受、想法或疑问。不要求发生了大事，小小的心事也可以。让读的人明白你在想什么、为什么有这个感触；可以轻松、好笑、犹豫或认真，语气属于你自己。" +
            "写成自然完整的一小段，通常三到六句，内容说清楚就停，不凑字数。意象可以有，但不能只剩一句孤立的风景、气味或含糊感叹。没有材料的地点、见闻、动作不要编造。" +
            "这是公开说说，保留自己的感受，避免复述私聊原话、身份信息和私密细节；不要写成发给她的私聊、问候、计划或流水账。" +
            ImageDecision +
            "只输出 JSON：{\"content\":\"说说正文\",\"image_prompt\":\"想分享的画面，或空字符串\"}。没有想发布的内容时两个字段都留空。";

        public const string ImageDecision =
            "我可以决定这条说说是否配一张图：如果画面能表达我想分享的内容，在 image_prompt 写下具体的分享意图（自拍、眼前生活或明确说明是想象的插画），镜头和参考图由相机处理。不必每条配图；不想配图或相机不可用时留空。图会和正文放在同一条说说，不另发私聊。不要把私聊照片、私人细节或未经记录的经历自动公开。";
        public const string ExistingPostImageInstructions =
            "我正在准备发布一条已经写好的 QQ 空间说说，正文不能修改。" + ImageDecision +
            "遵守正文和当前请求中的纯文字、不配图等明确要求。只输出 JSON：{\"image_prompt\":\"想分享的画面，或空字符串\"}。";
    }
}
