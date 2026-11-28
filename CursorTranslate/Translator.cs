using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;

namespace CursorTranslate;

/// <summary>Translates text using Google (unofficial endpoint) with MyMemory as a fallback.</summary>
public class Translator
{
    static readonly HttpClient http = new(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10) // keep the connection warm
    })
    { Timeout = TimeSpan.FromSeconds(8) };

    readonly Dictionary<string, string> cache = new();

    readonly string deepLApiKey;

    public Translator(string deepLApiKey = "")
    {
        this.deepLApiKey = deepLApiKey;
    }

    // If Google answers 429 (Too Many Requests), let it rest for a while
    DateTime googleBlockedUntil = DateTime.MinValue;
    static readonly TimeSpan GooglePause = TimeSpan.FromMinutes(2);

    // Minimum time between requests, to avoid hitting rate limits
    DateTime lastRequest = DateTime.MinValue;
    static readonly TimeSpan MinTimeBetweenRequests = TimeSpan.FromMilliseconds(300);

    /// <summary>Which service handled the last translation (shown in the status bar).</summary>
    public string LastService { get; private set; } = "";

    public async Task<string> TranslateAsync(string text, string source, string target)
    {
        text = text.Trim();

        // Don't waste requests on things with no letters (numbers, symbols, icons)
        if (text.Length < 2 || !text.Any(char.IsLetter))
        {
            LastService = "not translated";
            return text;
        }

        // Very long text: trim it to save quota
        if (text.Length > 400) text = text[..400];

        var key = $"{source}|{target}|{text}";
        if (cache.TryGetValue(key, out var cached))
        {
            LastService = "cache";
            return cached;
        }

        var wait = lastRequest + MinTimeBetweenRequests - DateTime.Now;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        lastRequest = DateTime.Now;

        string? result = null;

        string? deepLError = null;

        if (!string.IsNullOrWhiteSpace(deepLApiKey))
        {
            try
            {
                result = await TranslateWithDeepLAsync(text, source, target);
                LastService = "DeepL";
            }catch (Exception ex)
            {
                deepLError = ex.Message;
            }
        }

        if (result == null && DateTime.Now >= googleBlockedUntil)
        {
            try
            {
                result = await TranslateWithGoogleAsync(text, source, target);
                LastService = "Google";
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                googleBlockedUntil = DateTime.Now + GooglePause;
            }
        }

        // Fallback: MyMemory (free, no key, ~5,000 characters per day)
        if (result == null)
        {
            result = await TranslateWithMyMemoryAsync(text, source, target);
            LastService = "MyMemory (Google paused)";
        }

        if (deepLError != null) LastService += $" (DeepL failed: {deepLError})";
        if (string.IsNullOrWhiteSpace(result)) return text;
        cache[key] = result;
        return result;
    }

    static async Task<string?> TranslateWithGoogleAsync(string text, string source, string target)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t" +
                  $"&sl={source}&tl={target}&q={Uri.EscapeDataString(text)}";

        var json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        var parts = doc.RootElement[0];
        if (parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Array &&
                    part.GetArrayLength() > 0 &&
                    part[0].ValueKind == JsonValueKind.String)
                {
                    sb.Append(part[0].GetString());
                }
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    static async Task<string?> TranslateWithMyMemoryAsync(string text, string source, string target)
    {
        // MyMemory doesn't accept "auto": guess the opposite of the target language
        var from = source == "auto" ? (target == "en" ? "es" : "en") : source;
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={from}|{target}";

        var json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("responseData", out var data) &&
            data.TryGetProperty("translatedText", out var translated) &&
            translated.ValueKind == JsonValueKind.String)
        {
            return WebUtility.HtmlDecode(translated.GetString());
        }
        return null;
    }

    async Task<string?> TranslateWithDeepLAsync(string text, string source, string target)
    {
        var host = deepLApiKey.EndsWith(":fx") ? "https://api-free.deepl.com" : "https://api.deepl.com";

        var body = new Dictionary<string, object>
        {
            ["text"] = new[] { text },
            ["target_lang"] = ToDeepLTarget(target)
        };
        if (source != "auto") body["source_lang"] = source.ToUpperInvariant();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/v2/translate")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("DeepL-Auth-Key", deepLApiKey);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString();
    }

    static string ToDeepLTarget(string target) => target switch
    {
        "en" => "EN-US",
        "pt" => "PT-BR",
        _ => target.ToUpperInvariant()
    };
}