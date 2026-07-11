namespace UvEcs;

public sealed partial class World
{
    public void Add<T>(Entity e, T value) where T : unmanaged, IComponent
    {
        ref var rec = ref Entities.GetRecord(e);
        var from = ArchetypeById(rec.ArchetypeId);
        int componentId = ComponentType<T>.Id;

        if (from.Mask.Get(componentId))
        {
            from.Chunks[rec.ChunkIndex].GetRef<T>(rec.Row) = value;   // уже есть — просто перезапись
            return;
        }

        if (!from.TryGetAddEdge(componentId, out var to))
        {
            var mask = from.Mask;
            mask.Set(componentId);
            to = GetOrCreateArchetype(in mask);
            from.SetAddEdge(componentId, to);
            to.SetRemoveEdge(componentId, from);
        }

        Migrate(e, ref rec, from, to);
        ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex].GetRef<T>(rec.Row) = value;
    }

    public void Remove<T>(Entity e) where T : unmanaged, IComponent
    {
        ref var rec = ref Entities.GetRecord(e);
        var from = ArchetypeById(rec.ArchetypeId);
        int componentId = ComponentType<T>.Id;

        if (!from.Mask.Get(componentId))
            throw new InvalidOperationException($"У {e} нет компонента {typeof(T).Name}.");

        if (!from.TryGetRemoveEdge(componentId, out var to))
        {
            var mask = from.Mask;
            mask.Unset(componentId);
            to = GetOrCreateArchetype(in mask);
            from.SetRemoveEdge(componentId, to);
            to.SetAddEdge(componentId, from);
        }

        Migrate(e, ref rec, from, to);
    }

    /// <summary>
    /// Вставляем в приёмник, копируем общие колонки и теги, затем swap-remove из источника
    /// и чиним запись сущности, переехавшей на освободившуюся строку.
    /// </summary>
    internal void Migrate(Entity e, ref EntityRecord rec, Archetype from, Archetype to)
    {
        var fromChunk = from.Chunks[rec.ChunkIndex];
        int fromRow = rec.Row;
        int fromChunkIndex = rec.ChunkIndex;

        var toChunk = to.GetOrCreateChunkWithSpace(Pool, out int toChunkIndex);
        int toRow = toChunk.AddRow(e);

        fromChunk.CopyRowTo(fromRow, toChunk, toRow);
        toChunk.TagUnion = toChunk.TagUnion.Or(toChunk.TagAt(toRow));

        Entity moved = fromChunk.SwapRemove(fromRow);
        if (!moved.IsNull)
            // вытесненная swap-remove'ом сущность — не мигрирующая; у неё меняется только Row
            Entities.RecordRefUnchecked(moved.Id).Row = fromRow;

        from.ReleaseChunkIfEmpty(fromChunkIndex, Pool);

        rec.ArchetypeId = to.Id;
        rec.ChunkIndex = toChunkIndex;
        rec.Row = toRow;

        from.BumpStructuralVersion();
        to.BumpStructuralVersion();
    }
}
