using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CursorTranslate;

public partial class MainWindow : Window
{
    const int PollIntervalMs = 50;      // how often the mouse position is checked
    const int RestDelayMs = 250;        // how long the mouse must rest (Ctrl mode)
    const int RestDelayNoCtrlMs = 450;  // longer without Ctrl, to avoid too many requests
    const int JitterTolerancePx = 3;    // tiny mouse movements don't count
    const int HotkeyId = 1;

    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
    readonly TooltipWindow tooltip;
    readonly Translator translator = new();
    readonly TrayIcon tray;

    NativeMethods.POINT lastPosition;
    DateTime restingSince = DateTime.Now;
    bool busy, alreadyProcessed, exiting;

    public MainWindow()
    {
        InitializeComponent();
        tooltip = new TooltipWindow();

        tray = new TrayIcon();
        tray.OpenRequested += ShowMainWindow;
        tray.EnabledChanged += enabled => EnabledToggle.IsChecked = enabled;
        tray.ExitRequested += ExitApp;

        EnabledToggle.Checked += (_, _) => tray.SetEnabled(true);
        EnabledToggle.Unchecked += (_, _) => tray.SetEnabled(false);

        SourceInitialized += (_, _) => RegisterHotkey();
        Closing += OnClosing;

        timer.Tick += OnTick;
        timer.Start();

        _ = WarmUpAsync();
    }

    // Opens the connection to the translation service and initializes UI Automation
    // so the first real translation isn't slow
    async Task WarmUpAsync()
    {
        try
        {
            await translator.TranslateAsync("hello", "en", "es");
            await Task.Run(() => TextUnderCursor.FromUIAutomation(0, 0));
        }
        catch { }
    }

    // ---------- Window chrome ----------

    void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // The close button sends the app to the tray instead of exiting
    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        HideTooltip();
        Hide();
    }

    void ExitApp()
    {
        exiting = true;
        NativeMethods.UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
        tray.Dispose();
        Application.Current.Shutdown();
    }

    // ---------- Global hotkey (Ctrl+Alt+T) ----------

    void RegisterHotkey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        bool ok = NativeMethods.RegisterHotKey(hwnd, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_T);
        if (!ok) StatusText.Text = "Couldn't register Ctrl+Alt+T (another app is using it).";
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            bool enabled = EnabledToggle.IsChecked != true;
            EnabledToggle.IsChecked = enabled;
            tray.Notify("Cursor Translate", enabled ? "Enabled" : "Paused");
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------- Main loop ----------

    static string GetSelectedTag(ComboBox combo) => (string)((ComboBoxItem)combo.SelectedItem).Tag;

    void HideTooltip() { if (tooltip.IsVisible) tooltip.Hide(); }

    async void OnTick(object? sender, EventArgs e)
    {
        if (EnabledToggle.IsChecked != true) { HideTooltip(); return; }

        bool ctrlOnly = CtrlOnlyToggle.IsChecked == true;
        bool ctrlDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        if (ctrlOnly && !ctrlDown) { HideTooltip(); alreadyProcessed = false; return; }

        // Don't translate our own window
        if (IsMouseOver) { HideTooltip(); return; }

        NativeMethods.GetCursorPos(out var p);
        if (Math.Abs(p.X - lastPosition.X) > JitterTolerancePx || Math.Abs(p.Y - lastPosition.Y) > JitterTolerancePx)
        {
            lastPosition = p;
            restingSince = DateTime.Now;
            alreadyProcessed = false;
            HideTooltip();
            return;
        }

        int restDelay = ctrlOnly ? RestDelayMs : RestDelayNoCtrlMs;
        if (busy || alreadyProcessed || (DateTime.Now - restingSince).TotalMilliseconds < restDelay) return;

        busy = true;
        alreadyProcessed = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            int x = p.X, y = p.Y;
            string source = GetSelectedTag(SourceLanguage), target = GetSelectedTag(TargetLanguage);

            string? text = await Task.Run(() => TextUnderCursor.FromUIAutomation(x, y));
            string method = "UI Automation";

            if (string.IsNullOrWhiteSpace(text))
            {
                string ocrLanguage = source == "en" ? "en-US" : source == "es" ? "es-MX" : "auto";
                text = await Task.Run(() => TextUnderCursor.FromOcrAsync(x, y, ocrLanguage));
                method = "OCR";
            }
            long readMs = stopwatch.ElapsedMilliseconds;

            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText.Text = $"No text found under the cursor ({readMs} ms).";
                return;
            }

            // Show the bubble right away while the translation arrives
            if (!MouseMoved(x, y)) tooltip.ShowAt("Translating…", text, x, y);

            var translation = await translator.TranslateAsync(text, source, target);
            long translateMs = stopwatch.ElapsedMilliseconds - readMs;

            if (MouseMoved(x, y)) { HideTooltip(); return; }

            tooltip.ShowAt(translation, text, x, y);
            StatusText.Text = $"{method} → {translator.LastService}: read {readMs} ms · translate {translateMs} ms";
        }
        catch (Exception ex)
        {
            HideTooltip();
            StatusText.Text = "Error: " + ex.Message;
        }
        finally
        {
            busy = false;
        }
    }

    static bool MouseMoved(int x, int y)
    {
        NativeMethods.GetCursorPos(out var now);
        return Math.Abs(now.X - x) > 5 || Math.Abs(now.Y - y) > 5;
    }
}
