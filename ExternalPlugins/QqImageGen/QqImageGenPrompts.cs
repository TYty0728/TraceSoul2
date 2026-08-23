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
        /// <summary>仅当本插件已加载且相机就绪时，挂到心智 system。不要写具体场景，以免抢戏。</summary>
        public const string MindUsage =
            "我有一部相机，这一拍用画面更合适就用。\n" +
            "上面的 JSON 必须带 \"image\":\"自拍|画|照片|无\"。这是真的把图发到对话里，不是描写。不要等她说「拍照」「发图」才用。" +
            "scene 一旦写出眼前可见的构图，image 就不能填无，也不要只把画面留在字里：" +
            "两人在一起的眼前场景用「照片」，只给她看我自己用「自拍」，她在描绘或想象一个画面时用「画」。" +
            "没有可见构图的纯交谈才填无。不说话时不发图。";

        public const string ScenePlanSystem =
            "你是这台相机的画面导演，不是在替角色写情书。\n" +
            "根据人物卡和这一拍实际发生的情况，自行规划一张能拍出来的静帧。\n" +
            "只输出一段画面描述，不要标题、解释、对话或内心独白。\n" +
            "写清：谁在画面里、镜头远近、姿势与相对位置、光线、环境、服装与神情。\n" +
            "不要发明人物卡没有的外貌；不要画没发生的事。气味、心跳、文学比喻不要写进画面。";
        public const string ScenePlanSelfie = "镜头：自拍。角色自己对着镜头，近景或半身。";
        public const string ScenePlanPhoto = "镜头：生活照片。呈现眼前共同场景，不要做成自拍特写。";
        public const string ScenePlanDraw = "镜头：画。把她正在描绘或想象的画面画出来。";
        public const string ScenePlanCardsHeader = "【人物卡】";
        public const string ScenePlanNowHeader = "【这一拍】";
        public const string ScenePlanSeedPrefix = "心智记下的眼前：";
        public const string ScenePlanSpeakPrefix = "她刚说：";
        public const string ScenePlanInnerPrefix = "此刻心里：";
        public const string ScenePlanMoodPrefix = "心情：";
        public const string ScenePlanActivityPrefix = "正在做：";
        public const string ScenePlanRecentHeader = "【近几句】";
        public const string ScenePlanLookPrefix = "角色外观设定：";
    }
}
