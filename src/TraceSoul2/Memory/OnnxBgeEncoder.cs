using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Tools.Memory
{
    /// <summary>
    /// 在 Host / 迁移工具里用 ONNX Runtime（CPU）执行 bge-small-zh-v1.5。
    /// 与 Unity 侧 BgeSentisVectorEncoder 使用同一模型与同一 tokenizer，
    /// 输出同为 last_hidden_state 的 [CLS] 经 L2 归一化后的 512 维向量。
    /// </summary>
    public sealed class OnnxBgeEncoder : IVectorEncoder, IDisposable
    {
        public const string DefaultModelId = "BAAI/bge-small-zh-v1.5@xenova-onnx-opset11-fp32";
        public const string QueryPrefix = "为这个句子生成表示以用于检索相关文章：";

        private readonly InferenceSession session;
        private readonly BertWordPieceTokenizer tokenizer;
        private readonly string[] inputNames;
        private readonly int maxTokens;
        private bool disposed;

        public string ModelId { get { return DefaultModelId; } }
        public int Dimensions { get { return 512; } }

        public OnnxBgeEncoder(string modelPath, string vocabularyPath, int maxTokens = 128)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                throw new FileNotFoundException("BGE ONNX 模型不存在：" + modelPath);
            if (string.IsNullOrWhiteSpace(vocabularyPath) || !File.Exists(vocabularyPath))
                throw new FileNotFoundException("BGE vocab 不存在：" + vocabularyPath);

            this.maxTokens = Math.Max(8, Math.Min(512, maxTokens));
            tokenizer = new BertWordPieceTokenizer(File.ReadAllText(vocabularyPath));
            session = new InferenceSession(modelPath);
            inputNames = session.InputMetadata.Keys.ToArray();
        }

        public float[] Encode(string text, VectorTextPurpose purpose)
        {
            if (disposed) throw new ObjectDisposedException("OnnxBgeEncoder");
            var source = purpose == VectorTextPurpose.Query
                ? QueryPrefix + (text ?? string.Empty)
                : text ?? string.Empty;
            var encoded = tokenizer.Encode(source, maxTokens);

            var inputIds = ToTensor(encoded.InputIds);
            var attentionMask = ToTensor(encoded.AttentionMask);
            var tokenTypeIds = ToTensor(encoded.TokenTypeIds);
            var inputs = new List<NamedOnnxValue>(3);
            AddIfPresent(inputs, "input_ids", inputIds);
            AddIfPresent(inputs, "attention_mask", attentionMask);
            AddIfPresent(inputs, "token_type_ids", tokenTypeIds);
            if (inputs.Count == 0)
                throw new InvalidOperationException("BGE 模型的输入张量都不存在。");

            using (var results = session.Run(inputs))
            {
                if (results == null || results.Count == 0)
                    throw new InvalidOperationException("BGE 模型没有输出。");
                var output = results[0].AsEnumerable<float>().ToArray();
                if (output.Length < Dimensions)
                    throw new InvalidOperationException("BGE 输出短于 512 维。");
                var cls = new float[Dimensions];
                Array.Copy(output, 0, cls, 0, Dimensions);
                VectorMathUtil.NormalizeInPlace(cls);
                return cls;
            }
        }

        private static DenseTensor<long> ToTensor(int[] values)
        {
            var tensor = new DenseTensor<long>(new[] { 1, values.Length });
            for (var i = 0; i < values.Length; i++) tensor[0, i] = values[i];
            return tensor;
        }

        private void AddIfPresent(List<NamedOnnxValue> inputs, string name, Tensor<long> tensor)
        {
            if (inputNames.Contains(name)) inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            session.Dispose();
        }
    }
}
