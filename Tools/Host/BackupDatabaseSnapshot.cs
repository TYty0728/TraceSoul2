using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SQLite;

namespace TraceSoul2.Host
{
    public sealed partial class BackupArchiveService
    {
        private const string SnapshotWorkerFlag = "--archive-snapshot-worker";
        private const string ChangingDatabase = "数据库或事务日志在存档期间持续变化，已保留来源和已有成品。";

        // POSIX close() drops this process's advisory locks for the inode, even when
        // the file descriptor was opened by FileStream rather than SQLite. A helper
        // must capture source bytes without opening/closing them in the live Host.
        public static void CopyDatabaseSnapshot(string source, string destination, CancellationToken token = default)
        {
            if (OperatingSystem.IsWindows())
            {
                CopyDatabaseSnapshotCore(source, destination, token);
                return;
            }
            token.ThrowIfCancellationRequested();
            RejectLinks(source); RejectLinks(destination);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("数据库副本目标已存在，未覆盖。");
            var assembly = typeof(BackupArchiveService).Assembly.Location;
            if (string.IsNullOrEmpty(assembly)) throw new IOException("当前程序缺少独立数据库快照入口，已停止存档。");
            var start = new ProcessStartInfo
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(assembly)
            };
            var processPath = Environment.ProcessPath;
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.FileName = processPath;
                start.ArgumentList.Add(assembly);
            }
            else start.FileName = Path.Combine(Path.GetDirectoryName(assembly), Path.GetFileNameWithoutExtension(assembly));
            start.ArgumentList.Add(SnapshotWorkerFlag);
            start.ArgumentList.Add(Path.GetFullPath(source));
            start.ArgumentList.Add(Path.GetFullPath(destination));
            using var child = Process.Start(start) ?? throw new IOException("数据库快照进程未能启动。");
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            try { child.WaitForExitAsync(token).GetAwaiter().GetResult(); }
            catch
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); child.WaitForExit(); } catch { }
                throw;
            }
            output.GetAwaiter().GetResult(); errors.GetAwaiter().GetResult();
            // No raw SQLite/JSON errors, source paths or contents cross the public error boundary.
            if (child.ExitCode == 4) throw new InvalidDataException("数据库完整性检查失败。");
            if (child.ExitCode == 3) throw new IOException(ChangingDatabase);
            if (child.ExitCode != 0 || !File.Exists(destination))
                throw new IOException("数据库快照进程未能完成，请检查来源、权限和空间；原存档保留。");
        }

        // Call before resolving home, creating SQLite stores, or loading any plugins.
        public static bool TryRunSnapshotWorker(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args.Length == 0 || args[0] != SnapshotWorkerFlag) return false;
            if (args.Length != 3) { exitCode = 2; return true; }
            try { CopyDatabaseSnapshotCore(args[1], args[2], CancellationToken.None); }
            catch (InvalidDataException) { exitCode = 4; }
            catch (SQLiteException ex) when (ex.Result == SQLite3.Result.Corrupt || ex.Result == SQLite3.Result.NonDBFile)
            { exitCode = 4; }
            catch (IOException ex) when (ex.Message == ChangingDatabase) { exitCode = 3; }
            catch { exitCode = 5; }
            return true;
        }
    }
}
