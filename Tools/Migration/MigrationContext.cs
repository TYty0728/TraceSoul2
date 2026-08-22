using System;
using System.Globalization;
using System.IO;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Host;
using TraceSoul2.Manager;
using TraceSoul2.Tools.Memory;

namespace TraceSoul2.Migrate
{
    /// <summary>迁移工具共享的运行时上下文：数据目录、主库、迁移库、LLM 提供商。</summary>
    public sealed class MigrationContext : IDisposable
    {
        public const string ConversationId = "tracesoul2";
        public const string ImportPluginId = "legacy.corememory.import";
        public static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

        public string DataDirectory { get; private set; }
        public string BrainframePath { get; private set; }
        public SqliteMemoryManager Store { get; private set; }
        public MigrationDb Migration { get; private set; }
        public LlmProviderStore Providers { get; private set; }
        public ILlmClient Llm { get; private set; }
        public SqliteVectorManager Vectors { get; private set; }
        public string MigrationProviderId { get; private set; }
        public string MigrationModel { get; private set; }
        private OnnxBgeEncoder encoder;

        public static MigrationContext Create()
        {
            var dataDir = Environment.GetEnvironmentVariable("TRACESOUL2_DATA");
            if (string.IsNullOrWhiteSpace(dataDir))
                dataDir = TraceSoul2.Host.TraceHome.Resolve().SoulDirectory;
            dataDir = Path.GetFullPath(dataDir);
            Directory.CreateDirectory(dataDir);
            var context = new MigrationContext();
            context.DataDirectory = dataDir;
            context.BrainframePath = Path.Combine(dataDir, "tracesoul2-brainframe.sqlite3");
            context.Store = new SqliteMemoryManager(context.BrainframePath);
            // 迁移复盘跨度大，扩大候选窗口，防止早期事实被新事实挤出唤醒候选。
            context.Store.CandidateWindow = 2000;
            context.Migration = new MigrationDb(
                Path.Combine(dataDir, "migration.sqlite3"), context.BrainframePath);
            context.Vectors = new SqliteVectorManager(
                Path.Combine(dataDir, "tracesoul2-vectors.sqlite3"));
            context.Providers = new LlmProviderStore(Path.Combine(dataDir, "llm-providers.json"));
            // 日构建 / 复盘要短而稳定的结构化 JSON。优先环境变量，其次复盘槽，最后对话开口；一律关思考。
            context.MigrationProviderId = Environment.GetEnvironmentVariable("TRACESOUL2_MIGRATION_PROVIDER");
            context.MigrationModel = Environment.GetEnvironmentVariable("TRACESOUL2_MIGRATION_MODEL");
            if (!string.IsNullOrWhiteSpace(context.MigrationProviderId))
            {
                context.Llm = context.Providers.CreateClient(
                    context.MigrationProviderId, context.MigrationModel, false);
            }
            else
            {
                context.Llm = context.Providers.CreateReviewClient();
                if (context.Llm != null)
                {
                    context.MigrationProviderId = context.Llm.ProviderId;
                    context.MigrationModel = context.Llm.Model;
                }
                else
                    context.MigrationProviderId = context.Providers.CurrentId;
            }
            return context;
        }

        public ILlmClient CreateLlmClient()
        {
            return Providers.CreateClient(MigrationProviderId, MigrationModel, false);
        }

        /// <summary>BGE 编码器按需加载（模型约 90MB，非向量命令不加载）。</summary>
        public OnnxBgeEncoder RequireEncoder()
        {
            if (encoder != null) return encoder;
            var modelDir = Path.Combine(AppContext.BaseDirectory, "Models", "BgeSmallZh");
            encoder = new OnnxBgeEncoder(
                Path.Combine(modelDir, "bge-small-zh-v1.5.onnx"),
                Path.Combine(modelDir, "vocab.txt"));
            return encoder;
        }

        public ILlmClient RequireLlm()
        {
            if (Llm == null)
                throw new InvalidOperationException(
                    "还没有可用的语言模型提供商或 API Key。请先运行 Host 控制台保存提供商，"
                    + "或直接编辑 " + Path.Combine(DataDirectory, "llm-providers.json") + "。");
            return Llm;
        }

        public PairIdentity RequirePair()
        {
            var pair = Store.LoadPairIdentity();
            if (!pair.IsComplete)
                throw new InvalidOperationException(
                    "还没有保存两人名字。先运行：migrate seed-identity --username 用户名 --assname 角色名 --callname 称呼");
            return pair;
        }

        public void Dispose()
        {
            if (Migration != null) Migration.Dispose();
            if (Store != null) Store.Dispose();
            if (Vectors != null) Vectors.Dispose();
            if (encoder != null) encoder.Dispose();
        }
    }

    public static class CliArgs
    {
        public static string Value(string[] args, string name, string fallback = null)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return fallback;
        }

        public static bool Flag(string[] args, string name)
        {
            return args.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>按数据内时间（+08:00）解释的日期范围，两端都含。</summary>
    public sealed class DateRange
    {
        public DateTime? From { get; private set; }
        public DateTime? To { get; private set; }

        public static DateRange Parse(string[] args)
        {
            var range = new DateRange();
            var from = CliArgs.Value(args, "--from");
            var to = CliArgs.Value(args, "--to");
            if (!string.IsNullOrWhiteSpace(from))
                range.From = ParseDay(from);
            if (!string.IsNullOrWhiteSpace(to))
                range.To = ParseDay(to);
            if (range.From.HasValue && range.To.HasValue && range.From.Value > range.To.Value)
                throw new InvalidOperationException("--from 不能晚于 --to。");
            return range;
        }

        public static DateTime ParseDay(string value)
        {
            DateTime parsed;
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                throw new InvalidOperationException("日期必须是 yyyy-MM-dd：" + value);
            return parsed;
        }

        public bool Contains(DateTime day)
        {
            if (From.HasValue && day.Date < From.Value.Date) return false;
            if (To.HasValue && day.Date > To.Value.Date) return false;
            return true;
        }

        public long DayStartMs(DateTime day)
        {
            return new DateTimeOffset(day.Date.AddHours(DayBoundaryHour), MigrationContext.ChinaOffset)
                .ToUnixTimeMilliseconds();
        }

        public long DayEndMs(DateTime day)
        {
            return new DateTimeOffset(day.Date.AddDays(1).AddHours(DayBoundaryHour), MigrationContext.ChinaOffset)
                .ToUnixTimeMilliseconds();
        }

        /// <summary>记忆日边界：每天 04:00（+08:00）起算第二天；04:00 前的消息归前一天。</summary>
        public const int DayBoundaryHour = 4;

        public static string DayKey(long unixMs)
        {
            var shifted = DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
                .ToOffset(MigrationContext.ChinaOffset)
                .AddHours(-DayBoundaryHour);
            return shifted.ToString("yyyy-MM-dd");
        }
    }
}
