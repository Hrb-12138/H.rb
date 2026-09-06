using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace CnInstantTranslator.Capture;

internal static class UiaTextReader
{
    public static string? TryReadValueText(AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? patternObject)
            && patternObject is ValuePattern valuePattern)
        {
            if (valuePattern.Current.IsReadOnly)
            {
                return null;
            }

            string? value = valuePattern.Current.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public static string? TryReadFocusedValue()
    {
        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            string? text = TryReadValueText(focused) ?? TryReadTextPatternText(focused, 700);
            if (string.IsNullOrWhiteSpace(text) || IsLikelyPageOrMetadata(text))
            {
                return null;
            }

            return text;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    public static string? TryReadFromWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            AutomationElement? focus = AutomationElement.FocusedElement;
            string? best = null;
            int bestScore = int.MinValue;

            if (focus is not null)
            {
                var queue = new Queue<(AutomationElement Element, int Depth)>();
                queue.Enqueue((focus, 0));
                int visited = 0;

                // 先只围绕当前键盘焦点附近读取，避免 QQ 状态栏/聊天记录/页面元数据被当成输入框。
                while (queue.Count > 0 && visited < 48)
                {
                    (AutomationElement element, int depth) = queue.Dequeue();
                    visited++;

                    string? text = TryReadValueText(element);
                    if (string.IsNullOrEmpty(text) && LooksLikeInputCandidate(element))
                    {
                        text = TryReadTextPatternText(element, 700);
                    }

                    if (!string.IsNullOrEmpty(text) && !IsLikelyPageOrMetadata(text))
                    {
                        int score = ScoreInputCandidate(element, text);
                        if (score > bestScore)
                        {
                            best = text;
                            bestScore = score;
                        }
                    }

                    if (depth >= 4)
                    {
                        continue;
                    }

                    try
                    {
                        AutomationElementCollection children = element.FindAll(
                            TreeScope.Children,
                            System.Windows.Automation.Condition.TrueCondition);
                        foreach (AutomationElement child in children)
                        {
                            if (visited + queue.Count >= 48)
                            {
                                break;
                            }

                            queue.Enqueue((child, depth + 1));
                        }
                    }
                    catch (ElementNotAvailableException)
                    {
                        // 控件树在遍历期间变化，跳过该分支。
                    }
                }
            }

            // 部分网页输入框不暴露“焦点元素”，但会暴露可编辑的 ValuePattern。
            // 焦点附近读不到时，做一次只读 ValuePattern 扫描兜底（不读页面正文）。
            return best ?? TryScanWindowForEditableValues(windowHandle);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static string? TryScanWindowForEditableValues(IntPtr windowHandle)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(windowHandle);
            var queue = new Queue<(AutomationElement Element, int Depth)>();
            queue.Enqueue((root, 0));
            string? best = null;
            int bestScore = int.MinValue;
            int visited = 0;

            while (queue.Count > 0 && visited < 64)
            {
                (AutomationElement element, int depth) = queue.Dequeue();
                visited++;

                string? text = TryReadValueText(element);
                if (!string.IsNullOrEmpty(text) && !IsLikelyPageOrMetadata(text))
                {
                    int score = ScoreInputCandidate(element, text);
                    if (score > bestScore)
                    {
                        best = text;
                        bestScore = score;
                    }
                }

                if (depth >= 7)
                {
                    continue;
                }

                try
                {
                    AutomationElementCollection children = element.FindAll(
                        TreeScope.Children,
                        System.Windows.Automation.Condition.TrueCondition);
                    foreach (AutomationElement child in children)
                    {
                        if (visited + queue.Count >= 64)
                        {
                            break;
                        }

                        queue.Enqueue((child, depth + 1));
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
            }

            return best;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static bool LooksLikeInputCandidate(AutomationElement element)
    {
        try
        {
            ControlType type = element.Current.ControlType;
            if (type == ControlType.Edit || type == ControlType.Document)
            {
                return true;
            }

            if (type != ControlType.Custom)
            {
                return false;
            }

            string name = element.Current.Name ?? string.Empty;
            string className = element.Current.ClassName ?? string.Empty;
            return name.Contains("输入", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("消息", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("edit", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("message", StringComparison.OrdinalIgnoreCase)
                   || className.Contains("Edit", StringComparison.OrdinalIgnoreCase)
                   || className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    internal static bool IsLikelyPageOrMetadata(string text)
    {
        string trimmed = text.TrimStart();
        // 按“行数”计，而不是分别数 \r 和 \n，避免 Windows CRLF 一行被算成两次。
        int newlines = text.Replace("\r\n", "\n").Count(static c => c == '\n');
        if (trimmed.Length > 800
            || text.Contains("://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || newlines >= 30)
        {
            return true;
        }

        // 常见壳应用/门户导航文案，不应当成输入框内容。
        int chromeHits = 0;
        string[] chromeMarks =
        {
            "首页", "充值", "使用教程", "平台余额", "点击显示账号", "未激活", "已登录"
        };
        foreach (string mark in chromeMarks)
        {
            if (text.Contains(mark, StringComparison.Ordinal))
            {
                chromeHits++;
            }
        }

        return chromeHits >= 2;
    }

    private static int ScoreInputCandidate(AutomationElement element, string text)
    {
        int score = text.Length;
        try
        {
            if (element.Current.HasKeyboardFocus)
            {
                score += 10000;
            }

            ControlType type = element.Current.ControlType;
            if (type == ControlType.Edit || type == ControlType.Document)
            {
                score += 3000;
            }

            string name = element.Current.Name ?? string.Empty;
            if (name.Contains("输入", StringComparison.OrdinalIgnoreCase)
                || name.Contains("消息", StringComparison.OrdinalIgnoreCase)
                || name.Contains("edit", StringComparison.OrdinalIgnoreCase)
                || name.Contains("message", StringComparison.OrdinalIgnoreCase)
                || name.Contains("compose", StringComparison.OrdinalIgnoreCase))
            {
                score += 2000;
            }

            int newlines = text.Count(static c => c == '\n');
            if (newlines >= 4 || text.Length > 600)
            {
                score -= 6000;
            }
        }
        catch (ElementNotAvailableException)
        {
            // 元素已失效，降低候选分数即可。
            score -= 5000;
        }

        return score;
    }

    public static string? TryReadTextPatternText(AutomationElement? element, int maxLength = 200000)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? patternObject)
                && patternObject is TextPattern textPattern)
            {
                string? text = textPattern.DocumentRange.GetText(maxLength + 1);
                if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
                {
                    return null;
                }

                return text;
            }
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }

        return null;
    }

    public static string? TryReadTextPatternSelection(IntPtr windowHandle)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(windowHandle);
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
            AutomationElement? document = root.FindFirst(TreeScope.Descendants, condition);
            if (document is null)
            {
                return null;
            }

            if (document.TryGetCurrentPattern(TextPattern.Pattern, out object? patternObject)
                && patternObject is TextPattern textPattern)
            {
                TextPatternRange[] ranges = textPattern.GetSelection();
                return ranges.Length == 0 ? null : string.Concat(ranges.Select(static r => r.GetText(1024 * 1024)));
            }
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }

        return null;
    }
}
