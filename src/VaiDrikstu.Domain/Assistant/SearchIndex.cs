namespace VaiDrikstu.Domain.Assistant;

/// <summary>Minimal in-memory BM25 index. Deterministic: same question, same answer.</summary>
public sealed class SearchIndex<T>
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly List<(T Item, int Length)> _docs = [];
    private readonly Dictionary<string, List<(int Doc, int Tf)>> _postings = new(StringComparer.Ordinal);
    private long _totalLength;

    public int Count => _docs.Count;

    public void Add(T item, IEnumerable<string> terms)
    {
        var docId = _docs.Count;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = 0;
        foreach (var term in terms)
        {
            counts[term] = counts.GetValueOrDefault(term) + 1;
            length++;
        }

        foreach (var (term, tf) in counts)
        {
            if (!_postings.TryGetValue(term, out var list))
            {
                list = [];
                _postings[term] = list;
            }

            list.Add((docId, tf));
        }

        _docs.Add((item, Math.Max(length, 1)));
        _totalLength += Math.Max(length, 1);
    }

    /// <summary>Returns hits with BM25 score and the share of query terms that matched.</summary>
    public IReadOnlyList<SearchHit<T>> Search(IReadOnlyCollection<string> queryTerms, int limit)
    {
        if (queryTerms.Count == 0 || _docs.Count == 0)
        {
            return [];
        }

        var averageLength = (double)_totalLength / _docs.Count;
        var scores = new Dictionary<int, (double Score, HashSet<string> Matched)>();
        foreach (var term in queryTerms.Distinct(StringComparer.Ordinal))
        {
            if (!_postings.TryGetValue(term, out var list))
            {
                continue;
            }

            var idf = Math.Log(1 + (_docs.Count - list.Count + 0.5) / (list.Count + 0.5));
            foreach (var (doc, tf) in list)
            {
                var norm = tf * (K1 + 1) / (tf + K1 * (1 - B + B * _docs[doc].Length / averageLength));
                if (!scores.TryGetValue(doc, out var entry))
                {
                    entry = (0, new HashSet<string>(StringComparer.Ordinal));
                }

                entry.Matched.Add(term);
                scores[doc] = (entry.Score + idf * norm, entry.Matched);
            }
        }

        return scores
            .Select(pair => new SearchHit<T>(_docs[pair.Key].Item, pair.Value.Score, pair.Value.Matched))
            .OrderByDescending(hit => hit.Score)
            .Take(limit)
            .ToList();
    }
}

public sealed record SearchHit<T>(T Item, double Score, IReadOnlySet<string> Matched);
