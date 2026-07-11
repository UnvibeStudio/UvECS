namespace UvEcs;

public sealed partial class World
{
    private object?[] _sparseSets = new object?[16];

    public SparseSet<T> SparseSetOf<T>() where T : unmanaged, ISparse
    {
        int id = SparseType<T>.Id;
        if (id >= _sparseSets.Length) Array.Resize(ref _sparseSets, Math.Max(id + 1, _sparseSets.Length * 2));
        return (SparseSet<T>)(_sparseSets[id] ??= new SparseSet<T>());
    }

    internal ISparseSetView? SparseSetById(int sparseId)
        => sparseId < _sparseSets.Length ? (ISparseSetView?)_sparseSets[sparseId] : null;

    /// <summary>Не структурная операция: архетип не меняется.</summary>
    public void AddSparse<T>(Entity e, T value) where T : unmanaged, ISparse
    {
        _ = Entities.GetRecord(e);   // проверка живости
        SparseSetOf<T>().Add(e.Id, value);
    }

    public bool RemoveSparse<T>(Entity e) where T : unmanaged, ISparse
    {
        _ = Entities.GetRecord(e);
        return SparseSetOf<T>().Remove(e.Id);
    }

    public bool HasSparse<T>(Entity e) where T : unmanaged, ISparse
    {
        _ = Entities.GetRecord(e);
        return SparseSetOf<T>().Has(e.Id);
    }

    public ref T GetSparseRef<T>(Entity e) where T : unmanaged, ISparse
    {
        _ = Entities.GetRecord(e);
        return ref SparseSetOf<T>().GetRef(e.Id);
    }

    /// <summary>
    /// Вызывается из Destroy. Без этого EntityStore переиспользует Id, и новая сущность
    /// унаследует sparse-компоненты покойника.
    /// </summary>
    internal void RemoveFromAllSparseSets(int entityId)
    {
        for (int i = 0; i < _sparseSets.Length; i++)
            (_sparseSets[i] as ISparseSetView)?.Remove(entityId);
    }
}
