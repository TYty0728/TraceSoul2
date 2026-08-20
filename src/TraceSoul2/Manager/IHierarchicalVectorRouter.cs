using System.Collections.Generic;
using TraceSoul2.Data;

namespace TraceSoul2.Manager
{
    /// <summary>
    /// 分层多标签导航的插件可见面：域 → 维度 → 概念的激活种子路由。
    /// 宿主注入具体实现（HierarchicalVectorRouterLogic），外部插件只依赖此契约。
    /// </summary>
    public interface IHierarchicalVectorRouter
    {
        VectorRouteResult Route(string query, VectorRouteSettings settings = null);

        /// <summary>
        /// 把第三层人生 Tag 按与这一句的向量相近程度排好。不做域/维度门禁，也不截断。
        /// 给心智看候选时用；真正收窄检索仍走 <see cref="Route"/>。
        /// </summary>
        IReadOnlyList<VectorRouteHit> RankConcepts(string query, VectorRouteSettings settings = null);
    }
}
