using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Skapihub.IntegrationTests;

internal sealed class MockUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Url { get; private set; } = "";
    public ConcurrentQueue<(string Key, string Endpoint, string Model, string Query)> Calls { get; } = new();
    public bool FailModels { get; set; }
    public int ModelDelayMs { get; set; }
    public bool RejectUsageOption { get; set; }
    public ConcurrentDictionary<string, int> FailureStatuses { get; } = new();
    private MockUpstream(WebApplication app) => _app = app;
    public static async Task<MockUpstream> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.ListenLocalhostFixed());
        var mock = new MockUpstream(builder.Build());
        mock._app.MapGet("/{**path}", mock.Models);
        mock._app.MapPost("/{**path}", mock.Respond);
        await mock._app.StartAsync();
        mock.Url = mock._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return mock;
    }
    private async Task Models(HttpContext context)
    {
        if (ModelDelayMs > 0) await Task.Delay(ModelDelayMs, context.RequestAborted);
        if (FailModels) { context.Response.StatusCode = 503; return; }
        await context.Response.WriteAsJsonAsync(new { data = new[] { "gpt-test", "claude-test", "responses-test", "embed-test", "vendor/model-a", "no-usage", "broken-stream" }.Select(id => new { id }) });
    }
    private async Task Respond(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body);
        var input = document.RootElement;
        var model = input.GetProperty("model").GetString()!;
        var key = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal);
        var endpoint = context.Request.Path.Value!;
        Calls.Enqueue((key, endpoint, model, context.Request.QueryString.Value ?? ""));
        if (RejectUsageOption && input.TryGetProperty("stream_options", out _)) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { message = "Unsupported parameter: stream_options" } }); return; }
        if (FailureStatuses.TryGetValue(key, out var failureStatus)) { context.Response.StatusCode = failureStatus; await context.Response.WriteAsJsonAsync(new { error = new { message = "group unavailable" } }); return; }
        if (key == "key-fail") { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = new { message = "unavailable" } }); return; }
        if (model == "invalid-request") { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { message = "invalid request" } }); return; }
        var stream = input.TryGetProperty("stream", out var streamElement) && streamElement.ValueKind == JsonValueKind.True;
        if (!stream)
        {
            if (model == "no-usage") { await context.Response.WriteAsJsonAsync(new { choices = new[] { new { message = new { content = "ok" } } } }); return; }
            if (endpoint.EndsWith("embeddings", StringComparison.Ordinal))
            { await context.Response.WriteAsJsonAsync(new { data = new[] { new { embedding = new[] { .1, .2 } } }, usage = new { prompt_tokens = 12, total_tokens = 12 } }); return; }
            await context.Response.WriteAsJsonAsync(new { id = "mock-completion", served_by = key == "key-a" ? "a" : "b", choices = new[] { new { message = new { role = "assistant", content = "你好" }, finish_reason = "stop" } }, usage = new { prompt_tokens = 12, completion_tokens = 5, total_tokens = 17 } }); return;
        }
        context.Response.ContentType = "text/event-stream";
        if (endpoint.EndsWith("messages", StringComparison.Ordinal))
        {
            if (context.Request.Headers["x-api-key"].ToString() != key || !context.Request.Headers.ContainsKey("anthropic-version")) { context.Response.StatusCode = 401; return; }
            await Emit("{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":11,\"output_tokens\":0}}}");
            await Task.Delay(65);
            await Emit("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"你好\"}}");
            await Emit("{\"type\":\"message_delta\",\"usage\":{\"output_tokens\":4}}");
            await Emit("{\"type\":\"message_stop\"}"); return;
        }
        if (endpoint.Contains("responses", StringComparison.Ordinal))
        {
            await Emit("{\"type\":\"response.created\",\"response\":{\"id\":\"resp_test\"}}");
            await Task.Delay(65);
            await Emit("{\"type\":\"response.output_text.delta\",\"delta\":\"你好\"}");
            await Emit("{\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":9,\"output_tokens\":6,\"total_tokens\":15}}}"); return;
        }
        await context.Response.WriteAsync(": heartbeat\n\n"); await context.Response.Body.FlushAsync();
        await Emit("{\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}");
        await Task.Delay(65);
        await Emit("{\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}");
        if (model == "broken-stream") return;
        await Emit("{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}");
        if (input.TryGetProperty("stream_options", out var options) && options.TryGetProperty("include_usage", out var include) && include.ValueKind == JsonValueKind.True)
            await Emit("{\"choices\":[],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3,\"total_tokens\":10}}");
        await Emit("[DONE]");
        async Task Emit(string data) { await context.Response.WriteAsync("data: " + data + "\n\n"); await context.Response.Body.FlushAsync(); }
    }
    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
}

internal static class MockListenExtension
{
    public static void ListenLocalhostFixed(this Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions server) => server.Listen(System.Net.IPAddress.Loopback, 0);
}
