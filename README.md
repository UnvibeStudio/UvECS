# UvECS

A high-performance archetype ECS for .NET 8, built for MMO servers where most entities are alike.

The library optimises for execution speed over API elegance. It uses **no runtime reflection** — component identity, storage selection, and iteration are all resolved through generic constraints and JIT intrinsics. The core has no dependency on any game engine, so the server runs headless and the whole test suite executes without one.

> **Status: storage core complete.** Entities, three storage backends, archetype migration, tags, sparse sets, and queries are implemented, reviewed, and covered by 147 tests plus an invariant fuzzer. Networking (delta replication), systems (command buffers, tick stages, parallelism), and client-side prediction are designed but not yet built — see [Roadmap](#roadmap).

## Design in one screen

Three storage backends, each chosen at **compile time** by a marker interface:

| Backend | Marker | Lives in | Use for |
|---|---|---|---|
| Archetype column | `IComponent` | 16 KB SoA chunk | data on most entities (`Position`, `Velocity`) |
| Tag bit | `ITag` | one bit of a per-chunk mask | frequently-toggled flags (`Stunned`, `InCombat`) |
| Sparse set | `ISparse` | dense arrays keyed by entity id | data on a minority (< ~25% of a query's candidates) |

Putting a tag into an archetype column, or vice versa, is a **compile error** — the API is split by constraint (`Add<T> where T : IComponent`, `SetTag<T> where T : ITag`). All components are `unmanaged` structs.

- **Entities** are a generational index. A paged record table (`EntityRecord[][]`) records where each entity physically lives; a `ref` into it survives any amount of growth.
- **Chunks** are 16 KB, column-wise (SoA), on the Pinned Object Heap. The `Entity` column is mandatory and first. One pool serves the whole world because all chunks are the same size.
- **Archetypes** are never deleted; they hold a lazy transition graph so `Add<T>`/`Remove<T>` migrate an entity in one dictionary lookup. Empty chunks return to the pool with hysteresis.
- **Tags** don't change archetype identity, so `SetTag`/`UnsetTag` are non-structural (no migration). Each chunk caches a conservative `TagUnion` so a query can skip a whole chunk when no row carries a required tag.
- **Queries** match archetypes by a 256-bit mask, cache the match list incrementally (a cursor, never a full rescan), and iterate chunks. A query that requires a sparse component is driven by the smallest such set instead of scanning every chunk.

## Requirements

- .NET 8 SDK (`net8.0`, `LangVersion` latest)
- No external dependencies in the core library
- Editors: VS Code or Rider (the optional source generator targets `net8.0`; Visual Studio is not supported)

## Quick start

```csharp
using UvEcs;

// Components are plain unmanaged structs tagged by a marker interface.
public struct Position : IComponent { public float X, Y, Z; }
public struct Velocity : IComponent { public float X, Y, Z; }
public struct Stunned  : ITag { }
public struct Buff     : ISparse { public int Id; public float Until; }

var world = new World();

// Create an entity and give it archetype components.
var e = world.Create();
world.Add(e, new Position { X = 0, Y = 0, Z = 0 });
world.Add(e, new Velocity { X = 1, Y = 0, Z = 0 });

// Tags are a bit flip — no migration.
world.SetTag<Stunned>(e);

// Sparse data lives outside the archetype, keyed by entity id.
world.AddSparse(e, new Buff { Id = 3, Until = 12.5f });
```

### Iterating with a query

Build a query **once** (at system init — building one per tick is the one thing that kills this ECS), then iterate its chunks:

```csharp
readonly Query _movers = world.Query()
    .All<Position, Velocity>()   // must have both
    .None<Frozen>()              // must not have this component
    .WithoutTag<Stunned>()       // must not carry this tag
    .Build();

const float dt = 1f / 20f;

foreach (var chunk in _movers)
{
    var pos = chunk.GetWrite<Position>();   // Span<Position>
    var vel = chunk.GetRead<Velocity>();    // ReadOnlySpan<Velocity>

    for (int i = 0; i < chunk.Count; i++)
    {
        if (!chunk.Passes(i)) continue;     // per-row tag filter; trivially true on a dense chunk
        pos[i].X += vel[i].X * dt;
        pos[i].Y += vel[i].Y * dt;
        pos[i].Z += vel[i].Z * dt;
    }
}
```

### Iterating by a sparse component

When a query requires a sparse component, iterate via `BySparse()` — the smallest required set drives, so you touch its carriers and nothing else:

```csharp
var q = world.Query().All<Position>().AllSparse<Buff>().Build();

foreach (var hit in q.BySparse())
{
    // hit gives the entity and its physical location (chunk + row).
    ref var pos  = ref hit.Chunk.GetRef<Position>(hit.Row);   // archetype component: from the chunk
    ref var buff = ref world.GetSparseRef<Buff>(hit.Entity);  // sparse component: from its sparse set
}
```

Chunk iteration (`foreach (var chunk in q)`) is rejected for a query built with `AllSparse` — those carriers are scattered across chunks and can't be handed out as one `Span`.

## Building and testing

```bash
dotnet build                       # build all projects
dotnet test                        # 152 tests in Debug, 151 in Release
dotnet run -c Release --project bench/UvEcs.Bench   # benchmarks (not in CI)
```

The Debug build wires one extra safety net: iterating a query while making a structural change (`Add`/`Remove`/`Create`/`Destroy`) throws, instead of corrupting the iteration silently. It compiles out entirely in Release, which is why Debug has one more test than Release.

## Testing approach

The core is validated three ways:

1. **Unit tests** per component — masks, registries, chunk layout, swap-remove, migration, tag conservatism, sparse round-trips.
2. **An invariant fuzzer** that runs thousands of random operations (create / destroy / add / remove / tag / sparse) across several seeds and, after every batch, asserts five structural invariants: every live entity sits in exactly one chunk; each record's back-reference closes; each chunk's count matches its occupancy; `TagUnion` is never under-wide; every sparse carrier round-trips. This is the primary defence against the bugs unit tests miss — a swap-remove that forgot to fix a displaced record, a migration that dropped a tag.
3. **Hand-written benchmarks** following a strict methodology (shared warm-up, alternated order, ≥25 rounds, median, printed spread, checksum) — because a naive benchmark on a shared machine lies.

Measured baselines (10k entities, shared 2-vCPU container, noisy):

| scenario | median |
|---|---|
| iterate 10k × 2 components | ~13 µs |
| archetype migration (add+remove, 10k) | ~971 µs |
| create/destroy 10k | ~1220 µs |
| match 1000 archetypes | ~1.1 µs |

Server tick budget at 20 Hz is 50,000 µs for reference.

## Project layout

```
src/UvEcs.Abstractions   IComponent / ITag / ISparse markers
src/UvEcs                the storage core (World, archetypes, chunks, queries)
tests/UvEcs.Tests        unit tests + invariant fuzzer
bench/UvEcs.Bench        hand-written benchmarks (console, not in CI)
docs/superpowers/specs   the design spec
docs/superpowers/plans   the implementation plan
```

## Roadmap

The storage core is the first of three independent pieces. Still to build:

- **Networking** — per-entity change versions (`repVersion`), area-of-interest delta replication, and a Roslyn source generator for zero-reflection serialization.
- **Systems** — command buffers for deferred structural changes, tick stages, and opt-in per-chunk parallelism.
- **Client prediction** — rollback for the player's own entities, running the same pure movement logic on client and server.

Two open questions to settle before those: the real tag list (the mask currently holds 64) and thread-safety of the chunk pool (needed once systems add parallelism).

## License

Not yet chosen.
