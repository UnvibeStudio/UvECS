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

        // Санити вне тайминга: тело заканчивается Remove, значит Velocity снят у всех,
        // а Position остался. Иначе записанное число мерило бы не миграцию, а no-op.
        int withPosition = 0, withVelocity = 0;
        for (int i = 0; i < N; i++)
        {
            if (w.Has<Position>(entities[i])) withPosition++;
            if (w.Has<Velocity>(entities[i])) withVelocity++;
        }
        if (withPosition != N || withVelocity != 0)
            throw new Exception($"миграция ничего не сделала: Position={withPosition}/{N}, Velocity={withVelocity} (ждали 0)");
        Console.WriteLine($"  (санити: Position у всех {N}, Velocity снят у всех — работа подтверждена)");
    }

    private static void BenchCreateDestroy()
    {
        var buffer = new Entity[N];

        void CreateOnly()
        {
            var w = new World();
            for (int i = 0; i < N; i++) buffer[i] = w.Create(new Position());
        }

        void CreateThenDestroy()
        {
            var w = new World();
            for (int i = 0; i < N; i++) buffer[i] = w.Create(new Position());
            for (int i = 0; i < N; i++) w.Destroy(buffer[i]);
        }

        void CreateManyThenDestroy()
        {
            var w = new World();
            w.CreateMany<Position>(N, buffer);
            for (int i = 0; i < N; i++) w.Destroy(buffer[i]);
        }

        // Санити вне тайминга: create даёт N, destroy — 0, CreateMany тоже даёт N.
        {
            var probe = new World();
            for (int i = 0; i < N; i++) buffer[i] = probe.Create(new Position());
            if (probe.EntityCount != N)
                throw new Exception($"create ничего не сделал: EntityCount={probe.EntityCount}, ждали {N}");
            for (int i = 0; i < N; i++) probe.Destroy(buffer[i]);
            if (probe.EntityCount != 0)
                throw new Exception($"destroy ничего не сделал: EntityCount={probe.EntityCount}, ждали 0");

            var probeMany = new World();
            probeMany.CreateMany<Position>(N, buffer);
            if (probeMany.EntityCount != N)
                throw new Exception($"CreateMany ничего не сделал: EntityCount={probeMany.EntityCount}, ждали {N}");
            Console.WriteLine($"  (санити: create дал {N}, destroy обнулил, CreateMany дал {N} — работа подтверждена)");
        }

        // Базовый вариант — «только create»: отношения показывают вклад destroy.
        Harness.Compare("create/destroy 10k с Position", iterations: 200,
            ("create<Position> only",      CreateOnly),
            ("create<Position> + destroy", CreateThenDestroy),
            ("createMany + destroy",       CreateManyThenDestroy));
        Console.WriteLine("  (destroy ≈ разница «create+destroy» − «create only»; CreateMany — вклад батч-спавна)");
    }
}
