using System.Net.Http;
using System.Text.Json;

namespace CnInstantTranslator.Translation;

public sealed class GoogleTranslationEngine : TranslationEngineBase
{
    private readonly HttpClient _http;

    public GoogleTranslationEngine(HttpClient http, int cacheCapacity = 5000)
        : base(cacheCapacity)
    {
        _http = http;
    }

    public override string Name => "google";

    protected override async Task<string> TranslateCoreAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        string url = "https://translate.googleapis.com/translate_a/single"
                     + $"?client=gtx&sl={Uri.EscapeDataString(sourceLanguage)}"
                     + $"&tl={Uri.EscapeDataString(targetLanguage)}&dt=t&q={Uri.EscapeDataString(text)}";

        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array
            && root.GetArrayLength() > 0
            && root[0].ValueKind == JsonValueKind.Array)
        {
            var builder = new System.Text.StringBuilder();
            foreach (JsonElement segment in root[0].EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0)
                {
                    builder.Append(segment[0].GetString());
                }
            }

            return builder.ToString();
        }

        throw new InvalidOperationException("Unexpected Google Translate response.");
    }
}
