namespace CnInstantTranslator.Core;

public static class TextUtil
{
    public static bool HasChinese(string text)
    {
        return text.Any(static c => c >= '一' && c <= '鿿');
    }
}
