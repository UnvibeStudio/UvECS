using Xunit;

namespace UvEcs.Tests;

public class StructuralTests
{
    [Fact]
    public void Add_puts_the_component_and_the_value()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 1, Y = 2, Z = 3 });

        Assert.True(w.Has<Position>(e));
        Assert.Equal(2, w.Get<Position>(e).Y);
    }

    [Fact]
    public void Add_of_existing_component_overwrites_without_migrating()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 1 });
        int archetypes = w.ArchetypeCount;

        w.Add(e, new Position { X = 5 });

        Assert.Equal(5, w.Get<Position>(e).X);
        Assert.Equal(archetypes, w.ArchetypeCount);
    }

    [Fact]
    public void Adding_a_second_component_preserves_the_first()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 7, Y = 8, Z = 9 });
        w.Add(e, new Velocity { X = 4 });

        Assert.Equal(7, w.Get<Position>(e).X);
        Assert.Equal(9, w.Get<Position>(e).Z);
        Assert.Equal(4, w.Get<Velocity>(e).X);
    }

    [Fact]
    public void Remove_drops_the_component_and_keeps_the_rest()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 7 });
        w.Add(e, new Velocity { X = 4 });

        w.Remove<Velocity>(e);

        Assert.False(w.Has<Velocity>(e));
        Assert.True(w.Has<Position>(e));
        Assert.Equal(7, w.Get<Position>(e).X);
    }

    [Fact]
    public void Removing_an_absent_component_throws()
    {
        var w = new World();
        var e = w.Create();
        Assert.Throws<InvalidOperationException>(() => w.Remove<Position>(e));
    }

    [Fact]
    public void Migration_fixes_the_record_of_the_entity_that_moved_into_the_hole()
    {
        var w = new World();
        var a = w.Create();
        var b = w.Create();
        w.Add(a, new Position { X = 1 });
        w.Add(b, new Position { X = 2 });

        // a уезжает в архетип {Position,Velocity}; b должна остаться читаемой
        w.Add(a, new Velocity { X = 9 });

        Assert.Equal(2, w.Get<Position>(b).X);
        Assert.Equal(1, w.Get<Position>(a).X);
        Assert.Equal(9, w.Get<Velocity>(a).X);
    }

    [Fact]
    public void Tags_survive_migration()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 1 });

        // ставим тег напрямую в чанке (SetTag появится в Task 12)
        ref var rec = ref w.Entities.GetRecord(e);
        w.ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex].TagAt(rec.Row) = TagType<Stunned>.Bit;

        w.Add(e, new Velocity { X = 1 });

        ref var after = ref w.Entities.GetRecord(e);
        var chunk = w.ArchetypeById(after.ArchetypeId).Chunks[after.ChunkIndex];
        Assert.True(chunk.TagAt(after.Row).HasAll(TagType<Stunned>.Bit));
    }

    [Fact]
    public void Archetype_graph_stops_growing_after_warmup()
    {
        var w = new World();
        for (int i = 0; i < 100; i++)
        {
            var e = w.Create();
            w.Add(e, new Position());
            w.Add(e, new Velocity());
        }

        int afterWarmup = w.ArchetypeCount;   // {}, {P}, {P,V}

        for (int i = 0; i < 100; i++)
        {
            var e = w.Create();
            w.Add(e, new Position());
            w.Add(e, new Velocity());
        }

        Assert.Equal(afterWarmup, w.ArchetypeCount);
        Assert.Equal(3, afterWarmup);
    }

    [Fact]
    public void Add_then_remove_returns_to_the_original_archetype()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 3 });
        int posOnly = w.Entities.GetRecord(e).ArchetypeId;

        w.Add(e, new Velocity());
        w.Remove<Velocity>(e);

        Assert.Equal(posOnly, w.Entities.GetRecord(e).ArchetypeId);
        Assert.Equal(3, w.Get<Position>(e).X);
    }

    [Fact]
    public void Structural_version_bumps_on_migration()
    {
        var w = new World();
        var e = w.Create();
        var empty = w.ArchetypeById(0);
        int before = empty.StructuralVersion;

        w.Add(e, new Position());

        Assert.NotEqual(before, empty.StructuralVersion);
    }

    [Fact]
    public void Many_entities_migrating_keep_all_data_intact()
    {
        var w = new World();
        var entities = new List<Entity>();
        for (int i = 0; i < 1000; i++)
        {
            var e = w.Create();
            w.Add(e, new Position { X = i });
            entities.Add(e);
        }

        for (int i = 0; i < entities.Count; i += 2)
            w.Add(entities[i], new Velocity { X = i * 10 });

        for (int i = 0; i < entities.Count; i++)
        {
            Assert.Equal(i, w.Get<Position>(entities[i]).X);
            if (i % 2 == 0) Assert.Equal(i * 10, w.Get<Velocity>(entities[i]).X);
            else Assert.False(w.Has<Velocity>(entities[i]));
        }
    }
}
