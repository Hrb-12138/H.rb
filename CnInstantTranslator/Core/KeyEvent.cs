namespace CnInstantTranslator.Core;

public sealed record KeyEvent(uint VirtualKey, uint ScanCode, bool IsKeyDown, bool IsInjected, string Text = "");
