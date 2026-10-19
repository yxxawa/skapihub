using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Skapihub.Core;

public sealed class ProxyGateway(ConfigurationStore configuration, UsageStore usage, HttpClient client)
{
    private readonly ConcurrentDictionary<string, long> _roundRobin = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldown = new();
    private static readonly HashSet<string> Endpoints = new(StringComparer.Ordinal)
    { "chat/completions", "completions", "responses", "responses/compact", "messages", "messages/count_tokens", "embeddings", "rerank", "images/generations" };

    public static bool KeyMatches(string provided, string expected) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private bool Authorized(HttpContext context)
    {
        var expected = configuration.Current.ApiKey;
        if (expected.Length == 0) return true;
        var auth = context.Request.Headers.Authorization.ToString();
        var key = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : context.Request.Headers["x-api-key"].ToString();
        return KeyMatches(key, expected);
    }

    public async Task ModelsAsync(HttpContext context)
    {
        if (!Authorized(context)) { await ErrorAsync(context, 401, "API Key 不正确。", "invalid_api_key"); return; }
        if (!configuration.Current.Enabled) { await ErrorAsync(context, 503, "中转服务已暂停。", "gateway_paused"); return; }
        var ids = configuration.Current.Relays.Where(x => x.Enabled).SelectMany(x => x.Groups).Where(x => x.Enabled)
            .SelectMany(x => x.Models).Where(x => x.Enabled).Select(x => x.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        await context.Response.WriteAsJsonAsync(new { @object = "list", data = ids.Select(x => new { id = x, @object = "model", created = 0, owned_by = "skapihub" }) }, context.RequestAborted);
    }

    public async Task ForwardAsync(HttpContext context, string endpoint)
    {
        var watch = Stopwatch.StartNew();
        if (!Authorized(context)) { await ErrorAsync(context, 401, "API Key 不正确。", "invalid_api_key"); return; }
        var config = configuration.Current;
        if (!config.Enabled) { await ErrorAsync(context, 503, "中转服务已暂停。", "gateway_paused"); return; }
        if (!Endpoints.Contains(endpoint)) { await ErrorAsync(context, 404, "该接口未启用。", "unsupported_endpoint"); return; }
        if (!(context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
        { await ErrorAsync(context, 415, "请使用 application/json 请求。", "invalid_content_type"); return; }

        byte[] body;
        byte[] originalBody;
        var addedUsageOption = false;
        string model;
        try
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            body = buffer.ToArray();
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("model", out var element)
                || element.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(model = element.GetString()!))
            { await ErrorAsync(context, 400, "请指定 model。", "invalid_request_error"); return; }
            originalBody = body;
            if (endpoint == "chat/completions" && document.RootElement.TryGetProperty("stream", out var streaming) && streaming.ValueKind == JsonValueKind.True)
            {
                var node = JsonNode.Parse(body)!.AsObject();
                if (node["stream_options"] is null or JsonObject)
                {
                    var streamOptions = node["stream_options"] as JsonObject ?? new JsonObject();
                    if (!streamOptions.ContainsKey("include_usage"))
                    {
                        streamOptions["include_usage"] = true;
                        node["stream_options"] = streamOptions;
                        body = JsonSerializer.SerializeToUtf8Bytes(node);
                        addedUsageOption = true;
                    }
                }
            }
        }
        catch (JsonException) { await ErrorAsync(context, 400, "JSON 格式无效。", "invalid_request_error"); return; }
        var candidates = config.Relays.Where(x => x.Enabled).SelectMany(relay => relay.Groups
            .Where(group => group.Enabled && group.Models.Any(m => m.Enabled && m.Id == model)).Select(group => new RouteCandidate(relay, group))).ToArray();
        if (candidates.Length == 0) { await ErrorAsync(context, 404, "没有已启用的模型：" + model, "model_not_found"); return; }
        var sequence = _roundRobin.AddOrUpdate(model, 0, (_, value) => unchecked(value + 1));
        var offset = (int)((ulong)sequence % (uint)candidates.Length);
        var ordered = Enumerable.Range(0, candidates.Length).Select(i => candidates[(i + offset) % candidates.Length])
            .OrderBy(x => _cooldown.TryGetValue(x.Group.Id, out var until) && until > DateTimeOffset.UtcNow ? 1 : 0).ToArray();
        string? failure = null;
        var failureStatus = 502;
        RouteCandidate? last = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));

        foreach (var candidate in ordered)
        {
            last = candidate;
            if (context.RequestAborted.IsCancellationRequested) return;
            using var request = new HttpRequestMessage(HttpMethod.Post, UpstreamAddress.Endpoint(candidate.Relay.Url, endpoint, context.Request.QueryString.Value ?? ""));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + candidate.Group.Key);
            if (endpoint.StartsWith("messages", StringComparison.Ordinal))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", candidate.Group.Key);
                request.Headers.TryAddWithoutValidation("anthropic-version", context.Request.Headers["anthropic-version"].FirstOrDefault() ?? "2023-06-01");
            }
            foreach (var header in new[] { "accept", "anthropic-beta", "openai-beta" })
                if (context.Request.Headers.TryGetValue(header, out var values)) request.Headers.TryAddWithoutValidation(header, values.ToArray());
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            headersTimeout.CancelAfter(TimeSpan.FromSeconds(90));
            HttpResponseMessage? upstream = null;
            try
            {
                upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
                if (addedUsageOption && upstream.StatusCode == HttpStatusCode.BadRequest && upstream.Content.Headers.ContentLength is null or <= 1024 * 1024)
                {
                    await upstream.Content.LoadIntoBufferAsync(1024 * 1024, headersTimeout.Token);
                    var errorText = await upstream.Content.ReadAsStringAsync(headersTimeout.Token);
                    if (errorText.Contains("stream_options", StringComparison.OrdinalIgnoreCase) || errorText.Contains("include_usage", StringComparison.OrdinalIgnoreCase))
                    {
                        upstream.Dispose();
                        using var fallback = new HttpRequestMessage(HttpMethod.Post, request.RequestUri) { Content = new ByteArrayContent(originalBody) };
                        foreach (var header in request.Headers) fallback.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        fallback.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                        upstream = await client.SendAsync(fallback, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                upstream?.Dispose();
                if (context.RequestAborted.IsCancellationRequested) { Record(last, 499, "客户端取消。"); return; }
                failure = ex is OperationCanceledException ? "上游响应超时。" : "无法连接上游。";
                failureStatus = ex is OperationCanceledException ? 504 : 502;
                _cooldown[candidate.Group.Id] = DateTimeOffset.UtcNow.AddSeconds(15);
                continue;
            }
            using (upstream)
            {
                var status = (int)upstream.StatusCode;
                if (status is 401 or 403 or 404 or 408 or 429 or >= 500 && candidate != ordered[^1])
                {
                    failure = "上游请求失败 · HTTP " + status;
                    failureStatus = status;
                    _cooldown[candidate.Group.Id] = DateTimeOffset.UtcNow.AddSeconds(15);
                    continue;
                }
                if (upstream.IsSuccessStatusCode) _cooldown.TryRemove(candidate.Group.Id, out _);
                var isStream = upstream.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
                var observer = new ResponseObserver(isStream, watch);
                var recordedStatus = status;
                string? recordedError = status >= 400 ? "上游请求失败 · HTTP " + status : null;
                try
                {
                    await using var stream = await upstream.Content.ReadAsStreamAsync(deadline.Token);
                    var bytes = new byte[16 * 1024];
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(120));
                    var read = await stream.ReadAsync(bytes, idle.Token);
                    if (read == 0 && upstream.IsSuccessStatusCode)
                    {
                        failure = "上游返回空响应。"; failureStatus = 502;
                        _cooldown[candidate.Group.Id] = DateTimeOffset.UtcNow.AddSeconds(15);
                        continue;
                    }
                    context.Response.StatusCode = status;
                    context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                    context.Response.Headers.CacheControl = "no-store";
                    if (isStream) context.Response.Headers["X-Accel-Buffering"] = "no";
                    foreach (var header in new[] { "retry-after", "x-request-id", "request-id" })
                        if (upstream.Headers.TryGetValues(header, out var values)) context.Response.Headers[header] = string.Join(", ", values);
                    while (read > 0)
                    {
                        if (upstream.IsSuccessStatusCode) observer.Feed(bytes.AsSpan(0, read));
                        await context.Response.Body.WriteAsync(bytes.AsMemory(0, read), deadline.Token);
                        await context.Response.Body.FlushAsync(deadline.Token);
                        idle.CancelAfter(TimeSpan.FromSeconds(120));
                        read = await stream.ReadAsync(bytes, idle.Token);
                    }
                    observer.Finish();
                    if (upstream.IsSuccessStatusCode && (observer.Error is not null || isStream && !observer.TerminalSeen))
                    { recordedStatus = 502; recordedError = observer.Error ?? "上游流提前结束。"; }
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
                {
                    if (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
                    {
                        failure = "上游读取超时或连接中断。"; failureStatus = 504;
                        _cooldown[candidate.Group.Id] = DateTimeOffset.UtcNow.AddSeconds(15);
                        continue;
                    }
                    observer.Finish();
                    recordedStatus = context.RequestAborted.IsCancellationRequested ? 499 : 502;
                    recordedError = recordedStatus == 499 ? "客户端取消。" : "上游流中断。";
                    context.Abort();
                }
                usage.Record(new RequestUsage
                {
                    Model = model, RelayId = candidate.Relay.Id, RelayName = candidate.Relay.Name,
                    GroupId = candidate.Group.Id, GroupName = candidate.Group.Name, Endpoint = endpoint,
                    Status = recordedStatus, FirstTokenMs = observer.FirstTokenMs,
                    InputTokens = observer.InputTokens, OutputTokens = observer.OutputTokens,
                    TotalTokens = observer.TotalTokens, Error = recordedError
                });
                return;
            }
        }
        Record(last, failureStatus, failure);
        if (!context.RequestAborted.IsCancellationRequested) await ErrorAsync(context, failureStatus, failure ?? "所有分组暂不可用。", "upstream_error");

        void Record(RouteCandidate? candidate, int status, string? error) => usage.Record(new RequestUsage
        {
            Model = model, RelayId = candidate?.Relay.Id ?? "", RelayName = candidate?.Relay.Name ?? "",
            GroupId = candidate?.Group.Id ?? "", GroupName = candidate?.Group.Name ?? "", Endpoint = endpoint, Status = status, Error = error
        });
    }

    public static Task ErrorAsync(HttpContext context, int status, string message, string type)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = new { message, type, code = type } }, context.RequestAborted);
    }
}
