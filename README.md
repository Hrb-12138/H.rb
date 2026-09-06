# 中文即时翻译悬浮窗

> 为了你变成狼人摸羊

这是一个 C#/.NET WPF 实现的中文即时翻译悬浮窗项目骨架，目标是：在 QQ、微信、浏览器、学习通等 Windows 应用中检测输入框中的中文，停手 1 秒后翻译为英文并显示在可拖动悬浮窗中，按 CapsLock 可将英文覆盖回原输入框。

## 已实现范围

- 按应用分流的 `AppCaptureClassifier`
- 全局键盘/鼠标钩子
- 空闲 1 秒触发 + 空格/句读/Enter 边界键触发
- UIA `ValuePattern` 优先
- 微信 `QtBlind` 适配器：常规轮询只读镜像，盲剪贴板仅在门禁全通过时执行
- 盲剪贴板捕获：冷却、修饰键检测、鼠标选区检测、IME 组词检测、剪贴板恢复
- 翻译引擎路由：严格按 `EngineOrder` 配置顺序尝试，MyMemory 自动作为列表末尾的免费兜底
- LRU 缓存（源文本 + 目标语言 + 引擎标识）
- 长文本分段、并发批次、分段缓存、整段连贯翻译
- WPF 悬浮窗：拖动、右键菜单、半透明、位置保存、自动适配内容、截图热键隐藏
- UIA `ValuePattern` / `TextChanged` 事件监听 + 候选输入控件评分
- CapsLock 回填（SendInput Unicode，带焦点/源文本校验）、Tab 强制重扫、退格/删除即删即空
- 托盘菜单可暂停/恢复；盲剪贴板只在“近期确有输入”时执行，失败自动退避并恢复剪贴板

## 技术选型

| 模块 | 选择 | 原因 |
| --- | --- | --- |
| 桌面框架 | .NET WPF | 便于无边框半透明悬浮窗、右键菜单、Dispatcher 线程模型 |
| 全局输入监控 | `WH_KEYBOARD_LL` / `WH_MOUSE_LL` | 可捕获 QQ/微信/浏览器中的快捷键，且不需要目标进程注入 |
| 控件文本读取 | Windows UI Automation | 标准控件优先 `ValuePattern`，避免 `TextPattern.GetSelection()` 触发绿选 |
| 微信兜底 | `AttachThreadInput` + 模拟 Ctrl+A/C | 仅在 Qt 盲区且门禁全通过时使用 |
| 翻译请求 | `HttpClient` 异步 + 3 秒超时 | 非阻塞，避免卡 UI |
| 缓存 | 自实现线程安全 LRU | 无外部依赖，容量默认 5000 |
| 配置 | JSON 文件 | 无需数据库，便于用户手工调整 |

## 项目结构

```text
CnInstantTranslator/
  App.xaml / App.xaml.cs                 # 启动入口
  Config/
    AppSettings.cs                       # 配置模型
    SettingsStore.cs                     # JSON 读写
  Domain/
    CaptureKind.cs                       # 应用分类枚举
    FocusedContext.cs                    # 当前焦点上下文
    TranslationSegment.cs                # 分段与翻译更新
  Core/
    AppCaptureClassifier.cs              # 应用捕获分类器
    FocusTracker.cs                      # 前台窗口/焦点控件识别
    LowLevelKeyboardHook.cs              # 键盘钩子
    LowLevelMouseHook.cs                 # 鼠标钩子
    IdleOrSpaceTrigger.cs                # 空闲或边界键触发
    InputMirrorService.cs                # 本地中文镜像
    ImeStateProbe.cs                     # IME 组词状态检测
    TextReplacer.cs                      # CapsLock 英文回填
    TextUtil.cs                          # 中文文本判断等工具
    DebugLog.cs                          # %APPDATA%\CnInstantTranslator\debug.log
    AppController.cs                     # 总调度器
  Capture/
    ITextCaptureAdapter.cs
    UiaChatAdapter.cs                    # QQ 稳定 UIA 路径，禁止修改
    UiaTextChangeMonitor.cs              # UIA Value/Text 变化事件
    WebViewAdapter.cs
    Win32EditAdapter.cs
    DocumentAdapter.cs
    GenericAdapter.cs
    QtBlindAdapter.cs                    # 微信盲区适配器
    BlindClipboardCapture.cs             # 门禁式盲剪贴板
    CaptureAdapterFactory.cs
  Translation/
    ITranslationEngine.cs
    TranslationEngineBase.cs
    TranslationCache.cs
    TranslationRouter.cs
    TranslationScheduler.cs
    SogouTranslationEngine.cs
    GoogleTranslationEngine.cs
    BingTranslationEngine.cs
    DeepLTranslationEngine.cs
    OfflineTranslationEngine.cs
  UI/
    TranslationOverlay.cs                # 悬浮翻译窗
  Native/
    NativeMethods.cs
```

## 运行方式

```powershell
dotnet run --project CnInstantTranslator/CnInstantTranslator.csproj
```

或：

```powershell
dotnet build CnInstantTranslator/CnInstantTranslator.csproj -c Release
.\CnInstantTranslator\bin\Release\net8.0-windows\CnInstantTranslator.exe
```

## 配置

首次运行后会生成：

```text
%APPDATA%\CnInstantTranslator\settings.json
```

常用字段：

```json
{
  "Enabled": true,
  "Paused": false,
  "IdleDelayMs": 1000,
  "MaxSegmentLength": 200,
  "CacheCapacity": 5000,
  "RequestTimeoutSeconds": 3,
  "BatchSize": 4,
  "TargetLanguage": "en",
  "EngineOrder": ["sogou", "google", "bing", "deepl"],
  "OfflineMode": false,
  "HideOnScreenshotHotkey": false,
  "ExcludedProcesses": ["chatgpt", "codex", "gw"]
}
```

Bing 和 DeepL 需要环境变量：

```powershell
$env:BING_TRANSLATOR_KEY = "..."
$env:BING_TRANSLATOR_REGION = "..."
$env:DEEPL_AUTH_KEY = "..."
```

## 三条硬约束的落地位置

1. **微信 poll 禁止剪贴板**：`QtBlindAdapter.GetTextAsync` 先读 `InputMirrorService`，只有镜像为空才进入 `BlindClipboardCapture`。
2. **盲剪贴板只响应真实输入**：除 `IsIdle`、修饰键、鼠标左键、IME 组词门禁外，还要求最近 10 秒内确有键盘/粘贴/UIA 输入，避免在只读或空场景反复 Ctrl+A/C。
3. **QQ UIA 路径稳定**：`UiaChatAdapter` 单独封装，注释明确禁止修改；其它适配器不依赖 QQ 代码。

## 备注

- 搜狗/Google 当前使用的是公开网页接口，接口可能变化；路由会自动降级。
- 离线模式不会发送任何网络请求，当前 `OfflineTranslationEngine` 只是本地模型接口占位。
- 本项目是可运行骨架，生产环境还需补充设置 UI、单实例互斥、日志、自启动、签名等。
