using System.Drawing.Drawing2D;

namespace Skapihub.Desktop;

internal sealed class StartupSurface : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 32 };
    private readonly Bitmap? _logo;
    private float _angle;
    private string _statusText = "正在启动";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string StatusText { get => _statusText; set { _statusText = value; Invalidate(); } }

    public StartupSurface()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(247, 248, 250);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        _logo = icon?.ToBitmap();
        _timer.Tick += (_, _) => { _angle = (_angle + 12) % 360; if (Visible) Invalidate(); };
        VisibleChanged += (_, _) => { if (Visible) _timer.Start(); else _timer.Stop(); };
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var center = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
        if (_logo is not null) e.Graphics.DrawImage(_logo, center.X - 83 * scale, center.Y - 32 * scale, 36 * scale, 36 * scale);
        using var titleFont = new Font("Segoe UI", 20, FontStyle.Bold);
        using var textFont = new Font("Microsoft YaHei UI", 9);
        TextRenderer.DrawText(e.Graphics, "skapihub", titleFont, new Point((int)(center.X - 39 * scale), (int)(center.Y - 31 * scale)), Color.FromArgb(48, 45, 65), TextFormatFlags.NoPadding);
        using var pen = new Pen(Color.FromArgb(134, 120, 203), 1.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawArc(pen, center.X - 61 * scale, center.Y + 25 * scale, 11 * scale, 11 * scale, _angle, 270);
        TextRenderer.DrawText(e.Graphics, _statusText, textFont, new Point((int)(center.X - 41 * scale), (int)(center.Y + 22 * scale)), Color.FromArgb(158, 153, 174), TextFormatFlags.NoPadding);
        base.OnPaint(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Stop(); _timer.Dispose(); _logo?.Dispose(); }
        base.Dispose(disposing);
    }
}
