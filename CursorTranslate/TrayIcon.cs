using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CursorTranslate;

/// <summary>System tray icon with an Open / Enabled / Exit menu.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon icon;
    readonly Forms.ToolStripMenuItem enabledItem;

    public event Action? OpenRequested;
    public event Action<bool>? EnabledChanged;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        enabledItem = new Forms.ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabledItem.CheckedChanged += (_, _) => EnabledChanged?.Invoke(enabledItem.Checked);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(enabledItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        icon = new Forms.NotifyIcon
        {
            Icon = new Drawing.Icon (
                System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream,
                Forms.SystemInformation.SmallIconSize),
            Text = "Cursor Translate (enabled)",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        if (enabledItem.Checked != enabled) enabledItem.Checked = enabled;
        icon.Text = enabled ? "Cursor Translate (enabled)" : "Cursor Translate (paused)";
    }

    public void Notify(string title, string message) =>
        icon.ShowBalloonTip(1500, title, message, Forms.ToolTipIcon.None);

    static Drawing.Icon CreateIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(79, 140, 255));
            g.FillEllipse(background, 0, 0, 31, 31);
            using var font = new Drawing.Font("Segoe UI", 14, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var format = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center
            };
            g.DrawString("Aa", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 0, 32, 32), format);
        }
        IntPtr handle = bmp.GetHicon();
        var result = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        NativeMethods.DestroyIcon(handle);
        return result;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
