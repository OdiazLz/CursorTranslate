using System.Windows;
using System.Windows.Interop;

namespace CursorTranslate;

/// <summary>Floating bubble that shows the translation next to the cursor.</summary>
public partial class TooltipWindow : Window
{
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
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x + 16, y + 20, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }
}
