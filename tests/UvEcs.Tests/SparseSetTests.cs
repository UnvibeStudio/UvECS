using Xunit;

namespace UvEcs.Tests;

public class SparseSetTests
{
    [Fact]
    public void New_set_is_empty()
    {
        var s = new SparseSet<GuildBuff>();
        Assert.Equal(0, s.Count);
        Assert.False(s.Has(0));
        Assert.Empty(s.Entities.ToArray());
    }

    [Fact]
    public void Add_then_Has_and_GetRef()
    {
        var s = new SparseSet<GuildBuff>();
        s.Add(5, new GuildBuff { Id = 1, Until = 2.5f });

        Assert.True(s.Has(5));
        Assert.Equal(1, s.Count);
        Assert.Equal(2.5f, s.GetRef(5).Until);
    }

    [Fact]
    public void GetRef_is_writable()
    {
        var s = new SparseSet<GuildBuff>();
        s.Add(5, new GuildBuff { Id = 1 });
        s.GetRef(5).Id = 77;
        Assert.Equal(77, s.GetRef(5).Id);
    }

    [Fact]
    public void Adding_twice_throws()
    {
        var s = new SparseSet<GuildBuff>();
        s.Add(5, default);
        Assert.Throws<InvalidOperationException>(() => s.Add(5, default));
    }

    [Fact]
    public void GetRef_on_missing_throws()
    {
        var s = new SparseSet<GuildBuff>();
        Assert.Throws<InvalidOperationException>(() => s.GetRef(5));
    }

    [Fact]
    public void Remove_returns_false_when_absent()
    {
        var s = new SparseSet<GuildBuff>();
        Assert.False(s.Remove(5));
    }

    [Fact]
    public void Remove_swaps_the_last_element_into_the_hole()
    {
        var s = new SparseSet<GuildBuff>();
        s.Add(1, new GuildBuff { Id = 10 });
        s.Add(2, new GuildBuff { Id = 20 });
        s.Add(3, new GuildBuff { Id = 30 });

        Assert.True(s.Remove(1));

        Assert.Equal(2, s.Count);
        Assert.False(s.Has(1));
        Assert.Equal(20, s.GetRef(2).Id);
        Assert.Equal(30, s.GetRef(3).Id);
    }

    [Fact]
    public void Remove_of_last_element_works()
    {
        var s = new SparseSet<GuildBuff>();
        s.Add(1, new GuildBuff { Id = 10 });
        Assert.True(s.Remove(1));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Dense_arrays_stay_consistent_under_random_churn()
    {
        var s = new SparseSet<GuildBuff>();
        var rng = new Random(7);
        var present = new HashSet<int>();

        for (int step = 0; step < 5000; step++)
        {
            int id = rng.Next(0, 200);
            if (present.Contains(id))
            {
                s.Remove(id);
                present.Remove(id);
            }
            else
            {
                s.Add(id, new GuildBuff { Id = id });
                present.Add(id);
            }

            // инвариант: dense[sparse[e]] == e для каждого носителя
            Assert.Equal(present.Count, s.Count);
            var entities = s.Entities;
            for (int i = 0; i < entities.Length; i++)
                Assert.True(s.Has(entities[i]));
            foreach (int e in present)
                Assert.Equal(e, s.GetRef(e).Id);
        }
    }

    [Fact]
    public void Set_grows_for_large_entity_ids()
    {
        var s = new SparseSet<QuestFlag>();
        s.Add(100_000, new QuestFlag { Id = 3 });
        Assert.True(s.Has(100_000));
        Assert.Equal(3, s.GetRef(100_000).Id);
    }

    [Fact]
    public void Grown_slots_are_absent_not_zero()
    {
        // Ловит регрессию к нулевой заливке в EnsureSparse: 0 — валидный плотный
        // индекс, поэтому непронумерованная сущность выглядела бы носителем.
        // Верни Array.Fill(..., Absent) на default(int), и упадёт только этот тест.
        var s = new SparseSet<QuestFlag>();
        s.Add(100_000, new QuestFlag { Id = 3 });   // растит sparse далеко за начальную ёмкость

        Assert.False(s.Has(0));       // сущность 0 никогда не добавлялась
        Assert.False(s.Has(50));      // и эта — в выросшем хвосте, но не тронута
        Assert.False(s.Has(99_999));
        Assert.Throws<InvalidOperationException>(() => s.GetRef(50));
    }

    [Fact]
    public void Values_are_parallel_to_entities()
    {
        var s = new SparseSet<GuildBuff>();
        s.Add(4, new GuildBuff { Id = 40 });
        s.Add(9, new GuildBuff { Id = 90 });

        var entities = s.Entities;
        var values = s.Values;
        Assert.Equal(entities.Length, values.Length);
        for (int i = 0; i < entities.Length; i++)
            Assert.Equal(entities[i] * 10, values[i].Id);
    }
}
