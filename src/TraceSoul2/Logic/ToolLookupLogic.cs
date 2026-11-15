using System;
using TraceSoul2.Data;

namespace TraceSoul2.Logic
{
    /// <summary>Agent 能力目录的资格过滤；不再提供旧心智的向量预选和 tool_call 提示。</summary>
    public static class ToolLookupLogic
    {
        public static bool IsLookupEligible(TraceContributionDescriptorData item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id)) return false;
            if (!TraceContributionKindValues.IsBrainCallable(item.Kind)) return false;
            var id = item.Id.Trim();
            if (id.StartsWith("memory.", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("identity.", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("time.", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("dialogue.", StringComparison.OrdinalIgnoreCase))
                return false;
            var organ = MouthLogic.OrganOf(item);
            if (string.Equals(organ, BodyOrganValues.Text, StringComparison.Ordinal) ||
                string.Equals(organ, BodyOrganValues.Sticker, StringComparison.Ordinal) ||
                string.Equals(organ, BodyOrganValues.Image, StringComparison.Ordinal))
                return false;
            return true;
        }

    }
}
