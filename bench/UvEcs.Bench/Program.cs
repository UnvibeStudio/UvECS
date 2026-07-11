using UvEcs;
using UvEcs.Bench;

public struct Position : IComponent { public float X, Y, Z; }
public struct Velocity : IComponent { public float X, Y, Z; }
public struct Stunned : ITag { }

public static class Program
{
    private const int N = 10_000;

    public static void Main()
    {
        Console.WriteLine($"UvEcs bench, {N:N0} сущностей, бюджет тика при 20 Гц = 50 000 мкс");

        BenchIteration();
        BenchMigration();
        BenchCreateDestroy();
    }

    private static void BenchIteration()
    {
        var w = new World();
        for (int i = 0; i < N; i++)
        {
            var e = w.Create();
            w.Add(e, new Position());
            w.Add(e, new Velocity { X = 1, Y = 2, Z = 3 });
        }

        var q = w.Query().All<Position, Velocity>().Build();
        const float dt = 0.05f;

        long checksum = 0;
        Harness.Compare($"итерация {N:N0} × 2 компонента", iterations: 20_000,
            ("Query по чанкам", () =>
            {
                foreach (var chunk in q)
                {
                    var pos = chunk.GetWrite<Position>();
                    var vel = chunk.GetRead<Velocity>();
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        pos[i].X += vel[i].X * dt;
                        pos[i].Y += vel[i].Y * dt;
                        pos[i].Z += vel[i].Z * dt;
                    }
                }
                checksum++;
            }));

        Console.WriteLine($"  (базовое число из спеки: 12.8 мкс; checksum {checksum})");
    }

    private static void BenchMigration()
    {
        var w = new World();
        var entities = new Entity[N];
        for (int i = 0; i < N; i++)
        {
            entities[i] = w.Create();
            w.Add(entities[i], new Position());
        }

        Harness.Compare("миграция архетипа: add+remove Velocity на 10k", iterations: 200,
            ("add+remove", () =>
            {
                for (int i = 0; i < N; i++) w.Add(entities[i], new Velocity());
                for (int i = 0; i < N; i++) w.Remove<Velocity>(entities[i]);
            }));
    }

    private static void BenchCreateDestroy()
    {
        var buffer = new Entity[N];

        Harness.Compare("create/destroy 10k сущностей с Position", iterations: 200,
            ("create+destroy", () =>
            {
                var w = new World();
                for (int i = 0; i < N; i++)
                {
                    buffer[i] = w.Create();
                    w.Add(buffer[i], new Position());
                }
                for (int i = 0; i < N; i++) w.Destroy(buffer[i]);
            }));
    }
}
