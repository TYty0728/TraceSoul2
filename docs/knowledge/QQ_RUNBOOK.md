# QQ 回复与照片排障

## 多次回复

先用日志 `TraceId` 和入站 `event=message_id` 区分：

| 现象 | 检查点 |
|---|---|
| 同一个 `message_id` 多次进入收件箱 | OneBot 重放或多 WS 投递；本次已在入队前去重，日志为“QQ 重复消息已忽略” |
| 不同 ID、正文一样 | 可能是用户实际重复发送；不按正文吞消息 |
| 一次输入先说“等一下”，稍后带结果回来 | `KernelLogic` 外出分支设计上有等待与最终回应；不是普通轮重放 |
| 文字之后还有图、语音 | 附加表达；文字与表情合并，照片轮后单独发 |
| 用户回复附近又收到主动消息 | 比对心跳/时间触发的 TraceId；不能仅凭时间近认定为重复发送 |
| 消息或延迟照片发错聊天 | 优先检查触发 Moment 的会话载荷；当前已改为旧轮沿用原会话 |

去重是进程内的 30 分钟 / 4096 条有界缓存，跨进程重启或超出窗口不保证去重。没有有效 ID 时继续接收。不要盲目重试发送动作：超时可能发生在 QQ 已收到了消息之后。

## 不发照片

按顺序查，避免只改 Prompt：

1. **运行版本**：Host 和已安装 `qq-imagegen` DLL 是否都更新？编译 Host 不会安装外部插件。
2. **可用性**：QQ 已连接、回发开启、相机插件启用、生图供应商/模型/Key 配置齐全；只检查是否配置，不输出 Key。
3. **心智输入**：稳定段有相机说明与 `image` 字段；动态段有“相机此刻”。后者仅在生图器官可用时加入。
4. **选择**：看“心智判断完成”的 `image`。长期为无时，查看真实发图反馈是否存在、上下文是否适合分享。不要用固定概率强制拍照。
5. **映射**：看“外显图片路由”“后台生图已开始”。`qq.image.send` 只接 `file`，生图须走 `qq.imagegen.generate`。
6. **生成**：看画面规划、参考图、请求错误和“生图未成功，不发图”。供应商问题与未选择发图是两类问题。
7. **发送**：看“QQ 图片发送完成”的 status 与“后台图已发出/后台发图未成功”。发送失败不得伪造成功回执。

相机反馈读取当前会话最近 200 条运行事件内的照片回执，统计最近 80 条真实对话内后续的角色回应。没有记录只说明查询窗口没有，不能说从来没拍过。6 次文字回应是重新考虑分享的提醒阈值，**不是出图配额**。分享频率最终由心智模型和具体聊天决定，需要真聊观察。

## 本地验证与应用

在仓库根目录：

```powershell
dotnet build Tools/ChatCheck/ChatCheck.csproj --nologo
dotnet Tools/ChatCheck/bin/Debug/net8.0/ChatCheck.dll
dotnet Tools/ChatCheck/bin/Debug/net8.0/ChatCheck.dll --prompt-layout
dotnet build Tools/Host/TraceSoul2.Host.csproj --nologo
dotnet build ExternalPlugins/QqImageGen/TraceSoul2.Plugin.QqImageGen.csproj --nologo
```

应用时使用实际启动配置解析插件目录，备份并替换 `qq-imagegen/TraceSoul2.Plugin.QqImageGen.dll`，保留原 `plugin.json`、配置和图库。相机使用 PluginApi 1.3.0，与 Host 0.1.8 同批升级；正式 Release 已包含配套文件。

真人收发验收会产生外部消息；离线回归不启动真实 Host。用户自己启动后，观察普通聊天中照片是否自然出现、同一入站是否只处理一次；如仍异常，留取同一 TraceId 的阶段日志，不必导出整份私人聊天。
