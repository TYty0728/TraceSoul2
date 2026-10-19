# OpenRouter 决策中转客户端

配合现有 Nginx 的专用 HTTPS 入口使用，Python 3.10+ 标准库即可运行。通过同一 OpenRouter Decisions 协议调用 Jev 与 GPT-6 Luna Decisions，不转换成聊天接口，也不执行模型选择的动作。

这是独立联调客户端；尚未接入 Host 的供应商表单、Soul 调度或感官采集。现有 JEV 槽不能直接使用此配置冒充普通聊天供应商。

## 私密连接配置

在仓库外保存 JSON，填写实际入口和凭据；不要提交该文件：

```json
{
  "base_url": "https://relay.example.com/tracesoul",
  "relay_key": "YOUR_PRIVATE_RELAY_KEY",
  "openrouter_api_key": "YOUR_OPENROUTER_KEY",
  "model": "typesafe/jev-1.14"
}
```

`relay_key` 用于访问自己的中转，`openrouter_api_key` 用于 OpenRouter 计费鉴权。模型可改为 `openai/gpt-6-luna-decisions`。两者都不是 New API 令牌；此流量不使用 New API 额度。

列出决策模型（不触发付费推理，无需 OpenRouter Key）：

```sh
python client.py --config /private/connection.json models
```

一次真实调用（消耗 OpenRouter 额度）：准备 `state.json` 与 `questions.json`，例如：

```json
{"activity":"用户正在阅读，场景没有新变化，暂无待办。"}
```

```json
{
  "attention": {
    "type": "choice",
    "instructions": "依据当前状态选择下一步。不要为没有依据的变化采取行动。",
    "criteria": {
      "wait": "维持等待，没有需要处理的新情况",
      "observe": "现有事实不足，需要会话已允许的补充观察",
      "engage_main": "需要由主模型深入处理的变化或任务"
    }
  }
}
```

```sh
python client.py --config /private/connection.json decide --state state.json --questions questions.json
python client.py --config /private/connection.json decide --state state.json --questions questions.json --model openai/gpt-6-luna-decisions
```

返回原生 `answers`、概率、置信度与 usage。校验题目 ID、类型、选择范围和概率；失败不会当成有效决策，也不会自动重复推理。置信度不等同于正确率，是否行动仍由服务器程序判断。

## 部署约定

- Nginx 的 `POST /tracesoul/decisions` 固定转发到 `https://openrouter.ai/api/alpha/decisions`；`GET /tracesoul/models` 固定读取 `/api/v1/models?output_modalities=decisions`。
- 两个入口都检查 `X-TraceSoul-Relay-Key`。仅决策请求透传调用端的 Bearer Key；中转凭据不会发往 OpenRouter。不接受任意上游 URL，不把 New API 通用路径改为决策接口。
- HTTPS 两端校验证书；设置上游 Host/SNI、DNS 更新、限流、并发/请求体/超时上限，不自动重试 POST。当前部署限额为 256 KiB 请求、每 IP 2 请求/秒（突发 10）、并发 4；这些是传输上限，不是高频上下文预算。
- 真实站点路径、专用凭据及恢复备份留在本机运行记录。修改前备份现有站点，`nginx -t` 成功后平滑 reload，失败恢复原配置；保持原 New API 路由。
- 不记录请求正文、Authorization 或中转凭据。模型输出只作为数据，不拼成可执行代码或可信系统指令。

## 验证

```sh
python -m unittest discover -s Tools/DecisionRelay -p 'test_*.py' -v
```

2026-10-15 已部署私有 Nginx 中转：外网证书校验通过，模型列表包含两种目标模型；无凭据 403、错误方法 405、未知路径 404、超大请求 413，伪造 OpenRouter Key 得到上游 JSON 401，原 New API 状态仍正常。客户端 11 项测试及通过真实中转获取模型列表均通过。未使用真实 OpenRouter Key，尚未验证账号权限、余额、实际推理质量及延迟。

协议依据：[OpenRouter Jev](https://openrouter.ai/docs/guides/community/jev)、[决策模型列表](https://openrouter.ai/docs/guides/overview/models)。

### 首次真实联调（2026-10-15）

用户提供 OpenRouter Key 后，通过已部署中转使用模拟场景调用；没有发送实际聊天、记忆或感官数据，没有执行返回的动作。Key 保存在仓库外的私密连接配置。

| 模型 / 场景 | 结果 | 客户端总耗时 | 响应 usage.cost（USD） |
|---|---|---|---|
| Jev 1.14 / 安静阅读、无待办 | `wait` | 0.908 秒 | 0.000027132 |
| Jev 1.14 / 指示灯图像模糊、已允许补充观察 | `observe` | 0.644 秒 | 0.000029442 |
| Jev 1.14 / 实验已结束、结果齐全且待分析 | `engage_main` | 0.657 秒 | 0.000032088 |
| GPT-6 Luna Decisions / 同一安静阅读场景 | 上游 HTTP 403 | 0.297 秒（拒绝响应） | 未返回 usage |

Jev 实际模型快照为 `typesafe/jev-1.14-20261014`；三次调用均通过 choice、noul、score 校验，选择符合事先定义的场景预期。输入分别为 646、701、764 tokens，三次响应报告费用合计 USD 0.000088662。计时包括本机到中转及上游整个请求；每场景仅一次，不能据此推断长期延迟、准确率或生产预算。

GPT 首次 403 后额外一次诊断请求仍为 403；上游错误为 `The request is prohibited due to a violation of provider Terms Of Service.`，随后停止重试。该响应没有给出具体限制原因，不能认定为余额不足、模型名错误或已封禁整个账号；需要通过 OpenRouter 控制台或支持渠道核实该账号对该模型的访问限制。尚未获得 GPT 的有效决策响应。

联调证明 Jev 经中转可用；Host 供应商配置、定时决策及真实执行仍未接入。本轮未修改服务端配置、发布版本或启动自动模型调用。
