using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TraceSoul2.Host
{
    internal static class BackupArchiveEndpoints
    {
        public static void MapBackups(this WebApplication app)
        {
            app.MapGet("/backups", (BackupArchiveService backups) => Results.Json(backups.Status()));
            app.MapPut("/backups/settings", (BackupSettings settings, BackupArchiveService backups) => Results.Json(backups.Configure(settings)));
            app.MapPost("/backups/create", (BackupArchiveService backups) => Results.Json(new { started = backups.Start(), status = backups.Status() }));
            app.MapPost("/backups/confirm-version", (BackupVersionConfirmation body, BackupArchiveService backups) =>
                Results.Json(backups.ConfirmVersion(body.Version, body.PackageId)));
            app.MapGet("/backups/recovery-key", (BackupArchiveService backups) =>
                Results.File(backups.ExportKey(), "application/octet-stream", "tracesoul-recovery.key"));
            app.MapGet("/backups/{id}/download", (string id, BackupArchiveService backups) =>
                Results.Stream(backups.OpenPackage(id), "application/octet-stream", id + ".tsbackup"));
            app.MapPost("/backups/{id}/verify", (string id, BackupArchiveService backups) =>
                Results.Json(backups.Verify(id)));
        }
    }

    internal sealed class BackupVersionConfirmation
    {
        public string Version { get; set; }
        public string PackageId { get; set; }
    }

    internal sealed class BackupArchiveWorker : BackgroundService
    {
        private readonly BackupArchiveService backups;
        private readonly ILogger<BackupArchiveWorker> log;
        public BackupArchiveWorker(BackupArchiveService backups, ILogger<BackupArchiveWorker> log)
        { this.backups = backups; this.log = log; }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var reported = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                try { backups.Start(scheduled: true); reported = false; }
                catch { if (!reported) log.LogWarning("定时存档未能启动，请在备份与恢复页面检查配置、路径和权限。"); reported = true; }
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            backups.Dispose();
            await base.StopAsync(cancellationToken);
            await backups.WaitAsync().WaitAsync(cancellationToken);
        }
    }
}
