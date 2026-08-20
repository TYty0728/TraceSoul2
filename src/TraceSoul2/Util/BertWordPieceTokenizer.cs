using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TraceSoul2.Util
{
    public sealed class BertInputData
    {
        public int[] InputIds { get; private set; }
        public int[] AttentionMask { get; private set; }
        public int[] TokenTypeIds { get; private set; }

        public BertInputData(int[] inputIds, int[] attentionMask, int[] tokenTypeIds)
        {
            InputIds = inputIds;
            AttentionMask = attentionMask;
            TokenTypeIds = tokenTypeIds;
        }
    }

    /// <summary>
    /// BGE-small-zh 使用的 BERT WordPiece tokenizer。
    /// vocab.txt 每一行的位置就是 token id；中文字符先独立切分，再做最长匹配。
    /// </summary>
    public sealed class BertWordPieceTokenizer
    {
        private readonly Dictionary<string, int> vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly int clsId;
        private readonly int sepId;
        private readonly int unknownId;

        public BertWordPieceTokenizer(string vocabularyText)
        {
            if (string.IsNullOrWhiteSpace(vocabularyText))
                throw new ArgumentException("BERT vocabulary is empty.", "vocabularyText");

            using (var reader = new StringReader(vocabularyText))
            {
                string line;
                var index = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    var token = line.TrimEnd('\r');
                    if (!vocabulary.ContainsKey(token)) vocabulary.Add(token, index);
                    index++;
                }
            }

            clsId = RequiredId("[CLS]");
            sepId = RequiredId("[SEP]");
            unknownId = RequiredId("[UNK]");
        }

        public BertInputData Encode(string text, int maxTokens)
        {
            if (maxTokens < 2) throw new ArgumentOutOfRangeException("maxTokens");
            var ids = new List<int>(Math.Min(maxTokens, 128)) { clsId };
            foreach (var basicToken in BasicTokenize(text ?? string.Empty))
            {
                foreach (var tokenId in WordPiece(basicToken))
                {
                    if (ids.Count >= maxTokens - 1) break;
                    ids.Add(tokenId);
                }
                if (ids.Count >= maxTokens - 1) break;
            }
            ids.Add(sepId);

            var inputIds = ids.ToArray();
            var attention = new int[inputIds.Length];
            var types = new int[inputIds.Length];
            for (var i = 0; i < attention.Length; i++) attention[i] = 1;
            return new BertInputData(inputIds, attention, types);
        }

        private IEnumerable<int> WordPiece(string token)
        {
            int direct;
            if (vocabulary.TryGetValue(token, out direct))
            {
                yield return direct;
                yield break;
            }

            if (token.Length > 100)
            {
                yield return unknownId;
                yield break;
            }

            var start = 0;
            var pieces = new List<int>();
            while (start < token.Length)
            {
                var end = token.Length;
                var found = unknownId;
                var matched = false;
                while (start < end)
                {
                    var piece = token.Substring(start, end - start);
                    if (start > 0) piece = "##" + piece;
                    if (vocabulary.TryGetValue(piece, out found))
                    {
                        matched = true;
                        break;
                    }
                    end--;
                }

                // TryGetValue 失败时会把 out int 写成 0，不能用 found < 0 判断是否命中。
                // 显式 matched 也保证 start 只在真正找到子词时才向前移动。
                if (!matched)
                {
                    yield return unknownId;
                    yield break;
                }
                pieces.Add(found);
                start = end;
            }

            foreach (var piece in pieces) yield return piece;
        }

        private static IEnumerable<string> BasicTokenize(string text)
        {
            var buffer = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];
                if (char.IsWhiteSpace(character) || char.IsControl(character))
                {
                    if (buffer.Length > 0) { yield return buffer.ToString(); buffer.Length = 0; }
                    continue;
                }

                if (IsChinese(character) || char.IsPunctuation(character) || char.IsSymbol(character))
                {
                    if (buffer.Length > 0) { yield return buffer.ToString(); buffer.Length = 0; }
                    yield return character.ToString();
                    continue;
                }

                buffer.Append(character);
            }
            if (buffer.Length > 0) yield return buffer.ToString();
        }

        private int RequiredId(string token)
        {
            int id;
            if (!vocabulary.TryGetValue(token, out id))
                throw new InvalidOperationException("Required token is missing from vocabulary: " + token);
            return id;
        }

        private static bool IsChinese(char value)
        {
            return (value >= 0x4E00 && value <= 0x9FFF) ||
                   (value >= 0x3400 && value <= 0x4DBF) ||
                   (value >= 0xF900 && value <= 0xFAFF);
        }
    }
}
