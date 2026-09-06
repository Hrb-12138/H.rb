namespace CnInstantTranslator.Config;

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public bool Paused { get; set; }
    public int IdleDelayMs { get; set; } = 1000;
    public int MaxSegmentLength { get; set; } = 200;
    public int CacheCapacity { get; set; } = 5000;
    public int RequestTimeoutSeconds { get; set; } = 3;
    public int BatchSize { get; set; } = 4;
    public bool OfflineMode { get; set; }
    public string TargetLanguage { get; set; } = "en";
    public string SourceLanguage { get; set; } = "zh-CN";

    public List<string> EngineOrder { get; set; } = new()
    {
        "sogou",
        "google",
        "bing",
        "deepl"
    };

    public double OverlayLeft { get; set; } = double.NaN;
    public double OverlayTop { get; set; } = double.NaN;
    public double OverlayWidth { get; set; } = 640;
    public double OverlayHeight { get; set; } = 400;
    public double FontSize { get; set; } = 15;
    public double BackgroundOpacity { get; set; } = 0.88;
    public int BlindClipboardCooldownMs { get; set; } = 1500;
    public bool HideOnScreenshotHotkey { get; set; } = false;
    public List<string> ExcludedProcesses { get; set; } = new()
    {
        "chatgpt",
        "codex",
        "gw"
    };
}
