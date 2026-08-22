namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>OneBot 插件提示词。本插件不做决策，只保留会进入对话记录的出站说明。</summary>
    public static class OneBotPlatformPrompts
    {
        public const string SendImageMoment = "[QQ 发送图片]";
        public const string SendStickerMoment = "[QQ 表情：附在文字结尾]";
        public const string SendVoiceMoment = "[QQ 发送语音]";
        public const string TextEffectorDescription = "把同伴已经说出口的文字发到当前 QQ 会话。";
        public const string ImageEffectorDescription = "把一张图片发到当前 QQ 会话（file 为本地路径或 URL）。";
    }
}
