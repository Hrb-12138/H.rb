using System.Net.Http;
using System.Text.Json;

namespace CnInstantTranslator.Translation;

public sealed class DeepLTranslationEngine : TranslationEngineBase
{
    private readonly HttpClient _http;
    private readonly string? _authKey;

    public DeepLTranslationEngine(HttpClient http, string? authKey, int cacheCapacity = 5000)
        : base(cacheCapacity)
    {
        _http = http;
        _authKey = authKey;
    }

    public override string Name => "deepl";

    protected override async Task<string> TranslateCoreAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_authKey))
        {
            throw new InvalidOperationException("DeepL 翻译未配置 DEEPL_AUTH_KEY。");
        }

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text"] = text,
            ["source_lang"] = NormalizeSource(sourceLanguage),
            ["target_lang"] = targetLanguage.ToUpperInvariant()
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api-free.deepl.com/v2/translate")
        {
            Content = form
        };
        request.Headers.Add("Authorization", $"DeepL-Auth-Key {_authKey}");

        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement translations = document.RootElement.GetProperty("translations");
        return translations[0].GetProperty("text").GetString() ?? string.Empty;
    }

    private static string NormalizeSource(string language) =>
        language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "ZH" : language.ToUpperInvariant();
}
