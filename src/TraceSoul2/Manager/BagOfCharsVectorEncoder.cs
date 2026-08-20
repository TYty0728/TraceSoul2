using TraceSoul2.Data;
using TraceSoul2.Util;

namespace TraceSoul2.Manager
{
    /// <summary>
    /// Host 接通结构用的占位编码器。真正的 BGE / ONNX 以后替换，不改 Router 接口。
    /// </summary>
    public sealed class BagOfCharsVectorEncoder : IVectorEncoder
    {
        public string ModelId { get { return "bag-of-chars"; } }
        public int Dimensions { get { return 32; } }

        public float[] Encode(string text, VectorTextPurpose purpose)
        {
            var result = new float[Dimensions];
            var value = text ?? string.Empty;
            for (var i = 0; i < value.Length; i++)
                result[(value[i] + i) % Dimensions] += 1f;
            VectorMathUtil.NormalizeInPlace(result);
            return result;
        }
    }
}
