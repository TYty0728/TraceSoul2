using System.Collections.Generic;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Prompts;

namespace TraceSoul2.Logic
{
    public static class VectorActivationContextLogic
    {
        public static string FormatForLlm(VectorRouteResult route)
        {
            if (route == null) return CorePrompts.VectorActivation.None;
            var builder = new StringBuilder();
            Append(builder, CorePrompts.VectorActivation.DomainTitle, route.Domains);
            Append(builder, CorePrompts.VectorActivation.DimensionTitle, route.Dimensions);
            Append(builder, CorePrompts.VectorActivation.ConceptTitle, route.Concepts);
            return builder.ToString().TrimEnd();
        }

        private static void Append(StringBuilder builder, string title, IReadOnlyList<VectorRouteHit> hits)
        {
            builder.Append(title).Append("：");
            if (hits == null || hits.Count == 0)
            {
                builder.AppendLine(title == CorePrompts.VectorActivation.ConceptTitle
                    ? CorePrompts.VectorActivation.WeakConcepts
                    : CorePrompts.VectorActivation.Silent);
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
