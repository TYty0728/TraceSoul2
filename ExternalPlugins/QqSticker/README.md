# QQ表情：共用心情评分

本插件只负责把本轮共享评分选中的原图送到原QQ会话。图库、42词评分与预览由Host的`IExpressionLibrary`提供；不会根据传入情绪词临时随机选图，也不会将WXXZ输出转发QQ。

保留`qq.sticker`与`qq.sticker.send`身份。图片和索引留在原`plugins_data/qq-sticker/emojis/<persona>/`，配置保留在该插件的`config.json`；代码升级不覆盖这些数据。`threshold`默认0.55，`top_tag_count`默认4，`personas`保留原目录列表；保存后在「身体与感官 → 共用表情库」重新加载。旧`recent_avoid_count`不参与最高分选择。

不添加构建后自动覆盖本机插件的Target；构建产物由正式打包流程或本机手动更新脚本加载。无实际平台连接时器官休眠，Host共用图库仍可供板端和预览使用。详情见[共用表情库](../../docs/SHARED_EXPRESSION_LIBRARY.md)。

用途语义版新增`semantic_threshold`（默认0.60），图片通过`semantic_tags`独立标注睡觉、流汗、夸奖等用途。明确用途先筛候选再结合心情；缺对应图跳过，窄用途图不参与普通心情回退。全部未标注的旧图库保留原行为。WebUI「共用表情库」可逐张编辑保存标签并即时应用到两个平台；不修改主对话Prompt，也不新增模型决策调用。详见共用表情库的用途语义修订。
