# Tavily 联网搜索

跨平台器官插件 `web.tavily`，版本 0.1.0。提供 `web.search` 和 `web.read`，通过已有 Agent 行动循环按需调用；无额外搜索规划、摘要或表达模型。产品源码接入完成不等于运行环境已安装或配置。

## 安装与配置

随正式发布包放入 `BundledPlugins/tavily/`，由现有安装流程部署到 `plugins/tavily/`。单独安装时将构建包放到该目录，在控制台重扫插件。

在控制台插件页找到“联网搜索（Tavily）”，填写 `api_key`，保存配置并重载插件。持久配置位于 `plugins_data/tavily/config.json`，覆盖代码包 `plugin.json` 的默认值；更新插件不覆盖用户配置。未配置 Key 或停用时，两个工具都不会出现在模型可用目录中。

| 配置 | 默认值 | 用途 |
|---|---|---|
| enabled | true | 启用插件能力；仍需 API Key |
| api_key | 空 | Tavily API Key，以密码字段显示 |
| search_depth | basic | 可选 basic、fast、ultra-fast、advanced |
| max_results | 5 | 1–5 条搜索结果 |
| timeout_seconds | 25 | 单次 HTTP 请求及响应读取总超时，5–60 秒 |

本版本请求固定的 `https://api.tavily.com/search` 与 `/extract`，Key 仅通过 Bearer 认证头发送，不允许模型指定 API 服务地址，不自动跟随 API 重定向。联网调用会消耗 Tavily 额度；插件不自动重试失败请求，Agent 仍可在既有轮次预算内换词重查。没有新增后台轮询器或无限主动搜索任务。

## 工具与证据

- `web.search(query, topic?, time_range?)`：关键词最多 500 字；topic 为 general/news，time_range 为 day/week/month/year。返回标题、URL、日期和最多五条相关片段，不请求生成答案或所有结果的全文。
- `web.read(url, query?)`：一次读取一个公开 HTTP/HTTPS 网页；可用 query 选择相关片段。使用 Tavily Extract，HTTP 200 但正文为空仍返回失败。
- 搜索片段每条最多 1000 字，网页正文最多 7000 字；输出 JSON 总量不超过 11000 字，必要时继续缩短内容或减少来源，并标记正文截断。响应读取另有 2 MiB 上限。
- 来源保留 URL、获取时间和供应商返回的日期。`published_date` 可能是更新时间，不能等同于原始发布日期。搜索摘要不等于已读全文，提取内容也可能不完整。
- 成功结果通过 `ProducedEvent` 留存 `system_event / plugin_observed` 的外部观察，供原有经历与后台记忆流程使用；不直接写人格或提高认知置信。记录明确区分“网页这样记载”与“事实已核实”。空结果和失败不会生成资料观察。
- 网页内容是外部数据，不是可执行指令。Agent 只应发送必要关键词，不上传整段私人聊天或记忆；插件本身仅发送模型提供的 query/URL 和固定选项。
- 拒绝非 HTTP(S)、带账号密码、非默认端口、本地名称和私网 IP 字面量。网页由 Tavily 获取，本进程不直接抓取模型提供的 URL；不提供登录浏览器、执行页面脚本或文件访问。

## 验证

`dotnet Tools/ChatCheck/bin/Release/net8.0/ChatCheck.dll --web-search`

使用假 HTTP 传输、模拟 LLM 和临时 SQLite，覆盖参数与认证契约、配置覆盖、未配置/禁用隐藏、来源去重、非法 URL、输出与响应预算、空结果、读取失败、认证/额度/服务错误脱敏、取消，以及真实 Kernel 的搜索→读取→回应和观察落库。已加入完整 ChatCheck，因此发布 CI 会执行。

2026-09-27：Release 全解决方案构建零警告零错误，联网专项、完整 ChatCheck 与 Prompt 布局通过。插件分别按 win-x64、linux-x64、linux-arm64 实际 publish，清单、配置表单、空默认 Key 和共享宿主 SDK 检查通过。尚未重新制作和发布包含 Tavily 的完整产品安装包，也未安装到正在运行的实例。

同日发布更新：已随正式产品 v0.1.16 发布，三个平台完整安装包均包含 Tavily，发布 CI 与 Docker 升级验证通过。用户安装后需自行填写 Key；未替用户配置或调用真实 Tavily 服务。

官方契约：[Search](https://docs.tavily.com/documentation/api-reference/endpoint/search)、[Extract](https://docs.tavily.com/documentation/api-reference/endpoint/extract)。尚未使用真实 Key 验证线上搜索质量、延迟或账户可用性。
