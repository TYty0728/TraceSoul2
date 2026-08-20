using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Manager;

namespace TraceSoul2.Tools.Memory
{
    /// <summary>
    /// 把第四层条目的一句话总结编码成 BGE 语义向量，存入 vectors 库的
    /// event_entry_vectors 表。幂等：内容哈希 + 模型一致则跳过。
    /// </summary>
    public static class EntryEmbedder
    {
        public static int EmbedAll(
            IEnumerable<EventEntryRecord> entries,
            SqliteVectorManager vectors,
            OnnxBgeEncoder encoder)
        {
            var done = 0;
            foreach (var entry in entries ?? new List<EventEntryRecord>())
            {
                if (entry == null) continue;
                var text = (entry.Summary ?? string.Empty).Trim();
                if (text.Length == 0) continue;
                var hash = Hash(text + "|" + encoder.ModelId);
                if (vectors.HasEntryEmbedding(entry.Id, encoder.ModelId, hash)) continue;
                var vector = encoder.Encode(text, VectorTextPurpose.Index);
                vectors.PutEntryEmbedding(entry.Id, encoder.ModelId, hash, vector);
                done++;
            }
            return done;
        }

        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
