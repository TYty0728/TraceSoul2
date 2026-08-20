using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Tools.Memory;

namespace TraceSoul2.Migrate
{
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            SQLitePCL.Batteries_V2.Init();
            var context = MigrationContext.Create();
            try
            {
                if (args.Length == 0)
                {
                    PrintUsage(context);
                    return 2;
                }
                var command = args[0].ToLowerInvariant();
                var rest = args.Skip(1).ToArray();
                switch (command)
                {
                    case "import": return await FullLogImporter.RunAsync(context, rest);
                    case "classify": return await RealmClassifier.RunAsync(context, rest);
                    case "build": return await DayBuilder.RunAsync(context, rest);
                    case "cognition-backfill": return await DayBuilder.BackfillCognitionsAsync(context, rest);
                    case "dedupe-cognitions": DayBuilder.DedupeCognitions(context); return 0;
                    case "run-all": return await FullLoopRunner.RunAsync(context, rest);
                    case "promote-all": return await PromoteAllCommand(context);
                    case "normalize-ladder": return NormalizeLadderCommand(context);
                    case "embed": return EmbedCommand(context);
                    case "cards": return DayBuilder.PrintCardsCommand(context);
                    case "live": return await LiveAsync(context);
                    case "seed-identity": return IdentitySeeder.Run(context, rest);
                    default:
                        Console.WriteLine("未知命令：" + command);
                        PrintUsage(context);
                        return 2;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine("错误：" + exception.Message);
                Console.WriteLine(exception.StackTrace);
                return 1;
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>只启动实时监视控制台（不跑构筑），用于查看当前数据。</summary>
        private static async Task<int> LiveAsync(MigrationContext context)
        {
            MigrationLive.Start(context);
            Console.WriteLine("按 Ctrl+C 退出。");
            await Task.Delay(Timeout.Infinite);
            return 0;
        }

        /// <summary>把全部条目的一句话总结编码为 BGE 语义向量（幂等，可随时重跑）。</summary>
        private static int EmbedCommand(MigrationContext context)
        {
            var encoder = context.RequireEncoder();
            var indexes = context.Migration.GetActiveEventIndexes();
            var entries = context.Migration.GetEntriesByIndexIds(indexes.Select(x => x.Id).ToList());
            var done = EntryEmbedder.EmbedAll(entries, context.Vectors, encoder);
            Console.WriteLine("已编码 " + done + " 条条目向量（共 " + entries.Count + " 条条目，模型 " + encoder.ModelId + "）。");
            Console.WriteLine("向量库累计：" + context.Vectors.CountEntryEmbeddings() + " 条。");
            return 0;
        }

        /// <summary>把日榜数据里出现的所有周/月/年周期全部重建一遍（含永久榜）。</summary>
        private static async Task<int> PromoteAllCommand(MigrationContext context)
        {
            await DayLadderLogic.PromoteAllAsync(context, context.RequirePair(), context.RequireLlm());
            return 0;
        }

        private static int NormalizeLadderCommand(MigrationContext context)
        {
            var pruned = context.Migration.PruneCrossTierLadderDuplicates();
            Console.WriteLine("榜单跨层归一化完成：移除 " + pruned + " 条低层重复记录。");
            return 0;
        }

        private static void PrintUsage(MigrationContext context)
        {
            Console.WriteLine("TraceSoul2.Migrate — 老系统迁移与记忆构筑工具");
            Console.WriteLine("数据目录：" + context.DataDirectory + "（可用环境变量 TRACESOUL2_DATA 修改）");
            Console.WriteLine();
            Console.WriteLine("命令：");
            Console.WriteLine("  seed-identity [--username 用户名] [--assname 角色名] [--callname 称呼] [--cards <identity_cards.json>]");
            Console.WriteLine("      保存两人名字并创建四张身份短卡（新路线：cards 文件只放人格卡，其余三张从原始态开始成长）");
            Console.WriteLine("  import --log <full_log.txt> [--from yyyy-MM-dd] [--to yyyy-MM-dd] [--force|--missing]");
            Console.WriteLine("      把老日志解析为 moments（--missing 仅补入按 full_log 文件名/起止行 index 判定缺失的消息）");
            Console.WriteLine("  classify [--from] [--to] [--batch 100] [--parallel 4]");
            Console.WriteLine("      用 LLM 为 unclassified Moment 批量分 Realm");
            Console.WriteLine("  build --day yyyy-MM-dd");
            Console.WriteLine("      单天构筑：多维索引 + 条目（细节浸染）→ 日终三卡复盘 + 内心同步 + 日榜");
            Console.WriteLine("  cognition-backfill");
            Console.WriteLine("      认知回填：逐天用当天已有事件跑认知形成并排进日榜（主线老库用，同文 create 幂等）");
            Console.WriteLine("  dedupe-cognitions");
            Console.WriteLine("      认知合并去重：合并同文/近义（二元组 Jaccard≥0.75）认知，保留置信度最高者");
            Console.WriteLine("  run-all --log <full_log.txt> [--from yyyy-MM-dd] [--to yyyy-MM-dd]");
            Console.WriteLine("      全量自动循环：导入全部 → 分类 → 按记忆日逐天构筑（含空天），进度写入 full_run_report.txt");
            Console.WriteLine("  promote-all");
            Console.WriteLine("      把日榜数据里的所有周/月/年周期重建一遍（含永久榜，幂等）");
            Console.WriteLine("  normalize-ladder");
            Console.WriteLine("      清理同一 RefId 的跨层重复，只保留最高层级（幂等）");
            Console.WriteLine("  embed");
            Console.WriteLine("      把全部条目的一句话总结编码为 BGE 语义向量（幂等；Host 召回用）");
            Console.WriteLine("  cards");
            Console.WriteLine("      打印当前四张身份小卡与内心");
        }
    }
}
