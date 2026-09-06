using System.Windows;
using System.Net.Http;
using System.Windows.Threading;
using CnInstantTranslator.Capture;
using CnInstantTranslator.Config;
using CnInstantTranslator.Domain;
using CnInstantTranslator.Native;
using CnInstantTranslator.Translation;
using CnInstantTranslator.UI;

namespace CnInstantTranslator.Core;

public sealed class AppController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly TranslationOverlay _overlay;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _screenshotRestoreTimer;

    private readonly AppCaptureClassifier _classifier = new();
    private readonly FocusTracker _focusTracker;
    private readonly LowLevelKeyboardHook _keyboard = new();
    private readonly LowLevelMouseHook _mouse = new();
    private readonly ImeStateProbe _ime = new();
    private readonly InputMirrorService _mirror = new();
    private readonly IdleOrSpaceTrigger _idle;
    private readonly BlindClipboardCapture _blindCapture;
    private readonly CaptureAdapterFactory _adapterFactory;
    private readonly TranslationRouter _router;
    private readonly TranslationScheduler _scheduler;
    private readonly TextReplacer _replacer;
    private readonly TrayIconManager _tray;
    private readonly UiaTextChangeMonitor _uiaMonitor;

    private FocusedContext _currentContext = FocusedContext.Empty;
    private CancellationTokenSource? _translationCts;
    private string _lastScheduledSource = string.Empty;
    private string _lastEnglish = string.Empty;
    private TranslationUpdate? _lastDisplayUpdate;
    private bool _translateInFlight;
    private string _lastNoResultMessage = string.Empty;
    private DateTime _lastNoResultNoticeAt = DateTime.MinValue;
    private readonly DispatcherTimer _weChatTypingTimer;
    private DateTime _lastUiaTextUtc = DateTime.MinValue;
    private bool _captureInFlight;
    private long _translationVersion;
    private bool _uiaNeedsResync;
    private DateTime _lastEmptyCaptureUtc = DateTime.MinValue;
    private bool _weChatSelectAllPending;
    private DateTime _weChatSelectAllAtUtc = DateTime.MinValue;

    public AppController(AppSettings settings, TranslationOverlay overlay)
    {
        _settings = settings;
        _overlay = overlay;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _uiaMonitor = new UiaTextChangeMonitor(_dispatcher);
        _uiaMonitor.TextChanged += OnUiaTextChanged;

        _focusTracker = new FocusTracker(_classifier);
        _idle = new IdleOrSpaceTrigger(settings.IdleDelayMs);
        _blindCapture = new BlindClipboardCapture(
            _mirror,
            _idle,
            _keyboard,
            _mouse,
            _ime,
            settings.BlindClipboardCooldownMs,
            HasRecentInputForClipboardCapture);
        _adapterFactory = new CaptureAdapterFactory(_mirror, _blindCapture, _ime);
        _replacer = new TextReplacer(_keyboard, _ime);

        TranslationRouter router = CreateRouter(settings);
        _router = router;
        _scheduler = new TranslationScheduler(router, settings.MaxSegmentLength, settings.BatchSize);

        _pollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            DispatcherPriority.Background,
            OnPoll,
            _dispatcher);

        var screenshotRestoreTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1800)
        };
        screenshotRestoreTimer.Tick += (_, _) =>
        {
            screenshotRestoreTimer.Stop();
            _overlay.RestoreFromScreenshot();
        };
        _screenshotRestoreTimer = screenshotRestoreTimer;

        _weChatTypingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _weChatTypingTimer.Tick += (_, _) =>
        {
            _weChatTypingTimer.Stop();
            if (!_ime.IsComposing(_currentContext.FocusedControlHandle)
                && !_keyboard.IsModifierDown
                && !_mouse.IsLeftButtonDown)
            {
                _dispatcher.BeginInvoke(CaptureAndSchedule, DispatcherPriority.Background);
            }
        };

        _overlay.SettingsChanged += SettingsStore.Save;
        _tray = new TrayIconManager(
            ToggleOverlay,
            TogglePause,
            settings.Paused,
            () => System.Windows.Application.Current.Shutdown());
    }

    public void Start()
    {
        _keyboard.KeyDown += OnKeyDown;
        _keyboard.KeyUp += OnKeyUp;
        _mouse.LeftButtonDown += OnMouseActivity;
        _mouse.LeftButtonUp += OnMouseActivity;
        _idle.IdleElapsed += OnIdleElapsed;
        _idle.BoundaryKeyPressed += OnBoundaryKeyPressed;

        _keyboard.Start();
        _mouse.Start();
        _pollTimer.Start();
        _currentContext = _focusTracker.GetCurrent();
        DebugLog.Write($"start context={_currentContext.ProcessName} kind={_currentContext.Kind}");
    }

    private void OnKeyDown(KeyEvent key)
    {
        if (IsScreenshotHotkey(key))
        {
            if (_settings.HideOnScreenshotHotkey)
            {
                _overlay.HideForScreenshot();
                _screenshotRestoreTimer.Stop();
                _screenshotRestoreTimer.Start();
            }

            return;
        }

        if (_settings.Paused || _keyboard.SuppressActivity)
        {
            return;
        }

        _idle.NotifyKey(key);

        if (_currentContext.Kind == CaptureKind.QtBlind)
        {
            bool ctrlDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VkControl) & 0x8000) != 0;
            // Ctrl+A 后紧接着的 Backspace/Delete 视为清空整段，避免镜像只减一字。
            if (ctrlDown && (key.VirtualKey == (uint)'A' || key.VirtualKey == (uint)'a'))
            {
                _weChatSelectAllPending = true;
                _weChatSelectAllAtUtc = DateTime.UtcNow;
            }
            else if (key.VirtualKey is not NativeMethods.VkBack
                     and not NativeMethods.VkDelete
                     and not NativeMethods.VkShift
                     and not NativeMethods.VkControl
                     and not NativeMethods.VkMenu)
            {
                _weChatSelectAllPending = false;
            }

            _mirror.MarkStale();
            RefreshWeChatMirrorFromIme();
            if (key.VirtualKey is not NativeMethods.VkBack
                and not NativeMethods.VkDelete
                and not NativeMethods.VkTab
                and not NativeMethods.VkShift
                and not NativeMethods.VkControl
                and not NativeMethods.VkMenu)
            {
                _weChatTypingTimer.Stop();
                _weChatTypingTimer.Start();
            }
        }

        if (key.VirtualKey is NativeMethods.VkBack or NativeMethods.VkDelete)
        {
            HandleDeletion(key);
        }
        else if (key.VirtualKey == NativeMethods.VkTab)
        {
            _dispatcher.BeginInvoke(ForceRescan, DispatcherPriority.Background);
        }
    }

    private void OnKeyUp(KeyEvent key)
    {
        if (_settings.Paused || _keyboard.SuppressActivity)
        {
            return;
        }

        if (key.VirtualKey == NativeMethods.VkCapsLock)
        {
            _dispatcher.BeginInvoke(ReplaceLastEnglish, DispatcherPriority.Input);
        }
    }

    private void RefreshWeChatMirrorFromIme()
    {
        string? text = _mirror.PullImmSnapshot(
            _currentContext.WindowHandle,
            _currentContext.FocusedControlHandle,
            _ime,
            returnCurrentWhenEmpty: false);

        if (!string.IsNullOrEmpty(text)
            && TextUtil.HasChinese(text)
            && !_ime.IsComposing(_currentContext.FocusedControlHandle))
        {
            _mirror.SetFullText(text);
        }
    }

    private bool HasRecentInputForClipboardCapture() => HasRecentInputEvidence();

    private bool HasRecentInputEvidence()
    {
        DateTime last = _keyboard.LastNonModifierActivityUtc;
        if (last < _lastUiaTextUtc)
        {
            last = _lastUiaTextUtc;
        }

        return last != DateTime.MinValue
               && DateTime.UtcNow - last <= TimeSpan.FromSeconds(10);
    }

    private bool HasRecentKeyboardEvidence()
    {
        DateTime last = _keyboard.LastNonModifierActivityUtc;
        return last != DateTime.MinValue
               && DateTime.UtcNow - last <= TimeSpan.FromSeconds(10);
    }

    private void OnMouseActivity()
    {
        _idle.NotifyActivity();
    }

    private void OnIdleElapsed()
    {
        _dispatcher.BeginInvoke(CaptureAndSchedule, DispatcherPriority.Background);
    }

    private void OnBoundaryKeyPressed()
    {
        _dispatcher.BeginInvoke(CaptureAndSchedule, DispatcherPriority.Background);
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        if (!_settings.Enabled)
        {
            return;
        }

        FocusedContext current = _focusTracker.GetCurrent();

        // 排除进程：更新上下文并停止捕获，避免继续拿着上一个微信/QQ 上下文去盲剪贴板抢焦点。
        if (IsExcludedProcess(current.ProcessName))
        {
            if (!IsExcludedProcess(_currentContext.ProcessName))
            {
                ResetSession();
            }

            _currentContext = current;
            _uiaMonitor.Clear();
            _uiaNeedsResync = true;
            return;
        }

        if (IsOwnOverlay(current))
        {
            // 点击/滚动/拖动悬浮窗时不要清空当前译文；
            // 只暂停新捕获，回到目标应用后继续沿用当前会话。
            _uiaMonitor.Clear();
            _uiaNeedsResync = true;
            return;
        }

        if (ReferenceEquals(_currentContext, current) || _currentContext == current)
        {
            if (_uiaNeedsResync)
            {
                ResyncUiaIfNeeded(current);
            }

            return;
        }

        bool sameWindow = IsSameWindowSession(_currentContext, current);
        bool weChatFocusMoved = sameWindow
                                && current.Kind == CaptureKind.QtBlind
                                && _currentContext.Kind == CaptureKind.QtBlind
                                && _currentContext.FocusedControlHandle != IntPtr.Zero
                                && current.FocusedControlHandle != IntPtr.Zero
                                && _currentContext.FocusedControlHandle != current.FocusedControlHandle;
        bool weChatSoftKeep = _currentContext.IsSameWeChatSession(current)
                              && current.Kind == CaptureKind.QtBlind
                              && !_mirror.IsEmpty
                              && !weChatFocusMoved;

        // 微信内焦点控件变化（常见于切换联系人/点到会话列表）时清掉旧译文，防止串台。
        if ((!sameWindow && !weChatSoftKeep) || weChatFocusMoved)
        {
            ResetSession();
        }

        _currentContext = current;

        ResyncUiaIfNeeded(_currentContext);
    }

    private void ResyncUiaIfNeeded(FocusedContext context)
    {
        if (context.Kind != CaptureKind.QtBlind && !IsOwnOverlay(context) && !IsExcludedProcess(context.ProcessName))
        {
            _uiaMonitor.ResyncToFocus(context.FocusedControlHandle);
        }

        _uiaNeedsResync = false;
    }

    private void OnUiaTextChanged(string text)
    {
        if (!_settings.Enabled
            || _settings.Paused
            || _currentContext.Kind == CaptureKind.QtBlind
            || IsOwnOverlay(_currentContext)
            || IsExcludedProcess(_currentContext.ProcessName))
        {
            return;
        }

        if (!HasRecentKeyboardEvidence())
        {
            // 页面加载/自动更新造成的 TextChanged 不算用户输入，避免整页被抓去翻译。
            return;
        }

        if (TextUtil.HasChinese(text) && IsPlausibleSourceText(text))
        {
            _lastUiaTextUtc = DateTime.UtcNow;
            // 让 UIA 文本变化参与“停手一段时间再翻译”的节奏，
            // 而不是每 200ms 轮询才能发现。
            _idle.NotifyActivity();
        }
    }

    private async void CaptureAndSchedule()
    {
        if (!_settings.Enabled || _settings.Paused || _captureInFlight)
        {
            return;
        }

        _captureInFlight = true;
        try
        {
            await CaptureAndScheduleCore();
        }
        finally
        {
            _captureInFlight = false;
        }
    }

    private async Task CaptureAndScheduleCore()
    {
        FocusedContext capturedContext = _currentContext;
        long captureVersion = _translationVersion;

        if (IsExcludedProcess(capturedContext.ProcessName))
        {
            return;
        }

        if (!HasRecentInputEvidence())
        {
            return;
        }

        if (capturedContext.Kind == CaptureKind.QtBlind
            && !string.IsNullOrWhiteSpace(_lastScheduledSource)
            && !string.IsNullOrWhiteSpace(_lastEnglish)
            && string.Equals(_mirror.CurrentText, _lastScheduledSource, StringComparison.Ordinal)
            && DateTime.UtcNow - _keyboard.LastNonModifierActivityUtc > TimeSpan.FromSeconds(3))
        {
            return;
        }

        if (_ime.IsComposing(capturedContext.FocusedControlHandle))
        {
            return;
        }

        if (_keyboard.IsModifierDown || _mouse.IsLeftButtonDown)
        {
            return;
        }

        ITextCaptureAdapter adapter = _adapterFactory.Create(capturedContext.Kind);
        string? text;

        try
        {
            text = await adapter.GetTextAsync(capturedContext, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _overlay.ShowError("文本获取失败：" + ex.Message);
            return;
        }

        // 捕获期间可能已切换窗口/聊天会话，旧结果不得继续覆盖新会话。
        if (!IsSameInputSession(capturedContext, _currentContext))
        {
            _lastDisplayUpdate = null;
            _overlay.Clear();
            return;
        }

        if (_translationVersion != captureVersion)
        {
            // 捕获期间发生删除/重置/新输入，旧捕获不得继续回写。
            DebugLog.Write("stale capture dropped");
            _lastDisplayUpdate = null;
            _overlay.Clear();
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            if (capturedContext.Kind != CaptureKind.QtBlind)
            {
                _mirror.Clear();
                _lastScheduledSource = string.Empty;
                _lastEnglish = string.Empty;
                _lastDisplayUpdate = null;
                _overlay.Clear();
            }
            else
            {
                DateTime now = DateTime.UtcNow;
                if (now - _lastEmptyCaptureUtc < TimeSpan.FromSeconds(4))
                {
                    return;
                }

                _lastEmptyCaptureUtc = now;
                // 微信盲区读不到内容时一律清空并复位，宁可等下一次真实输入再翻译，
                // 也不能让旧镜像把悬浮窗卡在“翻译中…”。
                DebugLog.Write("qtblind empty capture clear");
                _mirror.Clear();
                _lastScheduledSource = string.Empty;
                _lastEnglish = string.Empty;
                _lastDisplayUpdate = null;
                _overlay.Clear();
            }

            return;
        }

        text = text.Trim();
        _lastEmptyCaptureUtc = DateTime.MinValue;
        DebugLog.Write($"capture kind={capturedContext.Kind} len={text.Length} text={Truncate(text)}");
        if (!IsPlausibleSourceText(text))
        {
            if (capturedContext.Kind != CaptureKind.QtBlind)
            {
                _lastDisplayUpdate = null;
                _overlay.Clear();
            }

            return;
        }

        if (capturedContext.Kind != CaptureKind.QtBlind)
        {
            _mirror.SetFullText(text);
        }

        if (text == _lastScheduledSource)
        {
            if (_translateInFlight)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_lastEnglish))
            {
                _overlay.ShowUpdate(_lastDisplayUpdate
                                    ?? new TranslationUpdate(text, Array.Empty<string?>(), _lastEnglish, false, "已缓存"));
            }
            else
            {
                ShowTransientError("没有可显示翻译");
            }

            return;
        }

        string previousSource = _lastScheduledSource;
        _lastScheduledSource = text;
        CancelActiveTranslation();
        _translationCts = new CancellationTokenSource();
        CancellationToken token = _translationCts.Token;
        long taskVersion = _translationVersion;

        bool incrementalEdit = IsIncrementalEdit(previousSource, text);
        if (!incrementalEdit)
        {
            _overlay.SetLoading();
        }
        _translateInFlight = true;

        var progress = new Progress<TranslationUpdate>(update =>
        {
            if (_translationVersion != taskVersion)
            {
                return;
            }

            if (_dispatcher.CheckAccess())
            {
                _overlay.ShowUpdate(update);
            }
            else
            {
                _dispatcher.InvokeAsync(() => _overlay.ShowUpdate(update));
            }
        });

        try
        {
            TranslationUpdate result = await _scheduler.TranslateAsync(
                text,
                _settings.SourceLanguage,
                _settings.TargetLanguage,
                progress,
                token);

            if (_translationVersion == taskVersion)
            {
                _lastEnglish = result.FullTranslation ?? string.Join("\n", result.Segments.Where(static s => !string.IsNullOrWhiteSpace(s)));
                DebugLog.Write($"translate done full={(result.FullTranslation?.Length ?? 0)} status={result.Status}");
                if (string.IsNullOrWhiteSpace(_lastEnglish))
                {
                    _lastEnglish = string.Empty;
                    _lastDisplayUpdate = null;
                    ShowTransientError(string.IsNullOrWhiteSpace(result.Status) ? "翻译失败：没有可用结果" : result.Status);
                }
                else
                {
                    _lastDisplayUpdate = result;
                    _overlay.ShowUpdate(result);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 新输入已取代旧任务，保持当前状态。
        }
        catch (Exception ex)
        {
            if (_translationVersion == taskVersion)
            {
                _lastEnglish = string.Empty;
                _lastDisplayUpdate = null;
                ShowTransientError("翻译失败：" + ex.Message);
            }
        }
        finally
        {
            if (_translationVersion == taskVersion)
            {
                _translateInFlight = false;
            }
        }
    }

    private static bool IsSameInputSession(FocusedContext left, FocusedContext right)
    {
        if (left == right)
        {
            return true;
        }

        return left.WindowHandle == right.WindowHandle
               && left.ProcessId == right.ProcessId
               && left.FocusedControlHandle == right.FocusedControlHandle;
    }

    private static bool IsIncrementalEdit(string previous, string current)
    {
        if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current) || previous == current)
        {
            return false;
        }

        int max = Math.Min(previous.Length, current.Length);
        int prefix = 0;
        while (prefix < max
               && previous[prefix] == current[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < max - prefix
               && previous[previous.Length - 1 - suffix] == current[current.Length - 1 - suffix])
        {
            suffix++;
        }

        int unchanged = prefix + suffix;
        return unchanged > 0
               && unchanged >= Math.Max(8, max / 2);
    }

    private async void ReplaceLastEnglish()
    {
        if (string.IsNullOrWhiteSpace(_lastEnglish))
        {
            return;
        }

        FocusedContext context = _currentContext;
        if (context.WindowHandle == IntPtr.Zero
            || NativeMethods.GetForegroundWindow() != context.WindowHandle)
        {
            _overlay.ShowError("已取消回填：焦点已不在原窗口");
            return;
        }

        if (_ime.IsComposing(context.FocusedControlHandle) || _keyboard.IsModifierDown)
        {
            return;
        }

        if (!IsSourceStillIntact(context))
        {
            _overlay.ShowError("已取消回填：输入内容已变化");
            return;
        }

        try
        {
            bool replaced = await _replacer.ReplaceAsync(
                _lastEnglish,
                context.WindowHandle,
                context.FocusedControlHandle);

            DebugLog.Write($"capslock replace ok={replaced} len={_lastEnglish.Length}");
            if (!replaced)
            {
                _overlay.ShowError("回填失败：焦点或输入状态已变化");
            }
        }
        catch (Exception ex)
        {
            _overlay.ShowError("替换失败：" + ex.Message);
        }
    }

    private bool IsSourceStillIntact(FocusedContext context)
    {
        string expected = _lastScheduledSource.Trim();
        if (expected.Length == 0)
        {
            return false;
        }

        if (context.Kind == CaptureKind.QtBlind)
        {
            return string.Equals(
                _mirror.CurrentText.Trim(),
                expected,
                StringComparison.Ordinal);
        }

        string? current = UiaTextReader.TryReadFocusedValue()
                          ?? UiaTextReader.TryReadFromWindow(context.WindowHandle);
        return string.Equals(
            (current ?? string.Empty).Trim(),
            expected,
            StringComparison.Ordinal);
    }

    private void ToggleOverlay()
    {
        if (_overlay.Visibility == Visibility.Visible)
        {
            _overlay.Hide();
        }
        else
        {
            _overlay.Show();
        }
    }

    private void TogglePause(bool paused)
    {
        _settings.Paused = paused;
        SettingsStore.Save(_settings);
    }

    private void ShowTransientError(string message)
    {
        if (string.Equals(_lastNoResultMessage, message, StringComparison.Ordinal)
            && DateTime.UtcNow - _lastNoResultNoticeAt < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastNoResultMessage = message;
        _lastNoResultNoticeAt = DateTime.UtcNow;
        _overlay.ShowError(message);
    }

    private async void ForceRescan()
    {
        // Tab 可能正在移动焦点，稍等焦点稳定后再重扫，避免读到旧控件。
        await Task.Delay(120);
        _lastScheduledSource = string.Empty;
        CaptureAndSchedule();
    }

    private void HandleDeletion(KeyEvent key)
    {
        if (_currentContext.Kind == CaptureKind.QtBlind)
        {
            _weChatTypingTimer.Stop();
            CancelActiveTranslation();
            bool ctrlDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VkControl) & 0x8000) != 0;
            bool recentSelectAll = _weChatSelectAllPending
                                   && DateTime.UtcNow - _weChatSelectAllAtUtc <= TimeSpan.FromSeconds(2);

            // Delete / Ctrl+Backspace / Ctrl+A 后的删除：按“整段清空”处理。
            // 单字符 Backspace：立刻清空悬浮窗（即删即空），镜像减一字并短延迟重扫。
            if (ctrlDown || key.VirtualKey == NativeMethods.VkDelete || recentSelectAll)
            {
                DebugLog.Write(recentSelectAll ? "delete all clear (select-all)" : "delete all clear");
                _weChatSelectAllPending = false;
                _mirror.OnDeleteAll();
                _lastScheduledSource = string.Empty;
                _lastEnglish = string.Empty;
                _lastDisplayUpdate = null;
                _overlay.Clear();
                return;
            }

            string current = _mirror.CurrentText;
            if (current.Length <= 1)
            {
                DebugLog.Write("delete all clear (backspace)");
                _mirror.OnDeleteAll();
                _lastScheduledSource = string.Empty;
                _lastEnglish = string.Empty;
                _lastDisplayUpdate = null;
                _overlay.Clear();
            }
            else
            {
                _mirror.OnBackspace();
                _lastScheduledSource = string.Empty;
                _lastEnglish = string.Empty;
                _lastDisplayUpdate = null;
                _overlay.Clear();
                _weChatTypingTimer.Stop();
                _weChatTypingTimer.Start();
            }

            return;
        }

        CancelActiveTranslation();
        _mirror.Clear();
        _lastScheduledSource = string.Empty;
        _lastEnglish = string.Empty;
        _lastDisplayUpdate = null;
        _overlay.Clear();
    }

    private void ResetSession()
    {
        CancelActiveTranslation();
        _mirror.Clear();
        _lastScheduledSource = string.Empty;
        _lastEnglish = string.Empty;
        _lastDisplayUpdate = null;
        _overlay.Clear();
    }

    private void CancelActiveTranslation()
    {
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = null;
        _translationVersion++;
    }

    private static bool IsSameWindowSession(FocusedContext left, FocusedContext right)
    {
        return left.WindowHandle == right.WindowHandle
               && left.ProcessId == right.ProcessId
               && string.Equals(left.WindowTitle, right.WindowTitle, StringComparison.Ordinal);
    }

    private static bool IsOwnOverlay(FocusedContext context)
    {
        return context.ProcessId == (uint)Environment.ProcessId
               || context.ProcessName.Equals("CnInstantTranslator", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsExcludedProcess(string processName)
    {
        return _settings.ExcludedProcesses.Any(p =>
            p.Equals(processName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsScreenshotHotkey(KeyEvent key)
    {
        if (key.VirtualKey != 0x53) // S
        {
            return false;
        }

        bool winDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VkLwin) & 0x8000) != 0
                       || (NativeMethods.GetAsyncKeyState(NativeMethods.VkRwin) & 0x8000) != 0;
        bool shiftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VkShift) & 0x8000) != 0;
        return winDown && shiftDown;
    }

    private static bool IsPlausibleSourceText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (KnownUiMetadata.Contains(trimmed))
        {
            return false;
        }

        if (UiaTextReader.IsLikelyPageOrMetadata(trimmed))
        {
            return false;
        }

        return TextUtil.HasChinese(trimmed)
               && !trimmed.Contains("://", StringComparison.OrdinalIgnoreCase)
               && !trimmed.StartsWith("app:", StringComparison.OrdinalIgnoreCase)
               && !trimmed.StartsWith("chrome:", StringComparison.OrdinalIgnoreCase)
               && !trimmed.StartsWith("edge:", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> KnownUiMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "随心输入",
        "输入搜索词",
        "搜索",
        "消息",
        "聊天",
        "全员禁言中",
        "群公告",
        "输入框"
    };

    private static string Truncate(string text, int max = 80)
    {
        return text.Length <= max ? text : text[..max] + "...";
    }

    private static TranslationRouter CreateRouter(AppSettings settings)
    {
        var http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 CnInstantTranslator/1.0");

        if (settings.OfflineMode)
        {
            return new TranslationRouter(
                new ITranslationEngine[] { new OfflineTranslationEngine(settings.CacheCapacity) },
                maxEngineAttempts: 1,
                TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        }

        var engines = new List<ITranslationEngine>();
        foreach (string engineName in settings.EngineOrder)
        {
            switch (engineName.ToLowerInvariant())
            {
                case "sogou":
                    engines.Add(new SogouTranslationEngine(http, settings.CacheCapacity));
                    break;
                case "google":
                    engines.Add(new GoogleTranslationEngine(http, settings.CacheCapacity));
                    break;
                case "mymemory":
                    engines.Add(new MyMemoryTranslationEngine(http, settings.CacheCapacity));
                    break;
                case "bing":
                    engines.Add(new BingTranslationEngine(
                        http,
                        Environment.GetEnvironmentVariable("BING_TRANSLATOR_KEY"),
                        settings.CacheCapacity));
                    break;
                case "deepl":
                    engines.Add(new DeepLTranslationEngine(
                        http,
                        Environment.GetEnvironmentVariable("DEEPL_AUTH_KEY"),
                        settings.CacheCapacity));
                    break;
                case "offline":
                    engines.Add(new OfflineTranslationEngine(settings.CacheCapacity));
                    break;
            }
        }

        if (engines.Count == 0)
        {
            engines.Add(new GoogleTranslationEngine(http, settings.CacheCapacity));
        }

        if (engines.All(static e => !e.Name.Equals("mymemory", StringComparison.OrdinalIgnoreCase)))
        {
            // MyMemory 作为免费兜底放在列表最后，不改变用户配置的前置顺序。
            engines.Add(new MyMemoryTranslationEngine(http, settings.CacheCapacity));
        }

        return new TranslationRouter(
            engines,
            maxEngineAttempts: Math.Max(1, engines.Count),
            TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _screenshotRestoreTimer.Stop();
        _weChatTypingTimer.Stop();
        _translationCts?.Cancel();
        _keyboard.KeyDown -= OnKeyDown;
        _keyboard.KeyUp -= OnKeyUp;
        _mouse.LeftButtonDown -= OnMouseActivity;
        _mouse.LeftButtonUp -= OnMouseActivity;
        _idle.IdleElapsed -= OnIdleElapsed;
        _idle.BoundaryKeyPressed -= OnBoundaryKeyPressed;
        _uiaMonitor.TextChanged -= OnUiaTextChanged;
        _uiaMonitor.Dispose();
        _keyboard.Dispose();
        _mouse.Dispose();
        _idle.Dispose();
        _translationCts?.Dispose();
        _tray.Dispose();
        SettingsStore.Save(_settings);
    }
}
