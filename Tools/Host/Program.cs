using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
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
var trustContainerProxy = string.Equals(
    Environment.GetEnvironmentVariable("TRACESOUL2_TRUST_CONTAINER_PROXY"),
    "1", StringComparison.Ordinal);
var publicHosts = ParsePublicHosts(
    Environment.GetEnvironmentVariable("TRACESOUL2_PUBLIC_HOSTS"));

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
    {
        if (trustContainerProxy) options.ListenAnyIP(onebotListenPort);
        else options.ListenLocalhost(onebotListenPort);
    }
    else if (onebotListenPort > 0)
        Console.WriteLine("OneBot 反向监听端口 " + onebotListenPort + " 被占用，已跳过（可在控制台改端口后保存重启）。");
});
builder.Services.AddSingleton(new SoulRuntime(
    dataDir, home.PluginsDirectory, home.PluginsDataDirectory));
builder.Services.AddSingleton(new UpdateService(home));
builder.Services.AddSingleton(new DebugBranchService(dataDir));
builder.Services.AddSingleton(new DashboardAuthService(home.Root));
builder.Services.AddSingleton<DashboardLoginLimiter>();
var authKeyDirectory = Path.Combine(home.Root, "auth-keys");
Directory.CreateDirectory(authKeyDirectory);
RestrictDirectoryPermissions(authKeyDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(authKeyDirectory))
    .SetApplicationName("TraceSoul2.Control");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "tracesoul2_control";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = publicHosts.Count > 0
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsJsonAsync(new { error = "请先登录 TraceSoul2 控制台。" });
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new { error = "当前账号无权执行此操作。" });
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var auth = context.HttpContext.RequestServices
                .GetRequiredService<DashboardAuthService>();
            var snapshot = auth.Snapshot();
            var stamp = context.Principal?.FindFirst("session_stamp")?.Value;
            if (!string.Equals(stamp, snapshot.SessionStamp, StringComparison.Ordinal))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddHostedService<BackgroundMomentWorker>();
builder.Services.AddSingleton<DailyPipelineWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DailyPipelineWorker>());

var app = builder.Build();
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    if (remote != null && !IPAddress.IsLoopback(remote) &&
        !(trustContainerProxy && IsPrivateNetwork(remote)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "TraceSoul2 控制台只接受本机连接。" });
        return;
    }

    var requestHost = NormalizeHost(context.Request.Host.Host);
    var publicRequest = publicHosts.Contains(requestHost);
    if (!IsLoopbackHost(requestHost) && !publicRequest)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "无效的控制台 Host。" });
        return;
    }

    var trustedProxy = trustContainerProxy &&
                       (remote == null || IPAddress.IsLoopback(remote) || IsPrivateNetwork(remote));
    var effectiveScheme = context.Request.Scheme;
    if (trustedProxy)
    {
        var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].ToString()
            .Split(',')[0].Trim();
        if (string.Equals(forwardedProto, "http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase))
            effectiveScheme = forwardedProto;
    }
    if (publicRequest && !string.Equals(effectiveScheme, "https", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "公网控制台必须通过 HTTPS 反向代理访问。" });
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
                          (string.Equals(effectiveScheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out originUri) ||
            !string.Equals(originUri.Scheme, effectiveScheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(NormalizeHost(originUri.Host), requestHost, StringComparison.OrdinalIgnoreCase) ||
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
        inner.Idle,
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

app.MapGet("/plugins", (SoulRuntime runtime) =>
{
    var catalog = runtime.Plugins.GetRegisteredCatalog();
    var payload = runtime.Plugins.GetPlugins().Select(plugin =>
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
            // 器官休眠 = 开关还开着，但所属平台不在/未连接；界面据此提示「身体不在」。
            dormant = runtime.Plugins.IsOrganDormant(plugin),
            configurable,
            folder = pack == null ? string.Empty : pack.Folder,
            contributions = catalog.Where(x => x.PluginId == plugin.Id &&
                                               !MouthLogic.IsProtocolFacet(x.Id) &&
                                               x.Kind == TraceContributionKindValues.Effector)
                .Select(x => new { x.Id, x.Kind, x.DisplayName, x.Organ, x.BodyId })
        };
    }).ToList();
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
app.MapGet("/plugins/external", (SoulRuntime runtime) =>
    Results.Json(runtime.ExternalPluginStatus()));

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

app.MapGet("/plugins/{id}/config", (SoulRuntime runtime, string id) =>
{
    try { return Results.Json(runtime.ReadPluginConfig(id)); }
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
        transientRetries = body.transientRetries ?? -1,
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
    var max = body.max > 0 || body.align > 0 ? body.max : body.limit;
    return Results.Json(runtime.SetHistoryWindow(max, body.align));
});

app.MapPut("/settings/heartbeat", (SoulRuntime runtime, HeartbeatWrite body) =>
{
    return Results.Json(runtime.SetHeartbeatRange(body.minMinutes, body.maxMinutes));
});

app.MapGet("/memory/nerve", (SoulRuntime runtime) => Results.Json(runtime.NerveStatus()));

app.MapGet("/memory/ladder", (SoulRuntime runtime) => Results.Json(runtime.LadderStatus()));

app.MapGet("/memory/day-trajectory", (SoulRuntime runtime) => Results.Json(runtime.DayTrajectoryStatus()));

app.MapGet("/platforms", (SoulRuntime runtime) =>
    Results.Json(runtime.PlatformStatus()));

app.MapGet("/mouths", (SoulRuntime runtime) =>
    Results.Json(runtime.MouthStatus()));

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

// 插件声明的 WebSocket 入口（OneBot 反向 WS、游戏桥接等）。
// 每次握手都从 Runtime 取当前实例：外部插件重扫、配置保存或新安装后无需重启宿主。
app.UseWebSockets();
app.Use(async (HttpContext context, RequestDelegate next) =>
{
    var runtime = context.RequestServices.GetRequiredService<SoulRuntime>();
    var endpoint = runtime.FindWebSocketEndpoint(context.Request.Path.Value);
    if (endpoint == null)
    {
        await next(context);
        return;
    }
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

app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        context.Response.Headers.CacheControl = "no-store";
        var snapshot = context.RequestServices.GetRequiredService<DashboardAuthService>().Snapshot();
        var path = context.Request.Path.Value ?? string.Empty;
        var accountRoute = string.Equals(path, "/auth/account", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(path, "/auth/logout", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(path, "/auth/status", StringComparison.OrdinalIgnoreCase);
        if (snapshot.MustChangePassword && !accountRoute)
        {
            context.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
            await context.Response.WriteAsJsonAsync(new { error = "首次登录必须先修改管理员账号和密码。" });
            return;
        }
    }
    await next();
});

app.MapGet("/auth/status", (HttpContext context, DashboardAuthService auth) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var snapshot = auth.Snapshot();
    var authenticated = context.User.Identity?.IsAuthenticated == true;
    return Results.Json(new
    {
        authenticated,
        username = authenticated ? snapshot.Username : string.Empty,
        mustChangePassword = authenticated && snapshot.MustChangePassword
    });
}).AllowAnonymous();

app.MapPost("/auth/login", async (
    HttpContext context,
    DashboardAuthService auth,
    DashboardLoginLimiter limiter,
    DashboardLoginWrite body) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var clientKey = DashboardClientKey(context, trustContainerProxy);
    var blockedFor = limiter.BlockedFor(clientKey);
    if (blockedFor > TimeSpan.Zero)
    {
        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(blockedFor.TotalSeconds)).ToString();
        return Results.Json(
            new { error = "登录失败次数过多，请稍后再试。" },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (body == null || !auth.Verify(body.username, body.password))
    {
        limiter.RegisterFailure(clientKey);
        await Task.Delay(1500, context.RequestAborted);
        return Results.Json(
            new { error = "用户名或密码不正确。" },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    limiter.RegisterSuccess(clientKey);
    var snapshot = auth.Snapshot();
    await SignInDashboardAsync(context, snapshot);
    return Results.Json(new
    {
        authenticated = true,
        username = snapshot.Username,
        mustChangePassword = snapshot.MustChangePassword
    });
}).AllowAnonymous();

app.MapPost("/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Json(new { signedOut = true });
});

app.MapPut("/auth/account", async (
    HttpContext context,
    DashboardAuthService auth,
    DashboardAccountWrite body) =>
{
    if (body == null) return Results.BadRequest(new { error = "账号信息不能为空。" });
    if (!string.Equals(body.newPassword, body.confirmPassword, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "两次输入的新密码不一致。" });

    try
    {
        var snapshot = auth.ChangeAccount(body.currentPassword, body.username, body.newPassword);
        await SignInDashboardAsync(context, snapshot);
        return Results.Json(new
        {
            saved = true,
            username = snapshot.Username,
            mustChangePassword = snapshot.MustChangePassword
        });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

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

app.MapGet("/events", async (
    HttpContext context,
    SoulRuntime runtime,
    DashboardAuthService auth) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-store";
    var sessionStamp = context.User.FindFirst("session_stamp")?.Value;
    using var subscription = runtime.SubscribeEvents();
    try
    {
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { hello = "tracesoul2" }) + "\n\n");
        await context.Response.Body.FlushAsync();
        var reader = subscription.Reader;
        while (!context.RequestAborted.IsCancellationRequested)
        {
            if (!string.Equals(
                    sessionStamp,
                    auth.Snapshot().SessionStamp,
                    StringComparison.Ordinal))
                return;

            string line;
            using (var interval = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
            {
                interval.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    line = await reader.ReadAsync(interval.Token);
                }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": keepalive\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    continue;
                }
            }
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
    if (string.Equals(Environment.GetEnvironmentVariable("TRACESOUL2_RESTART_MODE"),
            "supervisor", StringComparison.OrdinalIgnoreCase))
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Environment.Exit(0);
        });
        return;
    }
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

static HashSet<string> ParsePublicHosts(string value)
{
    return new HashSet<string>(
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeHost)
            .Where(host => !string.IsNullOrWhiteSpace(host) && !host.Contains('/') && !host.Contains('\\')),
        StringComparer.OrdinalIgnoreCase);
}

static string NormalizeHost(string host)
{
    return (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
}

static string DashboardClientKey(HttpContext context, bool trustContainerProxy)
{
    var remote = context.Connection.RemoteIpAddress;
    if (trustContainerProxy && remote != null &&
        (IPAddress.IsLoopback(remote) || IsPrivateNetwork(remote)))
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString()
            .Split(',')[0].Trim();
        if (IPAddress.TryParse(forwarded, out var client)) return client.ToString();
    }
    return remote?.ToString() ?? "unknown";
}

static Task SignInDashboardAsync(HttpContext context, DashboardAuthSnapshot snapshot)
{
    var identity = new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.Name, snapshot.Username),
            new Claim("session_stamp", snapshot.SessionStamp)
        },
        CookieAuthenticationDefaults.AuthenticationScheme);
    return context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
        });
}

static void RestrictDirectoryPermissions(string directory)
{
    if (OperatingSystem.IsWindows()) return;
    File.SetUnixFileMode(
        directory,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

static bool IsPrivateNetwork(IPAddress address)
{
    if (address == null) return false;
    if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
    if (address.AddressFamily == AddressFamily.InterNetwork)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
    return address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal;
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
internal sealed class LimitWrite
{
    public int limit { get; set; }
    public int max { get; set; }
    public int align { get; set; }
}
internal sealed class HeartbeatWrite { public int minMinutes { get; set; } public int maxMinutes { get; set; } }
internal sealed class NerveWrite { public int top_k { get; set; } public string provider_id { get; set; } }
internal sealed class DailyRunWrite { public string day { get; set; } }
internal sealed class SelectWrite { public string id { get; set; } public string model { get; set; } }
internal sealed class ForkWrite { public string fromDay { get; set; } public string note { get; set; } public bool freshMemory { get; set; } }
internal sealed class DatabaseSwitchRequest { public string nameOrPath { get; set; } }
internal sealed class DatabaseCreateRequest { public string name { get; set; } }
internal sealed class DashboardLoginWrite
{
    public string username { get; set; }
    public string password { get; set; }
}
internal sealed class DashboardAccountWrite
{
    public string currentPassword { get; set; }
    public string username { get; set; }
    public string newPassword { get; set; }
    public string confirmPassword { get; set; }
}

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
    public int? transientRetries { get; set; }
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
