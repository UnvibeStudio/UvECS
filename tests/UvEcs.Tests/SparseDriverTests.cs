using Xunit;

namespace UvEcs.Tests;

public class SparseDriverTests
{
    [Fact]
    public void AddSparse_then_HasSparse_and_GetSparseRef()
    {
        var w = new World();
        var e = w.Create();
        w.AddSparse(e, new GuildBuff { Id = 3, Until = 1.5f });

        Assert.True(w.HasSparse<GuildBuff>(e));
        Assert.Equal(3, w.GetSparseRef<GuildBuff>(e).Id);
    }

    [Fact]
    public void RemoveSparse_clears_it()
    {
        var w = new World();
        var e = w.Create();
        w.AddSparse(e, new GuildBuff { Id = 3 });

        Assert.True(w.RemoveSparse<GuildBuff>(e));
        Assert.False(w.HasSparse<GuildBuff>(e));
        Assert.False(w.RemoveSparse<GuildBuff>(e));
    }

    [Fact]
    public void Sparse_survives_archetype_migration_untouched()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position());
        w.AddSparse(e, new GuildBuff { Id = 42 });

        w.Add(e, new Velocity());   // миграция

        Assert.True(w.HasSparse<GuildBuff>(e));
        Assert.Equal(42, w.GetSparseRef<GuildBuff>(e).Id);
    }

    [Fact]
    public void Chunks_iteration_is_rejected_for_sparse_queries()
    {
        var w = new World();
        var q = w.Query().All<Position>().AllSparse<GuildBuff>().Build();
        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var _ in q) { }
        });
    }

    [Fact]
    public void BySparse_visits_only_carriers_matching_the_archetype()
    {
        var w = new World();
        var withBuff = new List<Entity>();

        for (int i = 0; i < 100; i++)
        {
            var e = w.Create();
            w.Add(e, new Position { X = i });
            if (i % 10 == 0) { w.AddSparse(e, new GuildBuff { Id = i }); withBuff.Add(e); }
        }

        // сущность с баффом, но без Position — не должна попасть
        var noPos = w.Create();
        w.AddSparse(noPos, new GuildBuff { Id = 999 });

        var q = w.Query().All<Position>().AllSparse<GuildBuff>().Build();
        var seen = new List<Entity>();
        foreach (var hit in q.BySparse()) seen.Add(hit.Entity);

        Assert.Equal(withBuff.Count, seen.Count);
        Assert.All(seen, e => Assert.Contains(e, withBuff));
    }

    [Fact]
    public void BySparse_gives_access_to_archetype_components()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 5 });
        w.AddSparse(e, new GuildBuff { Id = 1 });

        var q = w.Query().All<Position>().AllSparse<GuildBuff>().Build();
        foreach (var hit in q.BySparse())
        {
            Assert.Equal(5, hit.Chunk.GetRead<Position>()[hit.Row].X);
            hit.Chunk.GetRef<Position>(hit.Row).X = 6;
        }

        Assert.Equal(6, w.Get<Position>(e).X);
    }

    [Fact]
    public void BySparse_requires_every_listed_sparse_component()
    {
        var w = new World();
        var both = w.Create(); w.Add(both, new Position());
        w.AddSparse(both, new GuildBuff()); w.AddSparse(both, new QuestFlag());

        var onlyOne = w.Create(); w.Add(onlyOne, new Position());
        w.AddSparse(onlyOne, new GuildBuff());

        var q = w.Query().All<Position>().AllSparse<GuildBuff>().AllSparse<QuestFlag>().Build();
        var seen = new List<Entity>();
        foreach (var hit in q.BySparse()) seen.Add(hit.Entity);

        Assert.Single(seen);
        Assert.Equal(both, seen[0]);
    }

    [Fact]
    public void BySparse_picks_the_smallest_set_as_driver()
    {
        var w = new World();
        for (int i = 0; i < 50; i++)
        {
            var e = w.Create();
            w.Add(e, new Position());
            w.AddSparse(e, new GuildBuff());          // 50 носителей
            if (i < 3) w.AddSparse(e, new QuestFlag()); // 3 носителя — драйвер
        }

        var q = w.Query().All<Position>().AllSparse<GuildBuff>().AllSparse<QuestFlag>().Build();
        int seen = 0;
        foreach (var _ in q.BySparse()) seen++;

        Assert.Equal(3, seen);
        Assert.Equal(3, w.SparseSetOf<QuestFlag>().Count);
        Assert.Equal(50, w.SparseSetOf<GuildBuff>().Count);
    }

    [Fact]
    public void BySparse_respects_tag_filters()
    {
        var w = new World();
        var tagged = w.Create(); w.Add(tagged, new Position()); w.AddSparse(tagged, new GuildBuff()); w.SetTag<Stunned>(tagged);
        var plain = w.Create(); w.Add(plain, new Position()); w.AddSparse(plain, new GuildBuff());

        var q = w.Query().All<Position>().AllSparse<GuildBuff>().WithTag<Stunned>().Build();
        var seen = new List<Entity>();
        foreach (var hit in q.BySparse()) seen.Add(hit.Entity);

        Assert.Single(seen);
        Assert.Equal(tagged, seen[0]);
    }

    [Fact]
    public void BySparse_skips_dead_entities()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position());
        w.AddSparse(e, new GuildBuff());
        w.Destroy(e);   // World.Destroy чистит sparse-наборы (RemoveFromAllSparseSets) — носитель выбывает из драйвера, поэтому итог пуст

        var q = w.Query().All<Position>().AllSparse<GuildBuff>().Build();
        int seen = 0;
        foreach (var _ in q.BySparse()) seen++;
        Assert.Equal(0, seen);
    }

    [Fact]
    public void Destroy_clears_sparse_so_a_reused_id_starts_clean()
    {
        var w = new World();
        var first = w.Create();
        w.AddSparse(first, new GuildBuff { Id = 111 });
        w.Destroy(first);

        var second = w.Create();       // тот же Id, новая версия
        Assert.Equal(first.Id, second.Id);
        Assert.False(w.HasSparse<GuildBuff>(second));
    }
}
