using System.Security.Cryptography;
using System.Text;

namespace CnInstantTranslator.Translation;

public sealed class TranslationCache
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();

    public TranslationCache(int capacity = 5000)
    {
        _capacity = Math.Max(1, capacity);
    }

    public bool TryGet(string source, string targetLanguage, string engine, out string translated)
    {
        string key = BuildKey(source, targetLanguage, engine);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                translated = node.Value.Translated;
                return true;
            }
        }

        translated = string.Empty;
        return false;
    }

    public void Set(string source, string targetLanguage, string engine, string translated)
    {
        string key = BuildKey(source, targetLanguage, engine);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<CacheEntry>? existing))
            {
                existing.Value = existing.Value with { Translated = translated };
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = _lru.AddFirst(new CacheEntry(source, targetLanguage, engine, translated));
            _entries[key] = node;

            while (_lru.Count > _capacity && _lru.Last is not null)
            {
                LinkedListNode<CacheEntry> last = _lru.Last;
                _lru.RemoveLast();
                _entries.Remove(BuildKey(last.Value.Source, last.Value.TargetLanguage, last.Value.Engine));
            }
        }
    }

    private static string BuildKey(string source, string targetLanguage, string engine)
    {
        string raw = $"{source}\u001f{targetLanguage}\u001f{engine}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    private sealed record CacheEntry(string Source, string TargetLanguage, string Engine, string Translated);
}
