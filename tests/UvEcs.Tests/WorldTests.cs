using Xunit;

namespace UvEcs.Tests;

public class WorldTests
{
    [Fact]
    public void New_world_has_the_empty_archetype_only()
    {
        var w = new World();
        Assert.Equal(1, w.ArchetypeCount);
        Assert.Equal(0, w.EntityCount);
    }

    [Fact]
    public void Created_entity_is_alive_and_has_no_components()
    {
        var w = new World();
        var e = w.Create();

        Assert.True(w.IsAlive(e));
        Assert.False(w.Has<Position>(e));
        Assert.Equal(1, w.EntityCount);
    }

    [Fact]
    public void Destroy_removes_the_entity()
    {
        var w = new World();
        var e = w.Create();
        w.Destroy(e);

        Assert.False(w.IsAlive(e));
        Assert.Equal(0, w.EntityCount);
    }

    [Fact]
    public void Destroying_from_the_middle_keeps_other_entities_findable()
    {
        var w = new World();
        var a = w.Create();
        var b = w.Create();
        var c = w.Create();

        w.Destroy(a);   // b или c переезжает на освободившуюся строку

        Assert.True(w.IsAlive(b));
        Assert.True(w.IsAlive(c));
        Assert.Equal(2, w.EntityCount);
    }

    [Fact]
    public void Destroying_many_entities_in_random_order_keeps_records_consistent()
    {
        var w = new World();
        var entities = new List<Entity>();
        for (int i = 0; i < 500; i++) entities.Add(w.Create());

        var rng = new Random(42);
        var shuffled = entities.OrderBy(_ => rng.Next()).ToList();
        foreach (var e in shuffled.Take(250)) w.Destroy(e);

        foreach (var e in shuffled.Skip(250)) Assert.True(w.IsAlive(e), $"{e} должна быть жива");
        Assert.Equal(250, w.EntityCount);
    }

    [Fact]
    public void Get_on_absent_component_throws()
    {
        var w = new World();
        var e = w.Create();
        Assert.Throws<InvalidOperationException>(() => w.Get<Position>(e));
    }

    [Fact]
    public void Get_on_dead_entity_throws()
    {
        var w = new World();
        var e = w.Create();
        w.Destroy(e);
        Assert.Throws<InvalidOperationException>(() => w.Has<Position>(e));
    }

    [Fact]
    public void Same_component_set_produces_the_same_archetype()
    {
        var w = new World();
        var mask = new ComponentMask();
        mask.Set(ComponentType<Position>.Id);

        var first = w.GetOrCreateArchetype(in mask);
        var second = w.GetOrCreateArchetype(in mask);

        Assert.Same(first, second);
        Assert.Equal(2, w.ArchetypeCount);   // пустой + этот
    }
}
