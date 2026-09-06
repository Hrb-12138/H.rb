using CnInstantTranslator.Domain;

namespace CnInstantTranslator.Core;

public sealed class AppCaptureClassifier
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge",
        "chrome",
        "firefox",
        "opera",
        "brave",
        "iexplore",
        "360se",
        "360chrome",
        "sogouexplorer"
    };

    private static readonly HashSet<string> DocumentProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "winword",
        "wps",
        "et",
        "wpp"
    };

    public CaptureKind Classify(FocusedContext context)
    {
        string process = context.ProcessName;
        string windowClass = context.WindowClassName;

        if (IsQq(process, windowClass))
        {
            return CaptureKind.UiaChat;
        }

        if (IsWeChat(process, windowClass))
        {
            return CaptureKind.QtBlind;
        }

        if (IsBrowser(process, windowClass))
        {
            return CaptureKind.WebView;
        }

        if (DocumentProcesses.Contains(process))
        {
            return CaptureKind.Document;
        }

        if (IsWin32Edit(process, windowClass))
        {
            return CaptureKind.Win32Edit;
        }

        return CaptureKind.Generic;
    }

    private static bool IsQq(string process, string windowClass)
    {
        if (process.Length == 0)
        {
            return false;
        }

        return process.StartsWith("qq", StringComparison.OrdinalIgnoreCase)
               || process.Equals("tim", StringComparison.OrdinalIgnoreCase)
               || windowClass.Contains("qq", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWeChat(string process, string windowClass)
    {
        if (process.Length == 0)
        {
            return windowClass.Contains("wechat", StringComparison.OrdinalIgnoreCase)
                   || windowClass.Contains("weixin", StringComparison.OrdinalIgnoreCase);
        }

        return process.Contains("wechat", StringComparison.OrdinalIgnoreCase)
               || process.Contains("weixin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowser(string process, string windowClass)
    {
        if (BrowserProcesses.Contains(process))
        {
            return true;
        }

        return windowClass.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)
               || windowClass.Contains("MozillaWindowClass", StringComparison.OrdinalIgnoreCase)
               || windowClass.Contains("Internet Explorer_Server", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWin32Edit(string process, string windowClass)
    {
        if (windowClass.Contains("edit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return process.Equals("notepad", StringComparison.OrdinalIgnoreCase)
               || process.Equals("wordpad", StringComparison.OrdinalIgnoreCase);
    }
}
