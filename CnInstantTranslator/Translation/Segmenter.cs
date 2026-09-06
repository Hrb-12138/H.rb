using System.Security.Cryptography;
using System.Text;
using CnInstantTranslator.Domain;

namespace CnInstantTranslator.Translation;

public static class Segmenter
{
    private static readonly char[] BoundaryMarks = { '。', '！', '？', '；', '\r', '\n' };

    public static IReadOnlyList<TranslationSegment> Segment(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<TranslationSegment>();
        }

        maxLength = Math.Max(10, maxLength);
        var raw = new List<string>();
        var current = new StringBuilder();

        foreach (char c in text)
        {
            current.Append(c);
            if (BoundaryMarks.Contains(c) || current.Length >= maxLength)
            {
                raw.Add(current.ToString().Trim());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            raw.Add(current.ToString().Trim());
        }

        var result = new List<TranslationSegment>();
        foreach (string part in raw)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            foreach (string piece in SplitLongPiece(part, maxLength))
            {
                if (string.IsNullOrWhiteSpace(piece))
                {
                    continue;
                }

                result.Add(new TranslationSegment(
                    result.Count,
                    piece,
                    ComputeHash(piece)));
            }
        }

        return result;
    }

    public static string ComputeHash(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash);
    }

    private static IEnumerable<string> SplitLongPiece(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            yield return text;
            yield break;
        }

        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + maxLength, text.Length);

            if (end < text.Length)
            {
                int comma = text.LastIndexOfAny(new[] { '，', ',', '、' }, end - 1, end - start);
                if (comma > start)
                {
                    end = comma + 1;
                }
            }

            yield return text[start..end];
            start = end;
        }
    }
}
