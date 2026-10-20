using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Skapihub.Core;

namespace Skapihub.IntegrationTests;

internal static class GroupRoutingChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> check)
    {
        Relay MakeRelay(string name, string url, params string[] groups) => new() { Name = name, Url = url,
            Groups = groups.Select((group, index) => new KeyGroup { Name = group, Key = index == 0 ? "key-a" : "key-b", Models = [new("gpt-test"), new("vendor/model-a")] }).ToList() };
        var relay = MakeRelay("站点 A", "https://example.com", "默认", "备用");
        var config = new HubConfiguration { Relays = [relay] };
        await check("分组模型 · 同一模型按分组独立发布且附带原名", () =>
        {
            var models = ModelCatalog.Enabled(config).ToArray(); Equal(4, models.Length);
            Equal("站点 A/默认/gpt-test", models[0].Id); Equal("gpt-test", models[0].UpstreamId);
            True(models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() == 4, "模型 ID 重复"); return Task.CompletedTask;
        });
        await check("分组模型 · 同名站点和同名分组仍有唯一 ID", () =>
        {
            var first = MakeRelay("重复站点", "https://one.example", "同名", "同名");
            var second = MakeRelay("重复站点", "https://two.example", "同名", "同名");
            var models = ModelCatalog.Enabled(config with { Relays = [first, second] }).ToArray();
            Equal(8, models.Length); Equal(8, models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count());
            True(models.All(model => model.Id.Contains('@')), "没有重名后缀"); return Task.CompletedTask;
        });
        await check("分组模型 · 关闭重名分组不改变其他分组的调用 ID", () =>
        {
            var same = MakeRelay("站点", "https://one.example", "重复", "重复"); var before = config with { Relays = [same] };
            var id = ModelCatalog.RouteId(before, same, same.Groups[1], "gpt-test");
            var updated = same with { Groups = [same.Groups[0] with { Enabled = false }, same.Groups[1]] };
            var after = config with { Relays = [updated] };
            Equal(id, ModelCatalog.RouteId(after, updated, updated.Groups[1], "gpt-test"));
            True(ModelCatalog.Enabled(after).Any(model => model.Id == id), "关闭另一组破坏调用 ID"); return Task.CompletedTask;
        });
        await check("分组模型 · 名称中的斜线百分号与后缀符号不会串组", () =>
        {
            var first = MakeRelay("站/点%20@x", "https://one.example", "组/名@x");
            var second = MakeRelay("站%2F点%20@x", "https://two.example", "组/名@x");
            var c = config with { Relays = [first, second] }; var entries = ModelCatalog.Enabled(c).ToArray();
            Equal(4, entries.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count());
            Equal("站%2F点%2520%40x/组%2F名%40x/vendor/model-a", ModelCatalog.RouteId(c, first, first.Groups[0], "vendor/model-a")); return Task.CompletedTask;
        });

        await using var upstream = await MockUpstream.StartAsync();
        var data = Path.Combine(root, "output", "integration", "group-routing-" + Guid.NewGuid().ToString("N"));
        var settings = new ConfigurationStore(data);
        var target = MakeRelay("独立站点", upstream.Url, "A", "B");
        await settings.UpdateAsync(current => current with { Relays = [target] });
        await using var hub = await GatewayHost.StartAsync(new GatewayOptions(data, Path.Combine(root, "src", "Skapihub.Desktop", "wwwroot"), Port: 0));
        using var client = new HttpClient { BaseAddress = hub.Address }; client.DefaultRequestHeaders.Add("Authorization", "Bearer sk-local");
        using var admin = new HttpClient { BaseAddress = hub.Address }; admin.DefaultRequestHeaders.Add("X-Skapihub-Admin", hub.AdminToken);
        string Id(int group = 0, string model = "gpt-test") => ModelCatalog.RouteId(hub.Configuration.Current, hub.Configuration.Current.Relays[0], hub.Configuration.Current.Relays[0].Groups[group], model);
        async Task<HttpResponseMessage> Call(string model, string endpoint = "chat/completions") => await client.PostAsJsonAsync("/v1/" + endpoint, new { model, messages = new[] { new { role = "user", content = "test" } } });
        await check("分组模型 · 模型 API 与管理面板使用同一个完整 ID", async () =>
        {
            var models = await client.GetFromJsonAsync<JsonObject>("/v1/models");
            var state = await admin.GetFromJsonAsync<JsonObject>("/_app/state");
            var group = state!["relays"]![0]!["groups"]![0]!;
            Equal(Id(), group["models"]![0]!["routeId"]!.GetValue<string>());
            var row = models!["data"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == Id())!;
            Equal("gpt-test", row["upstream_model"]!.GetValue<string>()); Equal(target.Groups[0].Id, row["group_id"]!.GetValue<string>());
        });
        await check("分组模型 · 显示原名，分组标题与唯一调用 ID 分别保留", async () =>
        {
            var response = await client.GetFromJsonAsync<JsonObject>("/v1/models");
            var models = response!["data"]!.AsArray();
            Equal(4, models.Count);
            foreach (var entry in models)
            {
                var upstreamName = entry!["upstream_model"]!.GetValue<string>();
                Equal(upstreamName, entry["name"]!.GetValue<string>());
                Equal(upstreamName, entry["display_name"]!.GetValue<string>());
                var group = target.Groups.Single(group => group.Id == entry["group_id"]!.GetValue<string>());
                Equal(target.Name + "/" + group.Name, entry["owned_by"]!.GetValue<string>());
                Equal(ModelCatalog.RouteId(hub.Configuration.Current, target, group, upstreamName), entry["id"]!.GetValue<string>());
            }
            Equal(2, models.Count(entry => entry!["name"]!.GetValue<string>() == "gpt-test"));
            Equal(2, models.Count(entry => entry!["name"]!.GetValue<string>() == "vendor/model-a"));
            Equal(4, models.Select(entry => entry!["id"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count());
        });
        await check("分组模型 · 不接受原始裸模型名或不完整前缀", async () =>
        {
            var before = upstream.Calls.Count;
            foreach (var name in new[] { "gpt-test", "A/gpt-test", "独立站点/gpt-test", "other/A/gpt-test" })
            { using var response = await Call(name); Equal(HttpStatusCode.NotFound, response.StatusCode); }
            Equal(before, upstream.Calls.Count);
        });
        await check("分组模型 · 两个分组的并发请求各用各的 Key", async () =>
        {
            var before = upstream.Calls.Count;
            await Task.WhenAll(Enumerable.Range(0, 24).Select(async index => { using var response = await Call(Id(index % 2)); response.EnsureSuccessStatusCode(); }));
            var calls = upstream.Calls.Skip(before).ToArray(); Equal(12, calls.Count(call => call.Key == "key-a")); Equal(12, calls.Count(call => call.Key == "key-b"));
            True(calls.All(call => call.Model == "gpt-test"), "分组前缀泄漏到上游模型名");
        });
        await check("分组模型 · 401、403、429、503 都不会转发到其他分组", async () =>
        {
            foreach (var status in new[] { 401, 403, 429, 503 })
            {
                upstream.FailureStatuses["key-a"] = status; var before = upstream.Calls.Count;
                using var response = await Call(Id()); Equal(status, (int)response.StatusCode); Equal(before + 1, upstream.Calls.Count);
                Equal("key-a", upstream.Calls.Last().Key);
                using var other = await Call(Id(1)); other.EnsureSuccessStatusCode(); Equal("key-b", upstream.Calls.Last().Key);
            }
            upstream.FailureStatuses.Clear();
        });
        await check("分组模型 · usage 回退仍保持相同 Key 和原始上游模型名", async () =>
        {
            upstream.RejectUsageOption = true; var before = upstream.Calls.Count;
            using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = Id(), stream = true, messages = Array.Empty<object>() });
            response.EnsureSuccessStatusCode(); await response.Content.ReadAsStringAsync(); upstream.RejectUsageOption = false;
            var calls = upstream.Calls.Skip(before).ToArray(); Equal(2, calls.Length);
            True(calls.All(call => call.Key == "key-a" && call.Model == "gpt-test"), "usage 回退改变了来源");
        });
        await check("分组模型 · 关闭指定组后调用失败，其他同名模型继续可用", async () =>
        {
            var id = Id(); var bId = Id(1);
            await hub.Configuration.UpdateAsync(current => current with { Relays = [current.Relays[0] with { Groups = [target.Groups[0] with { Enabled = false }, target.Groups[1]] }] });
            var before = upstream.Calls.Count; using var response = await Call(id); Equal(HttpStatusCode.NotFound, response.StatusCode); Equal(before, upstream.Calls.Count);
            using var other = await Call(bId); other.EnsureSuccessStatusCode(); Equal("key-b", upstream.Calls.Last().Key);
            await hub.Configuration.UpdateAsync(current => current with { Relays = [target] });
        });
        await check("分组模型 · 修改分组名称后面板和 API 一致更新", async () =>
        {
            var oldId = Id();
            await hub.Configuration.UpdateAsync(current => current with { Relays = [target with { Groups = [target.Groups[0] with { Name = "新名称" }, target.Groups[1]] }] });
            using var old = await Call(oldId); Equal(HttpStatusCode.NotFound, old.StatusCode);
            using var updated = await Call(Id()); updated.EnsureSuccessStatusCode(); Equal("key-a", upstream.Calls.Last().Key);
            Equal("独立站点/新名称/gpt-test", Id());
            await hub.Configuration.UpdateAsync(current => current with { Relays = [target] });
        });
        await check("分组模型 · 统计保存完整调用 ID、原始模型与对应分组", async () =>
        {
            var previous = hub.Usage.Snapshot().Total.Requests;
            using var response = await Call(Id(1)); response.EnsureSuccessStatusCode();
            for (var attempt = 0; attempt < 50 && hub.Usage.Snapshot().Total.Requests == previous; attempt++) await Task.Delay(20);
            var record = hub.Usage.Snapshot().Recent.First(); Equal(Id(1), record.RouteId); Equal("gpt-test", record.Model); Equal(target.Groups[1].Id, record.GroupId);
        });
        await check("分组模型 · 所选中转无法连接时不会使用其他 URL", async () =>
        {
            var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            var offline = MakeRelay("离线站点", "http://127.0.0.1:" + port, "A");
            await hub.Configuration.UpdateAsync(current => current with { Relays = [offline, target] });
            var route = ModelCatalog.RouteId(hub.Configuration.Current, offline, offline.Groups[0], "gpt-test");
            var before = upstream.Calls.Count; using var response = await Call(route); Equal(HttpStatusCode.BadGateway, response.StatusCode); Equal(before, upstream.Calls.Count);
        });
    }
    private static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"expected {expected}, actual {actual}"); }
}
