namespace TraceSoul2.ExternalPlugins
{
    /// <summary>QQ 相机生图提示词。装配代码只引用这里。</summary>
    public static class QqImageGenPrompts
    {
        public const string Selfie =
            "生成一张角色亲自拍给对话对象看的真实自拍照片。视角是手机前置镜头，角色直视镜头，近景或半身，构图自然，人物是正在和对话对象真实相处，而不是摆拍剧情。画面中不要出现手机、相机或第三人称拍摄者。 ";
        public const string LockFace =
            "角色参考图仅用于严格保持同一人的脸、发型和整体身份一致，不照搬参考图的姿势、构图、背景或服装。 ";
        public const string Photo =
            "生成一张角色此刻拍给对话对象看的生活照片。画面必须呈现对方能直接看到的具体场景，不要写成舞台说明或旁白。 ";
        public const string Edit =
            "基于随附图片进行编辑；只修改用户明确要求的内容，其余人物身份、构图和细节尽量保持不变。 ";
        public const string Draw =
            "根据要求生成完整图片，主体、环境、动作、光线和构图清晰可见。 ";
        public const string RefsPrefix = "随附参考图分为：";
        public const string RefsHint = "。角色参考决定人物身份；服饰参考只决定衣服；辅助参考只提供相应物件或风格，不要混淆。 ";
        public const string CharacterPrefix = "角色气质与拍照风格：";
        public const string StylePrefix = "固定视觉风格：";
        public const string RequestPrefix = "本次要求：";
        public const string AspectPrefix = "画面比例为 ";
        public const string EffectorDescription = "自拍、生活照片、画图、基于来图修改或发送 URL 图片。";
        public const string EffectorBoundary =
            "QQ相机｜prompt + mode(selfie/photo/draw/edit/url) + refs/aspect_ratio/url（可选）";
    }
}
