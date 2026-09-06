namespace CnInstantTranslator.Translation;

/// <summary>
/// 离线模式预留接口：本实现绝不发起网络请求。
/// 生产环境可将此引擎替换为本地 ONNX/Whisper/LLM 模型适配器。
/// </summary>
public sealed class OfflineTranslationEngine : TranslationEngineBase
{
    private static readonly IReadOnlyDictionary<string, string> DemoDictionary =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["你好"] = "Hello",
            ["谢谢"] = "Thank you",
            ["再见"] = "Goodbye"
        };

    public OfflineTranslationEngine(int cacheCapacity = 5000)
        : base(cacheCapacity)
    {
    }

    public override string Name => "offline";

    protected override Task<string> TranslateCoreAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (DemoDictionary.TryGetValue(text.Trim(), out string? translated))
        {
            return Task.FromResult(translated);
        }

        throw new InvalidOperationException("离线模型未配置，无法翻译该文本。");
    }
}
