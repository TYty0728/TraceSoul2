# TraceSoul2.Migrate

导入历史对话，并按记忆日完成四步复盘：**提取事件 → 挑选事件 → 形成认知 → 更新当前 runtime**。实现、恢复规则、旧代码删除范围和测试见[每日复盘](../../docs/DAILY_REVIEW.md)。

记忆日使用北京时间 04:00 边界。默认数据目录与 Host 共用 TraceHome 当前 Soul，可用 `TRACESOUL2_DATA` 指向显式测试目录。不要把正式配置、对话或密钥放入仓库。

## 命令

```text
migrate seed-identity --username <用户名> --assname <角色名> --callname <称呼> [--cards <文件>]
migrate import --log <full_log.txt> [--from yyyy-MM-dd] [--to yyyy-MM-dd]
migrate classify [--from yyyy-MM-dd] [--to yyyy-MM-dd]
migrate build --day yyyy-MM-dd
migrate run-all --log <full_log.txt> [--from yyyy-MM-dd] [--to yyyy-MM-dd]
migrate cards
migrate embed
migrate promote-all
migrate normalize-ladder
migrate live
```

`build` 使用已保存的复盘槽及其配置；`TRACESOUL2_MIGRATION_PROVIDER` 和 `TRACESOUL2_MIGRATION_MODEL` 可覆盖本次选择。`run-all` 依次导入、分类、按日构筑；一天失败或预算让出即停止后续日期，重新运行从新检查点继续。它不再自动启动全历史榜单晋升。

`embed` 幂等补齐本地事件向量。`promote-all` 是显式请求模型的独立历史榜单维护；不是每日完成条件。`normalize-ladder` 清理跨层重复指针，不删除原始事件或认知。

完成日没有新来源时零模型调用；迟到消息只追加处理。旧版未完成检查点不导入新流程，原始数据和已入库记忆保留。源码不再提供 `replay`、`observe`、`cognition-backfill` 或 `dedupe-cognitions` 旧入口。

## 离线回归

```powershell
./scripts/Test-Review.ps1
```

脚本构建真实 Migration 程序，使用模拟 LLM、临时数据库以及独立子进程故障注入。不会加载正式角色或消耗真实模型 API。另执行完整 ChatCheck 和 Host 构建验证周边功能。
