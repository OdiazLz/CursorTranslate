using System.IO;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace CursorTranslate;

/// <summary>Reads the text under the mouse cursor, first via UI Automation, then via OCR.</summary>
public static class TextUnderCursor
{
    static readonly int OwnProcessId = Environment.ProcessId;

    // OCR engine cache (one engine per language)
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, global::Windows.Media.Ocr.OcrEngine?> engines = new();

    static readonly ControlType[] TextControlTypes =
    {
        ControlType.Text, ControlType.Button, ControlType.MenuItem, ControlType.ListItem,
        ControlType.Hyperlink, ControlType.TabItem, ControlType.TreeItem, ControlType.CheckBox
    };

    static global::Windows.Media.Ocr.OcrEngine? CreateEngine(string language)
    {
        global::Windows.Media.Ocr.OcrEngine? engine = null;
        if (language != "auto")
            engine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                new global::Windows.Globalization.Language(language));
        return engine ?? global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
    }

    /// <summary>Fast and accurate: asks the app itself for the text under the cursor.</summary>
    public static string? FromUIAutomation(int x, int y, bool singleWord)
    {
        try
        {
            var point = new System.Windows.Point(x, y);
            var element = AutomationElement.FromPoint(point);
            if (element == null || element.Current.ProcessId == OwnProcessId) return null;

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern))
            {
                var range = ((TextPattern)pattern).RangeFromPoint(point);
                range.ExpandToEnclosingUnit(singleWord ? TextUnit.Word : TextUnit.Line);
                var text = range.GetText(500).Trim();
                if (text.Length > 0) return text;
            }

            if (TextControlTypes.Contains(element.Current.ControlType))
            {
                var name = element.Current.Name?.Trim();
                if (!string.IsNullOrEmpty(name) && name.Length < 500) return name;
            }
        }
        catch { /* some apps don't respond to UI Automation */ }
        return null;
    }

    /// <summary>Fallback: captures a strip of the screen around the cursor and runs Windows OCR on it.</summary>
    public static async Task<string?> FromOcrAsync(int x, int y, string language, bool singleWord)
    {
        const int width = 600, height = 80;
        using var bmp = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(x - width / 2, y - height / 2, 0, 0, bmp.Size);

        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;

        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        using var image = await decoder.GetSoftwareBitmapAsync(
            global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

        // Reuse the OCR engine: created only once per language
        var engine = engines.GetOrAdd(language, CreateEngine);
        if (engine == null) return null;

        var result = await engine.RecognizeAsync(image);

        // Pick the line closest to the center of the capture (where the cursor is)
        double cx = width / 2.0, cy = height / 2.0, bestDistance = double.MaxValue;
        string? best = null;

        foreach (var line in result.Lines)
        {
            if (singleWord)
            {
                foreach (var word in line.Words)
                {
                    var r = word.BoundingRect;
                    double wdx = cx < r.X ? r.X - cx : cx > r.X + r.Width ? cx - (r.X + r.Width) : 0;
                    double wdy = cy < r.Y ? r.Y - cy : cy > r.Y + r.Height ? cy - (r.Y + r.Height) : 0;
                    double wd = wdx * wdx + wdy * wdy;
                    if (wd < bestDistance) { bestDistance = wd; best = word.Text; }
                }
            }
            double left = line.Words.Min(w => w.BoundingRect.X);
            double top = line.Words.Min(w => w.BoundingRect.Y);
            double right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            double bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            double dx = cx < left ? left - cx : cx > right ? cx - right : 0;
            double dy = cy < top ? top - cy : cy > bottom ? cy - bottom : 0;
            double distance = dx * dx + dy * dy;
            if (distance < bestDistance) { bestDistance = distance; best = line.Text; }
        }
        return bestDistance <= 20 * 20 ? best : null;
    }
}
