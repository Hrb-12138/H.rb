using System.Text;
using CnInstantTranslator.Native;

namespace CnInstantTranslator.Core;

public sealed class InputMirrorService
{
    private readonly object _gate = new();
    private readonly StringBuilder _builder = new();
    private DateTime _lastFullTextAt = DateTime.MinValue;
    private string _lastUia = string.Empty;
    private string _lastImmResult = string.Empty;
    private bool _wasComposing;

    public string CurrentText
    {
        get
        {
            lock (_gate)
            {
                return _builder.ToString();
            }
        }
    }

    public DateTime LastFullTextAt
    {
        get
        {
            lock (_gate)
            {
                return _lastFullTextAt;
            }
        }
    }

    public void SetFullText(string? text)
    {
        if (text is null)
        {
            return;
        }

        lock (_gate)
        {
            _builder.Clear();
            _builder.Append(text);
            _lastFullTextAt = DateTime.UtcNow;
            _lastUia = text;
        }
    }

    public void AppendPrintable(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            _builder.Append(text);
        }
    }

    public void DeleteLast()
    {
        lock (_gate)
        {
            if (_builder.Length > 0)
            {
                _builder.Length--;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _builder.Clear();
            _lastFullTextAt = DateTime.MinValue;
            _lastUia = string.Empty;
            _lastImmResult = string.Empty;
            _wasComposing = false;
        }
    }

    public bool IsEmpty => string.IsNullOrEmpty(CurrentText);

    public void OnBackspace()
    {
        DeleteLast();
        lock (_gate)
        {
            _lastFullTextAt = DateTime.MinValue;
            _lastUia = string.Empty;
            _lastImmResult = string.Empty;
            _wasComposing = false;
        }
    }

    public void OnDeleteAll()
    {
        Clear();
    }

    public void MarkStale()
    {
        lock (_gate)
        {
            _lastFullTextAt = DateTime.MinValue;
        }
    }

    public string? PullImmSnapshot(IntPtr foreground, IntPtr focus, ImeStateProbe ime, bool returnCurrentWhenEmpty = true)
    {
        string committed = string.Empty;
        string composing = string.Empty;
        bool gotFresh = false;

        foreach (IntPtr hwnd in CandidateHandles(foreground, focus))
        {
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            if (ime.TryReadResult(hwnd, out string result) && result.Length >= committed.Length)
            {
                committed = result;
                gotFresh = true;
            }

            if (ime.TryReadComposition(hwnd, out string comp) && comp.Length >= composing.Length)
            {
                composing = comp;
                gotFresh = true;
            }
        }

        lock (_gate)
        {
            bool composingNow = !string.IsNullOrEmpty(composing);
            if (composingNow && !_wasComposing)
            {
                // 新一轮组词开始，允许再次上屏相同文字。
                _lastImmResult = string.Empty;
            }

            if (!composingNow
                && !string.IsNullOrEmpty(committed)
                && TextUtil.HasChinese(committed)
                && (_wasComposing || !string.Equals(committed, _lastImmResult, StringComparison.Ordinal)))
            {
                // IME 上屏是一个离散事件：只在组词结束边界追加一次，
                // 避免把“新输入的文字”误判成旧镜像的重复后缀。
                _builder.Append(committed);
                _lastImmResult = committed;
            }

            _wasComposing = composingNow;

            if (composingNow && TextUtil.HasChinese(composing))
            {
                string current = _builder.ToString();
                if (string.IsNullOrEmpty(current))
                {
                    return composing;
                }

                if (current.EndsWith(composing, StringComparison.Ordinal))
                {
                    return current;
                }

                return current + composing;
            }

            string text = _builder.ToString();
            if (!gotFresh && !returnCurrentWhenEmpty)
            {
                return null;
            }

            return string.IsNullOrEmpty(text) ? null : text;
        }
    }

    private static IEnumerable<IntPtr> CandidateHandles(IntPtr foreground, IntPtr focus)
    {
        var seen = new HashSet<IntPtr>();

        void Add(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero && seen.Add(hwnd))
            {
            }
        }

        Add(focus);
        Add(foreground);

        if (foreground != IntPtr.Zero)
        {
            uint threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
            var info = new NativeMethods.GUITHREADINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
            };
            if (NativeMethods.GetGUIThreadInfo(threadId, ref info))
            {
                Add(info.hwndFocus);
                Add(info.hwndCaret);
                Add(info.hwndActive);
            }
        }

        return seen;
    }

}
