# Security

## 不应提交的内容

角色数据库、身份短卡、聊天记录、`llm-providers.json`、`onebot.json`、插件私有配置和任何 API Key 都只能放在软件外的家目录。仓库的 `.gitignore` 会拦截这些常见文件，但提交前仍应运行：

```powershell
pwsh scripts/Test-PublishSafety.ps1
```

如果密钥曾经进入 commit，仅从最新版本删除不够：应立即吊销并重新签发密钥，再清理 Git 历史。

## 控制台认证

WebUI 首次启动生成随机管理员密码，密码只显示一次，磁盘仅保存 PBKDF2-SHA256 摘要。所有控制 API 与 SSE 日志都要求加密 Cookie 会话；首次登录必须改密，改密会使旧会话失效。

Docker 模式只允许通过 `TRACESOUL2_TRUST_CONTAINER_PROXY=1` 信任容器私网转发，Compose 的宿主端口仍必须绑定 `127.0.0.1`。公网使用必须配置 `TRACESOUL2_PUBLIC_HOSTS` 精确域名白名单，并通过 HTTPS 反向代理访问。不要改成 `0.0.0.0:5080`，也不要把容器加入不受信任服务共享的 Docker 网络。

OneBot 和游戏桥 WebSocket 属于机器接口，不接受管理员 Cookie，继续使用各自的 `access_token`。不要把这些 token 与控制台密码设置成相同值。

## 更新安全

内置更新器只接受配置仓库的正式 GitHub Release，并要求发布 ZIP 同时提供 SHA-256 文件。更新前会校验摘要；角色数据与插件目录不参与软件替换。
