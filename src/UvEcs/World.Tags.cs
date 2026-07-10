namespace UvEcs;

public sealed partial class World
{
    /// <summary>Не структурная операция: архетип не меняется, миграции нет.</summary>
    public void SetTag<T>(Entity e) where T : unmanaged, ITag
    {
        ref var rec = ref Entities.GetRecord(e);
        var chunk = ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex];

        ref var mask = ref chunk.TagAt(rec.Row);
        mask = mask.Or(TagType<T>.Bit);

        chunk.TagUnion = chunk.TagUnion.Or(TagType<T>.Bit);   // расширяем — всегда корректно
        chunk.TagsDirty = true;
    }

    /// <remarks>TagUnion намеренно не сужается: оценка консервативна (§5 спеки).</remarks>
    public void UnsetTag<T>(Entity e) where T : unmanaged, ITag
    {
        ref var rec = ref Entities.GetRecord(e);
        var chunk = ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex];

        ref var mask = ref chunk.TagAt(rec.Row);
        mask = mask.AndNot(TagType<T>.Bit);

        chunk.TagsDirty = true;
    }

    public bool HasTag<T>(Entity e) where T : unmanaged, ITag
    {
        ref var rec = ref Entities.GetRecord(e);
        var chunk = ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex];
        return chunk.TagAt(rec.Row).HasAll(TagType<T>.Bit);
    }

    /// <summary>Вызывается в конце тика. OR по колонке, микросекунды.</summary>
    public void RecomputeTagUnions()
    {
        for (int a = 0; a < ArchetypeCount; a++)
        {
            var archetype = ArchetypeById(a);
            for (int c = 0; c < archetype.Chunks.Count; c++)
            {
                var chunk = archetype.Chunks[c];
                if (!chunk.TagsDirty) continue;

                var union = TagMask.Empty;
                var tags = chunk.Tags;
                for (int i = 0; i < tags.Length; i++) union = union.Or(tags[i]);

                chunk.TagUnion = union;
                chunk.TagsDirty = false;
            }
        }
    }
}
