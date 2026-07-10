using Xunit;

namespace UvEcs.Tests;

public class EntityStoreTests
{
    [Fact]
    public void Default_entity_is_null()
    {
        Assert.True(default(Entity).IsNull);
        Assert.True(Entity.Null.IsNull);
    }

    [Fact]
    public void Created_entities_are_alive_and_distinct()
    {
        var store = new EntityStore();
        var a = store.Create();
        var b = store.Create();

        Assert.NotEqual(a.Id, b.Id);
        Assert.False(a.IsNull);
        Assert.True(store.IsAlive(a));
        Assert.True(store.IsAlive(b));
    }

    [Fact]
    public void Destroyed_entity_is_not_alive()
    {
        var store = new EntityStore();
        var e = store.Create();
        store.Destroy(e);
        Assert.False(store.IsAlive(e));
    }

    [Fact]
    public void Destroyed_id_is_reused_with_a_new_version()
    {
        var store = new EntityStore();
        var first = store.Create();
        store.Destroy(first);
        var second = store.Create();

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Version, second.Version);
        Assert.False(store.IsAlive(first));
        Assert.True(store.IsAlive(second));
    }

    [Fact]
    public void GetRecord_throws_on_stale_entity()
    {
        var store = new EntityStore();
        var e = store.Create();
        store.Destroy(e);
        Assert.Throws<InvalidOperationException>(() => store.GetRecord(e));
    }

    [Fact]
    public void GetRecord_returns_writable_reference()
    {
        var store = new EntityStore();
        var e = store.Create();
        store.GetRecord(e).Row = 42;
        Assert.Equal(42, store.GetRecord(e).Row);
    }

    [Fact]
    public void Destroying_twice_throws()
    {
        var store = new EntityStore();
        var e = store.Create();
        store.Destroy(e);
        Assert.Throws<InvalidOperationException>(() => store.Destroy(e));
    }

    [Fact]
    public void Store_grows_beyond_initial_capacity()
    {
        var store = new EntityStore();
        var created = new List<Entity>();
        for (int i = 0; i < 5000; i++) created.Add(store.Create());

        Assert.All(created, e => Assert.True(store.IsAlive(e)));
        Assert.Equal(5000, created.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Free_list_is_lifo_and_does_not_leak_ids()
    {
        var store = new EntityStore();
        var a = store.Create();
        var b = store.Create();
        store.Destroy(a);
        store.Destroy(b);

        var c = store.Create();
        var d = store.Create();
        var e = store.Create();

        Assert.Equal(b.Id, c.Id);   // LIFO: последний уничтоженный отдаётся первым
        Assert.Equal(a.Id, d.Id);
        Assert.Equal(2, e.Id);      // свежий id, свободных больше нет
    }

    [Fact]
    public void Record_ref_survives_growth_caused_by_later_creates()
    {
        var store = new EntityStore();
        var first = store.Create();

        ref var rec = ref store.GetRecord(first);
        rec.Row = 111;

        for (int i = 0; i < 20_000; i++) store.Create();   // перешагиваем несколько страниц

        rec.Row = 222;                                     // пишем через ref, взятый до роста
        Assert.Equal(222, store.GetRecord(first).Row);     // запись видна через свежий поиск
    }
}
