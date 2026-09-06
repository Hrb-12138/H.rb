using System.IO;

namespace CnInstantTranslator.Core;

/// <summary>
/// 轻量本地调试日志，路径位于 %APPDATA%\CnInstantTranslator\debug.log。
/// 只用于测试和排障，不参与翻译逻辑。
/// </summary>
public static class DebugLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CnInstantTranslator",
        "debug.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
            }
        }
        catch
        {
            // 日志失败不能影响主流程。
        }
    }
}
