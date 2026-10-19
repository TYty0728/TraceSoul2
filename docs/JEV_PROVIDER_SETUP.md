# 原生 Decisions 供应商配置

2026-10-16：源码已新增 WebUI 配置、模型发现、原生客户端和单次模拟决策测试，Host 已构建验证；尚未发布或替换运行实例。WXXZ Moment 准入与 300/30 秒调度仍待实现，保存配置不会开启这些功能。

## 页面操作

1. 使用包含本次改动的 Host，在「大脑 · LLM → 供应商」选择模板 **OpenRouter Decisions** 并新增。
2. API Key 填 OpenRouter Key，选择连接方式及基址：

| 模式 | API Base URL | 中转凭据 |
|---|---|---|
| 直连 OpenRouter | `https://openrouter.ai/api` | 不发送；可留空 |
| 专用中转 | 自己部署的基址，例如 `https://relay.example.com/tracesoul` | 填部署时生成的专用凭据 |

直连请求使用 `/alpha/decisions`，模型列表使用 `/v1/models?output_modalities=decisions`；不要填写聊天接口的 `/api/v1` 或完整决策路径。专用中转分别追加 `/decisions` 和 `/models`，不是任意 New API 聊天渠道。新模板默认 Jev 模型 `typesafe/jev-1.14`，地址可自己修改。

3. 保存供应商，获取模型列表。列表可见不等于账号有推理权限；可以手动添加实际模型 ID。
4. 点击某模型旁的「决策测试（使用额度）」发送一次固定模拟场景，页面显示选择、耗时与上游报告费用。无真实聊天或记忆，不执行动作；没有自动重试。GPT Decisions 此前在该用户账号上返回过 403，不能因列表发现而认为限制已解除。
5. 在「默认用途槽 → JEV（决策）」选择供应商和模型，保存槽位。原生 Decisions 不可用于主模型、复盘或其它聊天槽。旧聊天型 JEV 仍保留并标注，原生客户端不会偷偷将旧聊天配置当作 Decisions 使用。

API Key 和中转凭据留空表示保持原值；中转凭据提供单独清除开关。页面只显示是否保存，接口不回传密钥。切换直连时即使仍保存中转凭据也不会发往 OpenRouter。聊天温度、Top-P、输出上限、思考开关及聊天重试不参与原生决策请求。

### 中转测试返回 404

先检查「决策连接方式」是否仍为「直连 OpenRouter」。仅修改 Base URL 不会自动切换模式；中转基址配直连模式会错误追加 `/alpha/decisions`，专用中转只有 `/decisions`。应选择「专用中转」，填写部署生成的独立中转凭据，保留 OpenRouter API Key，再保存并获取模型列表。2026-10-16 本地排障确认过这种配置组合；中转的受鉴权模型列表仍正常，未为排障重复调用付费推理。

## VPN 与代理

请求由 **Host 所在机器** 发出。Host 在本机且 VPN 已覆盖该进程时可直接填写官方基址；仅浏览器代理不保证 Host 可联网。Host 在服务器时，本机电脑 VPN 不会自动供服务器使用。

原生 Decisions 支持「高级配置 → 代理地址」，如 `http://127.0.0.1:7890`，端口必须以实际代理软件为准；也支持 HTTPS、SOCKS5。该地址从 Host 进程所在机器/容器解释，服务器里的 127.0.0.1 不是用户电脑。留空使用 .NET 默认代理行为，不自动探测本机 VPN。原生请求校验 TLS、禁止自动跳转，失败仅返回脱敏诊断。其他聊天协议的该代理字段仍是旧的暂存行为，此次没有一并修改。

## 验证与边界

- Host 构建通过，完整 ChatCheck 通过（ChatCheck 保留原有 24 项相机类型重复警告）。
- 原生专项覆盖配置重载、两种路径、上游/中转 Key 区分、槽位隔离、choice/noul/score 校验、异常响应与不重试。
- 临时真实 Host 配合本机 HTTP 模拟服务，验证供应商 API、JEV 保存、公开投影无密钥、拒绝误设聊天槽、HTTP 403、307 不跟随、实际显式代理 CONNECT。没有外部推理请求。
- 页面脚本检查覆盖原生与旧聊天筛选、模式切换、凭据保留/清除，以及保存不触发推理。

```powershell
dotnet build Tools/Host/TraceSoul2.Host.csproj --nologo
dotnet build Tools/ChatCheck/ChatCheck.csproj --nologo
dotnet Tools/ChatCheck/bin/Debug/net8.0/ChatCheck.dll --decisions
python scripts/tests/test-decisions-host.py
node scripts/tests/test-provider-slots-ui.cjs
node scripts/tests/test-decisions-ui.cjs
```

协议依据：[OpenRouter Jev / Decisions](https://openrouter.ai/docs/guides/community/jev)。真实中转既有联调见 [DecisionRelay](../Tools/DecisionRelay/README.md)，后续主链设计见 [WXXZ Moment](WXXZ_MOMENT_FLOW.md)。
