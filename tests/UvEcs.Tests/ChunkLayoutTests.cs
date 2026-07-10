using Xunit;

namespace UvEcs.Tests;

public class ChunkLayoutTests
{
    private static int[] Ids(params int[] ids) { Array.Sort(ids); return ids; }

    [Fact]
    public void Empty_archetype_holds_only_entity_and_tag_columns()
    {
        var layout = ChunkLayout.Create(Array.Empty<int>());
        // 8 байт Entity + 8 байт TagMask = 16 на сущность
        Assert.Equal(ChunkPool.ChunkBytes / 16, layout.Capacity);
        Assert.Equal(0, layout.EntityOffset);
        Assert.Empty(layout.ColumnOffsets);
    }

    [Fact]
    public void Columns_are_16_byte_aligned()
    {
        var layout = ChunkLayout.Create(Ids(ComponentType<Position>.Id, ComponentType<Health>.Id));
        Assert.Equal(0, layout.EntityOffset % ChunkLayout.ColumnAlignment);
        Assert.Equal(0, layout.TagOffset % ChunkLayout.ColumnAlignment);
        Assert.All(layout.ColumnOffsets, off => Assert.Equal(0, off % ChunkLayout.ColumnAlignment));
    }

    [Fact]
    public void Everything_fits_inside_the_chunk()
    {
        var layout = ChunkLayout.Create(Ids(ComponentType<Position>.Id, ComponentType<Velocity>.Id, ComponentType<Health>.Id));
        int cap = layout.Capacity;

        Assert.True(layout.TagOffset + 8 * cap <= ChunkPool.ChunkBytes);
        for (int i = 0; i < layout.ColumnOffsets.Length; i++)
        {
            int size = ComponentRegistry.SizeOf(layout.ComponentIds[i]);
            Assert.True(layout.ColumnOffsets[i] + size * cap <= ChunkPool.ChunkBytes,
                $"колонка {i} вылезает за чанк");
        }
    }

    [Fact]
    public void Capacity_is_maximal_one_more_entity_would_not_fit()
    {
        var ids = Ids(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
        var layout = ChunkLayout.Create(ids);
        Assert.True(ChunkLayout.TotalBytesFor(ids, layout.Capacity) <= ChunkPool.ChunkBytes);
        Assert.True(ChunkLayout.TotalBytesFor(ids, layout.Capacity + 1) > ChunkPool.ChunkBytes);
    }

    [Fact]
    public void Columns_do_not_overlap()
    {
        var ids = Ids(ComponentType<Position>.Id, ComponentType<Velocity>.Id, ComponentType<Health>.Id);
        var layout = ChunkLayout.Create(ids);
        int cap = layout.Capacity;

        var ranges = new List<(int start, int end)> { (layout.EntityOffset, layout.EntityOffset + 8 * cap), (layout.TagOffset, layout.TagOffset + 8 * cap) };
        for (int i = 0; i < ids.Length; i++)
            ranges.Add((layout.ColumnOffsets[i], layout.ColumnOffsets[i] + ComponentRegistry.SizeOf(ids[i]) * cap));

        ranges.Sort((a, b) => a.start.CompareTo(b.start));
        for (int i = 1; i < ranges.Count; i++)
            Assert.True(ranges[i].start >= ranges[i - 1].end, $"колонки {i - 1} и {i} пересекаются");
    }

    [Fact]
    public void ColumnOf_finds_registered_components_and_rejects_others()
    {
        var layout = ChunkLayout.Create(Ids(ComponentType<Position>.Id, ComponentType<Health>.Id));
        Assert.InRange(layout.ColumnOf(ComponentType<Position>.Id), 0, 1);
        Assert.InRange(layout.ColumnOf(ComponentType<Health>.Id), 0, 1);
        Assert.NotEqual(layout.ColumnOf(ComponentType<Position>.Id), layout.ColumnOf(ComponentType<Health>.Id));
        Assert.Equal(-1, layout.ColumnOf(ComponentType<Mana>.Id));
    }

    [Fact]
    public void Component_larger_than_chunk_is_rejected()
    {
        int fakeId = ComponentRegistry.Register(ChunkPool.ChunkBytes + 1);
        var ex = Assert.Throws<InvalidOperationException>(() => ChunkLayout.Create(new[] { fakeId }));
        Assert.Contains("не помещается", ex.Message);
    }

    // Независимый оракул. Предыдущий тест круговой: Create сам вызывает TotalBytesFor
    // в цикле поиска ёмкости, поэтому его постусловие выполняется тождественно.
    // Здесь ожидание посчитано руками и не зависит от кода.
    [Fact]
    public void Capacity_matches_a_hand_computed_oracle()
    {
        // Position(12) + Velocity(12) + Health(8) = 32; шаг = 8(Entity) + 8(TagMask) + 32 = 48.
        // 16384 / 48 = 341, но при 341: Entity 2728 -> Align 2736, Tag -> 5472, Position -> 9568,
        // Velocity -> 13664, Health -> 16392 > 16384. Не влезает.
        // При 340 все колонки кратны 16 без добивки: 2720, 5440, 9520, 13600, 16320 <= 16384.
        var ids = Ids(ComponentType<Position>.Id, ComponentType<Velocity>.Id, ComponentType<Health>.Id);
        Assert.Equal(340, ChunkLayout.Create(ids).Capacity);

        // Только Position: шаг 28. 16384/28 = 585 -> 16396 > 16384. При 584: 4672+4672+7008 = 16352.
        var onlyPosition = Ids(ComponentType<Position>.Id);
        Assert.Equal(584, ChunkLayout.Create(onlyPosition).Capacity);
    }

    [Fact]
    public void ColumnOf_returns_minus_one_on_an_empty_archetype()
    {
        var layout = ChunkLayout.Create(Array.Empty<int>());
        Assert.Equal(-1, layout.ColumnOf(ComponentType<Position>.Id));
    }

    [Fact]
    public void Layout_does_not_alias_the_caller_array()
    {
        var ids = Ids(ComponentType<Position>.Id, ComponentType<Health>.Id);
        var layout = ChunkLayout.Create(ids);
        int probe = ids[0];

        ids[0] = 999;   // портим массив вызывающего: сортировка сломана

        Assert.Equal(probe, layout.ComponentIds[0]);           // копия не пострадала
        Assert.InRange(layout.ColumnOf(probe), 0, 1);          // бинарный поиск по-прежнему работает
    }
}
