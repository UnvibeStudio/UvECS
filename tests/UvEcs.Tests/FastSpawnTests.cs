using Xunit;

namespace UvEcs.Tests;

public class FastSpawnTests
{
    [Fact]
    public void Create_with_one_component_places_it_with_its_value()
    {
        var w = new World();
        var e = w.Create(new Position { X = 1, Y = 2, Z = 3 });

        Assert.True(w.Has<Position>(e));
        Assert.Equal(2, w.Get<Position>(e).Y);
        Assert.True(w.IsAlive(e));
    }

    [Fact]
    public void Create_with_two_components_creates_only_the_final_archetype()
    {
        var w = new World();            // стартует с архетипом-∅ (Id 0)
        int before = w.ArchetypeCount;  // 1

        w.Create(new Position(), new Velocity());

        // Прямая вставка создаёт ТОЛЬКО {Position,Velocity}. Путь через миграции
        // создал бы ещё и промежуточный {Position} -> before + 2. Это и есть доказательство.
        Assert.Equal(before + 1, w.ArchetypeCount);
    }

    [Fact]
    public void Create_with_two_components_places_both_values()
    {
        var w = new World();
        var e = w.Create(new Position { X = 7 }, new Velocity { X = 4 });

        Assert.Equal(7, w.Get<Position>(e).X);
        Assert.Equal(4, w.Get<Velocity>(e).X);
    }

    [Fact]
    public void Create_with_four_components_places_all_values()
    {
        var w = new World();
        var e = w.Create(
            new Position { X = 1 },
            new Velocity { X = 2 },
            new Health { Current = 3, Max = 30 },
            new Mana { Current = 4 });

        Assert.Equal(1, w.Get<Position>(e).X);
        Assert.Equal(2, w.Get<Velocity>(e).X);
        Assert.Equal(3, w.Get<Health>(e).Current);
        Assert.Equal(4, w.Get<Mana>(e).Current);
    }

    [Fact]
    public void Create_direct_matches_create_then_add()
    {
        var direct = new World();
        var a = direct.Create(new Position { X = 5 }, new Velocity { X = 6 });

        var staged = new World();
        var b = staged.Create();
        staged.Add(b, new Position { X = 5 });
        staged.Add(b, new Velocity { X = 6 });

        // Наблюдаемое состояние совпадает: те же компоненты, те же значения.
        Assert.True(direct.Has<Position>(a) && direct.Has<Velocity>(a));
        Assert.True(staged.Has<Position>(b) && staged.Has<Velocity>(b));
        Assert.Equal(staged.Get<Position>(b).X, direct.Get<Position>(a).X);
        Assert.Equal(staged.Get<Velocity>(b).X, direct.Get<Velocity>(a).X);
    }

#if DEBUG
    [Fact]
    public void Create_with_duplicate_component_type_throws_in_debug()
    {
        var w = new World();
        Assert.Throws<ArgumentException>(() => w.Create(new Position { X = 1 }, new Position { X = 2 }));
    }
#endif

    [Fact]
    public void CreateMany_spawns_all_entities_in_one_archetype()
    {
        var w = new World();
        int before = w.ArchetypeCount;

        var dest = new Entity[500];
        w.CreateMany<Position, Velocity>(500, dest);

        Assert.Equal(500, w.EntityCount);
        Assert.Equal(before + 1, w.ArchetypeCount);   // только {Position,Velocity}
        for (int i = 0; i < 500; i++)
        {
            Assert.True(w.IsAlive(dest[i]));
            Assert.True(w.Has<Position>(dest[i]));
            Assert.True(w.Has<Velocity>(dest[i]));
        }
    }

    [Fact]
    public void CreateMany_ids_are_distinct()
    {
        var w = new World();
        var dest = new Entity[300];
        w.CreateMany<Position>(300, dest);

        var seen = new HashSet<int>();
        for (int i = 0; i < 300; i++)
            Assert.True(seen.Add(dest[i].Id), $"повтор Id на позиции {i}");
    }

    [Fact]
    public void CreateMany_spans_multiple_chunks()
    {
        // 5000 строк заведомо больше вместимости одного 16 КБ чанка -> несколько чанков.
        var w = new World();
        var dest = new Entity[5000];
        w.CreateMany<Position>(5000, dest);

        Assert.Equal(5000, w.EntityCount);
        for (int i = 0; i < 5000; i++)
            Assert.True(w.IsAlive(dest[i]));
    }

    [Fact]
    public void CreateMany_defaults_component_values_to_zero()
    {
        var w = new World();
        var dest = new Entity[10];
        w.CreateMany<Position>(10, dest);

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(0, w.Get<Position>(dest[i]).X);
            Assert.Equal(0, w.Get<Position>(dest[i]).Y);
        }
    }

    [Fact]
    public void CreateMany_with_zero_count_is_a_noop()
    {
        var w = new World();
        var dest = new Entity[4];
        w.CreateMany<Position>(0, dest);
        Assert.Equal(0, w.EntityCount);
    }

    [Fact]
    public void CreateMany_throws_when_dest_is_too_small()
    {
        var w = new World();
        var dest = new Entity[3];
        Assert.Throws<ArgumentException>(() => w.CreateMany<Position>(4, dest));
        Assert.Equal(0, w.EntityCount);   // бросили до любых мутаций
    }

    [Fact]
    public void CreateMany_throws_on_negative_count()
    {
        var w = new World();
        var dest = new Entity[4];
        Assert.Throws<ArgumentOutOfRangeException>(() => w.CreateMany<Position>(-1, dest));
    }
}
