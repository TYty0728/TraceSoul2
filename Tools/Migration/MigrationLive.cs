using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using TraceSoul2.Data;

namespace TraceSoul2.Migrate
{
    /// <summary>
    /// 迁移工具内置的实时监视控制台（HTTP + SSE）：
    /// - GET  /            实时网页：五张卡 + 内心 + 每次 LLM 调用流（SSE 自动刷新）
    /// - GET  /api/state   当前状态 JSON（供 Unity 等客户端做可视化）
    /// - PUT  /api/cards/{slot} 修改卡（人控卡：personality / user_profile）
    /// - GET  /events      SSE 调用事件流
    /// 端口 5090。build 时自动启动；也可单独跑 migrate live。
    /// </summary>
    public sealed class MigrationLive
    {
        public const int DefaultPort = 5090;
        public static MigrationLive Instance { get; private set; }

        private readonly MigrationContext context;
        private readonly HttpListener listener;
        private readonly object gate = new object();
        private readonly List<HttpListenerContext> sseClients = new List<HttpListenerContext>();

        private MigrationLive(MigrationContext context)
        {
            this.context = context;
            listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + DefaultPort + "/");
        }

        public static MigrationLive Start(MigrationContext context)
        {
            if (Instance != null) return Instance;
            Instance = new MigrationLive(context);
            Instance.listener.Start();
            var thread = new Thread(Instance.RunLoop) { IsBackground = true, Name = "MigrationLive" };
            thread.Start();
            Console.WriteLine("实时监视：http://127.0.0.1:" + DefaultPort + "/ （每次 LLM 调用实时可见）");
            return Instance;
        }

        /// <summary>广播一条事件（构建管线的每个 LLM 调用都调用它）。</summary>
        public void Notify(string kind, string digest, string detail)
        {
            var json = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.Now.ToString("HH:mm:ss"),
                kind,
                digest,
                detail = HumanJson(detail)
            });
            var payload = "data: " + json + "\n\n";
            var dead = new List<HttpListenerContext>();
            lock (gate)
            {
                foreach (var client in sseClients)
                {
                    try
                    {
                        var buffer = Encoding.UTF8.GetBytes(payload);
                        client.Response.OutputStream.Write(buffer, 0, buffer.Length);
                        client.Response.OutputStream.Flush();
                    }
                    catch
                    {
                        dead.Add(client);
                    }
                }
                foreach (var client in dead) sseClients.Remove(client);
            }
        }

        /// <summary>把 LLM 原始输出变成人类可读的缩进 JSON（中文字符不转义）。</summary>
        private static string HumanJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return json ?? string.Empty;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                    return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
            }
            catch
            {
                return json;
            }
        }

        private void RunLoop()
        {
            while (listener.IsListening)
            {
                try
                {
                    var httpContext = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => Handle(httpContext));
                }
                catch
                {
                    // listener closed
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url.AbsolutePath;
                if (path == "/events")
                {
                    HandleSse(ctx);
                    return;
                }
                if (path == "/api/state")
                {
                    RespondJson(ctx, BuildStateJson());
                    return;
                }
                if (path.StartsWith("/api/cards/") && ctx.Request.HttpMethod == "PUT")
                {
                    HandleCardPut(ctx, path.Substring("/api/cards/".Length));
                    return;
                }
                RespondHtml(ctx, BuildPage());
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* ignored */ }
            }
        }

        private void HandleSse(HttpListenerContext ctx)
        {
            var response = ctx.Response;
            response.Headers.Add("Content-Type", "text/event-stream; charset=utf-8");
            response.Headers.Add("Cache-Control", "no-cache");
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.KeepAlive = true;
            lock (gate) sseClients.Add(ctx);
            try
            {
                var hello = Encoding.UTF8.GetBytes("data: {\"hello\":\"tracesoul2-migrate\"}\n\n");
                response.OutputStream.Write(hello, 0, hello.Length);
                response.OutputStream.Flush();
                while (response.OutputStream.CanWrite)
                {
                    // 客户端断开时 Write 会抛异常，由 Notify 清理。
                    Thread.Sleep(2000);
                }
            }
            catch
            {
                /* disconnected */
            }
            finally
            {
                lock (gate) sseClients.Remove(ctx);
            }
        }

        private void HandleCardPut(HttpListenerContext ctx, string slot)
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = reader.ReadToEnd();
            string newBody;
            using (var doc = JsonDocument.Parse(body))
                newBody = doc.RootElement.GetProperty("body").GetString() ?? string.Empty;
            if (slot != IdentityCardSlotValues.Personality && slot != IdentityCardSlotValues.UserProfile)
            {
                RespondJson(ctx, "{\"error\":\"只有人格卡与档案卡允许手工修改\"}", 400);
                return;
            }
            var card = context.Store.SaveIdentityCard(
                MigrationContext.ConversationId, slot, newBody.Trim(), string.Empty);
            RespondJson(ctx, JsonSerializer.Serialize(new { slot = card.Slot, revision = card.Revision }));
            Notify("card_update", slot, "手工更新：" + (card.Body ?? string.Empty).Substring(0, Math.Min(60, (card.Body ?? string.Empty).Length)));
        }

        private string BuildStateJson()
        {
            var pair = context.Store.LoadPairIdentity();
            var cards = context.Store.LoadIdentityCards(MigrationContext.ConversationId)
                .Select(x => new
                {
                    x.Slot,
                    Title = IdentityCardSlotValues.Title(x.Slot, pair),
                    x.Body,
                    x.Revision
                }).ToList();
            var inner = context.Store.LoadOrCreateInnerRuntime(MigrationContext.ConversationId);
            var calls = context.Migration.GetCallLogsRecent(20).Select(x => new
            {
                x.DayKey,
                x.CallKind,
                x.ChunkIndex,
                OutputJson = HumanJson(x.OutputJson),
                x.CreatedUnixMs
            }).ToList();
            var injection = BuildInjectionText(context, inner);
            return JsonSerializer.Serialize(new
            {
                progress = ReadProgress(context.DataDirectory),
                cards,
                inner = new { inner.Narrative, inner.Mood, inner.Revision },
                injection,
                counts = new
                {
                    moments = context.Migration.CountMoments(),
                    indexes = context.Migration.GetActiveEventIndexes().Count,
                    entries = context.Migration.CountEventEntries(),
                    tags = context.Store.GetActiveLifeTags().Count(x => x.Origin == "sensory")
                },
                calls
            });
        }

        /// <summary>组装「每轮注入给 LLM 的状态」全文（六张卡 + 内心 + 时间 + 阶梯速览）并统计字数。</summary>
        private static object BuildInjectionText(
            MigrationContext context,
            InnerRuntimeData inner)
        {
            var pair = context.Store.LoadPairIdentity();
            var builder = new StringBuilder();
            builder.Append("我是 ").Append(pair.Assname).Append("。她是 ").Append(pair.Username).Append("。");
            builder.AppendLine();
            foreach (var card in context.Store.LoadIdentityCards(MigrationContext.ConversationId))
            {
                builder.Append("【").Append(IdentityCardSlotValues.Title(card.Slot, pair)).Append("】")
                    .AppendLine(card.Body);
            }
            builder.AppendLine();
            var mood = (inner.Mood ?? string.Empty).Trim();
            builder.Append("我此刻的内心：").Append(inner.Narrative)
                .AppendLine(mood.Length == 0 ? string.Empty : "（情绪：" + mood + "）");
            var timeText = BuildTimeText();
            builder.AppendLine();
            builder.AppendLine(timeText);
            var trajectory = context.Store.LoadDayTrajectory(
                DateTimeOffset.Now.ToOffset(MigrationContext.ChinaOffset).AddHours(-4).ToString("yyyy-MM-dd"));
            if (trajectory != null && !string.IsNullOrWhiteSpace(trajectory.Text))
            {
                builder.AppendLine();
                builder.Append("今天我们的轨迹：").Append(trajectory.Text.Trim());
            }
            var ladderText = BuildLadderText(context.Store.GetAllLadderItems());
            if (ladderText.Length > 0)
            {
                builder.AppendLine();
                builder.Append(ladderText);
            }
            var text = builder.ToString().TrimEnd();
            return new { text, total_chars = text.Length };
        }

        private static string BuildTimeText()
        {
            var now = DateTimeOffset.Now.ToOffset(MigrationContext.ChinaOffset);
            var week = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)now.DayOfWeek];
            var minute = now.Minute;
            var timeText = minute == 0 ? now.Hour + "点整" : now.Hour + "点" + minute + "分";
            return "现在是 " + now.Year + "年" + now.Month + "月" + now.Day + "日（" + week + "·" +
                   TimeLanguage.DayKindLabel(now) + "）" + TimeLanguage.PeriodZh(TimeLanguage.PeriodOf(now)) +
                   " " + timeText + "。";
        }

        private static string BuildLadderText(List<LadderItemRecord> items)
        {
            var tiers = new[] { "day", "week", "month", "year", "forever" };
            var names = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "day", "日榜" }, { "week", "周榜" }, { "month", "月榜" },
                { "year", "年榜" }, { "forever", "永久榜" }
            };
            var builder = new StringBuilder();
            foreach (var tier in tiers)
            {
                var latest = items.Where(x => x.Tier == tier)
                    .GroupBy(x => x.PeriodKey)
                    .OrderByDescending(x => x.Key)
                    .FirstOrDefault();
                if (latest == null)
                {
                    builder.AppendLine(names[tier] + "：（暂无）");
                    continue;
                }
                builder.AppendLine(names[tier] + "（" + latest.Key + "）：");
                foreach (var item in latest.OrderBy(x => x.Rank))
                    builder.AppendLine(item.Rank + ". " + item.Label + "（" + item.Reason + "）");
            }
            return builder.ToString().TrimEnd();
        }

        /// <summary>从 full_run_report.txt 解析当前进度：当前天/总数/剩余。</summary>
        private static object ReadProgress(string dataDirectory)
        {
            try
            {
                var path = Path.Combine(dataDirectory, "full_run_report.txt");
                if (!File.Exists(path)) return new { day = "", index = 0, total = 0, remaining = 0, running = false };
                var text = File.ReadAllText(path);
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    text, @"----- DAY (\d{4}-\d{2}-\d{2}) 开始（(\d+)/(\d+)）-----");
                if (matches.Count > 0)
                {
                    var last = matches[matches.Count - 1];
                    var day = last.Groups[1].Value;
                    var index = int.Parse(last.Groups[2].Value);
                    var total = int.Parse(last.Groups[3].Value);
                    return new { day, index, total, remaining = total - index, running = true };
                }
                if (text.Contains("全部完成"))
                    return new { day = "", index = 0, total = 0, remaining = 0, running = false };
            }
            catch { /* 进度解析失败不阻塞 */ }
            return new { day = "", index = 0, total = 0, remaining = 0, running = false };
        }

        private string BuildPage()
        {
            return @"<!DOCTYPE html><html lang='zh-CN'><head><meta charset='utf-8'/>
<title>TraceSoul2 构筑监视</title>
<style>
body{font-family:sans-serif;max-width:1000px;margin:16px auto;padding:0 12px;background:#111;color:#ddd}
h1{font-size:18px}h2{font-size:15px;margin-top:20px}
pre{background:#1c1c1c;padding:10px;overflow:auto;max-height:340px;border-radius:6px;font-size:12px}
.call{background:#1c1c1c;border-left:3px solid #4a9;margin:6px 0;padding:6px 10px;border-radius:4px}
.kind{color:#4a9;font-size:12px}.time{color:#888;font-size:12px;margin-left:8px}
textarea{width:100%;box-sizing:border-box;height:130px;background:#1c1c1c;color:#ddd;border:1px solid #444;border-radius:4px;padding:6px}
button{margin-top:6px;padding:6px 14px}
#state pre{max-height:260px}
</style></head><body>
<h1>TraceSoul2 构筑监视 <span class='time' id='conn'></span></h1>
<div id='progress' style='background:#1c2a1c;border:1px solid #3a5;padding:6px 10px;border-radius:6px;font-size:14px'>进度：读取中…</div>
<h2>注入给 LLM 的状态（实时）</h2>
<div id='state'><pre>加载中…</pre></div>
<h2>档案卡编辑（人控输入，Unity 可接同一 API）</h2>
<textarea id='profile'></textarea><br/><button onclick='saveProfile()'>保存档案卡</button>
<h2>LLM 调用流（实时）</h2>
<div id='calls'></div>
<script>
const stateEl=document.getElementById('state');
const callsEl=document.getElementById('calls');
const connEl=document.getElementById('conn');
async function refreshState(){
  const r=await fetch('/api/state'); const s=await r.json();
  const inj=s.injection||{text:'（暂无）',total_chars:0};
  stateEl.innerHTML='<pre>'+inj.text.replace(/&/g,'&amp;').replace(/</g,'&lt;')+'</pre>'
    +'<div style=""color:#8f8;font-size:13px;margin-top:6px"">注入合计：约 '+inj.total_chars+' 字（六张卡 + 内心 + 时间 + 阶梯速览）</div>';
  const p=s.cards.find(c=>c.slot==='user_profile');
  document.getElementById('profile').value=p?p.body:'';
  const g=s.progress||{};
  if(g.running){
    document.getElementById('progress').textContent='进度：'+g.day+'（'+g.index+'/'+g.total+'）剩余 '+g.remaining+' 天';
  } else if(g.total>0 || (s.counts&&s.counts.indexes>0)){
    document.getElementById('progress').textContent='进度：构建已完成';
  } else {
    document.getElementById('progress').textContent='进度：等待构建开始';
  }
}
async function saveProfile(){
  await fetch('/api/cards/user_profile',{method:'PUT',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({body:document.getElementById('profile').value})});
  refreshState();
}
function addCall(e){
  const d=JSON.parse(e.data); if(!d.kind) return;
  const div=document.createElement('div'); div.className='call';
  div.innerHTML='<span class=kind>'+d.kind+'</span><span class=time>'+d.time+'</span>'
    +'<div>'+d.digest+'</div>'
    +'<details><summary>原始输出</summary><pre>'+d.detail+'</pre></details>';
  callsEl.insertBefore(div,callsEl.firstChild);
  while(callsEl.children.length>20) callsEl.removeChild(callsEl.lastChild);
}
const es=new EventSource('/events');
es.onmessage=e=>{connEl.textContent='● 已连接'; addCall(e); if(e.data.includes('card_update')) refreshState();};
es.onerror=()=>connEl.textContent='○ 连接中断，重试中…';
refreshState();
setInterval(refreshState,5000);
</script></body></html>";
        }

        private static void RespondJson(HttpListenerContext ctx, string json, int status = 200)
        {
            var response = ctx.Response;
            response.StatusCode = status;
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
        }

        private static void RespondHtml(HttpListenerContext ctx, string html)
        {
            var response = ctx.Response;
            response.Headers.Add("Content-Type", "text/html; charset=utf-8");
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
        }
    }
}
