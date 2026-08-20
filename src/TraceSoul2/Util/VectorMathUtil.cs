using System;
using System.Security.Cryptography;
using System.Text;

namespace TraceSoul2.Util
{
    public static class VectorMathUtil
    {
        public static void NormalizeInPlace(float[] values)
        {
            if (values == null || values.Length == 0) return;
            double sum = 0d;
            for (var i = 0; i < values.Length; i++) sum += values[i] * values[i];
            var length = Math.Sqrt(sum);
            if (length < 1e-12d) return;
            for (var i = 0; i < values.Length; i++) values[i] = (float)(values[i] / length);
        }

        public static float Cosine(float[] left, float[] right)
        {
            if (left == null || right == null || left.Length != right.Length || left.Length == 0) return 0f;
            double dot = 0d;
            double leftLength = 0d;
            double rightLength = 0d;
            for (var i = 0; i < left.Length; i++)
            {
                dot += left[i] * right[i];
                leftLength += left[i] * left[i];
                rightLength += right[i] * right[i];
            }

            var denominator = Math.Sqrt(leftLength * rightLength);
            return denominator < 1e-12d ? 0f : (float)(dot / denominator);
        }

        public static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var value in bytes) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }

        public static byte[] ToBytes(float[] values)
        {
            var bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static float[] FromBytes(byte[] bytes, int dimensions)
        {
            if (bytes == null || bytes.Length != dimensions * sizeof(float)) return null;
            var values = new float[dimensions];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }
    }
}
