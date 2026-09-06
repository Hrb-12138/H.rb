using System.Collections.Concurrent;
using CnInstantTranslator.Domain;

namespace CnInstantTranslator.Translation;

public sealed class TranslationScheduler
{
    private readonly TranslationRouter _router;
    private readonly int _maxSegmentLength;
    private readonly int _batchSize;
    private readonly ConcurrentDictionary<string, string> _segmentCache = new(StringComparer.Ordinal);

    public TranslationScheduler(TranslationRouter router, int maxSegmentLength, int batchSize)
    {
        _router = router;
        _maxSegmentLength = maxSegmentLength;
        _batchSize = Math.Clamp(batchSize, 1, 8);
    }

    public async Task<TranslationUpdate> TranslateAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        IProgress<TranslationUpdate>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TranslationSegment> segments = Segmenter.Segment(sourceText, _maxSegmentLength);
        if (segments.Count == 0)
        {
            return new TranslationUpdate(sourceText, Array.Empty<string?>(), null, false, "无待翻译文本");
        }

        var results = new string?[segments.Count];
        var pending = new List<int>();

        for (int i = 0; i < segments.Count; i++)
        {
            string cacheKey = BuildCacheKey(segments[i].ContentHash, sourceLanguage, targetLanguage);
            if (_segmentCache.TryGetValue(cacheKey, out string? cached))
            {
                results[i] = cached;
            }
            else
            {
                pending.Add(i);
            }
        }

        if (pending.Count > 0)
        {
            for (int offset = 0; offset < pending.Count; offset += _batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int[] batch = pending.Skip(offset).Take(_batchSize).ToArray();
                var tasks = batch.Select(index => TranslateSegmentAsync(
                    segments[index],
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken));

                string?[] batchResults = await Task.WhenAll(tasks);

                for (int i = 0; i < batch.Length; i++)
                {
                    results[batch[i]] = batchResults[i];
                    if (!string.IsNullOrEmpty(batchResults[i]))
                    {
                        _segmentCache[BuildCacheKey(
                            segments[batch[i]].ContentHash,
                            sourceLanguage,
                            targetLanguage)] = batchResults[i]!;
                    }
                }

                progress?.Report(new TranslationUpdate(
                    sourceText,
                    results.ToArray(),
                    null,
                    IsPartial: true,
                    $"片段 {Math.Min(offset + _batchSize, pending.Count)}/{pending.Count}"));
            }
        }
        else
        {
            progress?.Report(new TranslationUpdate(sourceText, results.ToArray(), null, true, "全部命中缓存"));
        }

        string? full = null;
        if (segments.Count > 1)
        {
            // 分段已经稳定后，再尝试整段翻译以改善上下文连贯性；失败时保留分段结果。
            try
            {
                TranslationAttempt? attempt = await _router.TranslateAsync(
                    sourceText,
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken);
                full = attempt?.Text;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                full = null;
            }
            catch
            {
                full = null;
            }
        }

        bool hasEnglish = full is not null || results.Any(static r => !string.IsNullOrWhiteSpace(r));
        return new TranslationUpdate(
            sourceText,
            results.ToArray(),
            full,
            IsPartial: false,
            hasEnglish ? (full is null ? "分段翻译完成" : "整段连贯翻译完成") : "翻译失败：没有可用结果");
    }

    private async Task<string?> TranslateSegmentAsync(
        TranslationSegment segment,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        string cacheKey = BuildCacheKey(segment.ContentHash, sourceLanguage, targetLanguage);
        if (_segmentCache.TryGetValue(cacheKey, out string? cached))
        {
            return cached;
        }

        TranslationAttempt? attempt = await _router.TranslateAsync(
            segment.Text,
            sourceLanguage,
            targetLanguage,
            cancellationToken);

        return attempt?.Text;
    }

    private static string BuildCacheKey(string contentHash, string sourceLanguage, string targetLanguage)
    {
        return contentHash + "\u001f" + sourceLanguage + "\u001f" + targetLanguage;
    }
}
