using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Skapihub.Core;

public sealed record GatewayOptions(string DataDirectory, string WebRoot, int? Port = null, string? AdminToken = null, ISettingsCodec? Codec = null);

public sealed class GatewayHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _client;
    public ConfigurationStore Configuration { get; }
    public UsageStore Usage { get; }
    public ModelDiscovery Discovery { get; }
    public UiAssets Ui { get; }
    public string AdminToken { get; }
    public Uri Address { get; private set; } = null!;

    private GatewayHost(WebApplication app, ConfigurationStore configuration, UsageStore usage, HttpClient client, string token)
    {
        _app = app; Configuration = configuration; Usage = usage; _client = client; AdminToken = token;
        Ui = new UiAssets(app.Environment.WebRootPath);
        Discovery = new ModelDiscovery(configuration, client);
    }

    public static async Task<GatewayHost> StartAsync(GatewayOptions options, CancellationToken cancellationToken = default)
    {
        var configuration = new ConfigurationStore(options.DataDirectory, options.Codec);
        var usage = new UsageStore(options.DataDirectory);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], WebRootPath = Directory.Exists(options.WebRoot) ? Path.GetFullPath(options.WebRoot) : null });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, options.Port ?? configuration.Current.Port);
            server.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
            server.AddServerHeader = false;
        });
        var app = builder.Build();
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 64
        }) { Timeout = Timeout.InfiniteTimeSpan };
        var host = new GatewayHost(app, configuration, usage, client, options.AdminToken ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        host.MapRoutes();
        try
        {
            await app.StartAsync(cancellationToken);
            var server = app.Services.GetRequiredService<IServer>();
            host.Address = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.First());
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }

    private void MapRoutes()
    {
        _app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
                || context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
            { context.Response.StatusCode = 403; return; }
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (context.Request.Path.StartsWithSegments("/_app"))
            {
                var token = context.Request.Headers["X-Skapihub-Admin"].FirstOrDefault() ?? context.Request.Cookies["skapihub-session"] ?? "";
                if (!ProxyGateway.KeyMatches(token, AdminToken)) { context.Response.StatusCode = 403; return; }
                var origin = context.Request.Headers.Origin.FirstOrDefault();
                if (origin is not null && origin != context.Request.Scheme + "://" + context.Request.Host.Value)
                { context.Response.StatusCode = 403; return; }
                context.Response.Headers.CacheControl = "no-store";
            }
            else if (!context.Request.Path.StartsWithSegments("/v1"))
            {
                context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.Expires = "0";
                context.Response.Headers["X-Skapihub-Ui-Build"] = Ui.BuildId;
                context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            }
            try { await next(context); }
            catch (Exception ex) when (!context.Response.HasStarted && ex is ArgumentException or KeyNotFoundException or InvalidDataException)
            {
                context.Response.StatusCode = ex is KeyNotFoundException ? 404 : 400;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
            catch (Exception ex) when (!context.Response.HasStarted && ex is IOException or UnauthorizedAccessException)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { error = "保存失败，请检查数据目录权限或磁盘空间。" });
            }
        });
        _app.Use(async (context, next) =>
        {
            if ((context.Request.Path == "/" || context.Request.Path == "/index.html") && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.WriteAsync(Ui.Html, context.RequestAborted);
                return;
            }
            await next(context);
        });
        _app.Use(async (context, next) =>
        {
            if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) && Ui.TryGet(context.Request.Path.Value ?? "", out var bytes, out var contentType))
            {
                context.Response.ContentType = contentType;
                context.Response.ContentLength = bytes.Length;
                if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                return;
            }
            await next(context);
        });
        var proxy = new ProxyGateway(Configuration, Usage, _client);
        _app.MapGet("/v1/models", proxy.ModelsAsync);
        _app.MapPost("/v1/{**endpoint}", (HttpContext context, string endpoint) => proxy.ForwardAsync(context, endpoint));
        _app.MapGet("/_app/notices", () => Results.Text(UiAssets.Notices, "text/plain; charset=utf-8"));
        _app.MapGet("/_app/state", () => Results.Json(State(), JsonDefaults.Options));
        _app.MapGet("/_app/usage", () => Results.Json(new { snapshot = Usage.Snapshot(), persistenceError = Usage.PersistenceError }, JsonDefaults.Options));
        _app.MapGet("/_app/relays/{relayId}", (string relayId) => Results.Json(FindRelay(relayId), JsonDefaults.Options));
        _app.MapPost("/_app/relays", async (RelayInput input) =>
        {
            var relay = CreateRelay(input);
            await Configuration.UpdateAsync(current => current with { Relays = [.. current.Relays, relay] });
            return Results.Json(new { id = relay.Id });
        });
        _app.MapPut("/_app/relays/{relayId}", async (string relayId, RelayInput input) =>
        {
            await Configuration.UpdateAsync(current =>
            {
                var existing = current.Relays.FirstOrDefault(x => x.Id == relayId) ?? throw new KeyNotFoundException("中转不存在。");
                var updated = CreateRelay(input, existing);
                return current with { Relays = current.Relays.Select(x => x.Id == relayId ? updated : x).ToList() };
            });
            return Results.Ok();
        });
        _app.MapDelete("/_app/relays/{relayId}", async (string relayId) =>
        {
            FindRelay(relayId);
            await Configuration.UpdateAsync(current => current with { Relays = current.Relays.Where(x => x.Id != relayId).ToList() });
            return Results.Ok();
        });
        _app.MapPut("/_app/relays/{relayId}/enabled", async (string relayId, ToggleInput input) =>
        {
            await UpdateRelayAsync(relayId, relay => relay with { Enabled = input.Enabled });
            return Results.Ok();
        });
        _app.MapPut("/_app/relays/{relayId}/groups/{groupId}/enabled", async (string relayId, string groupId, ToggleInput input) =>
        {
            await UpdateGroupAsync(relayId, groupId, group => group with { Enabled = input.Enabled });
            return Results.Ok();
        });
        _app.MapPut("/_app/relays/{relayId}/groups/{groupId}/models/enabled", async (string relayId, string groupId, ModelToggleInput input) =>
        {
            await UpdateGroupAsync(relayId, groupId, group => group with { Models = group.Models.Select(model => input.Models.Contains(model.Id, StringComparer.Ordinal) ? model with { Enabled = input.Enabled } : model).ToList() });
            return Results.Ok();
        });
        _app.MapPost("/_app/relays/{relayId}/groups/{groupId}/models", async (string relayId, string groupId, ManualModelsInput input) =>
        {
            var names = input.Models.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length == 0 || names.Any(x => x.Length > 512)) throw new ArgumentException("请填写有效的模型 ID。");
            await UpdateGroupAsync(relayId, groupId, group => group with { Models = group.Models.Concat(names.Where(id => !group.Models.Any(x => x.Id == id)).Select(id => new ModelEntry(id))).OrderBy(x => x.Id, StringComparer.Ordinal).ToList() });
            return Results.Ok();
        });
        _app.MapPost("/_app/relays/{relayId}/groups/{groupId}/models/remove", async (string relayId, string groupId, ManualModelsInput input) =>
        {
            await UpdateGroupAsync(relayId, groupId, group => group with { Models = group.Models.Where(x => !input.Models.Contains(x.Id, StringComparer.Ordinal)).ToList() });
            return Results.Ok();
        });
        _app.MapPost("/_app/relays/{relayId}/sync", async (string relayId, HttpContext context) =>
        {
            await Discovery.SyncAsync(relayId, null, context.RequestAborted);
            return Results.Ok();
        });
        _app.MapPost("/_app/relays/{relayId}/groups/{groupId}/sync", async (string relayId, string groupId, HttpContext context) =>
        {
            await Discovery.SyncAsync(relayId, groupId, context.RequestAborted);
            return Results.Ok();
        });
        _app.MapPut("/_app/settings", async (SettingsInput input) =>
        {
            await Configuration.UpdateAsync(current => current with { Port = input.Port, ApiKey = input.ApiKey });
            return Results.Json(new { restartRequired = input.Port != Address.Port });
        });
        _app.MapPut("/_app/enabled", async (ToggleInput input) =>
        {
            await Configuration.UpdateAsync(current => current with { Enabled = input.Enabled });
            return Results.Ok();
        });
    }

    private object State()
    {
        var config = Configuration.Current;
        return new
        {
            name = "skapihub", version = "1.1.0", uiBuildId = Ui.BuildId, url = new Uri(Address, "/v1").ToString(), port = config.Port,
            boundPort = Address.Port, apiKey = config.ApiKey, enabled = config.Enabled,
            restartRequired = config.Port != Address.Port, dataDirectory = Configuration.DirectoryPath,
            relays = config.Relays.Select(relay => new
            {
                relay.Id, relay.Name, relay.Url, relay.Enabled,
                groups = relay.Groups.Select(group => new
                {
                    group.Id, group.Name, group.Enabled,
                    models = group.Models.Select(model => new { model.Id, model.Enabled, routeId = ModelCatalog.RouteId(config, relay, group, model.Id) }),
                    group.SyncedAt, group.SyncError,
                    keyPreview = group.Key.Length <= 8 ? new string('•', group.Key.Length) : group.Key[..4] + "••••" + group.Key[^4..]
                })
            }),
            totals = Usage.Snapshot().Total,
            persistenceError = Usage.PersistenceError
        };
    }

    private Relay FindRelay(string id) => Configuration.Current.Relays.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("中转不存在。");
    private Task UpdateRelayAsync(string relayId, Func<Relay, Relay> update) => Configuration.UpdateAsync(current =>
    {
        if (!current.Relays.Any(x => x.Id == relayId)) throw new KeyNotFoundException("中转不存在。");
        return current with { Relays = current.Relays.Select(x => x.Id == relayId ? update(x) : x).ToList() };
    });
    private Task UpdateGroupAsync(string relayId, string groupId, Func<KeyGroup, KeyGroup> update) => UpdateRelayAsync(relayId, relay =>
    {
        if (!relay.Groups.Any(x => x.Id == groupId)) throw new KeyNotFoundException("分组不存在。");
        return relay with { Groups = relay.Groups.Select(group => group.Id == groupId ? update(group) : group).ToList() };
    });

    private static Relay CreateRelay(RelayInput input, Relay? existing = null)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("请填写中转名称。");
        if (input.Groups is null || input.Groups.Count == 0) throw new ArgumentException("至少添加一个 Key 分组。");
        var normalized = ConfigurationStore.NormalizeUrl(input.Url);
        var usedIds = new HashSet<string>();
        var groups = input.Groups.Select((row, index) =>
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Key) || row.Key.Contains('\r') || row.Key.Contains('\n')) throw new ArgumentException("请填写分组 Key，每行一个。");
            var previous = existing?.Groups.FirstOrDefault(x => x.Id == row.Id);
            if (row.Id is not null && previous is null) throw new ArgumentException("分组不存在，请重新打开编辑。");
            var group = previous ?? new KeyGroup();
            if (!usedIds.Add(group.Id)) throw new ArgumentException("分组重复。");
            return group with
            {
                Name = string.IsNullOrWhiteSpace(row.Name) ? "分组 " + (index + 1) : row.Name.Trim(), Key = row.Key.Trim(),
                Models = previous is not null && (previous.Key != row.Key.Trim() || existing!.Url != normalized) ? [] : group.Models,
                SyncedAt = previous is not null && (previous.Key != row.Key.Trim() || existing!.Url != normalized) ? null : group.SyncedAt,
                SyncError = null
            };
        }).ToList();
        return (existing ?? new Relay()) with { Name = input.Name.Trim(), Url = normalized, Groups = groups };
    }

    public async ValueTask DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await _app.StopAsync(stop.Token); }
        catch (OperationCanceledException) { }
        await _app.DisposeAsync();
        _client.Dispose();
        await Usage.DisposeAsync();
    }
}
