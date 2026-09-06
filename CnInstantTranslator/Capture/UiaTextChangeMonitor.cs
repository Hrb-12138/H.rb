using System.Windows.Automation;
using System.Windows.Threading;

namespace CnInstantTranslator.Capture;

/// <summary>
/// 订阅当前可编辑控件的 UIA ValuePattern / TextPattern 变化事件，
/// 让 QQ/浏览器等支持 UIA 的应用不再只依赖 200ms 轮询。
/// </summary>
public sealed class UiaTextChangeMonitor : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly object _gate = new();

    private AutomationElement? _subscribed;
    private AutomationPropertyChangedEventHandler? _valueHandler;
    private AutomationEventHandler? _textHandler;
    private string _lastNotified = string.Empty;
    private long _lastNotifyTick;
    private bool _disposed;

    public UiaTextChangeMonitor(Dispatcher ui)
    {
        _ui = ui;
    }

    public event Action<string>? TextChanged;

    public void ResyncToFocus(IntPtr focusHwnd)
    {
        if (_disposed)
        {
            return;
        }

        AutomationElement? element = null;
        if (focusHwnd != IntPtr.Zero)
        {
            try
            {
                element = AutomationElement.FromHandle(focusHwnd);
            }
            catch (ElementNotAvailableException)
            {
                element = null;
            }
        }

        element ??= ReadFocusedElement();

        lock (_gate)
        {
            if (ReferenceEquals(_subscribed, element))
            {
                return;
            }

            UnsubscribeLocked();
            if (element is not null && LooksEditable(element))
            {
                SubscribeLocked(element);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            UnsubscribeLocked();
            _lastNotified = string.Empty;
        }
    }

    private void SubscribeLocked(AutomationElement element)
    {
        _subscribed = element;
        _valueHandler = OnValueChanged;
        _textHandler = OnTextChanged;

        try
        {
            Automation.AddAutomationPropertyChangedEventHandler(
                element,
                TreeScope.Element,
                _valueHandler,
                ValuePattern.ValueProperty);
        }
        catch
        {
            _valueHandler = null;
        }

        try
        {
            Automation.AddAutomationEventHandler(
                TextPattern.TextChangedEvent,
                element,
                TreeScope.Element,
                _textHandler);
        }
        catch
        {
            _textHandler = null;
        }

        if (_valueHandler is null && _textHandler is null)
        {
            _subscribed = null;
        }
    }

    private void UnsubscribeLocked()
    {
        if (_subscribed is null)
        {
            _valueHandler = null;
            _textHandler = null;
            return;
        }

        try
        {
            if (_valueHandler is not null)
            {
                Automation.RemoveAutomationPropertyChangedEventHandler(
                    _subscribed,
                    _valueHandler);
            }
        }
        catch
        {
        }

        try
        {
            if (_textHandler is not null)
            {
                Automation.RemoveAutomationEventHandler(
                    TextPattern.TextChangedEvent,
                    _subscribed,
                    _textHandler);
            }
        }
        catch
        {
        }

        _subscribed = null;
        _valueHandler = null;
        _textHandler = null;
    }

    private void OnValueChanged(object sender, AutomationPropertyChangedEventArgs e)
    {
        if (e.NewValue is string raw)
        {
            Notify(raw);
        }
        else
        {
            TryReadAndNotify(sender as AutomationElement);
        }
    }

    private void OnTextChanged(object sender, AutomationEventArgs e)
    {
        TryReadAndNotify(sender as AutomationElement);
    }

    private void TryReadAndNotify(AutomationElement? element)
    {
        if (element is null)
        {
            return;
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern)
                && pattern is ValuePattern valuePattern)
            {
                Notify(valuePattern.Current.Value ?? string.Empty);
            }
        }
        catch (ElementNotAvailableException)
        {
        }
    }

    private void Notify(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        long now = Environment.TickCount64;
        if (raw == _lastNotified && now - _lastNotifyTick < 120)
        {
            return;
        }

        _lastNotified = raw;
        _lastNotifyTick = now;

        try
        {
            _ui.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    TextChanged?.Invoke(raw);
                }
            });
        }
        catch
        {
        }
    }

    private static AutomationElement? ReadFocusedElement()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksEditable(AutomationElement element)
    {
        try
        {
            ControlType type = element.Current.ControlType;
            return type == ControlType.Edit
                   || type == ControlType.Document
                   || element.Current.IsKeyboardFocusable
                   || element.Current.HasKeyboardFocus;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            UnsubscribeLocked();
        }
    }
}
