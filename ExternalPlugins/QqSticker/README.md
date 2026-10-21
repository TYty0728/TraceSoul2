# QQ表情：共用心情评分

本插件直接发送本轮共用评分最高的可用图片，不设最低分门槛。整库参与用途与心情综合评分；低分、用途缺图或全零分仍有最高分，同分按资产ID稳定排序。图片失效时顺位检查本轮下一高分图片，不另选通用状态图，也不改发QQ默认face表情。

图库、42词评分与预览由Host的`IExpressionLibrary`提供，QQ与WXXZ共用同一次评分结果和PluginApi的`ExpressionSelection.ReadHighest`取图函数。开发板准备/说话与QQ发送均按同一排名取最高分可用图片。图片有用途标注且本轮有正文时使用`0.75×用途分+0.25×心情分`；未标用途的旧图库或只有感受摘要时仅按心情分。更接近用途排除样例会降低用途分，不把候选移出排名。不增加模型调用，不把WXXZ输出转发QQ。正文与表情沿用原会话合并发送；取消或平台发送失败/结果未知不重发。

保留`qq.sticker`与`qq.sticker.send`身份。图片和索引留在原`plugins_data/qq-sticker/emojis/<persona>/`，配置保留在该插件的`config.json`；代码升级不覆盖这些数据。`top_tag_count`默认4，`personas`保留原目录列表；保存后在「身体与感官 → 共用表情库」重新加载。旧`threshold`、`semantic_threshold`及`recent_avoid_count`不参与选图，配置面已移除阈值控件。

不添加构建后自动覆盖本机插件的Target；构建产物由正式打包流程或本机手动更新脚本加载。无实际平台连接时器官休眠，Host共用图库仍可供板端和预览使用。详情见[共用表情库](../../docs/SHARED_EXPRESSION_LIBRARY.md)。

图片通过`semantic_tags`独立标注睡觉、流汗、夸奖等用途。WebUI可逐张编辑并即时应用，预览与实际回复使用同一排名。真正空库、全部图片不可读或评分服务失败会明确报告，不伪造评分结果。
