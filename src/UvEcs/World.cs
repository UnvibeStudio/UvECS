namespace UvEcs;

public sealed partial class World
{
    private readonly List<Archetype> _archetypes = new();
    private readonly Dictionary<ComponentMask, Archetype> _byMask = new();

    internal EntityStore Entities { get; } = new();
    internal ChunkPool Pool { get; } = new();

    public int ArchetypeCount => _archetypes.Count;
    public int EntityCount => Entities.AliveCount;

    public World()
    {
        GetOrCreateArchetype(new ComponentMask());   // архетип без компонентов, Id == 0
    }

    internal Archetype ArchetypeById(int id) => _archetypes[id];

    private static int[] IdsFromMask(in ComponentMask mask)
    {
        var ids = new int[mask.PopCount()];
        int n = 0;
        for (int i = 0; i < ComponentMask.Capacity; i++)
            if (mask.Get(i)) ids[n++] = i;
        return ids;   // уже отсортированы по возрастанию
    }

    internal Archetype GetOrCreateArchetype(in ComponentMask mask)
    {
        if (_byMask.TryGetValue(mask, out var existing)) return existing;

        var archetype = new Archetype(_archetypes.Count, mask, IdsFromMask(in mask));
        _archetypes.Add(archetype);
        _byMask[mask] = archetype;
        return archetype;
    }

    public Entity Create()
    {
        var e = Entities.Create();
        var archetype = _archetypes[0];
        var chunk = archetype.GetOrCreateChunkWithSpace(Pool, out int chunkIndex);
        int row = chunk.AddRow(e);

        ref var rec = ref Entities.GetRecord(e);
        rec.ArchetypeId = archetype.Id;
        rec.ChunkIndex = chunkIndex;
        rec.Row = row;
        archetype.BumpStructuralVersion();
        return e;
    }

    public bool IsAlive(Entity e) => Entities.IsAlive(e);

    public void Destroy(Entity e)
    {
        ref var rec = ref Entities.GetRecord(e);
        RemoveFromChunk(rec.ArchetypeId, rec.ChunkIndex, rec.Row);
        Entities.Destroy(e);
    }

    /// <summary>Swap-remove + починка записи переехавшей сущности.</summary>
    private void RemoveFromChunk(int archetypeId, int chunkIndex, int row)
    {
        var archetype = _archetypes[archetypeId];
        var chunk = archetype.Chunks[chunkIndex];

        Entity moved = chunk.SwapRemove(row);
        if (!moved.IsNull)
        {
            ref var movedRec = ref Entities.RecordRefUnchecked(moved.Id);
            movedRec.Row = row;   // архетип и чанк те же
        }

        archetype.ReleaseChunkIfEmpty(chunkIndex, Pool);
        archetype.BumpStructuralVersion();
    }

    public bool Has<T>(Entity e) where T : unmanaged, IComponent
    {
        ref var rec = ref Entities.GetRecord(e);
        return _archetypes[rec.ArchetypeId].Mask.Get(ComponentType<T>.Id);
    }

    public ref T GetRef<T>(Entity e) where T : unmanaged, IComponent
    {
        ref var rec = ref Entities.GetRecord(e);
        var archetype = _archetypes[rec.ArchetypeId];
        if (!archetype.Mask.Get(ComponentType<T>.Id))
            throw new InvalidOperationException($"У {e} нет компонента {typeof(T).Name}.");
        return ref archetype.Chunks[rec.ChunkIndex].GetRef<T>(rec.Row);
    }

    public T Get<T>(Entity e) where T : unmanaged, IComponent => GetRef<T>(e);

    public void Set<T>(Entity e, T value) where T : unmanaged, IComponent => GetRef<T>(e) = value;

    public QueryBuilder Query() => new(this);
}
