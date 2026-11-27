using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CursorTranslate;

/// <summary>Floating bubble that shows the translation next to the cursor.</summary>
public partial class TooltipWindow : Window
{
    const int OffsetX = 16;  // default distance to the right of the cursor
    const int OffsetY = 20;  // default distance below the cursor
    const int Gap = 8;       // distance when flipped to the left / above

    public TooltipWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // Don't steal focus or block mouse clicks
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
        };
    }

    public void ShowAt(string translation, string original, int x, int y)
    {
        TranslationText.Text = translation;
        OriginalText.Text = original;
        if (!IsVisible) Show();

        // Make sure the window has resized to the new text before measuring it
        UpdateLayout();

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.GetWindowRect(hwnd, out var rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        var (left, top) = ComputePosition(x, y, width, height);

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Places the bubble below-right of the cursor, flipping it to the left and/or above
    /// when it would go past the edge of the monitor the cursor is on (multi-monitor and DPI aware).
    /// </summary>
    static (int Left, int Top) ComputePosition(int x, int y, int width, int height)
    {
        int left = x + OffsetX;
        int top = y + OffsetY;

        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y },
                                                     NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return (left, top);

        var work = info.rcWork; // screen area without the taskbar

        if (left + width > work.Right) left = x - width - Gap;    // flip to the left
        if (top + height > work.Bottom) top = y - height - Gap;   // flip above

        // Final safety net: never leave the work area
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));

        return (left, top);
    }
}
