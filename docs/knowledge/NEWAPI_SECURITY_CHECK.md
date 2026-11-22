# New API 注册与权限只读核对

## 2026-11-03 官方渠道模型获取失败

用户新增“gpt官网”渠道后反馈获取失败、列表最高5.6。只读确认渠道类型OpenAI、默认官方地址、存在已保存模型名单。使用该渠道现有凭据从New API服务器请求官方`/v1/models`，返回HTTP401、`invalid_api_key`；所填值是`key_`形式，疑似把密钥ID当作Secret Key，未包含可调用的`sk-`形式密钥。该版本单Key渠道直接使用存储值，不是加密密钥解读问题。

当前渠道保存的模型名单不能代表官方实时返回或账号权限上限；需要用户在领取额度所属组织/项目创建并复制完整Secret Key，填回New API渠道后重新获取。仅做一次模型列表诊断，无收费模型调用，未改渠道、凭据、服务或TraceSoul配置；账号实际可用模型与余额仍待有效凭据验证。

## 2026-11-03 非站长账号清理

用户明确确认该实例只有站长是本人、其余均为机器人，并授权删除其余账号。实际核对184个账号，其中唯一站长role=100，另183个普通账号均零余额/零调用；公开注册与密码注册已关闭，沿用现状。

先用SQLite在线备份生成受限恢复副本，完整性检查通过，再在独立副本演练同一删除函数。站长没有可用管理令牌，采用短SQLite事务按固定候选ID删除183个用户，并按该版本硬删除语义清理其83个令牌、68条登录会话；其余鉴权关联表无命中。已确认未配置Redis，用户鉴权读取数据库，无需重启清缓存。未更改站长凭据或生成临时管理身份。

事务内逐表指纹校验站长完整记录、非目标鉴权资料及所有其他表不变，含渠道、配置、余额和历史日志；数据库完整性与外键检查通过。完成后仅剩站长，3个已删除令牌抽检均401，站长令牌models200/27项。容器PID与启动时间保持，注册仍关闭；没有调用收费模型。服务器备份与私有脚本位置见工作区LOCAL_RUNTIME.md，不记录账号、令牌或数据库正文。

## 2026-11-03 官方生图中转路径核对

- 使用用户指定的SSH资料连接既有VPN/New API服务器，严格校验已知主机密钥；现运行New API `v1.0.0-rc.24`。
- 此机IPv4访问OpenAI官方`/v1/models`返回HTTP401（未带Key），证明网络与TLS可达，不代表账号权限或生图成功。New API本机`/v1/images/generations`、`/v1/images/edits`均返回鉴权401，并非404。
- TraceSoul服务器既有`newapi`供应商确实指向该机器；使用其现有令牌查询模型列表HTTP200，27个模型中没有`gpt-image`。这仅说明该令牌当前可见范围，不代表已检查全部上游渠道。
- 可复用“TraceSoul → 现有New API → OpenAI官方”路径，无需另建VPN代理。待用户在New API官方渠道填写领取额度组织的API Key、配置生图模型并匹配现有令牌分组后，再刷新TraceSoul模型并选择生图用途。TraceSoul端继续使用New API令牌；官方Key属于New API上游渠道。
- 本次只读核对，没有修改New API渠道/密钥/分组、TraceSoul用途槽或服务，没有真实生图和收费模型调用。已准备的独立官方直连供应商保留，未启用。

## 2026-10-22 当前结果

用户看到陌生/机器人账号注册，询问是否属于攻击或地址泄露。通过既有SSH只读检查现有中转服务，未修改注册设置、封禁账号、升级或重启服务。

- 运行版本为`v1.0.0-rc.24`。公开状态接口确认开放注册及密码注册，邮箱验证和Turnstile均关闭；脚本可直接使用正常注册流程。不能据此认定绕过鉴权或服务已失陷。
- 对正式数据库及事务日志先稳定复制，只在私有副本查询，完整性检查通过；原文件未改，临时副本已清理。
- 当前169个未删除账号：168个普通账号、1个最高权限账号。普通账号均无剩余额度、已用额度或调用次数；本次未发现普通账号的历史扣费调用，也未发现额外管理员。不能把所有普通账号都认定为机器人。
- 保留的Nginx日志中有注册请求和大量登录请求，11340个登录POST返回429，说明限流确有触发。HTTP200不能单独证明注册或登录成功；本次未核实管理员成功登录来源，也未进行整机入侵或密钥泄露排查。

公网地址可被批量扫描发现，陌生注册也可能来自域名或链接传播；目前不能确认发现途径，也没有理由仅由注册推断私人配置/聊天泄露。批量恶意注册属于业务滥用，伴随撞库或爆破时属于攻击尝试；尝试与成功入侵需要分别判断。

若中转仅供本人和熟人使用，优先关闭公开注册，再核对管理员使用独立强密码及2FA。需要公开注册时，可配置邮箱验证、Turnstile和注册/登录限流，并避免向陌生新账号赠送可消费额度。用户本轮仅询问状态，这些建议尚未实施。

TraceSoul2的`.tsbackup`覆盖Soul实例，并不包含另一台New API的账号、渠道、余额和配置；后者应单独备份。TraceSoul2恢复密钥由同一实例持久沿用，正常升级、生成新包和改存档目录均不轮换；每份包使用随机盐派生独立包密钥。新建实例或手工换钥后，旧包仍需原对应密钥。

参考：[New API官方设置说明](https://docs.newapi.pro/zh/docs/guide/feature-guide/admin/system-setting)、[对应版本默认配置](https://github.com/QuantumNous/new-api/blob/v1.0.0-rc.24/common/constants.go)。账号、日志原文、服务器地址、数据库和密钥不写入知识库。

## 2026-11-05 官方生图组织头修正

渠道2“gpt官网”现有完整Secret Key，配置模型含gpt-image-2，但OpenAI组织字段填了显示名称Personal。用实际配置请求官方models返回401 mismatched_organization；同一Key省略组织头返回200，含gpt-image-2等生图模型。通过用户现有已登录NewAPI渠道编辑页清空可选组织字段并保存，未改Key、模型或分组。保存后读取数据库确认字段为空，按实际存储配置再查models返回200。

随后用现有站长NewAPI令牌经本机/v1/images/generations做一次最小公开合成图验收：gpt-image-2、1024x1024、quality=low、n=1，HTTP200，约10.95秒，返回1张图。仅输出状态/计数，无原始图片、Key、组织ID或私人对话入库。证明该NewAPI官方生图通道可用，不等于已验证TraceSoul相机参考图编辑或赠送额度归属/余额。未重启服务、修改TraceSoul生图槽或向QQ发图。

## 2026-11-22 Google Gemini 官方经 New API 接入

用户授权使用其 Gemini API Key 配置既有外网 New API，再接入服务器 TraceSoul。密钥仅在内存、加密SSH传输及目标渠道配置中处理，不写入源码、输出或知识库。

- Google 原生 models 请求在本机与中转服务器均成功；新账户调用 gemini-2.5-flash 返回404并提示改用 gemini-3.8-flash。后者真实短回复成功。
- New API v1.0.0-rc.24 新增独立 Google Gemini official 渠道（原生Gemini/type24），仅配置 gemini-3.8-flash、沿用站长svip组。现有站长令牌无模型限制。因未有可用管理令牌，采用SQLite在线备份和短事务，成对插入channels/abilities，保留既有渠道完整记录，由服务周期刷新加载；未生成管理凭据或重启。
- 仅补该模型的计费项：ModelRatio=0.375、CompletionRatio=5、CacheRatio=0.1（已有对应选项时）。真实调用日志确认生效；对应当前官方标准文本输入0.75美元/百万token、输出3.75美元/百万token，官方列明2027-01-01起价格变化，后续需按实际价格维护。本次未验证音频等模态的计费。
- 现有New API令牌查询models可见目标；经OpenAI兼容chat/completions实际返回OK。TraceSoul已加入该模型，使用自己的model-test入口成功（3120ms），聊天主模型从原第三方渠道切为newapi/gemini-3.8-flash。
- TraceSoul配置备份后通过正式API更新；排除当前供应商选择及newapi该模型项后的配置指纹一致，复盘、识图、生图、语音和备用配置保持。两个服务PID及启动时间保持；未发QQ验收消息、恢复复盘或部署二进制。
- 仅证明合成短请求及路由可用，真实长上下文对话效果、Google账单28新加坡币性质和会员抵扣到账未验证。私有脚本、备份位置见工作区LOCAL_RUNTIME.md。

官方价格依据：https://ai.google.dev/gemini-api/docs/pricing#gemini-3.8-flash

## 2026-11-22 核对 Google AI Pro 的 CLI 接入

Google 已于 2026-06-18 停止旧 Gemini CLI 为个人 Google AI Pro、Ultra 和免费账号提供登录调用。个人订阅的终端入口改为 Antigravity CLI。Google FAQ 明确说明个人计划的 headless 模式面向直接脚本和 CI，第三方应用封装不受支持。项目维护者另确认，用户直接操作的 ACP 会话可以使用个人登录，后台模型调用则需 API Key 或 Google Cloud 认证，即使调用官方未修改的 CLI 子进程也一样。

TraceSoul 会组装角色与记忆上下文并在后台发起模型请求，因此没有接入个人订阅 CLI。前一节的已验证路线仍是 New API 转发 Gemini API。Pro 主订阅者可将每月 10 美元开发者抵扣领取到该 API Key 项目所用的账单账户。实际领取及抵扣状态尚未核对。

依据：[Gemini CLI 停用公告](https://github.com/google-gemini/gemini-cli/discussions/28017)、[Antigravity FAQ](https://antigravity.google/docs/faq/)、[ACP 与后台调用说明](https://github.com/google-antigravity/antigravity-cli/issues/1493)、[AI Studio 会员抵扣](https://aistudio.google.com/docs/google-ai-plans)。

同日核对 Gemini App 本身的接入方向。Google 的开发者论坛答复确认，第三方应用没有官方接口调用个人 Pro/Ultra 的订阅额度。Gemini Apps 的自定义 Connected Apps 可由 App 主动调用外部 MCP 工具，回答继续留在 App，不能把 App 变成 TraceSoul 的模型 API。自定义连接的帮助页当时列出美国、个人账号、年满18岁及开启历史等条件。本次仅核对资料，未接入 App、修改运行配置或验证该账号资格。见[第三方应用额度说明](https://discuss.ai.google.dev/t/question-official-way-for-a-third-party-desktop-app-to-use-a-users-google-ai-pro-ultra-subscription-with-gemini/199349)和[自定义连接帮助](https://support.google.com/gemini/answer/17209137?co=GENIE.Platform%3DAndroid&hl=en-MP)。

同日评估非官方 Gemini Web 转发。Wei-Shaw/Sub2API 的部署文档将 Gemini 入口列为 Code Assist OAuth、AI Studio OAuth 和 API Key，未列 Gemini App 聊天额度入口。另有项目通过本机已登录的网页标签页发送并读取文字，再提供 OpenAI 兼容接口。该方式可能使用网页会话的额度，真实账号的模型、用量、结构化回复及长上下文尚未实测。所查示例同时包含读取浏览器凭据的后端，服务默认监听所有网卡且接口密钥可为空。若做试验，先核查纯网页交互方案，在本机隔离测试合成文本，验证后再考虑私有接入。未安装第三方项目、读取登录凭据或改动运行配置。

## 2026-11-22 Gemini 网页本机桥试验

已准备独立的回环 HTTP 桥及浏览器扩展，只在用户从扩展面板选中的 Gemini App 网页发送文字并读回可见回复，谷歌登录保留在浏览器。HTTP 客户端和浏览器配对使用不同随机密钥。实验接口支持单并发、非流式纯文本和 JSON 对象，无提示词或回复持久化。模拟标签页验证了鉴权、重复完成拒绝、忙时拒绝、文本返回及 JSON 校验。扩展模拟验证了指定标签页限制。当前等待用户首次手动配对，未验证真实 Pro 网页/用量或接入服务器。私有路径和运行状态见工作区 LOCAL_RUNTIME。

2026-11-22 首次真实网页配对：本机收到 Edge 轮询，第一个合成文字请求进入桥后返回 HTTP 504，浏览器随后仍在线。未确认网页是否已生成回复。浏览器检查工具连接超时。已改进网页回复节点和完成状态识别，并新增扩展内阶段提示，模拟网页及本机接口验证通过。等待 Edge 重载并重新配对后复测，真实文本和 JSON 尚未通过。

2026-11-22 续验：用户在 Gemini 网页确认第一次测试实际生成了 OK。重新配对后本机第二次请求由浏览器回报 HTTP 502（gemini_reply_unconfirmed），仍未验证本机收到正文。扩展0.1.1按本轮用户消息关联已呈现回复，容忍重复节点并提取代码块内的 JSON。合成 DOM 与本机接口测试通过，等待 Edge 加载新版后实测。

同日轮到扩展新版重测，桥确认在线，两次返回 gemini_tab_unavailable，故尚未验证自动进入独立新对话或读回正文。已新增单条文字/JSON验收入口，可在同一标签页手动进入空白对话后分别验证发送和读回。未发送真实聊天材料或切换服务器模型。

同日手动打开空白 Gemini 对话后单测，桥接收到配对且一条合成请求等待浏览器回复，最终 HTTP 504。无法从桌面浏览器工具读取页面。已准备扩展0.1.2，新增不向网页提交消息的结构诊断，调整残留停止控件时的完成判定；合成测试通过，尚未在真实浏览器验证，等待从原配对页加载新版。
