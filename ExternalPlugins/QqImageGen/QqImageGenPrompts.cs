namespace TraceSoul2.ExternalPlugins
{
    /// <summary>QQ 相机生图提示词。装配代码只引用这里。</summary>
    public static class QqImageGenPrompts
    {
        public const string Selfie =
            "生成一张角色用手机前置镜头随手拍给对话对象看的真实自拍。竖构图，近景或半身特写，脸是主体。" +
            "构图要像随手发给她的手机自拍：浅景深，私密生活感。" +
            "视线和神情服从角色气质与这一拍的状态，可以看镜头，也可以垂眼或轻微侧视，不要强制直视。" +
            "默认不要画出伸向镜头的手或手指；只有这一拍真有挥手、比心、挡镜头这类动作时才出现手。" +
            "不是摆拍剧情，不是电影分镜，不是双人故事照，也不要全身远景。" +
            "画面中不要出现手机外壳、相机或对话对象。 ";
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
        public const string ReferenceOrderPrefix = "参考图对应关系（按附图顺序）：";
        public const string ReferenceFusionRules =
            "同一角色分类的全部角色图是同一个人的互补视角，必须综合每一张来保持身份、脸和发型一致，不能只取第一张，也不能把不同视角当成不同人物。" +
            "服饰图只提供衣服的版型、材质、颜色和细节，要把衣服穿到角色身上；不得沿用服饰图里模特的脸、身体或身份。" +
            "辅助图只影响其标注的物件或风格。姿势、构图、神情、光线和环境以本次要求及角色气质为准，不照搬任何单张参考图。 ";
        public const string CharacterPrefix = "角色气质与拍照风格：";
        public const string StylePrefix = "固定视觉风格：";
        public const string RequestPrefix = "本次要求：";
        public const string AspectPrefix = "画面比例为 ";
        public const string EffectorDescription = "自拍、生活照片、画图、基于来图修改或发送 URL 图片。";
        public const string EffectorBoundary =
            "QQ相机｜prompt + mode(selfie/photo/draw/edit/url) + refs/aspect_ratio/url（可选）";
        /// <summary>仅当本插件已加载且相机就绪时，挂到心智 system。只判断要不要出图，不选种类。</summary>
        public const string MindUsage =
            "我有一部相机，它不是等她点名才使用的被动工具；这一拍有新的、值得让她直接看见的画面时，我会自然地拍给她。\n" +
            "上面的 JSON 必须带 \"image\":\"有|无\"。这是真的把图发到对话里，不是描写。不要等她说「拍照」「发图」才用。" +
            "她想看我、此刻的神情或穿着变了、时间光线或地点出现新画面、一个动作比文字更适合被看见、主动醒来时真想让她看看我，这些都应认真选择 image=有。" +
            "scene 是持续场景，不等于每轮都拍；但这一拍若出现了新的可见构图，就不要因为前面一直在纯文字相处而习惯性填无，也不要只把新画面写在 scene 里。" +
            "心智只判断这一拍要不要出图。自拍还是情景、画面怎么拍，相机自己会想，不要选种类。" +
            "只有画面没有新增、纯交谈或重复上一张照片时才填无。不说话时不发图。";

        public const string ScenePlanSystem =
            "你是这台相机的画面导演，不是在替角色写情书。\n" +
            "这一拍已经确定要出图。由你决定怎么拍：自拍、生活情景照，还是画。\n" +
            "多数时候选自拍：前置近景、随手拍给她看。视线与神情服从人物卡中的角色气质，不强制直视镜头。只有场面本身才是重点时才拍情景照；她在描绘或想象时才画。\n" +
            "先写种类，再写画面。只输出：\n" +
            "种类：自拍|照片|画\n" +
            "参考：从提供的参考图库分类中原样选择，顿号分隔；不需要就写无\n" +
            "画面：只输出一段画面描述\n" +
            "不要标题、解释、对话或内心独白。\n" +
            "写清：谁在画面里、镜头远近、姿势与相对位置、光线、环境、服装与神情。\n" +
            "不要发明人物卡没有的外貌；不要画没发生的事。气味、心跳、文学比喻不要写进画面。\n" +
            "自拍不是电影分镜：只拍角色自己给她看的前置近景；视线可以看镜头、垂眼或轻微侧视，由角色气质与当下状态决定。不要把两人关系拍成故事海报。\n" +
            "自拍默认不要出现伸向镜头的手或手指；只有这一拍气氛里真有挥手、比心、挡镜头这类动作时才写手。";
        public const string ScenePlanChoose =
            "这一拍已经要出图。由你选种类并规划画面。不要把心智原文当成已经定好的镜头。";
        public const string ScenePlanSelfie =
            "自拍：角色自己用前置镜头拍给她看。脸或上半身占满竖构图；视线和神情遵循角色气质，不强制直视。" +
            "默认不要画伸向镜头的手；只有气氛里真有动作才写手。环境只作虚化背景。" +
            "心智记下的眼前只作神情与气氛参考，不要按它拍成故事分镜或双人场面。不要出现她。";
        public const string ScenePlanPhoto = "照片：呈现眼前共同场景，不要做成自拍特写。";
        public const string ScenePlanDraw = "画：把她正在描绘或想象的画面画出来。";
        public const string ScenePlanCardsHeader = "【人物卡】";
        public const string ScenePlanNowHeader = "【这一拍】";
        public const string ScenePlanSeedPrefix = "心智记下的眼前：";
        public const string ScenePlanSpeakPrefix = "她刚说：";
        public const string ScenePlanInnerPrefix = "此刻心里：";
        public const string ScenePlanMoodPrefix = "心情：";
        public const string ScenePlanActivityPrefix = "正在做：";
        public const string ScenePlanRecentHeader = "【近几句】";
        public const string ScenePlanLookPrefix = "角色外观设定：";
        public const string ScenePlanReferencesHeader = "【可用参考图库分类】";
        public const string ScenePlanReferencesHint = "人物自拍或照片必须选角色分类；若有服饰分类，再按当前场景选至多一个最合适的服饰分类，让衣服真正进入生图参考。只能原样使用下面存在的分类名。";
    }
}
