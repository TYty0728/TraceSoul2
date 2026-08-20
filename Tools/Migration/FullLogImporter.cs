using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Util;

namespace TraceSoul2.Migrate
{
    /// <summary>把老系统 full_log.txt 解析为新框架 moments（可增量续传、按天幂等）。</summary>
    public static class FullLogImporter
    {
        private static readonly Regex MessageStart = new Regex(
            @"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] (?:\[([^\]]+)\] )?(.*)$",
            RegexOptions.Compiled);

        private sealed class RawMessage
        {
            public DateTime Timestamp;
            public string Role;
            public string Tag;
            public string Content;
            public string File;
            public int StartLine;
            public int EndLine;
        }

        public static Task<int> RunAsync(MigrationContext context, string[] args)
        {
            var logPath = CliArgs.Value(args, "--log");
            if (string.IsNullOrWhiteSpace(logPath))
                throw new InvalidOperationException(
                    "需要 --log <full_log.txt 路径>。");
            logPath = Path.GetFullPath(logPath);
            if (!File.Exists(logPath))
                throw new InvalidOperationException("日志文件不存在：" + logPath);

            var range = DateRange.Parse(args);
            var force = CliArgs.Flag(args, "--force");
            var missingOnly = CliArgs.Flag(args, "--missing");
            if (force && missingOnly)
                throw new InvalidOperationException("--force 与 --missing 不能同时使用。");
            var lines = File.ReadAllLines(logPath);
            var messages = ParseFile(lines, logPath);
            var skipped = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var group in messages.GroupBy(x => ShiftToMemoryDay(x.Timestamp)).OrderBy(x => x.Key))
            {
                var day = group.Key;
                if (!range.Contains(day))
                {
                    skipped.Add(day.ToString("yyyy-MM-dd"));
                    continue;
                }
                var startMs = range.DayStartMs(day);
                var endMs = range.DayEndMs(day);
                var existing = context.Migration.CountImportedMomentsInRange(startMs, endMs);
                if (missingOnly)
                {
                    var existingFingerprints = context.Migration
                        .GetImportedMomentsInRange(startMs, endMs)
                        .GroupBy(x => x.SourceEventId ?? string.Empty)
                        .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
                    var imported = 0;
                    foreach (var message in group)
                    {
                        var record = ToMomentRecord(message);
                        var fingerprint = record.SourceEventId ?? string.Empty;
                        int remaining;
                        if (existingFingerprints.TryGetValue(fingerprint, out remaining) && remaining > 0)
                        {
                            existingFingerprints[fingerprint] = remaining - 1;
                            continue;
                        }
                        context.Store.SaveMoment(record);
                        imported += 1;
                    }
                    counts[day.ToString("yyyy-MM-dd")] = imported;
                    Console.WriteLine("补入 " + day.ToString("yyyy-MM-dd") + "：" + imported
                        + " 条；已存在 " + (group.Count() - imported) + " 条");
                    continue;
                }
                if (existing > 0 && !force)
                {
                    skipped.Add(day.ToString("yyyy-MM-dd") + "(已导入)");
                    continue;
                }
                if (existing > 0 && force)
                    context.Migration.DeleteImportedMomentsInRange(startMs, endMs);

                foreach (var message in group)
                    context.Store.SaveMoment(ToMomentRecord(message));
                counts[day.ToString("yyyy-MM-dd")] = group.Count();
                Console.WriteLine("导入 " + day.ToString("yyyy-MM-dd") + "：" + group.Count() + " 条");
            }

            var cursor = new ImportCursorRecord
            {
                SourceFile = logPath,
                LastLine = lines.Length,
                LastTimestamp = messages.Count == 0 ? string.Empty : messages[messages.Count - 1].Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                FileSize = new FileInfo(logPath).Length,
                LastLineHash = lines.Length == 0 ? string.Empty : VectorMathUtil.Sha256(lines[lines.Length - 1]),
                Status = "ok"
            };
            context.Migration.SaveCursor(cursor);

            Console.WriteLine();
            Console.WriteLine("总消息数：" + messages.Count
                + "；导入天数：" + counts.Count
                + "；跳过的天：" + (skipped.Count == 0 ? "无" : string.Join("、", skipped)));
            Console.WriteLine("注意：导入的 Moment Realm 暂为 unclassified，运行 migrate classify 后才会分层。");
            return Task.FromResult(0);
        }

        private static List<RawMessage> ParseFile(string[] lines, string path)
        {
            var messages = new List<RawMessage>();
            RawMessage current = null;
            var contentBuilder = new StringBuilder();

            void Flush()
            {
                if (current == null) return;
                current.Content = contentBuilder.ToString().Trim();
                if (current.Content.Length > 0) messages.Add(current);
                current = null;
                contentBuilder.Clear();
            }

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index] ?? string.Empty;
                var match = MessageStart.Match(line);
                if (!match.Success)
                {
                    if (current != null)
                    {
                        contentBuilder.Append('\n').Append(line);
                        current.EndLine = index + 1;
                    }
                    continue;
                }

                Flush();
                DateTime timestamp;
                if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
                    continue;

                var tag = match.Groups[2].Success ? match.Groups[2].Value.Trim() : string.Empty;
                var rest = match.Groups[3].Value ?? string.Empty;
                string role;
                if (match.Groups[2].Success)
                {
                    role = tag.StartsWith("Assistant", StringComparison.OrdinalIgnoreCase) ? "ass" : "user";
                    rest = StripSpeaker(rest);
                }
                else if (rest.StartsWith("Assistant:", StringComparison.OrdinalIgnoreCase))
                {
                    role = "ass";
                    rest = rest.Substring("Assistant:".Length).TrimStart();
                }
                else
                {
                    role = "user";
                    rest = StripSpeaker(rest);
                }

                current = new RawMessage
                {
                    Timestamp = timestamp,
                    Role = role,
                    Tag = tag,
                    File = Path.GetFileName(path),
                    StartLine = index + 1,
                    EndLine = index + 1
                };
                contentBuilder.Append(rest);
            }
            Flush();
            return messages;
        }

        private static DateTime ShiftToMemoryDay(DateTime timestamp)
        {
            return timestamp.AddHours(-DateRange.DayBoundaryHour).Date;
        }

        private static string StripSpeaker(string content)
        {
            content = content ?? string.Empty;
            if (content.StartsWith("User[", StringComparison.OrdinalIgnoreCase))
            {
                var close = content.IndexOf("]:", StringComparison.Ordinal);
                if (close > 0) return content.Substring(close + 2).TrimStart();
            }
            if (content.StartsWith("User:", StringComparison.OrdinalIgnoreCase))
                return content.Substring("User:".Length).TrimStart();
            return content;
        }

        private static MomentRecord ToMomentRecord(RawMessage message)
        {
            var occurred = new DateTimeOffset(message.Timestamp, MigrationContext.ChinaOffset);
            var payload = JsonSerializer.Serialize(new
            {
                file = message.File,
                lines = message.StartLine + "-" + message.EndLine,
                modality = "text",
                tag = message.Tag
            });
            return new MomentRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = MigrationContext.ConversationId,
                Role = message.Role,
                Content = message.Content,
                Realm = TraceRealmValues.Unclassified,
                EvidenceType = message.Role == "ass"
                    ? EvidenceTypeValues.AssPerformed
                    : EvidenceTypeValues.UserReported,
                SourcePluginId = MigrationContext.ImportPluginId,
                SourceEventId = message.File + ":" + message.StartLine + ":" + message.EndLine,
                PayloadJson = payload,
                MemoryStatus = "live",
                CreatedUnixMs = occurred.ToUnixTimeMilliseconds()
            };
        }

    }
}
