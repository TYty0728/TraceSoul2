using TraceSoul2.Data;

namespace TraceSoul2.Logic
{
    /// <summary>生活状态是「正在做」的唯一注入口；内心切片不再平行复述同一句。</summary>
    public static class LifeStateLogic
    {
        public static string FormatDoing(LifeStateData life)
        {
            return life == null ? string.Empty : life.FormatDoing();
        }
    }
}
