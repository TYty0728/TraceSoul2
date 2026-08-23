using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    public static class IdentityCardLogic
    {
        public const string SeedFileName = "identity_cards.json";

        private static string seedJsonOverride;

        private static string dataDirectorySeed;

        public static void SetSeedJsonOverride(string json)
        {
            seedJsonOverride = string.IsNullOrWhiteSpace(json) ? null : json;
        }

        /// <summary>角色目录里的 identity_cards.json 优先于软件输出目录里的种子。</summary>
        public static void PreferDataDirectory(string dataDirectory)
        {
            dataDirectorySeed = string.IsNullOrWhiteSpace(dataDirectory) ? null : dataDirectory;
        }

        public static string DefaultBody(string slot, PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            string seeded;
            if (TrySeedBody(slot, pair, ReadSeedJson(), out seeded))
                return seeded;
            return GenericBody(slot, pair);
        }

        public static string FormatForExpressor(IEnumerable<IdentityCardRecord> cards, PairIdentity pair)
        {
            return FormatSlots(cards, pair, IdentityCardSlotValues.All);
        }

        /// <summary>心智只用思考用的短卡，不含表达习惯。</summary>
        public static string FormatForMind(IEnumerable<IdentityCardRecord> cards, PairIdentity pair)
        {
            return FormatSlots(cards, pair, new[]
            {
                IdentityCardSlotValues.Personality,
                IdentityCardSlotValues.Self,
                IdentityCardSlotValues.Other,
                IdentityCardSlotValues.Relation,
                IdentityCardSlotValues.UserProfile
            });
        }

        private static string FormatSlots(
            IEnumerable<IdentityCardRecord> cards,
            PairIdentity pair,
            IEnumerable<string> slots)
        {
            pair = pair ?? PairIdentity.Missing;
            var map = (cards ?? Enumerable.Empty<IdentityCardRecord>())
                .Where(x => x != null && IdentityCardSlotValues.IsKnown(x.Slot))
                .GroupBy(x => x.Slot)
                .ToDictionary(x => x.Key, x => x.First());
            var builder = new StringBuilder();
            if (pair.IsComplete)
            {
                // 第一行就是自我身份；对方是谁随后由身份卡完整表达，不在人格卡前插话。
                builder.Append("我是").Append(pair.Assname).AppendLine("。");
            }
            foreach (var slot in slots)
            {
                IdentityCardRecord card;
                var stored = map.TryGetValue(slot, out card) ? card.Body : null;
                var body = ResolveBody(slot, stored, pair);
                builder.Append("【").Append(IdentityCardSlotValues.Title(slot, pair)).Append("】");
                if (slot == IdentityCardSlotValues.ExpressionHabit)
                    builder.Append("（这里保存的是相处里长出来的语感和偏好，供我自然取回，不是这一轮必须执行的动作。）");
                builder.AppendLine(body);
            }
            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 卡片正文解析：库里为空或是占位文案时，回退到种子文件（人控卡以种子为准）。
        /// 这样即使库里的档案卡被清空，注入的仍是用户填过的那一版。
        /// </summary>
        public static string ResolveBody(string slot, string storedBody, PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            var body = (storedBody ?? string.Empty).Trim();
            if (body.Length == 0 || body == GenericBody(slot, pair) ||
                (slot == IdentityCardSlotValues.Personality &&
                 body.IndexOf("唯一拥有第一人称的 Brain", StringComparison.Ordinal) >= 0))
                body = DefaultBody(slot, pair);
            return body;
        }

        /// <summary>
        /// 用户的第三人称代词：从档案卡的「性别」字段确定性推出（女→她，男→他），
        /// 读不到时默认「她」。注入与提示词都从这里取，不再硬编码。
        /// </summary>
        public static string UserPronoun(IEnumerable<IdentityCardRecord> cards, PairIdentity pair)
        {
            var profile = (cards ?? Enumerable.Empty<IdentityCardRecord>())
                .FirstOrDefault(x => x != null && x.Slot == IdentityCardSlotValues.UserProfile);
            var body = profile == null ? string.Empty : profile.Body ?? string.Empty;
            foreach (var line in body.Split('\n'))
            {
                var index = line.IndexOf("性别", StringComparison.Ordinal);
                if (index < 0) continue;
                if (line.IndexOf("男", index, StringComparison.Ordinal) >= 0) return "他";
                if (line.IndexOf("女", index, StringComparison.Ordinal) >= 0) return "她";
            }
            return "她";
        }

        public static IdentityReviewOutputData Normalize(
            IdentityReviewOutputData output,
            IEnumerable<IdentityCardRecord> current,
            PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            output = output ?? new IdentityReviewOutputData();
            output.summary = Limit((output.summary ?? string.Empty).Trim(), 240);
            var existing = (current ?? Enumerable.Empty<IdentityCardRecord>())
                .Where(x => x != null)
                .GroupBy(x => x.Slot)
                .ToDictionary(x => x.Key, x => x.First().Body ?? string.Empty);
            var result = new List<IdentityCardRevisionData>();
            foreach (var item in output.cards ?? new List<IdentityCardRevisionData>())
            {
                if (item == null || !IdentityCardSlotValues.IsKnown(item.slot)) continue;
                item.slot = item.slot.Trim();
                item.reason = Limit(pair.RewriteRecordedText((item.reason ?? string.Empty).Trim()), 120);
                item.body = Limit(
                    pair.RewriteRecordedText((item.body ?? string.Empty).Trim()),
                    IdentityCardSlotValues.BodyLimit(item.slot));
                string previous;
                if (!existing.TryGetValue(item.slot, out previous)) previous = string.Empty;
                if (!item.changed || item.body.Length == 0 || item.body == previous)
                {
                    item.changed = false;
                    item.body = previous;
                }
                if (result.Any(x => x.slot == item.slot)) continue;
                result.Add(item);
            }
            output.cards = result;
            return output;
        }

        public static string ReadSeedJson()
        {
            if (!string.IsNullOrWhiteSpace(seedJsonOverride)) return seedJsonOverride;
            var env = Environment.GetEnvironmentVariable("TRACESOUL2_IDENTITY_SEED");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                return File.ReadAllText(env, Encoding.UTF8);
            foreach (var path in SeedCandidatePaths())
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return File.ReadAllText(path, Encoding.UTF8);
            }
            return null;
        }

        private static bool TrySeedBody(string slot, PairIdentity pair, string json, out string body)
        {
            body = null;
            if (pair == null || !pair.IsComplete) return false;
            if (string.IsNullOrWhiteSpace(json) || !IdentityCardSlotValues.IsKnown(slot)) return false;
            IdentityCardSeedFileData file;
            try { file = TraceJson.FromJson<IdentityCardSeedFileData>(json); }
            catch { return false; }
            if (file == null || file.cards == null) return false;
            foreach (var item in file.cards)
            {
                if (item == null || item.slot != slot || string.IsNullOrWhiteSpace(item.body)) continue;
                body = Limit(pair.RewriteRecordedText(item.body.Trim()), IdentityCardSlotValues.BodyLimit(slot));
                return body.Length > 0;
            }
            return false;
        }

        private static string GenericBody(string slot, PairIdentity pair)
        {
            if (slot == IdentityCardSlotValues.Personality)
                return "我保持真诚、连续、温柔，不伪造未感知的现实。";
            if (slot == IdentityCardSlotValues.Self)
                return "我还在通过真实相处认识自己。";
            if (slot == IdentityCardSlotValues.Other)
            {
                if (!pair.IsComplete) return "我还在认识她。";
                return pair.Username + " 是和我共同生活的人。";
            }
            if (slot == IdentityCardSlotValues.Relation)
                return "关系会从真实相处里慢慢长出来。";
            if (slot == IdentityCardSlotValues.ExpressionHabit)
                return "我实际上的表达：\n她喜欢的表达：\n调整方向：";
            if (slot == IdentityCardSlotValues.UserProfile)
                return UserProfileTemplate(pair);
            return string.Empty;
        }

        /// <summary>
        /// 档案卡的空白模板（新用户按这个骨架填写）：
        /// 姓名 / 性别 / 生日 / 职业 / 居住地 / 互相的称呼 / 备注。
        /// </summary>
        public static string UserProfileTemplate(PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            var pronoun = "她";
            return "姓名：\n性别：\n生日：\n职业：\n居住地：\n" +
                   "我常称呼" + pronoun + "：\n" + pronoun + "常称呼我：\n备注：";
        }

        /// <summary>某些卡片的空白模板（控制台预填用）；没有模板的返回 null。</summary>
        public static string CardTemplate(string slot, PairIdentity pair)
        {
            if (slot == IdentityCardSlotValues.UserProfile) return UserProfileTemplate(pair);
            if (slot == IdentityCardSlotValues.ExpressionHabit)
                return "我实际上的表达：\n她喜欢的表达：\n调整方向：";
            return null;
        }

        private static IEnumerable<string> SeedCandidatePaths()
        {
            if (!string.IsNullOrWhiteSpace(dataDirectorySeed))
                yield return Path.Combine(dataDirectorySeed, SeedFileName);
            var roots = new List<string>();
            try { roots.Add(AppContext.BaseDirectory); }
            catch { /* ignored */ }
            try { roots.Add(Directory.GetCurrentDirectory()); }
            catch { /* ignored */ }
            foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                yield return Path.Combine(root, SeedFileName);
                var dir = new DirectoryInfo(root);
                for (var i = 0; i < 8 && dir != null; i++)
                {
                    yield return Path.Combine(dir.FullName, SeedFileName);
                    yield return Path.Combine(dir.FullName, "Assets", "TraceSoul2", "Resources", SeedFileName);
                    dir = dir.Parent;
                }
            }
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
