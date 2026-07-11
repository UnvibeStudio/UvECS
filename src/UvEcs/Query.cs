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

    public SparseEnumerable BySparse()
    {
        if (SparseAll.Length == 0)
            throw new InvalidOperationException("BySparse() требует хотя бы одного AllSparse<T>().");
        return new SparseEnumerable(this);
    }
}

public struct ChunkEnumerator
{
    private readonly Query _query;
    private readonly List<Archetype> _archetypes;
    private int _archetypeIndex;
    private int _chunkIndex;
    private Chunk? _current;
#if DEBUG
    private readonly int _structuralSnapshot;
#endif

    internal ChunkEnumerator(Query query, List<Archetype> archetypes)
    {
        _query = query;
        _archetypes = archetypes;
        _archetypeIndex = 0;
        _chunkIndex = -1;
        _current = null;
#if DEBUG
        _structuralSnapshot = StructuralSum(archetypes);
#endif
    }

#if DEBUG
    private static int StructuralSum(List<Archetype> archetypes)
    {
        int sum = 0;
        for (int i = 0; i < archetypes.Count; i++) sum += archetypes[i].StructuralVersion;
        return sum;
    }
#endif

    public ChunkView Current => new(_current!, _query.TagAll, _query.TagNone);

    public bool MoveNext()
    {
#if DEBUG
        if (StructuralSum(_archetypes) != _structuralSnapshot)
            throw new InvalidOperationException(
                "Структурное изменение (Add/Remove/Create/Destroy) во время foreach по запросу. " +
                "Отложите его в command buffer (§7 спеки).");
#endif
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

public readonly struct SparseHit
{
    public readonly Entity Entity;
    public readonly Chunk Chunk;
    public readonly int Row;

    internal SparseHit(Entity entity, Chunk chunk, int row)
    {
        Entity = entity;
        Chunk = chunk;
        Row = row;
    }
}

public readonly struct SparseEnumerable
{
    private readonly Query _query;
    internal SparseEnumerable(Query query) => _query = query;
    public SparseEnumerator GetEnumerator() => new(_query);
}

public struct SparseEnumerator
{
    private readonly Query _query;
    private readonly ISparseSetView _driver;
    private readonly int[] _otherSparse;
    private int _index;
    private SparseHit _current;
#if DEBUG
    private readonly List<Archetype> _archetypes;
    private readonly int _structuralSnapshot;
#endif

    internal SparseEnumerator(Query query)
    {
        _query = query;
        _index = -1;
        _current = default;

        // Драйвер — наименьший из обязательных наборов (§6 спеки).
        // Отсутствующий набор означает ноль носителей, то есть пустой результат.
        int driverId = -1;
        int driverCount = int.MaxValue;

        foreach (int id in query.SparseAll)
        {
            int count = query.World.SparseSetById(id)?.Count ?? 0;
            if (count < driverCount)
            {
                driverCount = count;
                driverId = id;
            }
        }

        _driver = query.World.SparseSetById(driverId) ?? EmptySparseSetView.Instance;
        _otherSparse = query.SparseAll.Where(id => id != driverId).ToArray();
#if DEBUG
        // Симметрично ChunkEnumerator: сумма StructuralVersion по заматченным архетипам.
        // SparseEnumerator ходит по entity-записям напрямую, а не по списку чанков,
        // но структурная мутация посреди BySparse() так же нарушает контракт (§11 спеки).
        _archetypes = query.MatchedArchetypes;
        _structuralSnapshot = StructuralSum(_archetypes);
#endif
    }

#if DEBUG
    private static int StructuralSum(List<Archetype> archetypes)
    {
        int sum = 0;
        for (int i = 0; i < archetypes.Count; i++) sum += archetypes[i].StructuralVersion;
        return sum;
    }
#endif

    public SparseHit Current => _current;

    public bool MoveNext()
    {
#if DEBUG
        if (StructuralSum(_archetypes) != _structuralSnapshot)
            throw new InvalidOperationException(
                "Структурное изменение (Add/Remove/Create/Destroy) во время foreach по BySparse(). " +
                "Отложите его в command buffer (§7 спеки).");
#endif
        var world = _query.World;
        var entities = _driver.Entities;

        while (++_index < entities.Length)
        {
            int entityId = entities[_index];

            ref var rec = ref world.Entities.RecordRefUnchecked(entityId);
            if (rec.ArchetypeId < 0) continue;                     // сущность мертва

            var archetype = world.ArchetypeById(rec.ArchetypeId);
            if (!archetype.Mask.HasAll(in _query.All)) continue;
            if (!archetype.Mask.HasNone(in _query.None)) continue;

            bool hasAllSparse = true;
            for (int i = 0; i < _otherSparse.Length; i++)
            {
                var other = world.SparseSetById(_otherSparse[i]);
                if (other is null || !other.Has(entityId)) { hasAllSparse = false; break; }
            }
            if (!hasAllSparse) continue;

            var chunk = archetype.Chunks[rec.ChunkIndex];
            var tags = chunk.TagAt(rec.Row);
            if (!tags.HasAll(_query.TagAll) || !tags.HasNone(_query.TagNone)) continue;

            _current = new SparseHit(chunk.EntityAt(rec.Row), chunk, rec.Row);
            return true;
        }
        return false;
    }
}

internal sealed class EmptySparseSetView : ISparseSetView
{
    public static readonly EmptySparseSetView Instance = new();
    public int Count => 0;
    public ReadOnlySpan<int> Entities => ReadOnlySpan<int>.Empty;
    public bool Has(int entityId) => false;
    public bool Remove(int entityId) => false;
}
