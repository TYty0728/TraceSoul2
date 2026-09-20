using System.Diagnostics;
using System.Text;

namespace TraceSoul2.ExternalPlugins.MediaUnderstanding;

public sealed class ExternalCommandResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public bool OutputTruncated { get; init; }
}

public sealed class ExternalCommandRunner
{
    public static bool CanResolve(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        if (Path.IsPathRooted(executable)) return File.Exists(executable);
        if (executable.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
            return File.Exists(Path.GetFullPath(executable));
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { string.Empty };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim(), executable.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                    ? executable : executable + extension);
                if (File.Exists(candidate)) return true;
            }
        }
        return false;
    }

    public async Task<ExternalCommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int maxOutputChars = 65_536)
    {
        if (!CanResolve(executable)) throw new MediaPipelineException("dependency_unavailable");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // External download tools must not inherit credentials through common proxy variables.
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            start.Environment.Remove(name);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new MediaPipelineException("dependency_start_failed");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var stdout = DrainAsync(process.StandardOutput, maxOutputChars, deadline.Token);
            var stderr = DrainAsync(process.StandardError, 4096, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout;
            await stderr; // Always drain; raw stderr may contain signed URLs and is deliberately discarded.
            return new ExternalCommandResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = output.Text,
                OutputTruncated = output.Truncated
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new MediaPipelineException("process_timeout");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (MediaPipelineException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw new MediaPipelineException("dependency_start_failed");
        }
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(
        StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(maxChars, 8192));
        var buffer = new char[2048];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            var remaining = maxChars - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) truncated = true;
        }
        return (result.ToString(), truncated);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch { }
    }
}
