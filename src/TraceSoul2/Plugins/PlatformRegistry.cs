using System;
using System.Collections.Generic;
using System.Linq;

namespace TraceSoul2.Plugins
{
    /// <summary>一个已注册平台的句柄：id、展示名与实时连接状态。</summary>
    public sealed class PlatformHandle
    {
        public string Id;
        public string DisplayName;
        public Func<bool> IsConnected;

        /// <summary>可选：平台详细运行态（控制台展示；返回可 JSON 序列化的对象）。</summary>
        public Func<object> Details;
    }

    /// <summary>
    /// 平台注册表：平台插件（连接桥）启动时注册自己；
    /// 感官目录与控制台从这里读取「当前连着哪些平台、各自通不通」。
    /// </summary>
    public sealed class PlatformRegistry
    {
        private readonly object gate = new object();
        private readonly List<PlatformHandle> handles = new List<PlatformHandle>();

        public void Register(PlatformHandle handle)
        {
            if (handle == null || string.IsNullOrWhiteSpace(handle.Id)) return;
            lock (gate)
            {
                if (handles.Any(x => x.Id == handle.Id)) return;
                handles.Add(handle);
            }
        }

        public void Unregister(string platformId)
        {
            lock (gate)
            {
                handles.RemoveAll(x => x.Id == platformId);
            }
        }

        public List<PlatformHandle> List()
        {
            lock (gate) return handles.ToList();
        }
    }
}
