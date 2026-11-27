using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace CursorTranslate;

public enum TextMode { Word, Sentence, Line }

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

    // Words ending in "." that don't end a sentence
    static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "e.g", "i.e", "inc", "ltd", "no", "fig"
    };

    static global::Windows.Media.Ocr.OcrEngine? CreateEngine(string language)
    {
        global::Windows.Media.Ocr.OcrEngine? engine = null;
        if (language != "auto")
            engine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                new global::Windows.Globalization.Language(language));
        return engine ?? global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
    }

    // ---------- UI Automation ----------

    /// <summary>Fast and accurate: asks the app itself for the text under the cursor.</summary>
    public static string? FromUIAutomation(int x, int y, TextMode mode)
    {
        try
        {
            var point = new System.Windows.Point(x, y);
            var element = AutomationElement.FromPoint(point);
            if (element == null || element.Current.ProcessId == OwnProcessId) return null;

            // The element under the cursor is often a small text node (e.g. in browsers).
            // Walk up the tree until we find something that exposes the full text.
            AutomationElement? current = element;
            for (int depth = 0; depth < 10 && current != null; depth++)
            {
                if (current.TryGetCurrentPattern(TextPattern.Pattern, out object pattern))
                {
                    var text = ReadFromTextPattern((TextPattern)pattern, point, mode);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                    break;
                }
                current = TreeWalker.ControlViewWalker.GetParent(current);
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

    static string? ReadFromTextPattern(TextPattern pattern, System.Windows.Point point, TextMode mode)
    {
        var atCursor = pattern.RangeFromPoint(point);

        if (mode != TextMode.Sentence)
        {
            atCursor.ExpandToEnclosingUnit(mode == TextMode.Word ? TextUnit.Word : TextUnit.Line);
            return atCursor.GetText(500).Trim();
        }

        // Sentence: get the whole paragraph, find where the cursor is inside it, cut the sentence
        var paragraph = atCursor.Clone();
        paragraph.ExpandToEnclosingUnit(TextUnit.Paragraph);
        string paragraphText = paragraph.GetText(4000);

        var beforeCursor = paragraph.Clone();
        beforeCursor.MoveEndpointByRange(TextPatternRangeEndpoint.End, atCursor, TextPatternRangeEndpoint.Start);
        int offset = beforeCursor.GetText(4000).Length;

        return SentenceAt(paragraphText, offset);
    }

    // ---------- OCR ----------

    /// <summary>Fallback: captures the screen around the cursor and runs Windows OCR on it.</summary>
    public static async Task<string?> FromOcrAsync(int x, int y, string language, TextMode mode)
    {
        // Wide and tall enough to catch full lines and the lines above/below
        const int width = 1400, height = 240;
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

        var engine = engines.GetOrAdd(language, CreateEngine);
        if (engine == null) return null;

        var result = await engine.RecognizeAsync(image);
        var lines = result.Lines.Where(l => l.Words.Count > 0).Select(l => new OcrLineInfo(l)).ToList();
        if (lines.Count == 0) return null;

        // Find the word closest to the cursor (the cursor is at the center of the capture)
        double cx = width / 2.0, cy = height / 2.0, best = double.MaxValue;
        OcrLineInfo? cursorLine = null;
        int wordIndex = -1;
        foreach (var line in lines)
        {
            for (int j = 0; j < line.Words.Count; j++)
            {
                double d = DistanceToRect(cx, cy, line.Words[j].BoundingRect);
                if (d < best) { best = d; cursorLine = line; wordIndex = j; }
            }
        }
        if (cursorLine == null || best > 20 * 20) return null;

        if (mode == TextMode.Word) return cursorLine.Words[wordIndex].Text;
        if (mode == TextMode.Line) return cursorLine.Text;

        // Sentence: keep only lines in the same column, then join the lines of the same paragraph
        var column = lines
            .Where(l => l == cursorLine || (l.Left < cursorLine.Right && cursorLine.Left < l.Right))
            .OrderBy(l => l.Top)
            .ToList();

        int index = column.IndexOf(cursorLine);
        double lineHeight = cursorLine.Bottom - cursorLine.Top;
        int first = index, last = index;
        while (first > 0 && SameParagraph(column[first - 1], column[first], lineHeight)) first--;
        while (last < column.Count - 1 && SameParagraph(column[last], column[last + 1], lineHeight)) last++;

        var sb = new StringBuilder();
        int offset = 0;
        for (int i = first; i <= last; i++)
        {
            if (i == index)
            {
                var wordsBefore = string.Join(" ", column[i].Words.Take(wordIndex).Select(w => w.Text));
                offset = sb.Length + wordsBefore.Length + (wordIndex > 0 ? 1 : 0);
            }
            sb.Append(column[i].Text).Append(' ');
        }
        return SentenceAt(sb.ToString(), offset);
    }

    sealed class OcrLineInfo
    {
        public List<global::Windows.Media.Ocr.OcrWord> Words { get; }
        public string Text { get; }
        public double Left { get; }
        public double Right { get; }
        public double Top { get; }
        public double Bottom { get; }

        public OcrLineInfo(global::Windows.Media.Ocr.OcrLine line)
        {
            Words = line.Words.ToList();
            Text = string.Join(" ", Words.Select(w => w.Text));
            Left = Words.Min(w => w.BoundingRect.X);
            Right = Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            Top = Words.Min(w => w.BoundingRect.Y);
            Bottom = Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
        }
    }

    static bool SameParagraph(OcrLineInfo above, OcrLineInfo below, double lineHeight)
    {
        double gap = below.Top - above.Bottom;
        return gap < lineHeight * 0.9 && Math.Abs(below.Left - above.Left) < lineHeight * 4;
    }

    static double DistanceToRect(double x, double y, global::Windows.Foundation.Rect r)
    {
        double dx = x < r.X ? r.X - x : x > r.X + r.Width ? x - (r.X + r.Width) : 0;
        double dy = y < r.Y ? r.Y - y : y > r.Y + r.Height ? y - (r.Y + r.Height) : 0;
        return dx * dx + dy * dy;
    }

    // ---------- Sentence detection ----------

    /// <summary>Returns the sentence that contains the character at <paramref name="offset"/>.</summary>
    public static string SentenceAt(string text, int offset)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        offset = Math.Clamp(offset, 0, text.Length - 1);

        int start = offset;
        while (start > 0 && !IsSentenceEnd(text, start - 1)) start--;

        int end = offset;
        while (end < text.Length && !IsSentenceEnd(text, end)) end++;
        if (end < text.Length) end++; // include the final punctuation

        var sentence = text[start..end];
        sentence = Regex.Replace(sentence, @"\[\d+\]", ""); // remove citation marks like [10]
        sentence = Regex.Replace(sentence, @"\s+", " ").Trim();
        return sentence.Length <= 400 ? sentence : sentence[..400];
    }

    static bool IsSentenceEnd(string text, int i)
    {
        char c = text[i];
        if (c == '\n' || c == '\r') return true;
        if (c != '.' && c != '!' && c != '?') return false;

        // Must be followed by a space or the end: "3.5", "e.g.," and "U.S.A" don't end a sentence
        if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] != '[') return false;
        if (c != '.') return true;

        // Skip abbreviations ("Mr.", "etc.") and initials ("J. Smith")
        int wordStart = i;
        while (wordStart > 0 && (char.IsLetter(text[wordStart - 1]) || text[wordStart - 1] == '.')) wordStart--;
        string word = text[wordStart..i];
        if (word.Length == 1 && char.IsUpper(word[0])) return false;
        return !Abbreviations.Contains(word);
    }
}