using Xunit;

namespace UvEcs.Tests;

public class FuzzInvariantTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(2026)]
    public void Random_operations_preserve_every_invariant(int seed)
    {
        var rng = new Random(seed);
        var w = new World();
        var alive = new List<Entity>();

        for (int step = 0; step < 3000; step++)
        {
            switch (rng.Next(10))
            {
                case 0:
                    alive.Add(w.Create());
                    break;

                case 1 when alive.Count > 0:
                {
                    int i = rng.Next(alive.Count);
                    w.RemoveSparse<GuildBuff>(alive[i]);
                    w.RemoveSparse<QuestFlag>(alive[i]);
                    w.Destroy(alive[i]);
                    alive.RemoveAt(i);
                    break;
                }

                case 2 when alive.Count > 0:
                    AddIfMissing<Position>(w, alive[rng.Next(alive.Count)]);
                    break;

                case 3 when alive.Count > 0:
                    AddIfMissing<Velocity>(w, alive[rng.Next(alive.Count)]);
                    break;

                case 4 when alive.Count > 0:
                    AddIfMissing<Health>(w, alive[rng.Next(alive.Count)]);
                    break;

                case 5 when alive.Count > 0:
                {
                    var e = alive[rng.Next(alive.Count)];
                    if (w.Has<Velocity>(e)) w.Remove<Velocity>(e);
                    break;
                }

                case 6 when alive.Count > 0:
                {
                    var e = alive[rng.Next(alive.Count)];
                    if (rng.Next(2) == 0) w.SetTag<Stunned>(e); else w.UnsetTag<Stunned>(e);
                    break;
                }

                case 7 when alive.Count > 0:
                {
                    var e = alive[rng.Next(alive.Count)];
                    if (rng.Next(2) == 0) w.SetTag<InCombat>(e); else w.UnsetTag<InCombat>(e);
                    break;
                }

                case 8 when alive.Count > 0:
                {
                    var e = alive[rng.Next(alive.Count)];
                    if (w.HasSparse<GuildBuff>(e)) w.RemoveSparse<GuildBuff>(e);
                    else w.AddSparse(e, new GuildBuff { Id = e.Id });
                    break;
                }

                case 9 when alive.Count > 0:
                {
                    // Второй sparse-носитель: без него QuestFlag-путь мёртв,
                    // а RemoveSparse<QuestFlag> в destroy — вечный no-op.
                    var e = alive[rng.Next(alive.Count)];
                    if (w.HasSparse<QuestFlag>(e)) w.RemoveSparse<QuestFlag>(e);
                    else w.AddSparse(e, new QuestFlag { Id = e.Id });
                    break;
                }
            }

            if (step % 50 == 0)
            {
                WorldInvariants.Check(w);
                WorldInvariants.CheckChunkCounts(w, alive);
                WorldInvariants.CheckSparse<GuildBuff>(w);
                WorldInvariants.CheckSparse<QuestFlag>(w);
            }
        }

        WorldInvariants.Check(w);
        WorldInvariants.CheckChunkCounts(w, alive);
        WorldInvariants.CheckSparse<GuildBuff>(w);
        WorldInvariants.CheckSparse<QuestFlag>(w);
        Assert.Equal(alive.Count, w.EntityCount);

        static void AddIfMissing<T>(World w, Entity e) where T : unmanaged, IComponent
        {
            if (!w.Has<T>(e)) w.Add(e, default(T));
        }
    }

    [Fact]
    public void RecomputeTagUnions_makes_unions_exact_after_churn()
    {
        var rng = new Random(5);
        var w = new World();
        var alive = new List<Entity>();
        for (int i = 0; i < 200; i++) { var e = w.Create(); w.Add(e, new Position()); alive.Add(e); }

        for (int step = 0; step < 2000; step++)
        {
            var e = alive[rng.Next(alive.Count)];
            if (rng.Next(2) == 0) w.SetTag<Stunned>(e); else w.UnsetTag<Stunned>(e);
        }

        w.RecomputeTagUnions();
        WorldInvariants.Check(w);

        for (int a = 0; a < w.ArchetypeCount; a++)
        foreach (var chunk in w.ArchetypeById(a).Chunks)
        {
            var exact = TagMask.Empty;
            for (int row = 0; row < chunk.Count; row++) exact = exact.Or(chunk.TagAt(row));
            Assert.Equal(exact, chunk.TagUnion);   // после пересчёта union точна, а не консервативна
        }
    }

    [Fact]
    public void CheckChunkCounts_fires_when_the_record_side_disagrees_with_chunk_count()
    {
        // Доказывает, что проверка НЕ вырожденная: чанк держит 3 сущности,
        // но список «живых» учитывает только 2 -> ожидание по записям (2)
        // расходится с chunk.Count (3), и CheckChunkCounts обязан упасть.
        var w = new World();
        var a = w.Create();
        var b = w.Create();
        var c = w.Create();

        Assert.ThrowsAny<Exception>(() =>
            WorldInvariants.CheckChunkCounts(w, new List<Entity> { a, b }));   // неполный список

        // Полный список проходит.
        WorldInvariants.CheckChunkCounts(w, new List<Entity> { a, b, c });
    }
}
