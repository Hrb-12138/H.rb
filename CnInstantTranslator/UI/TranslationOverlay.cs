using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CnInstantTranslator.Config;
using CnInstantTranslator.Domain;

namespace CnInstantTranslator.UI;

public sealed class TranslationOverlay : Window
{
    private const double ResizeMargin = 8;
    private const double OverlayMinWidth = 240;
    private const double OverlayMinHeight = 110;
    private const double OverlayMaxWidth = 1000;
    private const double OverlayMaxHeight = 720;

    private readonly AppSettings _settings;
    private readonly TextBlock _text;
    private readonly ScrollViewer _scrollViewer;
    private readonly Border _border;
    private readonly DispatcherTimer _dragHoldTimer;

    private bool _dragArmed;
    private bool _dragStarted;
    private System.Windows.Point _dragStart;

    private ResizeEdge _resizeEdge;
    private bool _isResizing;
    private double _defaultWidth;
    private double _defaultHeight;
    private System.Windows.Point _resizeStart;
    private double _resizeStartLeft;
    private double _resizeStartTop;
    private double _resizeStartWidth;
    private double _resizeStartHeight;

    public TranslationOverlay(AppSettings settings)
    {
        _settings = settings;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        Background = System.Windows.Media.Brushes.Transparent;
        Width = Math.Clamp(settings.OverlayWidth, OverlayMinWidth, OverlayMaxWidth);
        Height = Math.Clamp(settings.OverlayHeight, OverlayMinHeight, OverlayMaxHeight);
        _defaultWidth = Width;
        _defaultHeight = Height;
        Left = double.IsNaN(settings.OverlayLeft) ? 80 : settings.OverlayLeft;
        Top = double.IsNaN(settings.OverlayTop) ? 80 : settings.OverlayTop;
        ShowActivated = false;

        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
            (byte)Math.Clamp(settings.BackgroundOpacity * 255, 20, 255),
            18,
            22,
            28));

        _text = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = settings.FontSize,
            TextWrapping = TextWrapping.Wrap,
            Text = "中文即时翻译悬浮窗"
        };

        _scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _text
        };

        _border = new Border
        {
            Background = brush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = _scrollViewer
        };

        Content = _border;

        _dragHoldTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _dragHoldTimer.Tick += (_, _) =>
        {
            _dragHoldTimer.Stop();
            if (_dragArmed && System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                _dragStarted = true;
                CaptureMouse();
            }
            else
            {
                _dragArmed = false;
            }
        };

        AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseLeftButtonDown), true);
        AddHandler(MouseMoveEvent, new System.Windows.Input.MouseEventHandler(OnMouseMove), true);
        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseLeftButtonUp), true);
        MouseWheel += OnMouseWheel;

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "隐藏/显示翻译框", Command = new ShowHideCommand(this) });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "退出", Command = new ExitCommand() });
        ContextMenu = menu;
    }

    public event Action<AppSettings>? SettingsChanged;

    public void SetLoading()
    {
        _text.Text = "翻译中…";
        AutoFit("翻译中…");
    }

    public void ShowUpdate(TranslationUpdate update)
    {
        string[] segments = update.Segments
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s!)
            .ToArray();

        // 最终完成时优先显示整段连贯翻译，保证与 CapsLock 回填内容一致；
        // 若整段结果没有自带换行而源文本有多个片段，则回退到分段展示，
        // 避免其它应用场景里悬浮窗变成“一坨字”。
        bool fullHasOwnBreaks = !string.IsNullOrWhiteSpace(update.FullTranslation)
                                && (update.FullTranslation.Contains('\n') || update.FullTranslation.Contains('\r'));
        string? display;
        if (!update.IsPartial
            && !string.IsNullOrWhiteSpace(update.FullTranslation)
            && (segments.Length <= 1 || fullHasOwnBreaks))
        {
            display = update.FullTranslation;
        }
        else if (segments.Length > 0)
        {
            display = string.Join("\n\n", segments);
        }
        else
        {
            display = update.FullTranslation ?? update.Status;
        }

        if (string.IsNullOrWhiteSpace(display))
        {
            display = update.Status;
        }

        string formatted = NormalizeParagraphs(display);
        _text.Text = formatted;
        AutoFit(formatted);
    }

    public void ShowError(string message)
    {
        _text.Text = $"⚠ {message}";
        AutoFit(_text.Text);
    }

    public void Clear()
    {
        _text.Text = string.Empty;
        AutoFit(string.Empty);
    }

    public void HideForScreenshot()
    {
        if (Visibility == Visibility.Visible)
        {
            Hide();
        }
    }

    public void RestoreFromScreenshot()
    {
        if (Visibility != Visibility.Visible)
        {
            Show();
        }
    }

    private void AutoFit(string text)
    {
        string[] lines = string.IsNullOrEmpty(text)
            ? new[] { string.Empty }
            : text.Replace("\r\n", "\n").Split('\n');

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch);
        double maxLineWidth = 0;

        foreach (string line in lines)
        {
            var formatted = new FormattedText(
                line,
                CultureInfo.GetCultureInfo("zh-CN"),
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                _text.FontSize,
                System.Windows.Media.Brushes.White,
                pixelsPerDip);
            maxLineWidth = Math.Max(maxLineWidth, formatted.WidthIncludingTrailingWhitespace);
        }

        double lineHeight = _text.FontSize * 1.55;
        double defaultHeight = Math.Max(_defaultHeight, OverlayMinHeight);
        double desiredWidth = Math.Clamp(maxLineWidth + 52, _defaultWidth, OverlayMaxWidth);
        double desiredHeight = Math.Clamp(
            Math.Max(2, lines.Length) * lineHeight + 36,
            defaultHeight,
            OverlayMaxHeight);

        Width = desiredWidth;
        Height = desiredHeight;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideScrollBar(e.OriginalSource as DependencyObject))
        {
            return;
        }

        System.Windows.Point point = e.GetPosition(this);
        ResizeEdge edge = GetResizeEdge(point);
        if (edge != ResizeEdge.None)
        {
            _resizeEdge = edge;
            _isResizing = true;
            _resizeStart = point;
            _resizeStartLeft = Left;
            _resizeStartTop = Top;
            _resizeStartWidth = Width;
            _resizeStartHeight = Height;
            CaptureMouse();
            return;
        }

        _dragArmed = true;
        _dragStarted = false;
        _dragStart = point;
        _dragHoldTimer.Stop();
        _dragHoldTimer.Start();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        System.Windows.Point current = e.GetPosition(this);

        if (_isResizing)
        {
            double dx = current.X - _resizeStart.X;
            double dy = current.Y - _resizeStart.Y;
            ApplyResize(dx, dy);
            return;
        }

        if (_dragStarted)
        {
            Left += current.X - _dragStart.X;
            Top += current.Y - _dragStart.Y;
            return;
        }

        Cursor = GetResizeEdge(current) switch
        {
            ResizeEdge.Left or ResizeEdge.Right => System.Windows.Input.Cursors.SizeWE,
            ResizeEdge.Top or ResizeEdge.Bottom => System.Windows.Input.Cursors.SizeNS,
            ResizeEdge.TopLeft or ResizeEdge.BottomRight => System.Windows.Input.Cursors.SizeNWSE,
            ResizeEdge.TopRight or ResizeEdge.BottomLeft => System.Windows.Input.Cursors.SizeNESW,
            _ => System.Windows.Input.Cursors.Arrow
        };
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragHoldTimer.Stop();
        _dragArmed = false;
        ReleaseMouseCapture();

        if (_isResizing)
        {
            _isResizing = false;
            _resizeEdge = ResizeEdge.None;
            _defaultWidth = Width;
            _defaultHeight = Height;
            SavePositionAndSize();
            return;
        }

        if (_dragStarted)
        {
            SavePositionAndSize();
            _dragStarted = false;
        }
    }

    private void ApplyResize(double dx, double dy)
    {
        if (_resizeEdge.HasFlag(ResizeEdge.Left))
        {
            double newWidth = Math.Clamp(_resizeStartWidth - dx, OverlayMinWidth, OverlayMaxWidth);
            Left = _resizeStartLeft + (_resizeStartWidth - newWidth);
            Width = newWidth;
        }
        else if (_resizeEdge.HasFlag(ResizeEdge.Right))
        {
            Width = Math.Clamp(_resizeStartWidth + dx, OverlayMinWidth, OverlayMaxWidth);
        }

        if (_resizeEdge.HasFlag(ResizeEdge.Top))
        {
            double newHeight = Math.Clamp(_resizeStartHeight - dy, OverlayMinHeight, OverlayMaxHeight);
            Top = _resizeStartTop + (_resizeStartHeight - newHeight);
            Height = newHeight;
        }
        else if (_resizeEdge.HasFlag(ResizeEdge.Bottom))
        {
            Height = Math.Clamp(_resizeStartHeight + dy, OverlayMinHeight, OverlayMaxHeight);
        }
    }

    private ResizeEdge GetResizeEdge(System.Windows.Point point)
    {
        bool left = point.X <= ResizeMargin;
        bool right = point.X >= ActualWidth - ResizeMargin;
        bool top = point.Y <= ResizeMargin;
        bool bottom = point.Y >= ActualHeight - ResizeMargin;

        if (left && top)
        {
            return ResizeEdge.TopLeft;
        }

        if (right && top)
        {
            return ResizeEdge.TopRight;
        }

        if (left && bottom)
        {
            return ResizeEdge.BottomLeft;
        }

        if (right && bottom)
        {
            return ResizeEdge.BottomRight;
        }

        if (left)
        {
            return ResizeEdge.Left;
        }

        if (right)
        {
            return ResizeEdge.Right;
        }

        if (top)
        {
            return ResizeEdge.Top;
        }

        if (bottom)
        {
            return ResizeEdge.Bottom;
        }

        return ResizeEdge.None;
    }

    private static bool IsInsideScrollBar(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ScrollBar or Thumb)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _scrollViewer.ScrollToVerticalOffset(_scrollViewer.VerticalOffset - e.Delta);
    }

    private void SavePositionAndSize()
    {
        _settings.OverlayLeft = Left;
        _settings.OverlayTop = Top;
        _settings.OverlayWidth = Width;
        _settings.OverlayHeight = Height;
        SettingsChanged?.Invoke(_settings);
    }

    private static string NormalizeParagraphs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string[] lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        var builder = new System.Text.StringBuilder();
        bool anyParagraph = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (anyParagraph)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append(trimmed);
            anyParagraph = true;
        }

        return builder.ToString();
    }

    [Flags]
    private enum ResizeEdge
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        TopLeft = Top | Left,
        TopRight = Top | Right,
        BottomLeft = Bottom | Left,
        BottomRight = Bottom | Right
    }

    private sealed class ShowHideCommand : ICommand
    {
        private readonly TranslationOverlay _owner;

        public ShowHideCommand(TranslationOverlay owner)
        {
            _owner = owner;
        }

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (_owner.Visibility == Visibility.Visible)
            {
                _owner.Hide();
            }
            else
            {
                _owner.Show();
            }
        }
    }

    private sealed class ExitCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            System.Windows.Application.Current.Shutdown();
        }
    }
}
