using Xunit;

namespace UvEcs.Tests;

public class TagTests
{
    private static Chunk ChunkOf(World w, Entity e)
    {
        ref var rec = ref w.Entities.GetRecord(e);
        return w.ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex];
    }

    [Fact]
    public void Tag_round_trips()
    {
        var w = new World();
        var e = w.Create();

        Assert.False(w.HasTag<Stunned>(e));
        w.SetTag<Stunned>(e);
        Assert.True(w.HasTag<Stunned>(e));
        w.UnsetTag<Stunned>(e);
        Assert.False(w.HasTag<Stunned>(e));
    }

    [Fact]
    public void Setting_a_tag_twice_is_idempotent()
    {
        var w = new World();
        var e = w.Create();
        w.SetTag<Stunned>(e);
        w.SetTag<Stunned>(e);
        Assert.True(w.HasTag<Stunned>(e));
        w.UnsetTag<Stunned>(e);
        Assert.False(w.HasTag<Stunned>(e));
    }

    [Fact]
    public void Tags_do_not_change_the_archetype()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position());
        int archetypes = w.ArchetypeCount;
        int archetypeId = w.Entities.GetRecord(e).ArchetypeId;

        w.SetTag<Stunned>(e);
        w.SetTag<InCombat>(e);

        Assert.Equal(archetypes, w.ArchetypeCount);
        Assert.Equal(archetypeId, w.Entities.GetRecord(e).ArchetypeId);
    }

    [Fact]
    public void Tags_are_independent_of_each_other()
    {
        var w = new World();
        var e = w.Create();
        w.SetTag<Stunned>(e);
        w.SetTag<Dead>(e);
        w.UnsetTag<Stunned>(e);

        Assert.False(w.HasTag<Stunned>(e));
        Assert.True(w.HasTag<Dead>(e));
    }

    [Fact]
    public void TagUnion_grows_when_a_tag_is_set()
    {
        var w = new World();
        var e = w.Create();
        var chunk = ChunkOf(w, e);
        Assert.True(chunk.TagUnion.IsEmpty);

        w.SetTag<InCombat>(e);
        Assert.True(chunk.TagUnion.HasAll(TagType<InCombat>.Bit));
        Assert.True(chunk.TagsDirty);
    }

    [Fact]
    public void TagUnion_does_not_shrink_on_unset_it_is_conservative()
    {
        var w = new World();
        var e = w.Create();
        var chunk = ChunkOf(w, e);

        w.SetTag<InCombat>(e);
        w.UnsetTag<InCombat>(e);

        // консервативность: шире правды — безопасно, мы лишь не пропустим чанк
        Assert.True(chunk.TagUnion.HasAll(TagType<InCombat>.Bit));
        Assert.False(w.HasTag<InCombat>(e));
    }

    [Fact]
    public void RecomputeTagUnions_restores_the_exact_value()
    {
        var w = new World();
        var e = w.Create();
        var chunk = ChunkOf(w, e);

        w.SetTag<InCombat>(e);
        w.UnsetTag<InCombat>(e);
        w.RecomputeTagUnions();

        Assert.True(chunk.TagUnion.IsEmpty);
        Assert.False(chunk.TagsDirty);
    }

    [Fact]
    public void RecomputeTagUnions_keeps_tags_that_are_still_set()
    {
        var w = new World();
        var a = w.Create();
        var b = w.Create();
        var chunk = ChunkOf(w, a);

        w.SetTag<InCombat>(a);
        w.SetTag<Dead>(b);
        w.UnsetTag<InCombat>(a);
        w.RecomputeTagUnions();

        Assert.False(chunk.TagUnion.HasAny(TagType<InCombat>.Bit));
        Assert.True(chunk.TagUnion.HasAll(TagType<Dead>.Bit));
    }

    [Fact]
    public void Tag_survives_component_migration()
    {
        var w = new World();
        var e = w.Create();
        w.SetTag<Dead>(e);
        w.Add(e, new Position());
        Assert.True(w.HasTag<Dead>(e));
    }

    [Fact]
    public void Tag_of_a_swapped_entity_follows_it()
    {
        var w = new World();
        var a = w.Create();
        var b = w.Create();
        w.SetTag<Dead>(b);

        w.Destroy(a);   // b переезжает на строку a

        Assert.True(w.HasTag<Dead>(b));
    }
}
