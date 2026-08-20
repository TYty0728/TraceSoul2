using System;

namespace TraceSoul2.Util
{
    /// <summary>
    /// 无状态的通用计算工具。这里不读取图，也不决定召回流程。
    /// </summary>
    public static class ActivationScoreUtil
    {
        public static float ConceptSimilarity(float distanceDecay, int distance)
        {
            return (float)Math.Pow(distanceDecay, distance);
        }

        public static float ConvergenceMultiplier(int matchedDimensionCount, float bonusPerDimension)
        {
            return 1f + Math.Max(0, matchedDimensionCount - 1) * bonusPerDimension;
        }

        public static float Propagate(float sourceScore, float edgeWeight, float hopDecay)
        {
            return sourceScore * edgeWeight * hopDecay;
        }
    }
}
