namespace UvEcs;

public sealed class Query
{
    private readonly World _world;
    private readonly List<Archetype> _matched = new();
    private int _scannedArchetypes;

    internal ComponentMask All;
    internal ComponentMask None;
    internal TagMask TagAll;
    internal TagMask TagNone;
    internal int[] SparseAll = Array.Empty<int>();

    internal Query(World world) => _world = world;

    public int MatchedArchetypeCount
    {
        get { Refresh(); return _matched.Count; }
    }

    /// <summary>Доматчивает только архетипы, появившиеся с прошлого раза (§6 спеки).</summary>
    internal void Refresh()
    {
        int total = _world.ArchetypeCount;
        for (; _scannedArchetypes < total; _scannedArchetypes++)
        {
            var archetype = _world.ArchetypeById(_scannedArchetypes);
            if (archetype.Mask.HasAll(in All) && archetype.Mask.HasNone(in None))
                _matched.Add(archetype);
        }
    }

    public ChunkEnumerator GetEnumerator()
    {
        if (SparseAll.Length > 0)
            throw new InvalidOperationException(
                "Запрос с AllSparse<T>() итерируется через BySparse(): носители размазаны по чанкам, " +
                "Span на них не натянуть (§6 спеки).");

        Refresh();
        return new ChunkEnumerator(this, _matched);
    }

    internal List<Archetype> MatchedArchetypes { get { Refresh(); return _matched; } }
    internal World World => _world;
}

public struct ChunkEnumerator
{
    private readonly Query _query;
    private readonly List<Archetype> _archetypes;
    private int _archetypeIndex;
    private int _chunkIndex;
    private Chunk? _current;

    internal ChunkEnumerator(Query query, List<Archetype> archetypes)
    {
        _query = query;
        _archetypes = archetypes;
        _archetypeIndex = 0;
        _chunkIndex = -1;
        _current = null;
    }

    public ChunkView Current => new(_current!, _query.TagAll, _query.TagNone);

    public bool MoveNext()
    {
        while (_archetypeIndex < _archetypes.Count)
        {
            var archetype = _archetypes[_archetypeIndex];
            _chunkIndex++;

            if (_chunkIndex >= archetype.Chunks.Count)
            {
                _archetypeIndex++;
                _chunkIndex = -1;
                continue;
            }

            var chunk = archetype.Chunks[_chunkIndex];
            if (chunk.IsEmpty) continue;

            // требуемого тега нет ни у кого в этом чанке — пропускаем целиком
            if (!chunk.TagUnion.HasAll(_query.TagAll)) continue;

            _current = chunk;
            return true;
        }
        return false;
    }
}
