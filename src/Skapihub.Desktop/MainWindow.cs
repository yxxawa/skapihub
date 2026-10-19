using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Skapihub.Core;

namespace Skapihub.Desktop;

internal sealed class MainWindow : Form
{
    private static readonly Color SurfaceColor = Color.FromArgb(247, 248, 250);
    private readonly Panel _surface = new() { Dock = DockStyle.Fill, BackColor = SurfaceColor };
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = SurfaceColor };
    private readonly StartupSurface _loading = new();
    private readonly System.Windows.Forms.Timer _revealFallback = new() { Interval = 2500 };
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly string _dataDirectory;
    private readonly uint _restoreMessage;
    private GatewayHost? _host;
    private Task? _shutdown;
    private bool _browserRevealed;
    private bool _allowClose;
    public bool RestartRequested { get; private set; }

    public MainWindow(string dataDirectory, uint restoreMessage)
    {
        _dataDirectory = dataDirectory;
        _restoreMessage = restoreMessage;
        Text = "skapihub";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SurfaceColor;
        Size = new Size(1240, 810);
        MinimumSize = new Size(960, 660);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _surface.Controls.Add(_web);
        _surface.Controls.Add(_loading);
        Controls.Add(_surface);
        _loading.BringToFront();
        _revealFallback.Tick += (_, _) => RevealBrowser();
        _trayMenu.Items.Add("显示主界面", null, (_, _) => RestoreWindow());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("退出 skapihub", null, async (_, _) => await ShutdownAsync());
        _tray = new NotifyIcon { Text = "skapihub · 本地 API 中转", Icon = Icon ?? SystemIcons.Application, ContextMenuStrip = _trayMenu };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) RestoreWindow(); };
        Shown += async (_, _) => await InitializeAsync();
    }

    protected override void OnLoad(EventArgs e)
    {
        var workingArea = Screen.FromControl(this).WorkingArea;
        var scale = DeviceDpi / 96d;
        var maximumWidth = Math.Max(320, workingArea.Width - 48);
        var maximumHeight = Math.Max(320, workingArea.Height - 48);
        MinimumSize = new Size(Math.Min((int)(960 * scale), maximumWidth), Math.Min((int)(660 * scale), maximumHeight));
        Size = new Size(Math.Min((int)(1240 * scale), maximumWidth), Math.Min((int)(810 * scale), maximumHeight));
        Location = new Point(workingArea.Left + (workingArea.Width - Width) / 2, workingArea.Top + (workingArea.Height - Height) / 2);
        base.OnLoad(e);
    }

    private async Task InitializeAsync()
    {
        try
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "FFF7F8FA");
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            _loading.StatusText = "正在连接本地服务";
            _host = await GatewayHost.StartAsync(new GatewayOptions(_dataDirectory, Path.Combine(AppContext.BaseDirectory, "wwwroot"), Codec: new WindowsSettingsCodec()));
            _tray.Visible = true;
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dataDirectory, "webview"));
            _loading.StatusText = "正在加载界面";
            await _web.EnsureCoreWebView2Async(environment);
            if (_shutdown is not null || IsDisposed) return;
            var view = _web.CoreWebView2;
            await view.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            if (_shutdown is not null || IsDisposed) return;
            await view.AddScriptToExecuteOnDocumentCreatedAsync("document.addEventListener('DOMContentLoaded',()=>document.documentElement.classList.add('native-frame'),{once:true});");
            view.Settings.AreDefaultContextMenusEnabled = false;
            view.Settings.AreDevToolsEnabled = false;
            view.Settings.IsStatusBarEnabled = false;
            view.Settings.IsZoomControlEnabled = false;
            view.Settings.AreBrowserAcceleratorKeysEnabled = false;
            view.Settings.IsGeneralAutofillEnabled = false;
            view.Settings.IsPasswordAutosaveEnabled = false;
            var cookie = view.CookieManager.CreateCookie("skapihub-session", _host.AdminToken, _host.Address.Host, "/_app");
            cookie.IsHttpOnly = true;
            cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
            view.CookieManager.AddOrUpdateCookie(cookie);
            view.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination) || destination.GetLeftPart(UriPartial.Authority) != _host.Address.GetLeftPart(UriPartial.Authority)) args.Cancel = true;
            };
            view.NewWindowRequested += (_, args) => args.Handled = true;
            view.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            view.WebMessageReceived += OnWebMessage;
            view.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    _web.Visible = true;
                    _loading.BringToFront();
                    if (!_browserRevealed) _revealFallback.Start();
                }
                else _loading.StatusText = "界面加载失败，请从托盘退出后重试";
            };
            view.Navigate(new Uri(_host.Address, _host.Ui.EntryPath).ToString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "请先安装 Microsoft Edge WebView2 Evergreen Runtime，然后重新打开 skapihub。", "缺少 WebView2", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await ShutdownAsync();
        }
        catch (Exception ex)
        {
            if (_shutdown is not null || IsDisposed) return;
            MessageBox.Show(this, "启动失败：" + ex.Message, "skapihub", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await ShutdownAsync();
        }
    }

    private void RevealBrowser()
    {
        if (_browserRevealed || IsDisposed || _shutdown is not null) return;
        _browserRevealed = true;
        _revealFallback.Stop();
        _surface.SuspendLayout();
        _web.Visible = true;
        _web.BringToFront();
        _loading.Visible = false;
        _surface.ResumeLayout(true);
        if (Visible) _web.Focus();
    }

    private void RestoreWindow()
    {
        if (IsDisposed || _shutdown is not null) return;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        _surface.PerformLayout();
        _web.Invalidate(true);
    }

    private void HideToTray()
    {
        if (_shutdown is not null) return;
        _tray.Visible = true;
        Hide();
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_host is null || !args.Source.StartsWith(_host.Address.ToString(), StringComparison.Ordinal)) return;
        try
        {
            using var json = JsonDocument.Parse(args.WebMessageAsJson);
            if (!json.RootElement.TryGetProperty("action", out var action)) return;
            switch (action.GetString())
            {
                case "ready":
                    if (json.RootElement.TryGetProperty("buildId", out var buildId) && buildId.GetString() == _host.Ui.BuildId) RevealBrowser();
                    break;
                case "minimize": WindowState = FormWindowState.Minimized; break;
                case "maximize": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
                case "close": HideToTray(); break;
                case "restart": _ = ShutdownAsync(restart: true); break;
                case "data-folder": System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_dataDirectory) { UseShellExecute = true }); break;
            }
        }
        catch (JsonException) { }
    }

    private Task ShutdownAsync(bool restart = false)
    {
        if (_shutdown is not null) return _shutdown;
        RestartRequested = restart;
        _shutdown = StopAsync();
        return _shutdown;
    }

    private async Task StopAsync()
    {
        _tray.Visible = false;
        _revealFallback.Stop();
        Enabled = false;
        Hide();
        try { if (_host is not null) await _host.DisposeAsync(); }
        finally { _allowClose = true; Close(); }
    }

    protected override void WndProc(ref Message message)
    {
        if (_restoreMessage != 0 && message.Msg == _restoreMessage)
        {
            RestoreWindow();
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            if (e.CloseReason == CloseReason.UserClosing) HideToTray();
            else _ = ShutdownAsync();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _trayMenu.Dispose();
            _revealFallback.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }
}
