# Security

## 不应提交的内容

角色数据库、身份短卡、聊天记录、`llm-providers.json`、`onebot.json`、插件私有配置和任何 API Key 都只能放在软件外的家目录。仓库的 `.gitignore` 会拦截这些常见文件，但提交前仍应运行：

```powershell
pwsh scripts/Test-PublishSafety.ps1
```

如果密钥曾经进入 commit，仅从最新版本删除不够：应立即吊销并重新签发密钥，再清理 Git 历史。

## 本机控制台

WebUI 只应监听回环地址。不要把它通过反向代理、端口映射或隧道暴露到公网。

## 更新安全

内置更新器只接受配置仓库的正式 GitHub Release，并要求发布 ZIP 同时提供 SHA-256 文件。更新前会校验摘要；角色数据与插件目录不参与软件替换。
