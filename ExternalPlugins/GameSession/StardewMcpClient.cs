using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.ExternalPlugins.GameSession
{
    /// <summary>Small MCP stdio client owned by one active Stardew game session.</summary>
    internal sealed class StardewMcpClient : IDisposable
    {
        private readonly string command;
        private readonly IReadOnlyList<string> arguments;
        private readonly IReadOnlyDictionary<string, string> environment;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending =
            new ConcurrentDictionary<long, TaskCompletionSource<JsonElement>>();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private Process process;
        private Task stdoutLoop;
        private Task stderrLoop;
        private long nextId;
        private int disposeState;

        public bool IsRunning
        {
            get
            {
                try { return process != null && !process.HasExited && !shutdown.IsCancellationRequested; }
                catch { return false; }
            }
        }

        public StardewMcpClient(string command, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, Action<string> log)
        {
            this.command = string.IsNullOrWhiteSpace(command) ? "node" : command.Trim();
            this.arguments = arguments ?? Array.Empty<string>();
            this.environment = environment ?? new Dictionary<string, string>();
            this.log = log;
        }

        public async Task StartAsync(CancellationToken token)
        {
            if (Volatile.Read(ref disposeState) != 0) throw new ObjectDisposedException(nameof(StardewMcpClient));
            if (IsRunning) return;
            var info = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in arguments.Where(x => !string.IsNullOrWhiteSpace(x)))
                info.ArgumentList.Add(argument);
            foreach (var item in environment)
                if (!string.IsNullOrWhiteSpace(item.Key)) info.Environment[item.Key] = item.Value ?? string.Empty;
            process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Stardew MCP Server。");
            stdoutLoop = Task.Run(() => ReadStdoutAsync(shutdown.Token));
            stderrLoop = Task.Run(() => ReadStderrAsync(shutdown.Token));
            try
            {
                await RequestAsync("initialize", new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "TraceSoul2-game-session", version = "0.4.3" }
                }, token);
                await NotifyAsync("notifications/initialized", new { }, token);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public async Task<string> CallToolAsync(string name, object arguments, CancellationToken token)
        {
            if (!IsRunning) throw new InvalidOperationException("Stardew MCP Server 没有运行。");
            var result = await RequestAsync("tools/call", new
            {
                name,
                arguments = arguments ?? new { }
            }, token);
            if (result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException(ExtractText(result, "MCP 工具调用失败。"));
            return ExtractText(result, string.Empty);
        }

        private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token)
        {
            var id = Interlocked.Increment(ref nextId);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("MCP 请求编号冲突。");
            try
            {
                await SendAsync(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    method,
                    @params = parameters
                }), token);
                using var registration = token.Register(() => completion.TrySetCanceled(token));
                var response = await completion.Task;
                if (response.TryGetProperty("error", out var error))
                    throw new InvalidOperationException("Stardew MCP 返回错误：" + error.GetRawText());
                if (!response.TryGetProperty("result", out var result))
                    throw new InvalidOperationException("Stardew MCP 响应缺少 result。");
                return result.Clone();
            }
            finally { pending.TryRemove(id, out _); }
        }

        private Task NotifyAsync(string method, object parameters, CancellationToken token)
        {
            return SendAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters
            }), token);
        }

        private async Task SendAsync(string json, CancellationToken token)
        {
            await sendGate.WaitAsync(token);
            try
            {
                if (!IsRunning) throw new InvalidOperationException("Stardew MCP Server 已退出。");
                await process.StandardInput.WriteLineAsync(json.AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
            }
            finally { sendGate.Release(); }
        }

        private async Task ReadStdoutAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && process != null && !process.HasExited)
                {
                    var line = await process.StandardOutput.ReadLineAsync(token);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (!root.TryGetProperty("id", out var idElement)) continue;
                        long id;
                        if (idElement.ValueKind == JsonValueKind.Number) id = idElement.GetInt64();
                        else if (!long.TryParse(idElement.GetString(), out id)) continue;
                        if (pending.TryGetValue(id, out var completion))
                            completion.TrySetResult(root.Clone());
                    }
                    catch (Exception exception)
                    {
                        log?.Invoke("Stardew MCP 输出无法解析：" + exception.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { FailPending(exception); }
            finally
            {
                if (!token.IsCancellationRequested)
                    FailPending(new InvalidOperationException("Stardew MCP Server 已停止输出。"));
            }
        }

        private async Task ReadStderrAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && process != null && !process.HasExited)
                {
                    var line = await process.StandardError.ReadLineAsync(token);
                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line)) log?.Invoke("Stardew MCP：" + line.Trim());
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { log?.Invoke("Stardew MCP 日志读取失败：" + exception.Message); }
        }

        private void FailPending(Exception exception)
        {
            foreach (var item in pending.Values) item.TrySetException(exception);
        }

        private static string ExtractText(JsonElement result, string fallback)
        {
            if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return fallback;
            foreach (var item in content.EnumerateArray())
                if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString() ?? fallback;
            return fallback;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeState, 1) != 0) return;
            shutdown.Cancel();
            FailPending(new OperationCanceledException("Stardew MCP 客户端已关闭。"));
            try { process?.StandardInput.Close(); } catch { }
            try
            {
                if (process != null && !process.HasExited) process.Kill(true);
            }
            catch { }
            try { process?.Dispose(); } catch { }
            sendGate.Dispose();
            shutdown.Dispose();
        }
    }
}
