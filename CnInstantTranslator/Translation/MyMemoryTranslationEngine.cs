using System.Net.Http;
using System.Text.Json;

namespace CnInstantTranslator.Translation;

public sealed class MyMemoryTranslationEngine : TranslationEngineBase
{
    private readonly HttpClient _http;

    public MyMemoryTranslationEngine(HttpClient http, int cacheCapacity = 5000)
        : base(cacheCapacity)
    {
        _http = http;
    }

    public override string Name => "mymemory";

    protected override async Task<string> TranslateCoreAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        string url = "https://api.mymemory.translated.net/get"
                     + $"?q={Uri.EscapeDataString(text)}"
                     + $"&langpair={Uri.EscapeDataString(sourceLanguage)}|{Uri.EscapeDataString(targetLanguage)}"
                     + "&de=cninstanttranslator@example.com";

        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString()?.Trim()
               ?? string.Empty;
    }
}
