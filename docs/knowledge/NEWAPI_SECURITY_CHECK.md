# New API 注册与权限只读核对

## 2026-11-27 Google 模型桥接仅保留 AI Studio

通过现有 Host 接口备份并删除服务器供应商 `antigravity-local`。删除前确认主聊、备用和专用槽均未引用该供应商，删除后比对槽位、备用顺序、其余供应商配置和运行容器均一致。Windows 反重力桥接及其私有 SSH 转发已退出，本机和服务器容器的回环 5095 已关闭。撤除本地未发布的 ACP 供应商模板、客户端和桥接源码。

`启动Google模型.bat` 继续作为启动入口，只管理 AI Studio Playground。重复启动与状态查询复用了现有 5096/5097 监听，服务器容器的模型目录返回 200。当前主聊 Gemini 4 Neon 内置短测成功，用时 3272 毫秒。Host 构建零警告零错误，启动器冷启动派发与子进程失败处理通过模拟检查。此次线上验证沿用现有 AI Studio 进程。运行与备份位置见工作区 `LOCAL_RUNTIME.md`。

同日 22:12 起本机 AI Studio 连不上。生成进程已经退出，11 月 26 日 10:56 启动的隧道进程仍在，容器网络里的回环 5096 仍由该辅助进程监听；从容器访问模型目录时连接被中断。22:12 和 22:13 两次重新启动都能在本机拉起生成服务，但远端端口被占用，监听没有建立，启动器随后关掉刚拉起的本机服务。已用停止文件结束旧隧道，容器与 Host 未重启。重新启动后本机状态为已连接；容器内无密钥返回 401，使用桥接密钥读取模型目录返回 200，目录包含 Argon。未发送生成请求。生成进程当初退出的原因已经无法从日志确认，22:13 的新启动覆盖了原适配器日志。

## 2026-11-25 服务器接入本机 Antigravity

服务器阿循已新增可选供应商 `antigravity-local`（反重力，本机 Antigravity）。复用现有 OpenAI 兼容入口，Windows 独立桥通过私有 SSH 连接到服务器容器回环。桥从每次请求获取 Gemini API Key，以官方 `agy acp` 的 API Key 模式执行文本请求。运营设置在服务器「大脑 · LLM」填写，Windows 桥只启动 CLI，不运行另一个角色主体。

已实际从服务器容器读取桥接模型目录，HTTP 200、八个模型；缺少 Key 返回 401。桥接拒绝流式请求，接入时现有 Host 和其它用途槽经比对一致。本机角色里先前误登记的空白供应商已撤回。启动器和备份见工作区 `LOCAL_RUNTIME.md`。

服务器确认反重力渠道已保存 Key，主聊使用 Gemini 4 Carbon。原先模型目录可读，内置生成测试超时；本机直连 Gemini API 超时，经 Clash 可收到上游响应。统一启动器给反重力进程传入同一个本机代理，仅重启该 Windows 桥接。服务器用现有 Key 重试 Carbon 内置测试，HTTP 200、用时 5726 ms，真实生成通过。原对话提示的文字未复现，正常对话待用户重试。

同日将反重力和 AI Studio 的服务器连接并入一个本机启动入口。启动器根据各自回环服务和隧道心跳启动未运行项，已在线的进程继续复用。实测重复启动保留原监听进程，模拟验证两项均未启动时的派发。验证未发送推理请求；现有桥未停机重启。管理页、个人 CLI 和历史启动器的位置见 `LOCAL_RUNTIME.md`。

个人登录的 `agy models` 列出 Claude Sonnet 5.5 与 Opus 5.5，用户的 `/usage` 显示独立的 Claude 与 GPT 额度组，实际调用权限待验证。服务器桥的 Gemini API Key 模式仍为八个 Gemini 模型。Google Cloud 按量调用 Claude 需在项目的 Model Garden 开通合作模型权限，并由项目账单按 Token 结算。[Antigravity 模型表](https://antigravity.google/docs/models) · [Google Cloud 按量计费](https://antigravity.google/docs/pay-as-you-go/)

2026-11-26 只读核对服务器主聊为 `antigravity-local/gemini-4-argon`，北京时间 10:08–10:43 的留存日志中，该线路完成 7 次模型请求，无备用完成记录。桥接采用 Gemini API Key，使用计入 Key 对应的 Google Cloud 项目。具体项目账单扣款、开发者抵扣和现金预付余额待在 AI Studio 核对。[Gemini API 计费说明](https://antigravity.google/docs/pay-as-you-go/) · [AI Studio 余额说明](https://aistudio.google.com/docs/google-ai-plans#gemini-api-usage)

Google AI Studio Playground 使用网页套餐额度时不计入 Gemini API 支出。提示框内的钥匙图标可核对是否启用了付费 Key，启用后用量计入该 Key 所属项目。若 Playground 和反重力桥共用同一把 Key，仅凭支出页的 Key 筛选无法区分调用入口。[AI Studio 计费 FAQ](https://aistudio.google.com/docs/billing#is-AI-Studio-free) · [网页额度与 API 用量](https://aistudio.google.com/docs/google-ai-plans#gemini-api-usage)

2026-11-26 再次只读核对，服务器主聊为 AI Studio Playground Neon，两个备用为同桥的 Hydrogen、Gemini 3.8 Flash。反重力 API Key 供应商仍有配置，主聊、备用及专用槽均未引用。Google 确认个人 OAuth 可用于用户直接驱动的交互 ACP 会话及特定直接脚本，但不允许第三方 Agent 将其用作后台模型。TraceSoul 的自主调用属于后者，未切换到个人 OAuth。未发送测试推理或修改服务器配置。[Antigravity FAQ](https://antigravity.google/docs/faq) · [官方仓库 ACP 说明](https://github.com/google-antigravity/antigravity-cli/issues/1493)

### 2026-11-26 切换为本机个人登录

用户在了解 Google 对第三方后台使用的限制后，要求切换现有 `antigravity-local`。现显示为「反重力（个人订阅）」。Windows 桥使用官方 CLI 的本机个人登录，子进程不继承 Google API Key，隔离工作目录启用严格权限并关闭 `useG1Credits`。服务器沿私有 SSH 使用独立桥连接凭据，原 Gemini API Key 模式已退出这个供应商。统一启动器校验个人登录模式。主聊与两个备用仍为现有 AI Studio 路线。[Google 的后台使用说明](https://github.com/google-antigravity/antigravity-cli/issues/1493) · [AI Credits 设置](https://antigravity.google/docs/cli/credits)

本机测试注入无效的 Google Key 环境变量，Argon 合成短回复 HTTP 200，无凭据及错误凭据均 401。服务器同步 11 个模型，含 Claude Sonnet 5.5 和 Opus 5.5。服务器内置 Sonnet 短测通过，4.623 秒。其余供应商与槽位未切换，AI Studio 桥仍在线，未发送 QQ 消息。个人额度扣减量及正常长对话待后续核对。

## 2026-11-24 Google Cloud 项目封停核对

首次封停通知发于北京时间 11 月 22 日 20:20，Google 将活动归类为疑似资源被劫持，11 月 24 日 17:10 的申诉结果维持项目关闭。复核原始邮件，首封未给出触发判定的具体服务、凭据、来源或请求。当天的 Gemini App 网页自动化仅在本机试验，未接服务器。服务器 AI Studio Playground 桥接在 11 月 23 日接入，Antigravity 实测也晚于首封通知。

只读核对 New API 官方 Gemini 渠道的留存日志，11 月 22 日 14:25 至 19:43 有 22 条消费记录。该中转日志只覆盖经过此渠道的调用。凭据归属、Google 项目完整用量和具体触发活动待核对。[Google 封停与申诉说明](https://support.google.com/cloud/answer/7002354)

付款回执确认 Google 已收到款项，所属 Prepay 或 Postpay 及剩余金额待在交易页核对。Google 说明，关闭 Cloud Billing 账户会使剩余 Gemini Prepay 额度失效。符合资格转为 Postpay 时，未用 Prepay 余额会原路退回。Postpay 的未用实付余额可按账户状态申请退款，具体款项可由免费账单支持核对。[退款规则](https://docs.cloud.google.com/billing/docs/how-to/resolve-issues#request_a_refund) · [免费账单支持](https://docs.cloud.google.com/support/docs/get-billing-support)

线上只读核对确认，主聊使用 Playground 桥接的 Gemini 4 Argon，两个备用也使用该桥接。TraceSoul 容器读取桥接模型目录返回 HTTP 200。复盘和识图使用 DeepSeek Flash，生图和语音各有独立渠道。旧 New API 官方 Gemini 渠道用现有凭据请求 Google 模型列表返回 HTTP 401、`ACCOUNT_STATE_INVALID`。响应未提供可核对的 Cloud 项目 ID。此次只验证配置和目录，实际生成沿用已有记录。

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

## 2026-11-23 Google AI Pro 产品额度与 API 抵扣

- Gemini Apps（网页与手机 App）和 Antigravity 分别计算基础用量。两者的 Pro 基础额度均每五小时刷新，另有各自的周上限。Gemini Apps 根据模型、聊天长度和任务复杂度消耗用量，Antigravity 随代理完成的工作量消耗。另购的 Google One AI credits 可在支持的产品间共用。[Google One 权益](https://support.google.com/googleone/answer/14534406?hl=en-en)、[Gemini Apps 限额](https://support.google.com/gemini/answer/16275805?hl=en)、[Antigravity 计划](https://antigravity.google/docs/plans?app=antigravity-ide-)
- AI Studio 的 Pro 每日额度用于 Playground 和 Build 网页，Playground 按模型计限额。Gemini API Key 的请求由 Cloud 项目单独限流和计费，同一项目的多个 Key 共用限额。符合条件的 Pro 主订阅者可领取每月 10 美元开发者抵扣至该项目使用的 Cloud Billing 账号。Prepay 需要实付余额大于零才会优先应用合资格抵扣。[AI Studio 订阅说明](https://aistudio.google.com/docs/google-ai-plans)、[Playground 公告](https://discuss.ai.google.dev/t/daily-usage-limits-for-models-in-ai-studio/185935)、[API 限额](https://aistudio.google.com/docs/rate-limits)
- TraceSoul 后台可沿用已接通的 New API 官方 Gemini 渠道。个人 Antigravity 登录可供本人直接操作的 ACP 会话使用，后台代理调用需 API Key 或 Google Cloud 认证。AI Studio 网页日额度限于网页。此次仅核对公开资料与仓库记录，实际 API 项目抵扣、账单和长上下文表现未核对。[Antigravity FAQ](https://antigravity.google/docs/faq/)、[ACP 维护者说明](https://github.com/google-antigravity/antigravity-cli/issues/1493)

### Gemini 4 Argon 账户实测准备

- 官方确认符合条件的 Pro 账号可在 Antigravity 和 AI Studio Playground 使用 Argon。Antigravity 的同组模型共享 5 小时和周额度，CLI 的 `/usage` 可查看两项。AI Studio Playground 另有模型日限额，实际账号可用次数需现场验证。[Antigravity 模型](https://antigravity.google/docs/models?hl=en)、[Antigravity 用量](https://antigravity.google/docs/cli/commands/usage/)、[AI Studio 论坛答复](https://discuss.ai.google.dev/t/ai-pro-subscription/180857/230)
- Argon 标准 API 文本输入与输出（含思考）分别为每百万 token 2 和 10 美元，2026-12-01 起为 4 和 20 美元。[标准价格](https://aistudio.google.com/docs/pricing#gemini-4-argon)
- 已准备独立的合成长上下文及 JSON 回复探针。Edge 已打开带 Argon 参数的 Playground 链接，调试连接未建立。官方 Windows CLI 二进制下载超时，发行 ZIP 下载连接被重置。Antigravity 官方网页版可供已获 Remote Agents 的个人账号手动启动会话；账号开通状态未验证。准备阶段未提交推理请求或接入 TraceSoul。[Antigravity Remote Agents](https://antigravity.google/docs/remote-agents/)

同日用户在 AI Studio Playground 手动确认绑定 API Key 与未绑定 API Key 的合成短请求均返回。随后 8629 字符合成上下文在 6.1 秒取得合法 `reply` JSON，用户从 Run settings 确认当前模型为 `gemini-4-argon`。长测的 Key 状态与网页日额度扣减量未读取。Edge 调试连接复试仍超时。[Playground 快速入门](https://aistudio.google.com/docs/ai-studio-quickstart)

本机另备独立 Edge 读回探针，用户点扩展按钮时从当前 Playground 查找这次合成输入后的可见 JSON。扩展仅声明 `activeTab` 和 `scripting`，无网络接口或发送操作。语法检查及 7 个模拟 DOM 情形通过；真实 Edge 读回待验证，尚未加入 TraceSoul 供应商。

### AI Studio 网页转发社区方案核对

- Mag1cFall/AIStudio2API v0.3.14 提供 Windows 包。默认 SpiderMonkey 后端在 Go 进程内转发 Playground 私有协议和流式响应，无需每轮操作 Edge 网页；CJackHwang/AIstudioProxyAPI 使用 Playwright/Camoufox 驱动页面，iBUHub/AIStudioToAPI 的目标是 Build。Sub2API 的 AI Studio OAuth 使用 Cloud 项目的 Generative Language API。[版本](https://github.com/Mag1cFall/AIStudio2API/releases/tag/v0.3.14)、[实现](https://github.com/Mag1cFall/AIStudio2API/blob/main/docs/development.md)、[浏览器代理](https://github.com/CJackHwang/AIstudioProxyAPI)、[Build 代理](https://github.com/iBUHub/AIStudioToAPI)、[Sub2API 文档](https://github.com/Wei-Shaw/sub2api/blob/main/deploy/README.md#gemini-oauth-configuration)
- 已查到社区 Argon Playground 实跑，以及约 89 万字符请求在旧版更换后端后首字 45.5 秒、完成 94 秒的记录，测试材料与本机 8629 字符样本差异很大。另有账号访问受限报告，原因及适用范围未查明；Windows 的 Chrome 导入会读取账户会话材料。未查到同一普通 Pro 账号稳定每日 200 多轮并接近手动延迟的证据。本次只核对公开资料，未安装、登录或调用。[社区实测](https://linux.do/t/topic/3037340)、[延迟报告](https://github.com/Mag1cFall/AIStudio2API/issues/116)、[账户限制报告](https://github.com/Mag1cFall/AIStudio2API/issues/147)

### AI Studio 方案本机对照

- 复用 8629 字符合成材料，统一请求 `gemini-4-argon`，探针记录首段正式正文、流结束、`reply` JSON 和上游用量。三种代理标签的本机模拟 SSE 均通过，思考片段不计入首段正文。手动网页基线为 6.1 秒。
- AIStudio2API v0.3.14 的 Windows 发布包 SHA-256 与 GitHub 发行资产摘要一致。独立空会话实例仅监听回环，管理状态 HTTP 200，处于 STOPPED，验收进程已退出。iBUHub/AIStudioToAPI 1.4.1 构建通过，回环 `/v1/models` HTTP 200，自带 31 项未列 Argon，实际账户模型待验。CJackHwang/AIstudioProxyAPI 的独立 Python 3.12 依赖和启动器检查通过，Camoufox 已安装在试验目录，离线启动与 DOM 读回通过。测试副本将 HTTP 绑定回环并保留正常 TLS 验证，真实代理请求待验。
- 官方 Google Key 对本机 Argon 流式请求及从原中转机读取官方模型列表均返回 401，未取得模型输出。三种社区代理仍待用户在各自测试会话登录；本次未导入个人浏览器的登录数据、修改生产供应商或提交真实聊天材料。

用户选定沿用已在网页验证的 Google 账号。核对发现 AIStudio2API 内置隔离登录在需要时会自动同意 Drive 授权，故先验证 Playwright 的独立网页登录。修复测试副本误把 Google 登录页返回参数中的 AI Studio 域名当成已登录的问题，已确认专用浏览器可见且调试端口仅监听回环。等待用户登录期间未检出保存的会话或发生成请求。

首次独立 Google 登录窗口等候 180 秒后自动退出，账号会话未保存。已将本机测试窗口的登录等待改为 600 秒并重新打开，确认回环调试端口在线，模型服务仍等待账户会话。此阶段未发出模型请求。

用户完成独立登录后，旧代理额外转发模型列表的请求连接 Google IPv6 地址超时。关闭这一步并将会话保存移到模型列表加载之前后，登录状态成功落盘。随后窄窗口折叠了模型选择器，代理判定初始化失败并退出。以正常桌面宽度加载同一份测试会话后，模型选择器可见，Playground 页面显示未选 API Key。

- AIStudio2API v0.3.14 通过 `setup --storage-state` 复用用户主动登录的隔离会话，采用 Playground 与 SpiderMonkey，无需额外 Drive 授权。模型列表含 Argon。首段正式文本 7.689 秒，8.624 秒完成并通过 `reply` JSON 检查。
- CJackHwang/AIstudioProxyAPI 恢复会话后成功选中 Argon。旧设置和提交控件耗时，43.549 秒才取得 HTTP 200 流响应头，98.563 秒超时，未读回正文。
- iBUHub/AIStudioToAPI 1.4.1 通过同一隔离账户打开默认 Build applet，浏览器和本机 WebSocket 均就绪。模型列表 31 项且无 Argon。Argon 测试返回 HTTP 400，去掉 `reasoning_effort` 和 `max_tokens` 后仍返回 400，两次均无正文。
- 用 TraceSoul 自己的 OpenAI 兼容供应商测试客户端连接临时 Playground 代理，以独立供应商配置再次发送同一合成材料，10.295 秒取得合法 `reply` JSON。该客户端等待整段 JSON，因此此轮只记录完成时间。
- 本轮限于合成材料和本机回环。账号每日可用轮数、具体额度扣减、会话续期和长时间稳定性仍待核对。临时服务随测试结束退出，生产供应商及模型槽位未改动。

### Playground 桥接接入现有 TraceSoul

用户授权接入后，在现有服务器 Host 新增可选供应商 `aistudio-playground-local`，模型为 `gemini-4-argon`。Windows 上的 AIStudio2API 复用同一隔离会话，通过 SSH 私有转发连接现有 TraceSoul 容器。两端仅监听回环，Google 会话留在 Windows，TraceSoul 保存桥接专用 API Key。服务器容器读取模型列表为 200，无密钥请求和使用桥接 Key 请求管理接口均为 401。

服务器 TraceSoul 自带的 `model-test` 返回成功，耗时 5381 毫秒。主模型、其他用途槽、备用顺序及其余供应商配置与接入前一致，Host 未重启。工作区本机启动器会同时打开代理和 SSH 隧道，已验证退出后可重新启动。日常在「大脑 · LLM」可测试或选择该模型。账号日额度、会话长期续期和实际对话负载仍待验证。

重复双击启动器时，原实现检测到已有桥接后立即结束，容易误以为未启动。现在双击会打开本机管理页，运行中的桥接会显示状态并等待用户关闭状态窗口。可以按提示把本地管理口令复制到剪贴板，服务器仅使用独立的代理口令。

AIStudio2API 的「用量 → 配额日」以最近一次太平洋时间零点为起点，通过「筛选 → 模型」查看经过该桥的请求、Token 和 429 次数。已用只读统计核对跨日切换。这些指标来自桥接账本。Google 官方资料确认 Playground 按模型设每日额度，未查到返回账号精确余额的官方入口。Google AI Studio 的 Usage 页对应 Gemini API 项目。[Playground 限额公告](https://discuss.ai.google.dev/t/daily-usage-limits-for-models-in-ai-studio/185935)、[Google AI 订阅与 API 用量](https://aistudio.google.com/docs/google-ai-plans#gemini-api-usage)

同一隔离会话只读打开官方 Playground，确认模型选择器就绪、显示未选 API Key，核对时未发现每日余量提示。官方 Billing 页用于已领取的 Cloud API 抵扣和实付余额，此轮未发送生成请求。[Google AI 账单说明](https://aistudio.google.com/docs/google-ai-plans#gemini-api-usage)

达到限额时，在 AIStudio2API「冷却与请求」查看对应模型的原因和恢复时间。上游明确返回每日限额才按日判断；分钟限额和短期限额走短时冷却，单个 HTTP 429 无法区分。套餐限额以错误明示的恢复时间为准。列表为空表示桥接没有记录活动冷却，未提供 Google 余量。此轮用只读接口核对统计，不为检测额度发送请求。

AIStudio2API 将 `gemini-4-argon-search` 解析为基础 Argon 并为该请求启用 Google 搜索工具，模型判断是否需要实际检索。已只读核对现用桥接：两个型号均在目录内，搜索后缀已启用，全局搜索关闭。网页订阅额度受搜索影响的幅度及 TraceSoul 对结构化引用的展示未实测。[Google 搜索工具](https://aistudio.google.com/docs/google-search#how-it-works)

2026-11-23 晚间离线核对发现，管理页 `GET /v1/models` 的 401 来自启动时以及检查 SSH 链路时刻意发送的无密钥探针。实际主模型密钥与本机桥接一致。两次 Argon 请求在约 90 秒仍无首段内容并被客户端取消，桥接记为 499，无额度冷却。桥接在 20:54 已重新启动，期间未检出 Windows 休眠记录，原进程退出原因未确认。服务器内置短测试成功，耗时 7676 毫秒。日常启动改为用合法密钥检查模型目录，并用管理会话状态接口核实登录保护，以免在日志中产生预期的 401。

桥接无需刷新管理网页维持运行。启动器约每 5 秒向远端隧道发心跳；收到上次回复超过 20 秒、SSH 断开或远端容器更换时，隧道退出，当前版本的启动器随后关闭本机代理。此时可重新运行本机启动器恢复连接。晚间历史故障窗口未在本机电源事件和按当前 SSH 出口筛选的服务器日志中找到原因，复查时心跳和认证模型目录均正常。

21:09 的 Argon 请求在生成过程中被 AI Studio 以 `PROHIBITED_CONTENT` 终止，AIStudio2API 将该结果按 `502 upstream_error` 记账。非流式保活已经先写出 HTTP 200，TraceSoul 仍从响应体识别出上游错误。只读比较确认随后使用同一组消息的 Carbon 备用返回非空 JSON。核对时 SSH 心跳正常，无额度冷却记录，未重发原请求。

2026-11-24 的 Argon Playground 429 已核对为 Google 明示的模型套餐限额。北京时间 07:50 附近提示 8 小时 9 分钟，08:32 提示 7 小时 27 分钟，均指向当日 16:00 左右，与太平洋时间日界线一致。此时本机服务和 SSH 心跳正常。Gemini Apps 与 Antigravity 各自的五小时窗口不用于本次 Playground 额度，未发探测性生成或验证重置后的恢复。[AI Studio 订阅说明](https://aistudio.google.com/docs/google-ai-plans#overview)、[Gemini Apps 限额](https://support.google.com/gemini/answer/16275805?hl=en)、[Antigravity CLI 用量](https://antigravity.google/docs/cli/commands/usage/)

同日 08:35，冷却范围仍仅为 Argon。同一登录、同一 Playground 通道的一条 Neon 合成短测 2.91 秒返回 HTTP 200 和预期短回复，确认此次 Argon 429 没有阻断 Neon。此轮未切换 TraceSoul 模型配置，未实测其他模型的剩余配额。[Playground 按模型限额公告](https://discuss.ai.google.dev/t/daily-usage-limits-for-models-in-ai-studio/185935)

### 2026-11-24 Antigravity 官方本机入口

Windows 官方 CLI 1.5.15 已从 Google 发布源下载，SHA-512 与官方清单一致。已核对安装版本、`agy acp --help` 与本机 ACP v1 初始化。首次启动窗口已打开，查询会话模型时返回 `Authentication required`（`-32000`），等待用户完成 Google 登录。未发送推理请求或接入 TraceSoul 模型槽。Google 维护者确认，用户直接发起和操作的自定义 ACP 界面可使用个人 Pro 登录，自动聊天、日复盘或其他后台推理需 Gemini API Key 或 Google Cloud 认证。[ACP 官方文档](https://antigravity.google/docs/cli/acp)、[官方认证边界](https://github.com/google-antigravity/antigravity-cli/issues/1493)

首次 OAuth 授权时报 `token exchange failed`，直连 Google 令牌接口的 IPv6 地址超时。无凭据连通探针复现直连超时，经本机 Clash 的 HTTPS POST 约 1.25 秒收到预期的参数错误，本机启动器和 ACP 核对脚本均改用仅当前进程的代理环境变量，OAuth 回环回调走直连。已打开新的 CLI 窗口，观察到其与本机代理建立 TCP 连接，实际授权结果待用户重试验证。

用户经新窗口完成 Google 登录，官方 ACP v1 已成功创建会话并返回模型选项，目录含 Gemini 4 Argon、Gemini 4 Neon、Gemini 4 Hydrogen 与 Gemini 3.8 Flash。本轮只查会话元数据，真实生成及五小时、每周额度数值待确认。CLI 首次设置完成后，`/model` 选模型、`/usage` 查模型组额度。[CLI 模型设置](https://antigravity.google/docs/cli-settings)、[CLI 用量](https://antigravity.google/docs/cli/commands/usage/)

首次设置完成后，用户窗口与本机日志确认 Antigravity 返回地区资格错误。已登录及可读模型目录只证明认证和元数据可用。官方指引为使用同一账号核对 Google 条款页的国家版本，关联地有误时依实际情况申请更正。账户关联地、推理及额度仍未核实，未接入 TraceSoul。[反重力地区说明](https://antigravity.google/docs/faq/#what-is-google-antigravitys-geographical-availability)、[Google 地区更正](https://policies.google.com/country-association-form)

用户确认已更新账号地区后，沿用官方 CLI 的现有个人登录复验。Gemini 4 Neon 单条合成连通测试在约 11 秒内返回预期文本，交互入口识别 Google AI Pro，`/usage` 显示 Gemini 模型组共用的五小时和每周剩余百分比。此轮未验证 Argon 生成。第三方后台仍使用官方 API 认证入口，未注册个人登录的后台供应商。[CLI 用量](https://antigravity.google/docs/cli/commands/usage/) · [官方认证边界](https://github.com/google-antigravity/antigravity-cli/issues/1493)

接入准备复核通过 ACP v1 握手。用户在交互界面直接提交提示并确认工具权限时可沿用个人登录。后台若只需模型回复，可复用已验证的 New API 官方 Gemini 渠道。若需要 Antigravity 的代理能力，ACP 使用 `gemini-api-key` 并从子进程环境读取 `GEMINI_API_KEY`，也可采用 Google Cloud 按量计费认证。API 请求计入对应项目的账单，个人 Pro 的 Antigravity 五小时和周额度用于符合条件的个人登录会话。此次未验证付费 ACP 推理或账单抵扣，未变更运行配置。[ACP 认证方式](https://antigravity.google/docs/ide/extensions/acp/) · [官方认证边界](https://github.com/google-antigravity/antigravity-cli/issues/1493)

2026-11-25 沿用本机官方 CLI 1.5.15 与个人登录复查，ACP v1 初始化及会话元数据通过，模型选项含 Argon、Neon、Hydrogen。仅验证会话建立及目录。TraceSoul 当前有 Google Gemini 原生供应商模板，旧 New API 官方 Gemini 凭据实测 401，参见上文项目封停核对。个人登录对应本人直接操作的 ACP 界面，TraceSoul 角色提示与记忆组装后的后台推理使用有效 API 凭据。[ACP 官方文档](https://antigravity.google/docs/cli/acp) · [维护者说明](https://github.com/google-antigravity/antigravity-cli/issues/1493)

同日本机阿循配置新增 `antigravity-acp`（Antigravity 本机 ACP），按用户纠正替换先前误加的空白 Gemini 直连项。通过本机未修改的 `agy.exe acp` 子进程走 stdio，以独立临时工作目录和 `gemini-api-key` 认证提供文本聊天与复盘。API Key 从供应商配置注入子进程环境，使用 Google API 项目计费。既有个人登录档由独立 CLI 入口使用。WebUI 在「大脑 · LLM」填写 Key 后获取模型，再用合成提示测试。已构建启动器使用的 Host，使用占位 Key 验证 CLI 会话、目录和模型切换，模拟验证文本片段和工具权限拒绝。真实 Key 与生成待验证。

### 2026-11-24 本机额度面板

独立面板只监听本机回环地址。AI Studio Playground 来自现有桥接按模型的请求、429 和冷却统计，以太平洋时间的配额日查询。Antigravity 展示官方 `/usage` 快照及采集时间，可手动更新。Codex 通过本机 App Server 的 `account/rateLimits/read` 获取订阅窗口。New API 服务器通过现有 SSH 只读汇总近 24 小时中转用量，OpenRouter 的转发 Key 通过官方接口获取限额和当日用量，返回客户端的内容仅含汇总字段。OpenRouter 全账号余额需管理 Key 或账户页，Cursor、ChatGPT 网页及 Gemini 的账号数据目前可从官方页面抄录快照。已用真实本机接口、服务器只读查询和独立浏览器渲染验证。没有新增转发或远程服务。[OpenRouter Key 接口](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key) · [Codex App Server](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)

Windows 桌面入口为带图标的单文件 GUI 启动器。它先检查面板健康接口，在需要时启动既有本机服务，再以 Edge 应用窗口打开同一地址，沿用 Edge 配置中的快照存储。已核对桌面副本、内嵌图标、可执行文件类型和本机健康接口。桌面实际启动被工具策略拦截，冷启动待实测。
