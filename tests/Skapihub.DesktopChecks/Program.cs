using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Skapihub.Core;
using Skapihub.Desktop;
using Skapihub.IntegrationTests;
using Microsoft.Web.WebView2.WinForms;

namespace Skapihub.DesktopChecks;

internal static class Program
{
    private static int _passed, _failed;
    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = FindRoot();
        var data = Path.Combine(root, "output", "desktop-checks", Guid.NewGuid().ToString("N"));
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var upstream = MockUpstream.StartAsync().GetAwaiter().GetResult();
        var settings = new ConfigurationStore(data, new WindowsSettingsCodec());
        settings.UpdateAsync(cfg => cfg with
        {
            Port = port, ApiKey = "desktop-test", Relays = [new Relay { Name = "桌面验证中转", Url = upstream.Url, Groups = [new KeyGroup { Name = "默认", Key = "key-a", Models = [new("gpt-test")] }] }]
        }).GetAwaiter().GetResult();
        using var context = new ApplicationContext();
        var restoreMessage = NativeMethods.RegisterWindowMessage("Skapihub.TestRestore." + Guid.NewGuid().ToString("N"));
        using var window = new MainWindow(data, restoreMessage);
        window.Shown += async (_, _) =>
        {
            try { await RunAsync(window, upstream, port, restoreMessage); }
            catch (Exception ex) { _failed++; Console.Error.WriteLine("FAIL desktop checks :: " + ex); }
            finally
            {
                try
                {
                    if (!window.IsDisposed)
                    {
                        Field<ContextMenuStrip>(window, "_trayMenu").Items[2].PerformClick();
                        if (Field<Task?>(window, "_shutdown") is { } shutdown) await shutdown;
                    }
                }
                catch (Exception ex) { _failed++; Console.Error.WriteLine("FAIL shutdown :: " + ex.Message); }
                context.ExitThread();
            }
        };
        window.Show();
        Application.Run(context);
        upstream.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"DESKTOP RESULT {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunAsync(MainWindow window, MockUpstream upstream, int port, uint restoreMessage)
    {
        using var api = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(10) };
        api.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer desktop-test");
        var web = Field<WebView2>(window, "_web");
        await Task.Delay(120);
        CaptureWindow(window, "19-v04-startup.png");
        await UntilAsync(() => Field<bool>(window, "_browserRevealed"), "界面未就绪");
        CaptureWindow(window, "20-v04-native.png");
        await Check("系统原生边框与不透明窗体", () =>
        {
            True(window.FormBorderStyle == FormBorderStyle.Sizable && window.Opacity == 1 && window.TransparencyKey.IsEmpty, "仍在使用自定义透明窗体");
            True((GetWindowLong(window.Handle, -20) & 0x00080000) == 0, "窗体启用了 Layered 样式");
            return Task.CompletedTask;
        });
        await Check("原生框架模式已应用，网页没有重复窗口按钮", async () =>
        {
            var state = await EvaluateAsync(web, "({native:document.documentElement.classList.contains('native-frame'),titlebar:getComputedStyle(document.querySelector('.titlebar')).display,bg:getComputedStyle(document.body).backgroundColor})");
            True(state.GetProperty("native").GetBoolean() && state.GetProperty("titlebar").GetString() == "none", "重复窗口按钮仍可见");
            True(state.GetProperty("bg").GetString() == "rgb(247, 248, 250)", "网页背景不透明色错误");
        });
        await Check("浏览器填满客户区，启动遮罩已隐藏", () =>
        {
            var surface = Field<Panel>(window, "_surface");
            True(web.Bounds == surface.ClientRectangle && surface.Bounds == window.ClientRectangle, "客户区和 WebView 边界不一致");
            True(!Field<StartupSurface>(window, "_loading").Visible && web.Visible, "启动遮罩未关闭"); return Task.CompletedTask;
        });
        await Check("启动时注册托盘菜单", () =>
        {
            var tray = Field<NotifyIcon>(window, "_tray"); var menu = Field<ContextMenuStrip>(window, "_trayMenu");
            True(tray.Visible && tray.Icon is not null, "托盘图标未注册");
            True(menu.Items[0].Text == "显示主界面" && menu.Items[2].Text == "退出 skapihub", "托盘菜单缺失"); return Task.CompletedTask;
        });
        await Check("关闭按钮隐藏窗口，服务和控制器保留", async () =>
        {
            var controller = web.CoreWebView2;
            window.Close();
            True(!window.Visible && !window.IsDisposed && Field<NotifyIcon>(window, "_tray").Visible, "关闭窗口导致退出或托盘丢失");
            using var response = await api.GetAsync("/v1/models"); response.EnsureSuccessStatusCode();
            True(ReferenceEquals(controller, web.CoreWebView2), "隐藏后重建浏览器");
        });
        await Check("窗口隐藏后继续转发并记录用量", async () =>
        {
            using var response = await api.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-test", messages = new[] { new { role = "user", content = "test" } } });
            response.EnsureSuccessStatusCode(); var text = await response.Content.ReadAsStringAsync(); True(text.Contains("mock-completion", StringComparison.Ordinal), "后台代理未返回");
            var host = Field<GatewayHost>(window, "_host");
            await UntilAsync(() => host.Usage.Snapshot().Total.Requests == 1, "后台请求用量未记录");
        });
        await Check("托盘显示主界面保留原服务", () =>
        {
            Field<ContextMenuStrip>(window, "_trayMenu").Items[0].PerformClick();
            True(window.Visible && web.Visible && !window.IsDisposed, "托盘无法恢复窗口"); return Task.CompletedTask;
        });
        await Check("隐藏后单实例消息恢复原窗口", async () =>
        {
            window.Close(); NativeMethods.PostMessage(new IntPtr(0xffff), restoreMessage, IntPtr.Zero, IntPtr.Zero);
            await UntilAsync(() => window.Visible, "已有实例没有响应恢复消息");
            using var response = await api.GetAsync("/v1/models"); response.EnsureSuccessStatusCode();
        });
        await Check("调整窗口大小后内容边界仍完整", async () =>
        {
            window.Size = new Size(window.Size.Width - 50, window.Size.Height - 50); await Task.Delay(50);
            True(web.Bounds == Field<Panel>(window, "_surface").ClientRectangle, "缩放后 WebView 出现空洞");
        });
        await Check("网页没有余额界面，分组默认收起", async () =>
        {
            var state = await EvaluateAsync(web, "({text:document.body.innerText,groups:document.querySelectorAll('.group-row').length})");
            True(!state.GetProperty("text").GetString()!.Contains("余额", StringComparison.Ordinal) && state.GetProperty("groups").GetInt32() == 0, "余额界面或默认展开仍存在");
        });
        await Check("退出托盘菜单真正停止 API", async () =>
        {
            Field<ContextMenuStrip>(window, "_trayMenu").Items[2].PerformClick();
            if (Field<Task?>(window, "_shutdown") is { } shutdown) await shutdown;
            True(window.IsDisposed, "选择退出后窗口未释放");
            True(!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port), "退出后监听端口仍存在");
        });
    }

    private static void CaptureWindow(Form window, string name)
    {
        using var bitmap = new Bitmap(window.Width, window.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(247, 248, 250));
            var context = graphics.GetHdc();
            try { PrintWindow(window.Handle, context, 2); }
            finally { graphics.ReleaseHdc(context); }
        }
        var output = Path.Combine(FindRoot(), "output", "playwright"); Directory.CreateDirectory(output);
        bitmap.Save(Path.Combine(output, name), System.Drawing.Imaging.ImageFormat.Png);
    }
    private static async Task<JsonElement> EvaluateAsync(WebView2 web, string expression)
    {
        var encoded = await web.CoreWebView2.ExecuteScriptAsync("JSON.stringify(" + expression + ")");
        using var json = JsonDocument.Parse(JsonSerializer.Deserialize<string>(encoded)!); return json.RootElement.Clone();
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static async Task UntilAsync(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 400; attempt++) { if (condition()) return; await Task.Delay(25); }
        throw new InvalidOperationException(message);
    }
    private static async Task Check(string name, Func<Task> check)
    {
        try { await check(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.Error.WriteLine("FAIL " + name + " :: " + ex.Message); }
    }
    private static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Skapihub.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("请从项目目录运行。");
    }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
}
