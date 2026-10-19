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
        var config = configuration.Current;
        if (!config.Enabled) { await ErrorAsync(context, 503, "中转服务已暂停。", "gateway_paused"); return; }
        var models = ModelCatalog.Enabled(config).OrderBy(model => model.Id, StringComparer.Ordinal).Select(model => new
        {
            id = model.Id, @object = "model", created = 0, owned_by = model.Relay.Name + "/" + model.Group.Name,
            upstream_model = model.UpstreamId, relay_id = model.Relay.Id, relay_name = model.Relay.Name,
            group_id = model.Group.Id, group_name = model.Group.Name
        });
        await context.Response.WriteAsJsonAsync(new { @object = "list", data = models }, context.RequestAborted);
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

        JsonObject input;
        string routeId;
        try
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            var node = JsonNode.Parse(buffer.ToArray());
            if (node is not JsonObject obj || obj["model"] is not JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrEmpty(name))
            { await ErrorAsync(context, 400, "请指定 model。", "invalid_request_error"); return; }
            input = obj; routeId = name;
        }
        catch (JsonException) { await ErrorAsync(context, 400, "JSON 格式无效。", "invalid_request_error"); return; }
        var selected = ModelCatalog.Enabled(config).FirstOrDefault(model => model.Id == routeId);
        if (selected is null)
        {
            await ErrorAsync(context, 404, "没有已启用的模型：" + routeId + "。请使用 /v1/models 中包含中转名和分组名的完整模型 ID。", "model_not_found");
            return;
        }
        input["model"] = selected.UpstreamId;
        var originalBody = JsonSerializer.SerializeToUtf8Bytes(input);
        var addedUsageOption = false;
        if (endpoint == "chat/completions" && input["stream"] is JsonValue streaming && streaming.TryGetValue<bool>(out var streamEnabled) && streamEnabled)
        {
            if (input["stream_options"] is null or JsonObject)
            {
                var options = input["stream_options"] as JsonObject ?? new JsonObject();
                if (!options.ContainsKey("include_usage"))
                {
                    options["include_usage"] = true; input["stream_options"] = options; addedUsageOption = true;
                }
            }
        }
        var body = addedUsageOption ? JsonSerializer.SerializeToUtf8Bytes(input) : originalBody;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, UpstreamAddress.Endpoint(selected.Relay.Url, endpoint, context.Request.QueryString.Value ?? ""));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + selected.Group.Key);
        if (endpoint.StartsWith("messages", StringComparison.Ordinal))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", selected.Group.Key);
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
            var status = context.RequestAborted.IsCancellationRequested ? 499 : ex is OperationCanceledException ? 504 : 502;
            var message = status == 499 ? "客户端取消。" : status == 504 ? "上游响应超时。" : "无法连接此分组的上游。";
            Record(status, message);
            if (status != 499) await ErrorAsync(context, status, message, "upstream_error");
            return;
        }
        using (upstream)
        {
            var status = (int)upstream.StatusCode;
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
                    observer.Finish(); Record(502, "此分组的上游返回空响应。");
                    await ErrorAsync(context, 502, "此分组的上游返回空响应。", "upstream_error"); return;
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
                observer.Finish();
                recordedStatus = context.RequestAborted.IsCancellationRequested ? 499 : ex is OperationCanceledException ? 504 : 502;
                recordedError = recordedStatus == 499 ? "客户端取消。" : "此分组的上游读取超时或连接中断。";
                if (context.Response.HasStarted) context.Abort();
                else if (recordedStatus != 499) await ErrorAsync(context, recordedStatus, recordedError, "upstream_error");
            }
            Record(recordedStatus, recordedError, observer);
        }

        void Record(int status, string? error, ResponseObserver? observer = null) => usage.Record(new RequestUsage
        {
            Model = selected.UpstreamId, RouteId = selected.Id, RelayId = selected.Relay.Id, RelayName = selected.Relay.Name,
            GroupId = selected.Group.Id, GroupName = selected.Group.Name, Endpoint = endpoint, Status = status, Error = error,
            FirstTokenMs = observer?.FirstTokenMs, InputTokens = observer?.InputTokens,
            OutputTokens = observer?.OutputTokens, TotalTokens = observer?.TotalTokens
        });
    }

    public static Task ErrorAsync(HttpContext context, int status, string message, string type)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = new { message, type, code = type } }, context.RequestAborted);
    }
}
