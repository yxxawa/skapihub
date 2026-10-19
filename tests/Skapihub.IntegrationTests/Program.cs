using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Skapihub.Core;

namespace Skapihub.IntegrationTests;

internal static class Program
{
    private static int _passed, _failed;
    private static async Task<int> Main(string[] args)
    {
        var root = FindRoot();
        if (args.Contains("--prepare-native"))
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "output", "playwright", "session.json")));
            var upstreamUrl = fixture.RootElement.GetProperty("upstream").GetString()!;
            var nativeData = Path.Combine(root, "output", "native-tray-smoke");
            var store = new ConfigurationStore(nativeData, new Skapihub.Desktop.WindowsSettingsCodec());
            await store.UpdateAsync(cfg => cfg with
            {
                Port = 27125, ApiKey = "native-smoke", Relays = [new Relay
                {
                    Name = "本地模拟中转", Url = upstreamUrl,
                    Groups = [new KeyGroup { Name = "默认分组", Key = "key-a", Models = [new("gpt-test")] }, new KeyGroup { Name = "备用分组", Key = "key-b", Models = [new("gpt-test")] }]
                }]
            });
            Console.WriteLine("NATIVE_PROFILE " + nativeData); return 0;
        }
        var data = Path.Combine(root, "output", "integration", Guid.NewGuid().ToString("N"));
        var webRoot = Path.Combine(root, "src", "Skapihub.Desktop", "wwwroot");
        await using var upstream = await MockUpstream.StartAsync();
        var serveUi = args.Contains("--serve-ui");
        await using var hub = await GatewayHost.StartAsync(new GatewayOptions(data, webRoot, Port: serveUi ? 27124 : 0, AdminToken: "integration-session"));
        using var admin = new HttpClient { BaseAddress = hub.Address };
        admin.DefaultRequestHeaders.Add("X-Skapihub-Admin", hub.AdminToken);
        using var client = new HttpClient { BaseAddress = hub.Address };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer sk-local");
        if (serveUi)
        {
            upstream.ModelDelayMs = 800;
            Directory.CreateDirectory(Path.Combine(root, "output", "playwright"));
            await File.WriteAllTextAsync(Path.Combine(root, "output", "playwright", "session.json"), JsonSerializer.Serialize(new { url = hub.Address, upstream = upstream.Url, token = hub.AdminToken, data }, JsonDefaults.Options));
            Console.WriteLine("UI_FIXTURE " + hub.Address + " UPSTREAM " + upstream.Url);
            var stop = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
            await stop.Task; return 0;
        }

        await Check("URL 根地址、版本地址及子路径规范化", () =>
        {
            Equal("https://example.com/v1/models", UpstreamAddress.Endpoint("https://example.com/", "models").ToString());
            Equal("https://example.com/v1/models", UpstreamAddress.Endpoint("https://example.com/v1/", "models").ToString());
            Equal("https://example.com/api/v1/chat/completions?test=1", UpstreamAddress.Endpoint("https://example.com/api/v1", "chat/completions", "?test=1").ToString());
            return Task.CompletedTask;
        });
        await Check("管理接口不接受统一 API Key", async () => { using var response = await client.GetAsync("_app/state"); Equal(HttpStatusCode.Forbidden, response.StatusCode); });
        await Check("API 拒绝错误 Key", async () => { using var other = new HttpClient { BaseAddress = hub.Address }; using var response = await other.GetAsync("v1/models"); Equal(HttpStatusCode.Unauthorized, response.StatusCode); });
        await Check("跨站管理写入被拒绝", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "_app/enabled") { Content = JsonContent.Create(new { enabled = false }) };
            request.Headers.Add("Origin", "https://other.example"); using var response = await admin.SendAsync(request); Equal(HttpStatusCode.Forbidden, response.StatusCode);
        });
        await Check("首次启动无中转、无测试数据", () => { Equal(0, hub.Configuration.Current.Relays.Count); Equal(0L, hub.Usage.Snapshot().Total.Requests); return Task.CompletedTask; });
        var relay = new RelayInput("测试中转", upstream.Url + "/api/v1", [new("默认", "key-a"), new("备用", "key-b")]);
        var created = await Send(admin, "_app/relays", HttpMethod.Post, relay);
        var relayId = created!["id"]!.GetValue<string>();
        await Send(admin, "_app/relays/" + relayId + "/sync", HttpMethod.Post);
        var current = hub.Configuration.Current.Relays.Single();
        var groupA = current.Groups[0].Id; var groupB = current.Groups[1].Id;
        var groupRouteA = "_app/relays/" + relayId + "/groups/" + groupA;
        var groupRouteB = "_app/relays/" + relayId + "/groups/" + groupB;
        await Check("两个 Key 独立发现模型、同名模型按分组分别公开", async () =>
        {
            Equal(7, current.Groups[0].Models.Count); Equal(7, current.Groups[1].Models.Count);
            var models = await client.GetFromJsonAsync<JsonObject>("v1/models"); Equal(14, models!["data"]!.AsArray().Count);
        });
        await Check("分组调用 ID 固定选择各自 Key，不跨组轮询", async () =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Equal("a", (await Chat(client))!["served_by"]!.GetValue<string>());
                Equal("b", (await Chat(client, group: "备用"))!["served_by"]!.GetValue<string>());
            }
            True(upstream.Calls.All(call => call.Model == "gpt-test"), "上游收到带分组前缀的模型名");
        });
        await Check("逐模型关闭后对应 ID 不可调用，不切换至其他分组", async () =>
        {
            await Send(admin, groupRouteA + "/models/enabled", HttpMethod.Put, new { models = new[] { "gpt-test" }, enabled = false });
            var before = upstream.Calls.Count;
            using var response = await client.PostAsJsonAsync("v1/chat/completions", new { model = Qualified("gpt-test"), messages = Array.Empty<object>() });
            Equal(HttpStatusCode.NotFound, response.StatusCode); Equal(before, upstream.Calls.Count);
            Equal("b", (await Chat(client, group: "备用"))!["served_by"]!.GetValue<string>());
        });
        await Check("关闭分组后模型立即不可调用", async () =>
        {
            await Send(admin, groupRouteB + "/enabled", HttpMethod.Put, new { enabled = false });
            var before = upstream.Calls.Count; using var response = await client.PostAsJsonAsync("v1/chat/completions", new { model = Qualified("gpt-test", "备用"), messages = Array.Empty<object>() });
            Equal(HttpStatusCode.NotFound, response.StatusCode); Equal(before, upstream.Calls.Count);
            await Send(admin, groupRouteB + "/enabled", HttpMethod.Put, new { enabled = true });
        });
        await Check("同步模型保留已关闭状态", async () =>
        {
            await Send(admin, "_app/relays/" + relayId + "/sync", HttpMethod.Post);
            True(!hub.Configuration.Current.Relays.Single().Groups[0].Models.Single(x => x.Id == "gpt-test").Enabled, "模型开关被覆盖");
        });
        await Check("获取失败保留原模型及开关", async () =>
        {
            upstream.FailModels = true; await Send(admin, "_app/relays/" + relayId + "/sync", HttpMethod.Post); upstream.FailModels = false;
            var group = hub.Configuration.Current.Relays.Single().Groups[0]; Equal(7, group.Models.Count); True(group.SyncError is not null, "未记录错误");
            True(!group.Models.Single(x => x.Id == "gpt-test").Enabled, "丢失开关");
        });
        await Send(admin, groupRouteA + "/models/enabled", HttpMethod.Put, new { models = new[] { "gpt-test" }, enabled = true });
        await Check("指定分组返回 503 时不切换到其他 Key", async () =>
        {
            var snapshot = hub.Configuration.Current.Relays.Single();
            await hub.Configuration.UpdateAsync(cfg => cfg with { Relays = [snapshot with { Groups = [snapshot.Groups[0] with { Key = "key-fail" }, snapshot.Groups[1]] }] });
            var before = upstream.Calls.Count;
            using var response = await client.PostAsJsonAsync("v1/chat/completions", new { model = Qualified("gpt-test"), messages = Array.Empty<object>() });
            Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Equal(before + 1, upstream.Calls.Count);
            Equal("key-fail", upstream.Calls.Last().Key);
            await hub.Configuration.UpdateAsync(cfg => cfg with { Relays = [snapshot] });
        });
        await Check("普通 400 不重试其他 Key", async () =>
        {
            await Send(admin, groupRouteA + "/models", HttpMethod.Post, new { models = new[] { "invalid-request" } });
            await Send(admin, groupRouteB + "/models", HttpMethod.Post, new { models = new[] { "invalid-request" } });
            var before = upstream.Calls.Count; using var response = await client.PostAsJsonAsync("v1/chat/completions", new { model = Qualified("invalid-request"), messages = Array.Empty<object>() });
            Equal(HttpStatusCode.BadRequest, response.StatusCode); Equal(before + 1, upstream.Calls.Count);
        });
        await Check("Chat 流透传、自动请求用量、首字不计算心跳和角色", async () =>
        {
            var text = await Stream(client, "chat/completions", "gpt-test"); True(text.Contains("[DONE]", StringComparison.Ordinal), "流终止符缺失");
            True(text.Contains("你好", StringComparison.Ordinal), "内容损坏");
            var row = await WaitUsage(hub, x => x.Model == "gpt-test" && x.FirstTokenMs is not null);
            Equal(7L, row.InputTokens!.Value); Equal(3L, row.OutputTokens!.Value); True(row.FirstTokenMs >= 50, "首字误计为响应头/心跳");
        });
        await Check("不支持 stream_options 的上游用原请求回退，统计保持未知", async () =>
        {
            upstream.RejectUsageOption = true;
            var beforeRequests = hub.Usage.Snapshot().Total.Requests;
            var beforeCalls = upstream.Calls.Count;
            var text = await Stream(client, "chat/completions", "gpt-test");
            upstream.RejectUsageOption = false;
            True(text.Contains("[DONE]", StringComparison.Ordinal), "回退失败");
            Equal(beforeCalls + 2, upstream.Calls.Count);
            var row = await WaitUsage(hub, x => x.Model == "gpt-test" && x.FirstTokenMs is not null && x.InputTokens is null);
            Equal(beforeRequests + 1, hub.Usage.Snapshot().Total.Requests);
        });
        await Check("客户端显式关闭 include_usage 时保持原值", async () =>
        {
            using var response = await client.PostAsJsonAsync("v1/chat/completions", new { model = Qualified("gpt-test"), stream = true, messages = Array.Empty<object>(), stream_options = new { include_usage = false } });
            response.EnsureSuccessStatusCode(); var text = await response.Content.ReadAsStringAsync(); True(!text.Contains("prompt_tokens", StringComparison.Ordinal), "覆盖了客户端用量设置");
        });
        await Check("Claude 流鉴权及起止事件用量合并", async () =>
        {
            var text = await Stream(client, "messages", "claude-test"); True(text.Contains("message_stop", StringComparison.Ordinal), "Claude 流损坏");
            var row = await WaitUsage(hub, x => x.Model == "claude-test"); Equal(11L, row.InputTokens!.Value); Equal(4L, row.OutputTokens!.Value);
        });
        await Check("Responses 流及嵌套 usage", async () =>
        {
            await Stream(client, "responses", "responses-test"); var row = await WaitUsage(hub, x => x.Model == "responses-test");
            Equal(9L, row.InputTokens!.Value); Equal(6L, row.OutputTokens!.Value); Equal(15L, row.TotalTokens!.Value);
        });
        await Check("Embeddings 透传与输入 Token 统计", async () =>
        {
            using var response = await client.PostAsJsonAsync("v1/embeddings", new { model = Qualified("embed-test"), input = "hello" }); response.EnsureSuccessStatusCode();
            var row = await WaitUsage(hub, x => x.Model == "embed-test"); Equal(12L, row.InputTokens!.Value); True(row.FirstTokenMs is null, "向量请求不应伪造首字延迟");
        });
        await Check("未返回用量时保留未知，非流式不伪造首字", async () =>
        {
            await Chat(client, "no-usage"); var row = await WaitUsage(hub, x => x.Model == "no-usage");
            True(row.InputTokens is null && row.OutputTokens is null && row.TotalTokens is null, "未知用量被计为零"); True(row.FirstTokenMs is null, "非流式首字被伪造");
        });
        await Check("流提前结束记录失败，不重放请求", async () =>
        {
            var before = upstream.Calls.Count; await Stream(client, "chat/completions", "broken-stream");
            var row = await WaitUsage(hub, x => x.Model == "broken-stream"); Equal(502, row.Status); Equal(before + 1, upstream.Calls.Count);
        });
        await Check("带斜线模型名和查询参数完整转发", async () =>
        {
            using var response = await client.PostAsJsonAsync("v1/chat/completions?custom=one", new { model = Qualified("vendor/model-a"), messages = Array.Empty<object>() }); response.EnsureSuccessStatusCode();
            var last = upstream.Calls.Last(); Equal("vendor/model-a", last.Model); Equal("?custom=one", last.Query); Equal("/api/v1/chat/completions", last.Endpoint);
        });
        await Check("全局暂停仅停止模型 API，管理仍可用", async () =>
        {
            await Send(admin, "_app/enabled", HttpMethod.Put, new { enabled = false });
            using var response = await client.GetAsync("v1/models"); Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var management = await admin.GetAsync("_app/state"); management.EnsureSuccessStatusCode();
            await Send(admin, "_app/enabled", HttpMethod.Put, new { enabled = true });
        });
        await Check("中转级关闭即时生效", async () =>
        {
            await Send(admin, "_app/relays/" + relayId + "/enabled", HttpMethod.Put, new { enabled = false });
            var models = await client.GetFromJsonAsync<JsonObject>("v1/models"); Equal(0, models!["data"]!.AsArray().Count);
            await Send(admin, "_app/relays/" + relayId + "/enabled", HttpMethod.Put, new { enabled = true });
        });
        await Check("手动添加和删除带斜线的模型", async () =>
        {
            await Send(admin, groupRouteA + "/models", HttpMethod.Post, new { models = new[] { "manual/vendor-model" } });
            using var response = await client.PostAsJsonAsync("v1/chat/completions", new { model = Qualified("manual/vendor-model"), messages = Array.Empty<object>() }); response.EnsureSuccessStatusCode();
            await Send(admin, groupRouteA + "/models/remove", HttpMethod.Post, new { models = new[] { "manual/vendor-model" } });
            True(!hub.Configuration.Current.Relays.Single().Groups[0].Models.Any(x => x.Id == "manual/vendor-model"), "手动模型删除失败");
        });
        await Check("接入 Key 无长度和前缀限制，修改即时生效", async () =>
        {
            await Send(admin, "_app/settings", HttpMethod.Put, new { port = 27123, apiKey = "x" });
            using var old = await client.GetAsync("v1/models"); Equal(HttpStatusCode.Unauthorized, old.StatusCode);
            client.DefaultRequestHeaders.Remove("Authorization"); client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer x");
            using var next = await client.GetAsync("v1/models"); next.EnsureSuccessStatusCode();
        });
        await Check("逐字节 UTF-8、多行 SSE、工具调用解析", async () =>
        {
            var watch = Stopwatch.StartNew(); var observer = new ResponseObserver(true, watch);
            observer.Feed(Encoding.UTF8.GetBytes(": ping\n\ndata: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n"));
            True(observer.FirstTokenMs is null, "角色被算作首字"); await Task.Delay(25);
            var bytes = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\ndata: {\n" + "data: \"usage\":{\"prompt_tokens\":8,\"completion_tokens\":2}}\n\ndata: [DONE]\n\n");
            foreach (var b in bytes) observer.Feed([b]); observer.Finish();
            True(observer.FirstTokenMs >= 20 && observer.TerminalSeen, "碎片流解析失败"); Equal(8L, observer.InputTokens!.Value); Equal(2L, observer.OutputTokens!.Value);
            var tool = new ResponseObserver(true, Stopwatch.StartNew()); tool.Feed(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\"}}]}}]}\n\n")); tool.Finish(); True(tool.FirstTokenMs is not null, "工具调用首字漏计");
        });
        await Check("Windows 配置使用当前账号 DPAPI 加密并可重新解密", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var encryptedDirectory = Path.Combine(data, "dpapi");
            var codec = new Skapihub.Desktop.WindowsSettingsCodec();
            var store = new ConfigurationStore(encryptedDirectory, codec);
            const string secret = "test-secret-do-not-store-as-plaintext";
            await store.UpdateAsync(config => config with { ApiKey = secret });
            var bytes = await File.ReadAllBytesAsync(Path.Combine(encryptedDirectory, "settings.dat"));
            True(!Encoding.UTF8.GetString(bytes).Contains(secret, StringComparison.Ordinal), "Key 以明文保存");
            Equal(secret, new ConfigurationStore(encryptedDirectory, codec).Current.ApiKey);
        });
        await Check("配置持久化保留 Key、模型和开关", () =>
        {
            var reopened = new ConfigurationStore(data); Equal("x", reopened.Current.ApiKey); Equal(2, reopened.Current.Relays.Single().Groups.Count); return Task.CompletedTask;
        });
        await Check("统计按请求记一次，不把失败重试重复累计", () =>
        {
            var snapshot = hub.Usage.Snapshot(); Equal(snapshot.Recent.Count, (int)snapshot.Total.Requests);
            Equal(snapshot.Recent.Sum(x => x.InputTokens ?? 0), snapshot.Total.InputTokens);
            Equal(snapshot.Recent.Sum(x => x.OutputTokens ?? 0), snapshot.Total.OutputTokens);
            True(snapshot.Total.UnknownUsage > 0, "未记录未知用量"); return Task.CompletedTask;
        });
        await Check("更换上游 Key 后清空旧模型，避免误用旧权限", async () =>
        {
            var r = hub.Configuration.Current.Relays.Single();
            await Send(admin, "_app/relays/" + relayId, HttpMethod.Put, new RelayInput(r.Name, r.Url, [new("默认", "new-key", groupA), new("备用", "key-b", groupB)]));
            Equal(0, hub.Configuration.Current.Relays.Single().Groups[0].Models.Count);
        });
        await Check("多个不同 URL 的同名模型分别固定到各自中转", async () =>
        {
            await using var second = await MockUpstream.StartAsync();
            var result = await Send(admin, "_app/relays", HttpMethod.Post, new RelayInput("第二个中转", second.Url, [new("独立分组", "key-c")]));
            var secondId = result!["id"]!.GetValue<string>();
            await Send(admin, "_app/relays/" + secondId + "/sync", HttpMethod.Post);
            var beforeFirst = upstream.Calls.Count;
            var beforeSecond = second.Calls.Count;
            for (var i = 0; i < 3; i++) await Chat(client, "embed-test", "备用");
            Equal(beforeFirst + 3, upstream.Calls.Count); Equal(beforeSecond, second.Calls.Count);
            for (var i = 0; i < 3; i++) await Chat(client, "embed-test", "独立分组", "第二个中转");
            Equal(beforeFirst + 3, upstream.Calls.Count); Equal(beforeSecond + 3, second.Calls.Count);
            await Send(admin, "_app/relays/" + secondId, HttpMethod.Delete);
        });
        await Check("并发请求固定分组、统计不丢失", async () =>
        {
            var before = hub.Usage.Snapshot().Total.Requests;
            await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Chat(client, "embed-test", "备用")));
            for (var i = 0; i < 40 && hub.Usage.Snapshot().Total.Requests < before + 24; i++) await Task.Delay(25);
            Equal(before + 24, hub.Usage.Snapshot().Total.Requests);
        });
        await Check("退出时刷新统计，重新读取累计和未知用量", async () =>
        {
            var usageDirectory = Path.Combine(data, "usage-persistence");
            await using (var writer = new UsageStore(usageDirectory))
            {
                for (var i = 0; i < 120; i++) writer.Record(new RequestUsage { Model = "persisted", GroupId = "group", Status = 200, InputTokens = i == 0 ? null : 2, OutputTokens = i == 0 ? null : 1, FirstTokenMs = i == 0 ? null : 123 });
            }
            await using var reader = new UsageStore(usageDirectory);
            var totals = reader.Snapshot().Total;
            Equal(120L, totals.Requests); Equal(238L, totals.InputTokens); Equal(119L, totals.OutputTokens); Equal(1L, totals.UnknownUsage); Equal(123d, totals.AverageFirstTokenMs!.Value);
        });
        await Check("Token 保持未知与超大 SSE 事件保持有界，不破坏后续事件", () =>
        {
            var observer = new ResponseObserver(true, Stopwatch.StartNew());
            observer.Feed(Encoding.UTF8.GetBytes("data: " + new string('x', 1100000) + "\n\ndata: {\"usage\":{\"input_tokens\":\"invalid\",\"output_tokens\":3}}\n\ndata: [DONE]\n\n"));
            observer.Finish(); True(observer.TerminalSeen && observer.InputTokens is null, "超大事件影响后续解析"); Equal(3L, observer.OutputTokens!.Value); return Task.CompletedTask;
        });
        await Check("Claude 缓存输入 Token 计入输入，不重复累加输出", () =>
        {
            var observer = new ResponseObserver(true, Stopwatch.StartNew());
            observer.Feed(Encoding.UTF8.GetBytes("data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":20,\"cache_creation_input_tokens\":5,\"output_tokens\":1}}}\n\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":8}}\n\ndata: {\"type\":\"message_stop\"}\n\n"));
            observer.Finish(); Equal(35L, observer.InputTokens!.Value); Equal(8L, observer.OutputTokens!.Value); return Task.CompletedTask;
        });
        await Check("空接入 Key 可以保存并在本机无鉴权使用", async () =>
        {
            await Send(admin, "_app/settings", HttpMethod.Put, new { port = 27123, apiKey = "" });
            using var unauthenticated = new HttpClient { BaseAddress = hub.Address }; using var response = await unauthenticated.GetAsync("v1/models"); response.EnsureSuccessStatusCode();
        });
        await Check("删除中转移除所有路由", async () =>
        {
            await Send(admin, "_app/relays/" + relayId, HttpMethod.Delete); var models = await client.GetFromJsonAsync<JsonObject>("v1/models"); Equal(0, models!["data"]!.AsArray().Count);
        });
        await Check("旧配置保留中转与 Key，保存后移除废弃余额字段", async () =>
        {
            var upgradeData = Path.Combine(data, "upgrade"); Directory.CreateDirectory(upgradeData);
            var original = new HubConfiguration { Relays = [new Relay { Name = "原中转", Url = "https://example.com", Groups = [new KeyGroup { Name = "原分组", Key = "original-key" }] }] };
            var node = JsonSerializer.SerializeToNode(original, JsonDefaults.Options)!.AsObject();
            node["relays"]![0]!["balance"] = new JsonObject { ["enabled"] = true, ["credential"] = "deprecated-secret" };
            await File.WriteAllTextAsync(Path.Combine(upgradeData, "settings.dat"), node.ToJsonString());
            var upgraded = new ConfigurationStore(upgradeData); Equal("original-key", upgraded.Current.Relays[0].Groups[0].Key);
            await upgraded.UpdateAsync(current => current);
            var stored = await File.ReadAllTextAsync(Path.Combine(upgradeData, "settings.dat"));
            True(!stored.Contains("balance", StringComparison.OrdinalIgnoreCase) && !stored.Contains("deprecated-secret", StringComparison.Ordinal), "旧字段仍被保存");
        });
        await UiAssetChecks.RunAsync(root, Check);
        await GroupRoutingChecks.RunAsync(root, Check);
        Console.WriteLine($"RESULT {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task Check(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.Error.WriteLine("FAIL " + name + " :: " + ex.Message); }
    }
    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"expected {expected}, actual {actual}"); }
    private static async Task<JsonObject?> Send(HttpClient client, string url, HttpMethod method, object? value = null)
    {
        using var request = new HttpRequestMessage(method, url); if (value is not null) request.Content = JsonContent.Create(value);
        using var response = await client.SendAsync(request); var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text) as JsonObject;
    }
    private static string Qualified(string model, string group = "默认", string relay = "测试中转") => relay + "/" + group + "/" + model;
    private static Task<JsonObject?> Chat(HttpClient client, string model = "gpt-test", string group = "默认", string relay = "测试中转") => Send(client, "v1/chat/completions", HttpMethod.Post, new { model = Qualified(model, group, relay), messages = new[] { new { role = "user", content = "hello" } } });
    private static async Task<string> Stream(HttpClient client, string endpoint, string model)
    {
        using var response = await client.PostAsJsonAsync("v1/" + endpoint, new { model = Qualified(model), stream = true, messages = new[] { new { role = "user", content = "hello" } }, input = "hello", max_tokens = 20 });
        response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync();
    }
    private static async Task<RequestUsage> WaitUsage(GatewayHost hub, Func<RequestUsage, bool> predicate)
    {
        for (var i = 0; i < 40; i++) { var found = hub.Usage.Snapshot().Recent.FirstOrDefault(predicate); if (found is not null) return found; await Task.Delay(25); }
        throw new InvalidOperationException("未找到对应请求统计");
    }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Skapihub.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("请从 skapihub 项目目录运行测试。");
    }
}
