using Xunit;

namespace UvEcs.Tests;

public class ArchetypeTests
{
    private static Archetype Make(int id, params int[] componentIds)
    {
        Array.Sort(componentIds);
        var mask = new ComponentMask();
        foreach (int c in componentIds) mask.Set(c);
        return new Archetype(id, mask, componentIds);
    }

    private static Archetype PosVel(int id = 0) => Make(id, ComponentType<Position>.Id, ComponentType<Velocity>.Id);

    [Fact]
    public void Archetype_exposes_mask_and_layout()
    {
        var a = PosVel();
        Assert.True(a.Mask.Get(ComponentType<Position>.Id));
        Assert.True(a.Mask.Get(ComponentType<Velocity>.Id));
        Assert.False(a.Mask.Get(ComponentType<Health>.Id));
        Assert.True(a.Layout.Capacity > 0);
        Assert.Equal(0, a.EntityCount);
    }

    [Fact]
    public void First_chunk_is_created_on_demand()
    {
        var pool = new ChunkPool();
        var a = PosVel();
        Assert.Empty(a.Chunks);

        var chunk = a.GetOrCreateChunkWithSpace(pool, out int index);
        Assert.Equal(0, index);
        Assert.Single(a.Chunks);
        Assert.Same(chunk, a.Chunks[0]);
    }

    [Fact]
    public void Full_chunk_forces_a_new_one()
    {
        var pool = new ChunkPool();
        var a = PosVel();

        var first = a.GetOrCreateChunkWithSpace(pool, out int i0);
        for (int i = 0; i < first.Capacity; i++) first.AddRow(new Entity(i, 1));

        var second = a.GetOrCreateChunkWithSpace(pool, out int i1);
        Assert.NotSame(first, second);
        Assert.Equal(1, i1);
        Assert.Equal(2, a.Chunks.Count);
    }

    [Fact]
    public void EntityCount_sums_all_chunks()
    {
        var pool = new ChunkPool();
        var a = PosVel();
        var c = a.GetOrCreateChunkWithSpace(pool, out _);
        c.AddRow(new Entity(1, 1));
        c.AddRow(new Entity(2, 1));
        Assert.Equal(2, a.EntityCount);
    }

    [Fact]
    public void Empty_chunk_is_kept_as_spare_but_second_one_is_returned()
    {
        var pool = new ChunkPool();
        var a = PosVel();

        a.GetOrCreateChunkWithSpace(pool, out int i0);
        var c0 = a.Chunks[0];
        for (int i = 0; i < c0.Capacity; i++) c0.AddRow(new Entity(i, 1));
        a.GetOrCreateChunkWithSpace(pool, out int i1);

        // оба пусты -> первый остаётся про запас, второй уходит в пул
        while (!c0.IsEmpty) c0.SwapRemove(c0.Count - 1);
        a.ReleaseChunkIfEmpty(i1, pool);
        Assert.Equal(1, pool.FreeCount);

        a.ReleaseChunkIfEmpty(i0, pool);
        Assert.Equal(1, pool.FreeCount);       // гистерезис: последний пустой не отдаём
        Assert.Single(a.Chunks);
    }

    [Fact]
    public void A_non_last_empty_chunk_is_never_removed()
    {
        // Ровно та защита, ради которой ReleaseChunkIfEmpty удаляет только последний чанк:
        // удаление из середины сдвинуло бы ChunkIndex в EntityRecord соседних чанков.
        // Без этого теста будущая «оптимизация» вернула бы удаление из середины молча.
        var pool = new ChunkPool();
        var a = PosVel();

        // Три чанка: [полный c0][полный c1][пустой c2]
        a.GetOrCreateChunkWithSpace(pool, out _);
        var c0 = a.Chunks[0];
        for (int i = 0; i < c0.Capacity; i++) c0.AddRow(new Entity(i, 1));

        a.GetOrCreateChunkWithSpace(pool, out _);
        var c1 = a.Chunks[1];
        for (int i = 0; i < c1.Capacity; i++) c1.AddRow(new Entity(10_000 + i, 1));

        a.GetOrCreateChunkWithSpace(pool, out int i2);   // c2 пустой
        Assert.Equal(3, a.Chunks.Count);

        // Опустошаем СРЕДНИЙ чанк и пробуем освободить его.
        while (!c1.IsEmpty) c1.SwapRemove(c1.Count - 1);
        a.ReleaseChunkIfEmpty(1, pool);

        // c1 не последний -> остаётся на месте, порядок и число чанков не меняются.
        Assert.Equal(3, a.Chunks.Count);
        Assert.Same(c0, a.Chunks[0]);
        Assert.Same(c1, a.Chunks[1]);
    }

    [Fact]
    public void Add_and_remove_edges_are_stored_and_found()
    {
        var a = PosVel(0);
        var b = Make(1, ComponentType<Position>.Id, ComponentType<Velocity>.Id, ComponentType<Health>.Id);

        Assert.False(a.TryGetAddEdge(ComponentType<Health>.Id, out _));

        a.SetAddEdge(ComponentType<Health>.Id, b);
        b.SetRemoveEdge(ComponentType<Health>.Id, a);

        Assert.True(a.TryGetAddEdge(ComponentType<Health>.Id, out var found));
        Assert.Same(b, found);
        Assert.True(b.TryGetRemoveEdge(ComponentType<Health>.Id, out var back));
        Assert.Same(a, back);
    }

    [Fact]
    public void StructuralVersion_changes_on_bump()
    {
        var a = PosVel();
        int before = a.StructuralVersion;
        a.BumpStructuralVersion();
        Assert.NotEqual(before, a.StructuralVersion);
    }

    [Fact]
    public void Reserve_pre_allocates_capacity_for_count_rows()
    {
        var pool = new ChunkPool();
        var a = PosVel();

        a.Reserve(pool, 1000);

        int capacity = 0;
        foreach (var ch in a.Chunks) capacity += ch.Capacity;
        Assert.True(capacity >= 1000, $"ждали ёмкость >= 1000, получили {capacity}");
    }

    [Fact]
    public void Reserve_counts_existing_free_space_and_does_not_over_allocate()
    {
        var pool = new ChunkPool();
        var a = PosVel();

        a.Reserve(pool, 100);
        int chunksAfterFirst = a.Chunks.Count;

        // Второй резерв в пределах уже свободного места не должен добавлять чанки.
        a.Reserve(pool, 100);
        Assert.Equal(chunksAfterFirst, a.Chunks.Count);
    }

    [Fact]
    public void Reserve_with_non_positive_count_does_nothing()
    {
        var pool = new ChunkPool();
        var a = PosVel();
        a.Reserve(pool, 0);
        a.Reserve(pool, -5);
        Assert.Empty(a.Chunks);
    }
}
