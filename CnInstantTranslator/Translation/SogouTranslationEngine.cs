using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CnInstantTranslator.Translation;

public sealed class SogouTranslationEngine : TranslationEngineBase
{
    private readonly HttpClient _http;
    private static readonly object SecretGate = new();
    private static int _secretCode;
    private static DateTime _secretFetchedUtc = DateTime.MinValue;

    public SogouTranslationEngine(HttpClient http, int cacheCapacity = 5000)
        : base(cacheCapacity)
    {
        _http = http;
    }

    public override string Name => "sogou";

    protected override async Task<string> TranslateCoreAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["from"] = NormalizeSource(sourceLanguage),
            ["to"] = NormalizeTarget(targetLanguage),
            ["text"] = text,
            ["client"] = "pc",
            ["fr"] = "browser_pc",
            ["pid"] = "sogou-dict-vr",
            ["dict"] = "true",
            ["word_group"] = "true",
            ["second_query"] = "true",
            ["needQc"] = "1",
            ["s"] = Sign(NormalizeSource(sourceLanguage), NormalizeTarget(targetLanguage), text, await EnsureSecretCodeAsync(cancellationToken))
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://fanyi.sogou.com/reventondc/translateV3")
        {
            Content = form
        };
        request.Headers.Referrer = new Uri("https://fanyi.sogou.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 CnInstantTranslator/1.0");

        using HttpResponseMessage response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ExtractTranslation(json);
    }

    private static string ExtractTranslation(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (document.RootElement.TryGetProperty("data", out JsonElement data)
            && data.TryGetProperty("translate", out JsonElement translate))
        {
            if (translate.TryGetProperty("errorCode", out JsonElement errorCode)
                && errorCode.ValueKind != JsonValueKind.Undefined)
            {
                string? error = errorCode.ValueKind == JsonValueKind.String
                    ? errorCode.GetString()
                    : errorCode.GetInt32().ToString();
                if (!string.Equals(error, "0", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Sogou returned error code {error}.");
                }
            }

            if (translate.TryGetProperty("dit", out JsonElement dit)
                && dit.ValueKind == JsonValueKind.String)
            {
                string? ditText = dit.GetString();
                if (!string.IsNullOrWhiteSpace(ditText))
                {
                    return ditText.Trim();
                }
            }
        }

        foreach (string name in new[] { "translation", "translatedText", "result", "trans" })
        {
            string? found = FindString(document.RootElement, name);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }

        throw new InvalidOperationException("Sogou response did not contain a translation field.");
    }

    private async Task<int> EnsureSecretCodeAsync(CancellationToken cancellationToken)
    {
        lock (SecretGate)
        {
            if (_secretCode != 0 && DateTime.UtcNow - _secretFetchedUtc < TimeSpan.FromMinutes(30))
            {
                return _secretCode;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://fanyi.sogou.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        string html = await response.Content.ReadAsStringAsync(cancellationToken);
        Match match = Regex.Match(html, "\"secretCode\":(\\d+)");
        if (!match.Success)
        {
            throw new InvalidOperationException("Sogou secretCode not found.");
        }

        int secret = int.Parse(match.Groups[1].Value);
        lock (SecretGate)
        {
            _secretCode = secret;
            _secretFetchedUtc = DateTime.UtcNow;
        }

        return secret;
    }

    private static string Sign(string from, string to, string text, int secret)
    {
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{from}{to}{text}{secret}")))
            .ToLowerInvariant();
    }

    private static string? FindString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                string? nested = FindString(property.Value, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                string? nested = FindString(child, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string NormalizeSource(string language) =>
        language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CHS" : language;

    private static string NormalizeTarget(string language) =>
        language.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en" : language;
}
