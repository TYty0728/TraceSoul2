using System.Collections.Generic;
using System.Text;
using TraceSoul2.Data;

namespace TraceSoul2.Logic
{
    public static class VectorActivationContextLogic
    {
        public static string FormatForLlm(VectorRouteResult route)
        {
            if (route == null) return "（没有向量激活）";
            var builder = new StringBuilder();
            Append(builder, "域", route.Domains);
            Append(builder, "维度", route.Dimensions);
            Append(builder, "概念入口", route.Concepts);
            return builder.ToString().TrimEnd();
        }

        private static void Append(StringBuilder builder, string title, IReadOnlyList<VectorRouteHit> hits)
        {
            builder.Append(title).Append("：");
            if (hits == null || hits.Count == 0)
            {
                builder.AppendLine(title == "概念入口"
                    ? "（没有达到可靠阈值的已有概念；不要从弱候选推断记忆）"
                    : "（未点亮，保持沉寂）");
                return;
            }

            for (var i = 0; i < hits.Count; i++)
            {
                if (i > 0) builder.Append("；");
                builder.Append(hits[i].Node.Label)
                    .Append("(")
                    .Append(hits[i].Score.ToString("0.000"))
                    .Append(")");
            }
            builder.AppendLine();
        }
    }
}
