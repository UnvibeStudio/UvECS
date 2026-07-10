using System.Runtime.CompilerServices;
using Xunit;

namespace UvEcs.Tests;

public class ChunkTests
{
    private static readonly ChunkPool Pool = new();

    private static Chunk NewChunk(params int[] componentIds)
    {
        Array.Sort(componentIds);
        var layout = ChunkLayout.Create(componentIds);
        return new Chunk(layout, Pool.Rent());
    }

    private static Chunk PosVelChunk() => NewChunk(ComponentType<Position>.Id, ComponentType<Velocity>.Id);

    [Fact]
    public void New_chunk_is_empty()
    {
        var c = PosVelChunk();
        Assert.Equal(0, c.Count);
        Assert.True(c.IsEmpty);
        Assert.False(c.IsFull);
        Assert.True(c.Capacity > 0);
    }

    [Fact]
    public void AddRow_appends_entity_with_empty_tags()
    {
        var c = PosVelChunk();
        var e = new Entity(7, 1);
        int row = c.AddRow(e);

        Assert.Equal(0, row);
        Assert.Equal(1, c.Count);
        Assert.Equal(e, c.Entities[0]);
        Assert.True(c.Tags[0].IsEmpty);
    }

    [Fact]
    public void Spans_are_sized_by_count_not_capacity()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        c.AddRow(new Entity(2, 1));

        Assert.Equal(2, c.Entities.Length);
        Assert.Equal(2, c.Tags.Length);
        Assert.Equal(2, c.GetRead<Position>().Length);
        Assert.Equal(2, c.GetWrite<Position>().Length);
    }

    [Fact]
    public void Component_data_round_trips()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        c.AddRow(new Entity(2, 1));

        var pos = c.GetWrite<Position>();
        pos[0] = new Position { X = 1, Y = 2, Z = 3 };
        pos[1] = new Position { X = 4, Y = 5, Z = 6 };

        var vel = c.GetWrite<Velocity>();
        vel[1] = new Velocity { X = 9 };

        Assert.Equal(1, c.GetRead<Position>()[0].X);
        Assert.Equal(6, c.GetRead<Position>()[1].Z);
        Assert.Equal(9, c.GetRead<Velocity>()[1].X);
        Assert.Equal(0, c.GetRead<Velocity>()[0].X);   // AddRow обнулил строку
    }

    [Fact]
    public void AddRow_zeroes_the_row_even_when_the_buffer_was_reused()
    {
        // Пул отдаёт буфер с данными предыдущего чанка. Без затирания строки
        // сущность унаследовала бы значения покойника, а на свежем буфере
        // тест зеленел бы случайно — ОС отдаёт новые страницы нулевыми.
        var pool = new ChunkPool();
        var layout = ChunkLayout.Create(new[] { ComponentType<Position>.Id });

        var buffer = pool.Rent();
        var first = new Chunk(layout, buffer);
        first.AddRow(new Entity(1, 1));
        first.GetWrite<Position>()[0] = new Position { X = 1234.5f, Y = 1, Z = 2 };
        first.SwapRemove(0);
        pool.Return(buffer);

        var second = new Chunk(layout, pool.Rent());   // тот же буфер
        second.AddRow(new Entity(2, 1));

        Assert.Equal(0, second.GetRead<Position>()[0].X);
        Assert.Equal(0, second.GetRead<Position>()[0].Y);
        Assert.Equal(0, second.GetRead<Position>()[0].Z);
    }

    [Fact]
    public void GetRef_gives_writable_reference()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        c.GetRef<Position>(0).X = 12.5f;
        Assert.Equal(12.5f, c.GetRead<Position>()[0].X);
    }

    [Fact]
    public void Accessing_absent_component_throws()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        Assert.Throws<InvalidOperationException>(() => c.GetRead<Health>());
    }

    [Fact]
    public void AddRow_beyond_capacity_throws()
    {
        var c = PosVelChunk();
        for (int i = 0; i < c.Capacity; i++) c.AddRow(new Entity(i, 1));
        Assert.True(c.IsFull);
        Assert.Throws<InvalidOperationException>(() => c.AddRow(new Entity(9999, 1)));
    }

    [Fact]
    public void SwapRemove_from_middle_moves_last_row_and_reports_it()
    {
        var c = PosVelChunk();
        var e0 = new Entity(10, 1);
        var e1 = new Entity(11, 1);
        var e2 = new Entity(12, 1);
        c.AddRow(e0); c.AddRow(e1); c.AddRow(e2);
        c.GetWrite<Position>()[2] = new Position { X = 99 };
        c.TagAt(2) = TagType<Stunned>.Bit;

        Entity moved = c.SwapRemove(0);

        Assert.Equal(e2, moved);
        Assert.Equal(2, c.Count);
        Assert.Equal(e2, c.Entities[0]);
        Assert.Equal(99, c.GetRead<Position>()[0].X);
        Assert.True(c.Tags[0].HasAll(TagType<Stunned>.Bit));
    }

    [Fact]
    public void SwapRemove_of_last_row_moves_nothing()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        c.AddRow(new Entity(2, 1));

        Entity moved = c.SwapRemove(1);

        Assert.True(moved.IsNull);
        Assert.Equal(1, c.Count);
    }

    [Fact]
    public void SwapRemove_of_only_row_empties_chunk()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        Assert.True(c.SwapRemove(0).IsNull);
        Assert.True(c.IsEmpty);
    }

    [Fact]
    public void CopyRowTo_transfers_shared_columns_and_tags()
    {
        var src = NewChunk(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
        var dst = NewChunk(ComponentType<Position>.Id, ComponentType<Health>.Id);

        src.AddRow(new Entity(5, 1));
        src.GetWrite<Position>()[0] = new Position { X = 7, Y = 8, Z = 9 };
        src.GetWrite<Velocity>()[0] = new Velocity { X = 1 };
        src.TagAt(0) = TagType<InCombat>.Bit;

        int destRow = dst.AddRow(new Entity(5, 1));
        src.CopyRowTo(0, dst, destRow);

        Assert.Equal(7, dst.GetRead<Position>()[0].X);   // общая колонка перенеслась
        Assert.Equal(9, dst.GetRead<Position>()[0].Z);
        Assert.True(dst.Tags[0].HasAll(TagType<InCombat>.Bit));   // теги перенеслись
        Assert.Equal(0, dst.GetRead<Health>()[0].Current);        // новая колонка не тронута
    }

    // Имя честное: различить sizeof(long) и Unsafe.SizeOf<Entity>() тестом нельзя,
    // пока обе величины равны восьми. Этот тест проверяет ровно то, что умеет —
    // согласованность трёх способов добраться до строки.
    [Fact]
    public void Multi_row_addressing_stays_consistent_across_accessors()
    {
        var c = PosVelChunk();
        for (int i = 0; i < 5; i++) c.AddRow(new Entity(100 + i, 1));

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(100 + i, c.EntityAt(i).Id);
            Assert.Equal(c.Entities[i], c.EntityAt(i));
        }

        c.TagAt(3) = TagType<Dead>.Bit;
        Assert.True(c.Tags[3].HasAll(TagType<Dead>.Bit));
        Assert.True(c.Tags[2].IsEmpty);   // соседняя строка не задета
    }

    // А вот это ловит настоящий рассинхрон: если ChunkLayout зарезервирует под строку
    // не столько байт, сколько Chunk отсчитывает, колонки наедут друг на друга.
    [Fact]
    public void Layout_reserves_room_for_every_row_at_the_types_stride()
    {
        var layout = ChunkLayout.Create(new[] { ComponentType<Position>.Id });

        int entityBytes = Unsafe.SizeOf<Entity>() * layout.Capacity;
        int tagBytes = Unsafe.SizeOf<TagMask>() * layout.Capacity;

        Assert.True(layout.TagOffset >= layout.EntityOffset + entityBytes,
            "колонка тегов начинается внутри колонки сущностей");
        Assert.True(layout.ColumnOffsets[0] >= layout.TagOffset + tagBytes,
            "первая колонка компонента начинается внутри колонки тегов");
    }

    [Fact]
    public void EntityAt_and_TagAt_reject_rows_outside_count()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = c.EntityAt(1).Id; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = c.EntityAt(-1).Id; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = c.TagAt(1); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = c.TagAt(999_999); });
    }
}
