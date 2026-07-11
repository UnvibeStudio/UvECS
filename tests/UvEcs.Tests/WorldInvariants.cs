using Xunit;

namespace UvEcs.Tests;

public static class WorldInvariants
{
    public static void Check(World w)
    {
        int totalRows = 0;

        for (int a = 0; a < w.ArchetypeCount; a++)
        {
            var archetype = w.ArchetypeById(a);

            for (int c = 0; c < archetype.Chunks.Count; c++)
            {
                var chunk = archetype.Chunks[c];
                totalRows += chunk.Count;

                Assert.True(chunk.Count <= chunk.Capacity, "Count превысил Capacity");

                var union = TagMask.Empty;
                for (int row = 0; row < chunk.Count; row++)
                {
                    var e = chunk.EntityAt(row);

                    // 1. сущность в чанке жива
                    Assert.True(w.IsAlive(e), $"{e} лежит в чанке, но мертва");

                    // 2. обратная ссылка сходится: запись указывает сюда же
                    ref var rec = ref w.Entities.GetRecord(e);
                    Assert.Equal(a, rec.ArchetypeId);
                    Assert.Equal(c, rec.ChunkIndex);
                    Assert.Equal(row, rec.Row);

                    union = union.Or(chunk.TagAt(row));
                }

                // 3. TagUnion — надмножество OR всех масок (консервативна, но не уже правды)
                Assert.True(chunk.TagUnion.HasAll(union),
                    $"TagUnion уже реального OR в архетипе {a}, чанке {c}");
            }
        }

        // 4. каждая живая сущность лежит ровно в одном чанке
        Assert.Equal(w.EntityCount, totalRows);
    }

    /// <summary>
    /// Инвариант 3 по-чанково, а не только в сумме. Агрегатная проверка
    /// (EntityCount == totalRows) пропускает компенсирующие ошибки:
    /// +1 в одном чанке и -1 в другом дают ту же сумму. Источник истины здесь —
    /// таблица записей (через список живых), а не сам chunk.Count: иначе проверка
    /// выродилась бы в Assert.Equal(Count, Count), потому что forward-scan
    /// ограничен Count. Заодно ловит утёкшую сущность (в чанке, но не в alive).
    /// </summary>
    public static void CheckChunkCounts(World w, IReadOnlyList<Entity> alive)
    {
        var expected = new Dictionary<(int arch, int chunk), int>();
        foreach (var e in alive)
        {
            ref var rec = ref w.Entities.GetRecord(e);
            var key = (rec.ArchetypeId, rec.ChunkIndex);
            expected[key] = expected.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        for (int a = 0; a < w.ArchetypeCount; a++)
        {
            var archetype = w.ArchetypeById(a);
            for (int c = 0; c < archetype.Chunks.Count; c++)
            {
                int exp = expected.TryGetValue((a, c), out var n) ? n : 0;
                Assert.Equal(exp, archetype.Chunks[c].Count);
            }
        }
    }

    public static void CheckSparse<T>(World w) where T : unmanaged, ISparse
    {
        var set = w.SparseSetOf<T>();
        var entities = set.Entities;

        for (int i = 0; i < entities.Length; i++)
        {
            // 5. настоящий round-trip: dense[sparse[e]] == e, а не только "sparse[e] != Absent".
            // Порча, указывающая sparse[e] на чужой dense-слот, прошла бы Has(), но не это.
            Assert.True(set.DebugRoundTrips(entities[i]), $"sparse[{entities[i]}] указывает не на свой dense-слот");
        }
    }
}
