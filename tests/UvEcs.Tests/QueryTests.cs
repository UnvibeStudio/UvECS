using Xunit;

namespace UvEcs.Tests;

public class QueryTests
{
    private static int CountEntities(Query q)
    {
        int n = 0;
        foreach (var chunk in q)
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Passes(i)) n++;
        return n;
    }

    private static List<Entity> PassingEntities(Query q)
    {
        var seen = new List<Entity>();
        foreach (var chunk in q)
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Passes(i)) seen.Add(chunk.Entities[i]);
        return seen;
    }

    [Fact]
    public void Query_matches_archetypes_containing_all_components()
    {
        var w = new World();
        var a = w.Create(); w.Add(a, new Position());
        var b = w.Create(); w.Add(b, new Position()); w.Add(b, new Velocity());
        var c = w.Create(); w.Add(c, new Velocity());

        var q = w.Query().All<Position>().Build();
        Assert.Equal(2, CountEntities(q));
    }

    [Fact]
    public void Query_with_two_components_matches_the_intersection()
    {
        var w = new World();
        var a = w.Create(); w.Add(a, new Position());
        var b = w.Create(); w.Add(b, new Position()); w.Add(b, new Velocity());

        var q = w.Query().All<Position, Velocity>().Build();
        Assert.Equal(1, CountEntities(q));
    }

    [Fact]
    public void None_excludes_archetypes()
    {
        var w = new World();
        var a = w.Create(); w.Add(a, new Position());
        var b = w.Create(); w.Add(b, new Position()); w.Add(b, new Velocity());

        var q = w.Query().All<Position>().None<Velocity>().Build();
        Assert.Equal(1, CountEntities(q));
    }

    [Fact]
    public void New_archetypes_are_picked_up_incrementally()
    {
        var w = new World();
        var q = w.Query().All<Position>().Build();
        Assert.Equal(0, CountEntities(q));

        var e = w.Create();
        w.Add(e, new Position());

        Assert.Equal(1, CountEntities(q));   // архетип {Position} появился после Build
        Assert.Equal(1, q.MatchedArchetypeCount);

        var e2 = w.Create();
        w.Add(e2, new Position());
        w.Add(e2, new Velocity());

        Assert.Equal(2, CountEntities(q));
        Assert.Equal(2, q.MatchedArchetypeCount);   // {P} и {P,V}
    }

    [Fact]
    public void GetWrite_mutates_and_GetRead_sees_it()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 1 });
        w.Add(e, new Velocity { X = 10 });

        var q = w.Query().All<Position, Velocity>().Build();
        foreach (var chunk in q)
        {
            var pos = chunk.GetWrite<Position>();
            var vel = chunk.GetRead<Velocity>();
            for (int i = 0; i < chunk.Count; i++) pos[i].X += vel[i].X;
        }

        Assert.Equal(11, w.Get<Position>(e).X);
    }

    [Fact]
    public void Empty_chunks_are_skipped()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position());
        w.Destroy(e);

        var q = w.Query().All<Position>().Build();
        int chunks = 0;
        foreach (var _ in q) chunks++;
        Assert.Equal(0, chunks);
    }

    // Симметричная популяция (1 с тегом, 1 без) + проверка count==1 НЕ отличила бы
    // WithTag от WithoutTag: перепутай TagAll и TagNone, и прошёл бы не тот объект,
    // но счётчик остался бы единицей. Поэтому проверяем ИДЕНТИЧНОСТЬ прошедшего.
    [Fact]
    public void WithTag_passes_only_the_tagged_entity()
    {
        var w = new World();
        var tagged = w.Create(); w.Add(tagged, new Position()); w.SetTag<Stunned>(tagged);
        var plain = w.Create(); w.Add(plain, new Position());

        var q = w.Query().All<Position>().WithTag<Stunned>().Build();
        var seen = PassingEntities(q);

        Assert.Single(seen);
        Assert.Equal(tagged, seen[0]);        // именно с тегом, не plain
    }

    [Fact]
    public void WithoutTag_passes_only_the_untagged_entity()
    {
        var w = new World();
        var tagged = w.Create(); w.Add(tagged, new Position()); w.SetTag<Dead>(tagged);
        var plain = w.Create(); w.Add(plain, new Position());

        var q = w.Query().All<Position>().WithoutTag<Dead>().Build();
        var seen = PassingEntities(q);

        Assert.Single(seen);
        Assert.Equal(plain, seen[0]);         // именно без тега, не tagged
    }

    [Fact]
    public void Chunk_without_the_tag_is_skipped_entirely()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position());

        var q = w.Query().All<Position>().WithTag<InCombat>().Build();
        int visited = 0;
        foreach (var _ in q) visited++;
        Assert.Equal(0, visited);   // TagUnion пуст -> чанк не посещается вовсе
    }

    [Fact]
    public void AllRowsPass_is_true_when_no_tag_filter_applies()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position());

        var q = w.Query().All<Position>().Build();
        foreach (var chunk in q) Assert.True(chunk.AllRowsPass);
    }

    [Fact]
    public void Entities_span_exposes_the_entities_of_the_chunk()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position());

        var q = w.Query().All<Position>().Build();
        foreach (var chunk in q)
        {
            Assert.Equal(1, chunk.Entities.Length);
            Assert.Equal(e, chunk.Entities[0]);
        }
    }

#if DEBUG
    [Fact]
    public void Structural_change_during_foreach_throws()
    {
        var w = new World();
        var a = w.Create(); w.Add(a, new Position());
        var b = w.Create(); w.Add(b, new Position());

        var q = w.Query().All<Position>().Build();

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var chunk in q)
            {
                w.Add(a, new Velocity());   // структурное изменение посреди итерации
            }
        });
    }

    [Fact]
    public void Non_structural_change_during_foreach_is_allowed()
    {
        // Теги не структурны — менять их посреди foreach можно.
        var w = new World();
        var a = w.Create(); w.Add(a, new Position());

        var q = w.Query().All<Position>().Build();
        foreach (var chunk in q)
            w.SetTag<Stunned>(a);   // не должно бросить

        Assert.True(w.HasTag<Stunned>(a));
    }
#endif
}
