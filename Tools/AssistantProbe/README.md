# 助手协作诊断

显式的真实模型诊断程序，不在发布安装包或自动回归中运行。仅用于已准备好的独立测试快照；不使用正式角色目录。

```powershell
dotnet run --project Tools/AssistantProbe -- <snapshot-directory> seed
dotnet run --project Tools/AssistantProbe -- <snapshot-directory> expand
dotnet run --project Tools/AssistantProbe -- <snapshot-directory> recall
```

快照必须包含 `baseline.json` 和关闭 QQ 的 `onebot.json`，插件目录必须为空。程序使用快照配置中的真实模型，会产生 API 消耗并写入测试对话、诊断日志和输出；内置话题均明确标为虚构协作用例。`recall` / `recall-reviewed` 关闭近期历史以检查召回。配置、数据库和结果只保留在本地，不提交 Git。
