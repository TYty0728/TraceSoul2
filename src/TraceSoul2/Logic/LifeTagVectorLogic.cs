using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Manager;

namespace TraceSoul2.Logic
{
    /// <summary>把数据库中逐渐生长的第三层人生 Tag 转成向量导航节点。</summary>
    public static class LifeTagVectorLogic
    {
        public static List<VectorIndexNode> BuildOntology(
            IMemoryStore store,
            IEnumerable<VectorIndexNode> fixedOntology)
        {
            if (store == null) throw new ArgumentNullException("store");
            var fixedNodes = (fixedOntology ?? Enumerable.Empty<VectorIndexNode>()).ToList();
            store.SeedLifeTags(fixedNodes);
            var result = fixedNodes.Where(x => x.Level != VectorNodeLevel.Concept).ToList();

            foreach (var tag in store.GetActiveLifeTags())
            {
                var routes = store.GetLifeTagRoutes(tag.Id);
                var examples = store.GetLifeTagExamples(tag.Id);
                var domains = routes.Where(x => x.RouteLevel == "domain")
                    .Select(x => RemovePrefix(x.RouteNodeId, "domain."))
                    .Distinct().ToArray();
                var dimensionRoutes = routes.Where(x => x.RouteLevel == "dimension")
                    .Select(x => x.RouteNodeId).Distinct().ToArray();
                var primaryDimension = dimensionRoutes
                    .Select(x => RemovePrefix(x, "dimension."))
                    .FirstOrDefault() ?? string.Empty;
                result.Add(new VectorIndexNode(
                    tag.Id,
                    VectorNodeLevel.Concept,
                    tag.Label,
                    tag.Definition,
                    primaryDimension,
                    domains,
                    dimensionRoutes,
                    examples.Where(x => x.Role == "positive")
                        .OrderBy(x => x.ExampleIndex).Select(x => x.Text),
                    examples.Where(x => x.Role == "negative")
                        .OrderBy(x => x.ExampleIndex).Select(x => x.Text),
                    tag.ActivationCount));
            }
            return result;
        }

        private static string RemovePrefix(string value, string prefix)
        {
            value = value ?? string.Empty;
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? value.Substring(prefix.Length)
                : value;
        }
    }
}
