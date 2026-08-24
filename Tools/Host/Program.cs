using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TraceSoul2.Data;
using TraceSoul2.Host;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

SQLitePCL.Batteries_V2.Init();

// 分支切换自重启：等旧进程释放端口后再绑定。
var restartDelay = Environment.GetEnvironmentVariable("TRACESOUL2_RESTART_DELAY");
if (int.TryParse(restartDelay, out var delayMs) && delayMs > 0)
    Thread.Sleep(delayMs);

var home = TraceHome.Resolve();
var dataDir = home.SoulDirectory;

// OneBot 反向 WS（AstrBot aiocqhttp 同款）：NapCat 主动连 ws://127.0.0.1:{listen_port}/ws，
// 宿主额外监听这个端口。改端口在控制台保存后宿主会自动重启。
// 注意：只有 onebot.json 真实存在（用户在控制台保存过）才绑端口，默认值不算配置。
var onebotConfigPath = Path.Combine(dataDir, "onebot.json");
var onebotListenPort = 0;
try
{
    if (File.Exists(onebotConfigPath))
    {
        var onebotConfig = OneBotConfig.Load(dataDir);
        if (onebotConfig.enabled && onebotConfig.mode == "reverse" && onebotConfig.listen_port > 0)
            onebotListenPort = onebotConfig.listen_port;
    }
}
catch
{
    /* onebot.json 损坏按未配置处理 */
}

var urls = Environment.GetEnvironmentVariable("TRACESOUL2_URLS") ?? home.Urls ?? TraceHome.DefaultUrls;
// 静态控制台（wwwroot）与内容根固定到程序目录，与启动时的工作目录无关（重启/后台启动都稳定）。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = AppContext.BaseDirectory,
    Args = args
});
builder.WebHost.ConfigureKestrel(options =>
{
    // NapCat 心跳约 30s 一次，远低于 Kestrel 默认 240B/s；不关掉会把反向 WS 当空闲连接掐掉。
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;
    ConfigureListenUrls(options, urls);
    if (onebotListenPort > 0 && PortFree(onebotListenPort))
        options.ListenLocalhost(onebotListenPort);
    else if (onebotListenPort > 0)
        Console.WriteLine("OneBot 反向监听端口 " + onebotListenPort + " 被占用，已跳过（可在控制台改端口后保存重启）。");
});
builder.Services.AddSingleton(new SoulRuntime(
    dataDir, home.PluginsDirectory, home.PluginsDataDirectory));
builder.Services.AddSingleton(new UpdateService(home));
builder.Services.AddSingleton(new DebugBranchService(dataDir));
builder.Services.AddHostedService<BackgroundMomentWorker>();
builder.Services.AddSingleton<DailyPipelineWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DailyPipelineWorker>());

var app = builder.Build();
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    if (remote != null && !IPAddress.IsLoopback(remote))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "TraceSoul2 控制台只接受本机连接。" });
        return;
    }

    if (!IsLoopbackHost(context.Request.Host.Host))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "无效的控制台 Host。" });
        return;
    }

    var isWebSocketUpgrade = context.Request.Headers.Upgrade.ToString()
        .Split(',')
        .Any(part => string.Equals(part.Trim(), "websocket", StringComparison.OrdinalIgnoreCase));
    var origin = context.Request.Headers.Origin.ToString();
    // NapCat 反向 WS 的 Origin 常是 ws://127.0.0.1:9021，和 HTTP 控制台 scheme/port 对不上。
    if (!isWebSocketUpgrade && !string.IsNullOrWhiteSpace(origin))
    {
        Uri originUri;
        var requestPort = context.Request.Host.Port ??
                          (context.Request.IsHttps ? 443 : 80);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out originUri) ||
            !string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(originUri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase) ||
            originUri.Port != requestPort)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "拒绝跨来源访问本机控制台。" });
            return;
        }
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // 客户端断开（浏览器关闭/SSE 断开）：正常结束，不写响应。
    }
    catch (Exception exception)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
        else
        {
            // 响应已开始（如 SSE 流）：不能再改状态码，只能记录。
            Console.WriteLine("请求处理异常（响应已开始，仅记录）：" + exception.Message);
        }
    }
});

app.MapGet("/status", (SoulRuntime runtime) => Results.Json(runtime.Status()));
app.MapGet("/live-state", (SoulRuntime runtime) => Results.Json(runtime.LiveState()));

// 正式版更新：GitHub Release → SHA-256 校验 → 外置 runner 替换应用目录。
app.MapGet("/updates", (UpdateService updates) => Results.Json(updates.Status()));

app.MapPut("/updates/config", (UpdateService updates, UpdateConfigWrite body) =>
    Results.Json(updates.ConfigureRepository(body == null ? string.Empty : body.repository)));

app.MapPost("/updates/check", async (UpdateService updates, CancellationToken token) =>
    Results.Json(await updates.CheckAsync(token)));

app.MapPost("/updates/install", async (UpdateService updates, CancellationToken token) =>
{
    var result = await updates.BeginInstallAsync(token);
    _ = Task.Run(async () =>
    {
        await Task.Delay(800);
        app.Lifetime.StopApplication();
    });
    return Results.Json(result);
});

// 数据库切换（控制台）：枚举 / 切换 / 新建。切换与新建都会重启宿主到目标数据目录。
var databaseSwitch = new DatabaseSwitchService(dataDir, home.SoulsDirectory);
app.MapGet("/databases", () => Results.Json(databaseSwitch.List()));

app.MapPost("/databases/switch", (DatabaseSwitchRequest body) =>
{
    var target = databaseSwitch.ResolveForSwitch(body.nameOrPath);
    if (string.Equals(target, databaseSwitch.Current, StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { switching = false, message = "已经是当前数据库：" + target });
    RestartHost(target);
    return Results.Json(new { switching = true, target });
});

app.MapPost("/databases/create", (DatabaseCreateRequest body) =>
{
    var target = databaseSwitch.Create(body.name);
    RestartHost(target);
    return Results.Json(new { switching = true, target, message = "新数据库已创建，宿主正在切换（新库没有 API Key，请在「大脑」页填写）。" });
});

app.MapGet("/identity/cards", (SoulRuntime runtime) =>
{
    var pair = runtime.Store.LoadPairIdentity();
    return Results.Json(runtime.Store.LoadIdentityCards(runtime.ConversationId).Select(x => new
    {
        x.Slot,
        title = IdentityCardSlotValues.Title(x.Slot, pair),
        x.Body,
        x.Revision,
        template = IdentityCardLogic.CardTemplate(x.Slot, pair)
    }));
});

app.MapPut("/identity/pair", (SoulRuntime runtime, PairWrite body) =>
{
    var current = runtime.Store.LoadPairIdentity();
    var pair = runtime.Store.SavePairIdentity(body.username, body.assname, current.CallName);
    runtime.RebuildOntology();
    runtime.Emit("名字已保存：" + pair.Username + " / " + pair.Assname);
    return Results.Json(new { pair.Username, pair.Assname, pair.CallName });
});

app.MapPut("/identity/cards/{slot}", (SoulRuntime runtime, string slot, CardWrite body) =>
{
    var card = runtime.Store.SaveIdentityCard(runtime.ConversationId, slot, body.body, string.Empty);
    runtime.Emit("短卡已保存：" + slot);
    return Results.Json(new { card.Slot, card.Body, card.Revision });
});

app.MapGet("/inner", (SoulRuntime runtime) =>
{
    var inner = runtime.Store.LoadOrCreateInnerRuntime(runtime.ConversationId);
    return Results.Json(new
    {
        inner.Narrative,
        inner.Mood,
        inner.Revision,
        inner.RelationshipLens,
        inner.OngoingActivity,
        sharedScene = inner.OngoingActivity,
        inner.Asleep,
        nextHeartbeatUnixMs = HeartbeatLogic.NextDueUnixMs(runtime.Store, runtime.ConversationId),
        attention = (inner.Attention ?? new List<AttentionItemData>())
            .Take(3)
            .Select(x => new { kind = x.kind ?? string.Empty, content = x.content ?? string.Empty, updatedUnixMs = x.UpdatedUnixMs })
            .ToList(),
        inner.SnapshotId,
        inner.SourceMomentId,
        inner.UpdatedUnixMs
    });
});

app.MapGet("/plugins", async (SoulRuntime runtime, CancellationToken token) =>
{
    var payload = await runtime.ExclusiveAsync(() =>
    {
        var catalog = runtime.Plugins.GetRegisteredCatalog();
        return runtime.Plugins.GetPlugins().Select(plugin =>
        {
            var pack = runtime.FindExternalPackage(plugin.Id);
            var configurable = pack != null ||
                               string.Equals(plugin.Id, "builtin.onebot", StringComparison.OrdinalIgnoreCase);
            return new
            {
                plugin.Id,
                plugin.DisplayName,
                plugin.Description,
                plugin.Version,
                plugin.Role,
                plugin.PlatformId,
                plugin.Enabled,
                plugin.LoadError,
                plugin.Note,
                configurable,
                folder = pack == null ? string.Empty : pack.Folder,
                contributions = catalog.Where(x => x.PluginId == plugin.Id &&
                                                   !MouthLogic.IsProtocolFacet(x.Id) &&
                                                   x.Kind == TraceContributionKindValues.Effector)
                    .Select(x => new { x.Id, x.Kind, x.DisplayName, x.Organ, x.BodyId })
            };
        }).ToList();
    }, token);
    return Results.Json(payload);
});

app.MapPut("/plugins/{id}/enabled", async (SoulRuntime runtime, string id, EnabledWrite body, CancellationToken token) =>
{
    try
    {
        return Results.Json(await runtime.ExclusiveAsync(
            () => runtime.SetPluginEnabled(id, body.enabled), token));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

// 外部插件包（主项目之外、运行时插拔）：状态 / 重扫 / 卸载。
app.MapGet("/plugins/external", async (SoulRuntime runtime, CancellationToken token) =>
    Results.Json(await runtime.ExclusiveAsync(runtime.ExternalPluginStatus, token)));

app.MapPost("/plugins/external/rescan", async (SoulRuntime runtime, CancellationToken token) =>
{
    var status = await runtime.ExclusiveAsync(runtime.RescanExternalPlugins, token);
    return Results.Json(status);
});

app.MapDelete("/plugins/external/{id}", async (SoulRuntime runtime, string id, CancellationToken token) =>
{
    try
    {
        return Results.Json(await runtime.ExclusiveAsync(
            () => runtime.UninstallExternalPlugin(id), token));
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
});

app.MapGet("/plugins/{id}/config", async (SoulRuntime runtime, string id, CancellationToken token) =>
{
    try { return Results.Json(await runtime.ExclusiveAsync(() => runtime.ReadPluginConfig(id), token)); }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapPut("/plugins/{id}/config", async (SoulRuntime runtime, string id, PluginConfigWrite body, CancellationToken token) =>
{
    try
    {
        var result = await runtime.ExclusiveAsync(
            () => runtime.WritePluginConfig(id, body == null ? null : body.values), token);
        if (result != null && result.restart) RestartHost(runtime.DataDirectory);
        return Results.Json(result);
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapGet("/moments", (SoulRuntime runtime, int? take) =>
{
    var pair = runtime.Store.LoadPairIdentity();
    return Results.Json(runtime.Store.GetRecentMoments(runtime.ConversationId, take ?? 40).Select(x => new
    {
        x.Id,
        role = pair.LabelForRole(x.Role),
        x.Content,
        x.SourcePluginId,
        x.CreatedUnixMs
    }));
});

app.MapGet("/operational-events", (SoulRuntime runtime, int? take) =>
    Results.Json(runtime.Store.GetRecentOperationalEvents(
        runtime.ConversationId, take ?? 40)));

app.MapGet("/turns/last", (SoulRuntime runtime) =>
{
    var payload = runtime.LastTurnPayload();
    return payload == null ? Results.Json(new { }) : Results.Json(payload);
});

app.MapPost("/moments", async (SoulRuntime runtime, MomentWrite body, CancellationToken token) =>
{
    var turn = await runtime.PostMomentAsync(body.content, token);
    return Results.Json(new { turn.Reply, turn.BrainMode, turn.BrainIntent, turn.DecisionSummary });
});

app.MapGet("/providers", (SoulRuntime runtime) =>
    Results.Json(runtime.Providers.List().Select(runtime.PublicProvider)));

app.MapGet("/providers/slots", (SoulRuntime runtime) => Results.Json(runtime.PublicSlots()));

app.MapGet("/providers/templates", () => Results.Json(LlmProviderCatalog.Templates()));

app.MapPost("/providers", (SoulRuntime runtime, AddProviderWrite body) =>
{
    var saved = runtime.Providers.AddFromTemplate(body.templateKey, body.id);
    runtime.Emit("已新增供应商：" + saved.id);
    return Results.Json(runtime.PublicProvider(saved));
});

app.MapDelete("/providers/{id}", (SoulRuntime runtime, string id) =>
{
    runtime.Providers.Delete(id);
    runtime.Emit("已删除供应商：" + id);
    return Results.Json(runtime.Providers.List().Select(runtime.PublicProvider));
});

app.MapPut("/providers/{id}", (SoulRuntime runtime, string id, ProviderWrite body) =>
{
    var saved = runtime.Providers.Upsert(new LlmProviderRecord
    {
        id = id,
        type = body.type,
        displayName = body.displayName,
        baseUrl = body.baseUrl,
        model = body.model,
        apiKey = body.apiKey,
        temperature = body.temperature,
        topP = body.topP,
        maxTokens = body.maxTokens,
        timeout = body.timeout,
        proxy = body.proxy,
        thinkingEnabled = body.thinkingEnabled,
        reasoningEffort = body.reasoningEffort
    });
    runtime.Emit("提供商已保存：" + id);
    return Results.Json(runtime.PublicProvider(saved));
});

app.MapPost("/providers/{id}/models", async (SoulRuntime runtime, string id, CancellationToken token) =>
{
    var client = runtime.Providers.CreateClient(id);
    if (client == null) throw new InvalidOperationException("这个提供商还没有 API Key。");
    var fetched = await client.ListModelsAsync(token);
    var saved = runtime.Providers.MergeFetched(id, fetched);
    runtime.Emit("已获取 " + fetched.Count + " 个模型，已写入供应商「" + id + "」");
    return Results.Json(new
    {
        models = fetched,
        configured = runtime.PublicProvider(saved)
    });
});

app.MapPut("/providers/{id}/models", (SoulRuntime runtime, string id, ModelWrite body) =>
{
    if (body == null || string.IsNullOrWhiteSpace(body.id))
        throw new InvalidOperationException("模型 id 不能为空。");
    var saved = runtime.Providers.UpsertModel(id, body.id, body.enabled, body.roles);
    runtime.Emit("已保存模型：" + id + " / " + body.id);
    return Results.Json(runtime.PublicProvider(saved));
});

app.MapDelete("/providers/{id}/models", (SoulRuntime runtime, string id, string model) =>
{
    var saved = runtime.Providers.DeleteModel(id, model);
    runtime.Emit("已删除模型：" + id + " / " + model);
    return Results.Json(runtime.PublicProvider(saved));
});

app.MapPut("/providers/current", (SoulRuntime runtime, SelectWrite body) =>
{
    var selected = runtime.Providers.Select(body.id, body.model);
    runtime.RefreshReviewClient();
    runtime.Emit("当前模型：" + selected.model);
    return Results.Json(runtime.PublicProvider(selected));
});

app.MapPut("/providers/slots", (SoulRuntime runtime, SlotBundleWrite body) =>
{
    if (body == null) throw new InvalidOperationException("槽位不能为空。");
    ProviderSlotApi.Apply(runtime, LlmSlotNames.Thinking, body.thinking);
    ProviderSlotApi.Apply(runtime, LlmSlotNames.Review, body.review);
    ProviderSlotApi.Apply(runtime, LlmSlotNames.Multimodal, body.multimodal);
    ProviderSlotApi.Apply(runtime, LlmSlotNames.Image, body.image);
    ProviderSlotApi.Apply(runtime, LlmSlotNames.Speech, body.speech);
    if (body.chat != null && !string.IsNullOrWhiteSpace(body.chat.providerId))
        runtime.Providers.Select(body.chat.providerId, body.chat.model);
    runtime.RefreshReviewClient();
    runtime.Emit("已保存默认模型槽");
    return Results.Json(runtime.PublicSlots());
});

app.MapPut("/settings/context-limit", (SoulRuntime runtime, LimitWrite body) =>
{
    return Results.Json(new { limit = runtime.SetContextInjectionCount(body.limit) });
});

app.MapPut("/settings/heartbeat", (SoulRuntime runtime, HeartbeatWrite body) =>
{
    return Results.Json(runtime.SetHeartbeatRange(body.minMinutes, body.maxMinutes));
});

app.MapGet("/memory/nerve", (SoulRuntime runtime) => Results.Json(runtime.NerveStatus()));

app.MapGet("/memory/ladder", (SoulRuntime runtime) => Results.Json(runtime.LadderStatus()));

app.MapGet("/memory/day-trajectory", (SoulRuntime runtime) => Results.Json(runtime.DayTrajectoryStatus()));

app.MapGet("/platforms", async (SoulRuntime runtime, CancellationToken token) =>
    Results.Json(await runtime.ExclusiveAsync(runtime.PlatformStatus, token)));

app.MapGet("/mouths", async (SoulRuntime runtime, CancellationToken token) =>
    Results.Json(await runtime.ExclusiveAsync(runtime.MouthStatus, token)));

app.MapPut("/mouths", async (SoulRuntime runtime, MouthsWrite body, CancellationToken token) =>
{
    var items = (body.items ?? Array.Empty<MouthWriteItem>()).Select(x => new MouthRankEntry
    {
        id = x.id,
        priority = x.score != 0 ? x.score : x.priority
    }).ToArray();
    return Results.Json(await runtime.ExclusiveAsync(
        () => runtime.SaveMouths(body.scene, body.activeBody, items), token));
});

app.MapGet("/life-state", (SoulRuntime runtime) => Results.Json(runtime.LifeStateStatus()));

app.MapPut("/life-state", (SoulRuntime runtime, LifeStateWrite body) =>
    Results.Json(runtime.UpdateLifeState(
        body.location, body.activity, body.activityDetail,
        body.source, body.sourceId, body.force)));

// OneBot 配置（控制台编辑；保存后宿主自动重启应用）。
app.MapGet("/platforms/onebot/config", (SoulRuntime runtime) =>
{
    var config = OneBotConfig.Load(runtime.DataDirectory);
    return Results.Json(new
    {
        config.enabled,
        config.mode,
        config.listen_port,
        config.ws_url,
        config.http_url,
        config.access_token,
        config.self_id,
        config.reply_enabled,
        config.napcat_path,
        napcat_candidates = NapCatLauncher.Discover(config.napcat_path),
        napcat_url = "ws://127.0.0.1:" + config.listen_port + "/ws",
        note = "NapCat 侧：网络配置用 websocketClients（反向 WS）连上面的地址，token 与此处一致；与 AstrBot aiocqhttp 同款模式。"
    });
});

app.MapPut("/platforms/onebot/config", (SoulRuntime runtime, OneBotConfigWrite body) =>
{
    var config = new OneBotConfig
    {
        enabled = body.enabled,
        mode = (body.mode ?? "reverse") == "forward" ? "forward" : "reverse",
        listen_port = Math.Max(1, Math.Min(65535, body.listen_port <= 0 ? 9021 : body.listen_port)),
        ws_url = string.IsNullOrWhiteSpace(body.ws_url) ? "ws://127.0.0.1:3001" : body.ws_url.Trim(),
        http_url = string.IsNullOrWhiteSpace(body.http_url) ? "http://127.0.0.1:3000" : body.http_url.Trim(),
        access_token = (body.access_token ?? string.Empty).Trim(),
        self_id = (body.self_id ?? string.Empty).Trim(),
        reply_enabled = body.reply_enabled,
        napcat_path = (body.napcat_path ?? string.Empty).Trim()
    };
    File.WriteAllText(Path.Combine(runtime.DataDirectory, "onebot.json"),
        JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    runtime.Emit("OneBot 配置已保存，宿主即将重启应用…");
    RestartHost(runtime.DataDirectory);
    return Results.Json(new { saved = true, message = "配置已保存，宿主正在重启应用（约 2 秒后生效）。" });
});

app.MapPost("/platforms/onebot/napcat/start", (SoulRuntime runtime) =>
{
    try
    {
        var config = OneBotConfig.Load(runtime.DataDirectory);
        var result = NapCatLauncher.Start(config.napcat_path);
        runtime.Emit(result.message);
        return Results.Json(result);
    }
    catch (FileNotFoundException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (System.ComponentModel.Win32Exception exception)
    {
        return Results.BadRequest(new { error = "启动 NapCat 失败：" + exception.Message });
    }
});

// 插件声明的 WebSocket 入口（OneBot 反向 WS 等）。
var pluginWsEndpoints = app.Services.GetRequiredService<SoulRuntime>().WebSocketEndpoints();
if (pluginWsEndpoints.Count > 0)
{
    app.UseWebSockets();
    foreach (var endpoint in pluginWsEndpoints)
    {
        app.Map(endpoint.Path, async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (!endpoint.Accept(
                    context.Request.Headers.Authorization.ToString(),
                    context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            using (var socket = await context.WebSockets.AcceptWebSocketAsync())
                await endpoint.OnConnectedAsync(socket, context.RequestAborted);
        });
    }
}

app.MapPut("/memory/nerve", (SoulRuntime runtime, NerveWrite body) =>
    Results.Json(runtime.UpdateNerve(body.top_k, body.provider_id)));

app.MapPost("/memory/daily-run", (DailyRunWrite body, [FromServices] DailyPipelineWorker worker) =>
{
    var day = worker.Trigger(body == null ? null : body.day);
    return Results.Json(new
    {
        started = worker.IsAvailable,
        day,
        available = worker.IsAvailable,
        migrateDll = worker.MigrateDll ?? string.Empty,
        message = worker.IsAvailable
            ? "日构建已启动，LLM 调用可到 5090 实时监视台观看。"
            : "找不到 TraceSoul2.Migrate.dll，无法自动构建。"
    });
});

app.MapGet("/events", async (HttpContext context, SoulRuntime runtime) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    using var subscription = runtime.SubscribeEvents();
    try
    {
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { hello = "tracesoul2" }) + "\n\n");
        await context.Response.Body.FlushAsync();
        var reader = subscription.Reader;
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var line = await reader.ReadAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { message = line }) + "\n\n");
            await context.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // 浏览器断开：正常退出流。
    }
    catch (Exception exception)
    {
        Console.WriteLine("SSE 流异常：" + exception.Message);
    }
});

app.MapGet("/debug/branches", (DebugBranchService branches) => Results.Json(branches.List()));

app.MapPost("/debug/fork", (DebugBranchService branches, SoulRuntime runtime, ForkWrite body) =>
{
    var record = branches.Fork(body.fromDay, body.note, body.freshMemory);
    runtime.Emit("调试分支已创建：" + record.id);
    return Results.Json(record);
});

app.MapDelete("/debug/branches/{id}", (DebugBranchService branches, SoulRuntime runtime, string id) =>
{
    var removed = branches.Destroy(id);
    if (!removed) return Results.NotFound(new { error = "分支不存在：" + id });
    runtime.Emit("调试分支已销毁：" + id);
    return Results.Json(new { id, destroyed = true });
});

app.MapGet("/debug/mode", (SoulRuntime runtime) =>
{
    var state = DebugMode.Read(runtime.DataDirectory);
    return Results.Json(new
    {
        mode = state == null ? "main" : "debug",
        mainDir = state == null ? runtime.DataDirectory : state.mainDir,
        debugDir = state == null ? DebugMode.ActiveDir(runtime.DataDirectory) : state.debugDir
    });
});

app.MapPost("/debug/enter", (SoulRuntime runtime, DebugBranchService branches) =>
{
    if (DebugMode.Read(runtime.DataDirectory) != null)
        return Results.Conflict(new { error = "已经处于调试模式。" });
    var debugDir = DebugMode.ActiveDir(runtime.DataDirectory);
    if (Directory.Exists(debugDir)) Directory.Delete(debugDir, recursive: true);
    branches.ForkTo(runtime.DataDirectory, debugDir);
    DebugMode.Write(runtime.DataDirectory, debugDir);
    RestartHost(debugDir);
    return Results.Json(new { mode = "debug", message = "已平移全部数据到调试分支，宿主正在切换。" });
});

app.MapPost("/debug/exit", (SoulRuntime runtime) =>
{
    var state = DebugMode.Read(runtime.DataDirectory);
    if (state == null)
        return Results.Conflict(new { error = "当前不是调试模式。" });
    RestartHost(state.mainDir);
    return Results.Json(new { mode = "main", message = "正在切回主线，测试侧数据将被清空。" });
});

app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<SoulRuntime>().Dispose());
Console.WriteLine("TraceSoul2 Host  v" + TraceHome.HostVersion() +
                  "  " + urls +
                  "  home=" + (home.Root ?? "(legacy)") +
                  "  soul=" + dataDir +
                  "  plugins=" + home.PluginsDirectory +
                  "  plugins_data=" + home.PluginsDataDirectory);

// 切回主线后的清理：等旧调试进程完全退出后，删除测试侧全部数据。
if (File.Exists(Path.Combine(dataDir, "debug-mode.json")))
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(1500);
        try
        {
            DebugMode.Clear(dataDir);
            Console.WriteLine("调试分支已清空（切回主线完成）。");
        }
        catch (Exception exception)
        {
            Console.WriteLine("调试分支清理失败：" + exception.Message);
        }
    });
}

app.Run();

// 分支切换：以指定数据目录重启本宿主（同端口、同控制台，只换底层数据）。
static void RestartHost(string targetDataDir)
{
    TraceHome.RememberActiveSoul(targetDataDir);
    var urls = Environment.GetEnvironmentVariable("TRACESOUL2_URLS")
               ?? TraceHome.Current?.Urls
               ?? TraceHome.DefaultUrls;
    var dllPath = Path.Combine(AppContext.BaseDirectory, "TraceSoul2.Host.dll");
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = "\"" + dllPath + "\"",
        UseShellExecute = false
    };
    if (TraceHome.Current != null && !string.IsNullOrWhiteSpace(TraceHome.Current.Root))
        startInfo.Environment["TRACESOUL2_HOME"] = TraceHome.Current.Root;
    startInfo.Environment["TRACESOUL2_DATA"] = targetDataDir;
    startInfo.Environment["TRACESOUL2_URLS"] = urls;
    startInfo.Environment["TRACESOUL2_RESTART_DELAY"] = "1500";
    var plugins = Environment.GetEnvironmentVariable("TRACESOUL2_PLUGINS")
        ?? TraceHome.Current?.PluginsDirectory;
    if (!string.IsNullOrWhiteSpace(plugins))
        startInfo.Environment["TRACESOUL2_PLUGINS"] = plugins;
    var pluginsData = Environment.GetEnvironmentVariable(TraceHome.EnvPluginsData)
        ?? TraceHome.Current?.PluginsDataDirectory;
    if (!string.IsNullOrWhiteSpace(pluginsData))
        startInfo.Environment[TraceHome.EnvPluginsData] = pluginsData;
    var dump = Environment.GetEnvironmentVariable("TRACESOUL2_LLM_DUMP_DIR");
    if (!string.IsNullOrWhiteSpace(dump))
        startInfo.Environment["TRACESOUL2_LLM_DUMP_DIR"] = dump;
    var migrate = Environment.GetEnvironmentVariable("TRACESOUL2_MIGRATE_DLL");
    if (!string.IsNullOrWhiteSpace(migrate))
        startInfo.Environment["TRACESOUL2_MIGRATE_DLL"] = migrate;
    Process.Start(startInfo);
    _ = Task.Run(async () =>
    {
        await Task.Delay(500);
        Environment.Exit(0);
    });
}

// 控制台端口 + OneBot 反向端口统一由代码配置（Kestrel 规则：出现显式 Listen 后 UseUrls 不再生效）。
static void ConfigureListenUrls(KestrelServerOptions options, string urls)
{
    foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var uri = new Uri(raw.Trim());
        switch (uri.Host)
        {
            case "0.0.0.0":
            case "*":
            case "+":
                options.ListenAnyIP(uri.Port);
                break;
            case "localhost":
                options.ListenLocalhost(uri.Port);
                break;
            default:
                options.Listen(IPAddress.Parse(uri.Host), uri.Port);
                break;
        }
    }
}

// OneBot 反向端口被占用时跳过而不是整机崩溃（改端口在控制台保存后重启即可）。
static bool PortFree(int port)
{
    try
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch
    {
        return false;
    }
}

static bool IsLoopbackHost(string host)
{
    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
    IPAddress address;
    return IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address);
}

internal sealed class BackgroundMomentWorker : BackgroundService
{
    private readonly SoulRuntime runtime;
    public BackgroundMomentWorker(SoulRuntime runtime) { this.runtime = runtime; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await runtime.PollBackgroundAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) { runtime.Emit("后台轮询失败：" + exception.Message); }
            await Task.Delay(2000, stoppingToken);
        }
    }
}

internal sealed class PairWrite
{
    public string username { get; set; }
    public string assname { get; set; }
}

internal sealed class CardWrite { public string body { get; set; } }
internal sealed class EnabledWrite { public bool enabled { get; set; } }
internal sealed class PluginConfigWrite
{
    public Dictionary<string, JsonElement> values { get; set; }
}
internal sealed class UpdateConfigWrite { public string repository { get; set; } }
internal sealed class MomentWrite { public string content { get; set; } }
internal sealed class LimitWrite { public int limit { get; set; } }
internal sealed class HeartbeatWrite { public int minMinutes { get; set; } public int maxMinutes { get; set; } }
internal sealed class NerveWrite { public int top_k { get; set; } public string provider_id { get; set; } }
internal sealed class DailyRunWrite { public string day { get; set; } }
internal sealed class SelectWrite { public string id { get; set; } public string model { get; set; } }
internal sealed class ForkWrite { public string fromDay { get; set; } public string note { get; set; } public bool freshMemory { get; set; } }
internal sealed class DatabaseSwitchRequest { public string nameOrPath { get; set; } }
internal sealed class DatabaseCreateRequest { public string name { get; set; } }

internal sealed class MouthsWrite
{
    public string scene { get; set; }
    public string activeBody { get; set; }
    public MouthWriteItem[] items { get; set; }
}
internal sealed class MouthWriteItem
{
    public string id { get; set; }
    public int priority { get; set; }
    public int score { get; set; }
}

internal sealed class LifeStateWrite
{
    public string location { get; set; }
    public string activity { get; set; }
    public string activityDetail { get; set; }
    public string source { get; set; }
    public string sourceId { get; set; }
    public bool force { get; set; } = true;
}

internal sealed class OneBotConfigWrite
{
    public bool enabled { get; set; } = true;
    public string mode { get; set; } = "reverse";
    public int listen_port { get; set; } = 9021;
    public string ws_url { get; set; }
    public string http_url { get; set; }
    public string access_token { get; set; }
    public string self_id { get; set; }
    public bool reply_enabled { get; set; } = true;
    public string napcat_path { get; set; }
}

internal sealed class AddProviderWrite
{
    public string templateKey { get; set; }
    public string id { get; set; }
}

internal sealed class ProviderWrite
{
    public string type { get; set; }
    public string displayName { get; set; }
    public string baseUrl { get; set; }
    public string model { get; set; }
    public string apiKey { get; set; }
    public float temperature { get; set; }
    public float topP { get; set; }
    public int maxTokens { get; set; }
    public int timeout { get; set; }
    public string proxy { get; set; }
    public bool thinkingEnabled { get; set; }
    public string reasoningEffort { get; set; }
}

internal sealed class ModelWrite
{
    public string id { get; set; }
    public bool? enabled { get; set; }
    public List<string> roles { get; set; }
}

internal sealed class SlotRefWrite
{
    public string providerId { get; set; }
    public string model { get; set; }
}

internal sealed class SlotBundleWrite
{
    public SlotRefWrite chat { get; set; }
    public SlotRefWrite thinking { get; set; }
    public SlotRefWrite review { get; set; }
    public SlotRefWrite multimodal { get; set; }
    public SlotRefWrite image { get; set; }
    public SlotRefWrite speech { get; set; }
}

static class ProviderSlotApi
{
    public static void Apply(SoulRuntime runtime, string slot, SlotRefWrite value)
    {
        if (value == null) return;
        runtime.Providers.SetSlot(slot, value.providerId, value.model);
    }
}
