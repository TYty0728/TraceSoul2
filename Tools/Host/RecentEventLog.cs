using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace TraceSoul2.Host
{
    // 在没有网页订阅时也保留历史；回放与订阅在同一锁内完成。
    internal sealed class RecentEventLog : IDisposable
    {
        public const int Capacity = 300;
        private readonly object gate = new object();
        private readonly Queue<string> history = new Queue<string>();
        private readonly List<Channel<string>> subscribers = new List<Channel<string>>();
        private bool disposed;

        public Subscription Subscribe()
        {
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(RecentEventLog));
                foreach (var line in history) channel.Writer.TryWrite(line);
                subscribers.Add(channel);
            }
            return new Subscription(this, channel);
        }

        public void Emit(string message)
        {
            var line = DateTimeOffset.Now.ToString("HH:mm:ss.fff") + " " + Sanitize(message);
            lock (gate)
            {
                if (disposed) return;
                history.Enqueue(line);
                while (history.Count > Capacity) history.Dequeue();
                foreach (var channel in subscribers) channel.Writer.TryWrite(line);
            }
        }

        internal static string Sanitize(string message)
        {
            var value = message ?? string.Empty;
            value = Regex.Replace(value, @"(?i)(?:data:[^,\s""<>]+;base64,|base64://)[a-z0-9+/=\\\s]*", "[图片数据已省略]");
            // 兼容 JSON 转义的 Base64；不让无前缀的大段编码占满日志。
            value = Regex.Replace(value, @"(?:[A-Za-z0-9+/=]|\\u00(?:2[BbFf]|3[Dd])|\\[rn/]){100,}", "[编码数据已省略]");
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= 2000 ? value : value.Substring(0, 2000) + "…[已截断]";
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                foreach (var channel in subscribers) channel.Writer.TryComplete();
                subscribers.Clear();
                history.Clear();
            }
        }

        public sealed class Subscription : IDisposable
        {
            private readonly RecentEventLog owner;
            private readonly Channel<string> channel;
            internal Subscription(RecentEventLog owner, Channel<string> channel)
            {
                this.owner = owner;
                this.channel = channel;
            }
            public ChannelReader<string> Reader => channel.Reader;
            public void Dispose()
            {
                lock (owner.gate)
                {
                    owner.subscribers.Remove(channel);
                    channel.Writer.TryComplete();
                }
            }
        }
    }
}
