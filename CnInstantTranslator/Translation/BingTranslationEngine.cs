using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CnInstantTranslator.Translation;

public sealed class BingTranslationEngine : TranslationEngineBase
{
    private readonly HttpClient _http;
    private readonly string? _subscriptionKey;

    public BingTranslationEngine(HttpClient http, string? subscriptionKey, int cacheCapacity = 5000)
        : base(cacheCapacity)
    {
        _http = http;
        _subscriptionKey = subscriptionKey;
    }

    public override string Name => "bing";

    protected override async Task<string> TranslateCoreAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_subscriptionKey))
        {
            throw new InvalidOperationException("Bing 翻译未配置 BING_TRANSLATOR_KEY。");
        }

        string url = $"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to={Uri.EscapeDataString(targetLanguage)}";
        var payload = new[] { new { Text = text } };
        string body = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", _subscriptionKey);
        request.Headers.Add("Ocp-Apim-Subscription-Region", Environment.GetEnvironmentVariable("BING_TRANSLATOR_REGION") ?? "global");

        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            JsonElement translations = root[0].GetProperty("translations");
            if (translations.GetArrayLength() > 0)
            {
                return translations[0].GetProperty("text").GetString() ?? string.Empty;
            }
        }

        throw new InvalidOperationException("Unexpected Bing Translate response.");
    }
}
