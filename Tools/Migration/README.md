# TraceSoul2.Migrate — 老系统迁移与记忆重建工具

把 AstrBot 老系统的 `full_log.txt` 导入为新框架 moments，并按数据内时间逐日复盘出
事实切片 / 认知切片 / 人生 Tag / 时间阶梯（比较晋升制）。

**记忆日边界**：每天 04:00（+08:00）起算第二天；04:00 前的消息归前一天。导入、复盘、
阶梯滚动统一使用该边界（`DateRange.DayKey / DayStartMs / DayEndMs`）。

所有命令共享数据目录（默认 `bin/Debug/net8.0/data`，可用环境变量 `TRACESOUL2_DATA` 修改）。
老系统文件全程只读。

迁移器固定关闭对话槽的深度思考，只请求短结构化 JSON，避免推理内容耗尽输出额度；这不会修改
WebUI 中保存的提供商或日常对话设置。若当前对话模型本身强制推理，可临时设置
`TRACESOUL2_MIGRATION_PROVIDER` 与可选的 `TRACESOUL2_MIGRATION_MODEL`，只为本次迁移选择另一套模型。

## 命令顺序（试点与全量相同）

```powershell
$env:TRACESOUL2_DATA = 'D:\path\to\trial-data'

# 0. 两人名字 + 四张身份短卡（无 --cards 时自动使用项目内的 identity_cards.json 种子）
migrate seed-identity --username 小雨 --assname 小光 --callname 雨雨

# 0b. 配置 LLM：运行 Host 控制台粘贴 API Key，或直接编辑
#     <data>\llm-providers.json（默认 baseUrl=https://api.deepseek.com, model=deepseek-v4-flash）

# 1. 导入（无需 LLM；按天幂等；--missing 可补入已复盘日期后来追加的消息；--force 重导整天）
migrate import --log <full_log.txt 路径> --from 2026-02-16 --to 2026-08-16

# 2. Realm 批量分类（需要 LLM；默认 100 条/批，上限 120；unclassified → 四层现实）
migrate classify --from 2026-02-16 --to 2026-08-16 [--batch 100]

# 3. 逐日复盘（需要 LLM；观察→认知→日榜；done 的天自动跳过）
migrate replay --from 2026-02-16 --to 2026-08-16

# 3b. 诊断单天观察（只读不提交：打印原始输出 + 归一化后，用于审核质量问题）
migrate observe --day 2026-02-16

# 3c. 强制重放单天（先清理该天的事实/唤醒/观察记录/日榜，认知保留；用于审核后修正）
migrate replay --day 2026-02-16 --force

# 4. 审核报告（控制台 + data/review_report.txt）
migrate report --from 2026-02-16 --to 2026-08-16

# 或一键链式（全量推荐）：import → classify → replay → report，失败自动继续，可断点续跑
migrate run-all --log <full_log.txt 路径> --from 2026-02-16 --to 2026-08-16

# 维护：清除同一记忆在多个榜单层级的旧重复，只保留最高层（幂等）
migrate normalize-ladder

# 维护：补齐全部有效事件条目的语义向量（幂等）
migrate embed
```

`--continue`（classify/replay 通用）：某批/某天失败时记入 failed 状态并继续，最后汇总失败数；
重跑会自动跳过 done 的天、重试 unclassified/failed 的部分，失败天可用 `replay --day X --force` 单独修复。

每个 LLM 调用的原始输出 JSON 都会留痕到迁移库的 `replay_call_log` 表（DayKey/CallKind/OutputJson），
审核时可逐条核对「观察器到底看到了什么」。

## 离线冒烟（不花 API 钱，验证机理）

```powershell
migrate replay --mock --from 2026-02-16 --to 2026-02-19
migrate classify --mock --from 2026-02-16 --to 2026-02-19
```

mock 返回结构合法、内容标注 `(mock)` 的输出，只验证链路，不验证内容质量。

## 增量续传（老系统还在跑时）

- `import_cursors`：每个源文件一行，记录 `last_line / file_size / last_line_hash`。
  文件变大时从游标续读；文件轮转后新建游标行。
- `migration_review_state`：每天一行（`pending / in_progress / done / failed`）。
  `done` 的天默认跳过，除非删掉该行或重建数据目录。
- 新的一天消息追加到日志后，重复运行 `import → classify → replay` 即可接着做，不会重来。
- 日志补齐了已存在日期的旧消息时，使用 `import ... --missing`。它沿用旧导入约定，按
  `full_log.txt:起始行:结束行` 来源 index 比对，只插入主线尚不存在的 Moment；当天已有事件 index
  不会被删除，新 Moment 会在下一次构筑时追加复盘。源日志必须保持原文件的行顺序并采用尾部追加。

## 每日复盘产出与上限

| 项目 | 规则 |
|---|---|
| 事实切片 | 每批 ≤3（内核 Normalize 上限），每天 ≤8，<20 字，带 Realm/证据 |
| 认知切片 | 每天 ≤3（内核上限），create/reinforce/revise/weaken，必须连当日激活 Tag |
| 新人生 Tag | 每批 ≤1、每天 ≤3（可长期复用主题，重名自动复用） |
| 日榜 | 事件/认知各 ≤10，允许不足；比较选出，只存指针不新增数据 |

## 质量防线（增量 + 存量）

- 增量止血（复盘内置）：观察提示词附带「已有高频 Tag Top30」名单要求同义复用；提交层对新 Tag
  提案做归一化同名查重，命中即复用旧 Tag；认知 create 与已有认知完全同摘要时自动转 reinforce。
- 存量清理：`migrate dedupe`（默认预览计划）→ `migrate dedupe --apply`（只自动合并完全重复认知，
  Tag 模糊簇需人工裁决，清单写入 `data/dedupe_plan.txt`）。

## 时间阶梯滚动（比较晋升制）

批量回放在周期边界自动滚上层阶梯（与实时系统「周一 04:00 滚周」语义一致）：

| 边界 | 触发 | 挑战者 → 在位者 | 容量 |
|---|---|---|---|
| 周日 | day 复盘后 | 本周全部日榜条目 → 上周周榜 | 10/10 |
| 月末 | 当天复盘后 | 本月全部日榜条目 → 上月月榜 | 8/8 |
| 年末 | 月滚之后 | 本年月榜条目 → 去年年榜 | 5/5 |
| 年末 | 年滚之后 | 新年榜条目 → 永久榜 | 10/10 |

被替换下来的条目只退出榜单，事实/认知仍在网中。`ladder_items` 只存指针（RefId/RefKind/一句原因），永不新增数据。
自动晋升与 `normalize-ladder` 都会清除跨层重复，保证同一 RefId 只保留在最高榜单层级。

## 数据表

- 主库 `tracesoul2-brainframe.sqlite3`：moments / fact_slices / cognition_slices /
  life_tags / memory_observation_runs / **ladder_items（时间阶梯，供活体系统
  `memory.ladder.snapshot` Facet 常驻注入）** 等（与 Host 同库，可直接被 Host 使用）。
- 迁移库 `migration.sqlite3`：`import_cursors` / `migration_review_state` / `replay_call_log`。
