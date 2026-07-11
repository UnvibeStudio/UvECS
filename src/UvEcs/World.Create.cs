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

    public Entity Create<T1, T2>(in T1 c1, in T2 c2)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        AssertDistinct(2, in mask);

        var to = GetOrCreateArchetype(in mask);
        var chunk = PlaceNew(to, out var e, out int row);
        chunk.GetRef<T1>(row) = c1;
        chunk.GetRef<T2>(row) = c2;
        to.BumpStructuralVersion();
        return e;
    }

    public Entity Create<T1, T2, T3>(in T1 c1, in T2 c2, in T3 c3)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        mask.Set(ComponentType<T3>.Id);
        AssertDistinct(3, in mask);

        var to = GetOrCreateArchetype(in mask);
        var chunk = PlaceNew(to, out var e, out int row);
        chunk.GetRef<T1>(row) = c1;
        chunk.GetRef<T2>(row) = c2;
        chunk.GetRef<T3>(row) = c3;
        to.BumpStructuralVersion();
        return e;
    }

    public Entity Create<T1, T2, T3, T4>(in T1 c1, in T2 c2, in T3 c3, in T4 c4)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        mask.Set(ComponentType<T3>.Id);
        mask.Set(ComponentType<T4>.Id);
        AssertDistinct(4, in mask);

        var to = GetOrCreateArchetype(in mask);
        var chunk = PlaceNew(to, out var e, out int row);
        chunk.GetRef<T1>(row) = c1;
        chunk.GetRef<T2>(row) = c2;
        chunk.GetRef<T3>(row) = c3;
        chunk.GetRef<T4>(row) = c4;
        to.BumpStructuralVersion();
        return e;
    }

    private void CreateManyInto(int count, Span<Entity> dest, in ComponentMask mask, int arity)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (dest.Length < count) throw new ArgumentException("dest короче count.", nameof(dest));
        AssertDistinct(arity, in mask);
        if (count == 0) return;

        var to = GetOrCreateArchetype(in mask);
        to.Reserve(Pool, count);
        for (int i = 0; i < count; i++)
        {
            PlaceNew(to, out var e, out _);
            dest[i] = e;
        }
        to.BumpStructuralVersion();
    }

    public void CreateMany<T1>(int count, Span<Entity> dest)
        where T1 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        CreateManyInto(count, dest, in mask, 1);
    }

    public void CreateMany<T1, T2>(int count, Span<Entity> dest)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        CreateManyInto(count, dest, in mask, 2);
    }

    public void CreateMany<T1, T2, T3>(int count, Span<Entity> dest)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        mask.Set(ComponentType<T3>.Id);
        CreateManyInto(count, dest, in mask, 3);
    }

    public void CreateMany<T1, T2, T3, T4>(int count, Span<Entity> dest)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        mask.Set(ComponentType<T3>.Id);
        mask.Set(ComponentType<T4>.Id);
        CreateManyInto(count, dest, in mask, 4);
    }
}
