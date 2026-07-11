using System.Diagnostics;

namespace UvEcs;

public sealed partial class World
{
    /// <summary>
    /// Общий примитив прямого создания: заводит сущность и кладёт её строку в <paramref name="to"/>,
    /// сразу проставляя EntityRecord. Компоненты НЕ пишет и версию НЕ бампает — это делает вызывающий
    /// (одиночный Create — на сущность; CreateMany — один раз на пакет).
    /// </summary>
    private Chunk PlaceNew(Archetype to, out Entity e, out int row)
    {
        e = Entities.Create();
        var chunk = to.GetOrCreateChunkWithSpace(Pool, out int chunkIndex);
        row = chunk.AddRow(e);

        ref var rec = ref Entities.GetRecord(e);
        rec.ArchetypeId = to.Id;
        rec.ChunkIndex = chunkIndex;
        rec.Row = row;
        return chunk;
    }

    /// <summary>
    /// В Debug ловит дубликат типа в дженериках (Create&lt;Position,Position&gt;): число битов
    /// в маске обязано равняться арности. В Release вызов вырезается.
    /// </summary>
    [Conditional("DEBUG")]
    private static void AssertDistinct(int arity, in ComponentMask mask)
    {
        if (mask.PopCount() != arity)
            throw new ArgumentException("Повторяющийся тип компонента в Create/CreateMany.");
    }

    public Entity Create<T1>(in T1 c1)
        where T1 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);

        var to = GetOrCreateArchetype(in mask);
        var chunk = PlaceNew(to, out var e, out int row);
        chunk.GetRef<T1>(row) = c1;
        to.BumpStructuralVersion();
        return e;
    }
}
