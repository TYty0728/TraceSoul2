namespace TraceSoul2.ExternalPlugins
{
    /// <summary>QQ 语音插件提示词。</summary>
    public static class QqTtsPrompts
    {
        public const string Usage =
            "她明确想听语音，或我主动觉得这一句用声音会更亲近、更像随手来到她身边时使用；不必等她点名，也不必有正式内容，但不要每条消息都机械转成语音。单段填 voice/voice_emotion；多段填 voices。" +
            "3.2 模型可在语音文字中自然使用 (sighs)/(laughs)/(emm) 等官方语气词；3及以下模型用 voice_emotion；动作旁白不要念。";
        public const string EffectorDescription = "把一段话合成情感语音发到当前 QQ 会话。";
        public const string EffectorBoundary = "QQ语音｜text + emotion（可省略）";
    }
}
