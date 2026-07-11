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

    public static void CheckSparse<T>(World w) where T : unmanaged, ISparse
    {
        var set = w.SparseSetOf<T>();
        var entities = set.Entities;

        for (int i = 0; i < entities.Length; i++)
        {
            // 5. dense[sparse[e]] == e
            Assert.True(set.Has(entities[i]), $"висячий индекс для сущности {entities[i]}");
        }
        Assert.Equal(entities.Length, set.Count);
    }
}
