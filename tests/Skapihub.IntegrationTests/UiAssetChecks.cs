using System.Net;
using Skapihub.Core;

namespace Skapihub.IntegrationTests;

internal static class UiAssetChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> check)
    {
        var webRoot = Path.Combine(root, "src", "Skapihub.Desktop", "wwwroot");
        var data = Path.Combine(root, "output", "integration", "ui-cache-" + Guid.NewGuid().ToString("N"));
        await using var gateway = await GatewayHost.StartAsync(new GatewayOptions(data, webRoot, Port: 0));
        using var client = new HttpClient { BaseAddress = gateway.Address };
        await check("已移除功能 · 页面没有客户端配置入口", async () =>
        {
            var html = await client.GetStringAsync("/");
            True(!html.Contains("一键配置", StringComparison.Ordinal) && !html.Contains("page-clients", StringComparison.Ordinal), "页面仍有已移除入口");
        });
        await check("已移除功能 · 客户端配置 API 不再注册", async () =>
        {
            using var admin = new HttpClient { BaseAddress = gateway.Address };
            admin.DefaultRequestHeaders.Add("X-Skapihub-Admin", gateway.AdminToken);
            using var status = await admin.GetAsync("/_app/clients"); True(status.StatusCode == HttpStatusCode.NotFound, "状态接口仍可用");
            using var apply = await admin.PostAsync("/_app/clients/codex/apply", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            True(apply.StatusCode == HttpStatusCode.NotFound, "写入接口仍可用");
        });
        await check("已移除功能 · 旧模型绑定字段不再保存", async () =>
        {
            var legacyDirectory = Path.Combine(data, "legacy-client-field"); Directory.CreateDirectory(legacyDirectory);
            await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "settings.dat"), "{\"apiKey\":\"preserve-key\",\"clientModels\":{\"codex\":\"old-model\"},\"relays\":[]}");
            var store = new ConfigurationStore(legacyDirectory); await store.UpdateAsync(current => current);
            var saved = await File.ReadAllTextAsync(Path.Combine(legacyDirectory, "settings.dat"));
            True(store.Current.ApiKey == "preserve-key" && !saved.Contains("clientModels", StringComparison.Ordinal), "旧字段未清理或接入 Key 丢失");
        });
        await check("单文件资源 · 无 wwwroot 目录也能读取全部内嵌网页资源", async () =>
        {
            var missingRoot = Path.Combine(data, "no-loose-ui-files");
            var embedded = new UiAssets(missingRoot);
            True(embedded.BuildId == gateway.Ui.BuildId, "内嵌资源与源码内容不一致");
            await using var host = await GatewayHost.StartAsync(new GatewayOptions(Path.Combine(data, "embedded-only"), missingRoot, Port: 0));
            using var embeddedClient = new HttpClient { BaseAddress = host.Address };
            foreach (var asset in new[] { "/", "/app.css", "/app.js", "/logo.svg" })
            {
                using var response = await embeddedClient.GetAsync(asset); response.EnsureSuccessStatusCode();
                True((await response.Content.ReadAsByteArrayAsync()).Length > 0, "内嵌文件为空");
            }
            True(!Directory.Exists(missingRoot), "创建了外部网页资源目录");
        });
        await check("单文件资源 · 内嵌开源许可可从管理界面读取", async () =>
        {
            using var admin = new HttpClient { BaseAddress = gateway.Address }; admin.DefaultRequestHeaders.Add("X-Skapihub-Admin", gateway.AdminToken);
            var notices = await admin.GetStringAsync("/_app/notices");
            True(notices.Contains("MIT License", StringComparison.Ordinal) && notices.Contains("WebView2", StringComparison.Ordinal), "内嵌许可缺失");
        });
        await check("界面缓存 · 首页地址包含内容版本指纹", () =>
        {
            True(gateway.Ui.BuildId.Length == 16 && gateway.Ui.EntryPath == "/?ui=" + gateway.Ui.BuildId, "首页没有指纹"); return Task.CompletedTask;
        });
        await check("界面缓存 · 根页面和 index.html 都注入脚本样式指纹", async () =>
        {
            foreach (var endpoint in new[] { "/", "/index.html", gateway.Ui.EntryPath })
            {
                var html = await client.GetStringAsync(endpoint);
                True(html.Contains("/app.js?ui=" + gateway.Ui.BuildId, StringComparison.Ordinal), "脚本未带指纹 " + endpoint);
                True(html.Contains("/app.css?ui=" + gateway.Ui.BuildId, StringComparison.Ordinal), "样式未带指纹 " + endpoint);
                True(html.Contains("name=\"skapihub-build\" content=\"" + gateway.Ui.BuildId, StringComparison.Ordinal), "页面未报告指纹");
            }
        });
        await check("界面缓存 · HTML、脚本、样式、图标都禁止复用旧缓存", async () =>
        {
            foreach (var endpoint in new[] { "/", "/index.html", "/app.js", "/app.css", "/logo.svg" })
            {
                using var response = await client.GetAsync(endpoint); response.EnsureSuccessStatusCode();
                True(response.Headers.CacheControl?.NoStore == true && response.Headers.CacheControl.NoCache, "缓存头缺失 " + endpoint);
                True(response.Headers.TryGetValues("X-Skapihub-Ui-Build", out var values) && values.Single() == gateway.Ui.BuildId, "响应指纹缺失");
            }
        });
        await check("界面缓存 · 内容相同版本指纹稳定", () =>
        {
            True(new UiAssets(webRoot).BuildId == gateway.Ui.BuildId, "指纹不稳定"); return Task.CompletedTask;
        });
        await check("界面缓存 · 文件时间不变但内容更新仍更换资源地址", async () =>
        {
            var copy = Path.Combine(data, "ui-copy"); Directory.CreateDirectory(copy);
            foreach (var name in new[] { "index.html", "app.css", "app.js", "logo.svg" }) File.Copy(Path.Combine(webRoot, name), Path.Combine(copy, name));
            var before = new UiAssets(copy); var css = Path.Combine(copy, "app.css"); var modified = File.GetLastWriteTimeUtc(css);
            await File.AppendAllTextAsync(css, "\n/* new UI build */\n"); File.SetLastWriteTimeUtc(css, modified);
            var after = new UiAssets(copy);
            True(before.BuildId != after.BuildId && before.EntryPath != after.EntryPath, "修改后指纹未更新");
        });
        await check("界面缓存 · 普通浏览器可以读取最新页面资源", async () =>
        {
            using var response = await client.GetAsync(gateway.Ui.EntryPath); True(response.StatusCode == HttpStatusCode.OK, "浏览器不能读取页面");
            using var script = await client.GetAsync("/app.js?ui=" + gateway.Ui.BuildId); script.EnsureSuccessStatusCode();
        });
    }
    private static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
