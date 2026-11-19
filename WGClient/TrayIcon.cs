using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WGClient;

/// <summary>Tray-Icon (WinForms NotifyIcon) mit grünem/grauem Status.</summary>
public sealed class TrayIcon : IDisposable
{
    static readonly Drawing.Color OnColor = Drawing.Color.FromArgb(46, 204, 113);
    static readonly Drawing.Color OffColor = Drawing.Color.FromArgb(110, 118, 134);

    readonly Forms.NotifyIcon _icon;
    readonly Drawing.Icon _on = CreateIcon(OnColor);
    readonly Drawing.Icon _off = CreateIcon(OffColor);

    public TrayIcon(Action open, Action coffee, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => open());
        menu.Items.Add("☕ Buy me a coffee", null, (_, _) => coffee());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = _off,
            Text = "WG Client – getrennt",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void SetStatus(bool active, string text)
    {
        _icon.Icon = active ? _on : _off;
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _on.Dispose();
        _off.Dispose();
    }

    static Drawing.Icon CreateIcon(Drawing.Color color)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            // Schild-Form
            using var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddLines(new Drawing.PointF[]
            {
                new(16, 2), new(28, 6), new(28, 15),
            });
            path.AddBezier(28, 15, 28, 23, 22, 28, 16, 30);
            path.AddBezier(16, 30, 10, 28, 4, 23, 4, 15);
            path.AddLines(new Drawing.PointF[] { new(4, 15), new(4, 6), new(16, 2) });
            path.CloseFigure();
            using var fill = new Drawing.SolidBrush(color);
            g.FillPath(fill, path);

            // Schloss-Symbol
            using var pen = new Drawing.Pen(Drawing.Color.White, 2.4f);
            g.DrawArc(pen, 11.5f, 8.5f, 9, 9, 180, 180);
            g.DrawLine(pen, 11.5f, 13, 11.5f, 15);
            g.DrawLine(pen, 20.5f, 13, 20.5f, 15);
            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            g.FillRectangle(white, 9.5f, 14.5f, 13, 9.5f);
        }
        var h = bmp.GetHicon();
        try { return (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
