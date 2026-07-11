# Fast Spawn Path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Убрать цепочку миграций из create — прямое создание сущности в финальном архетипе (`Create<T…>()`) и батч-спавн одного архетипа (`CreateMany<T…>()`), чтобы закрыть разрыв к Arch/Friflo на горячем пути спавна.

**Architecture:** `World.Create()` кладёт сущность в архетип-∅, и каждый `Add<T>` мигрирует её в следующий архетип — N компонентов = N миграций. Новые перегрузки `Create<T1…T4>(…)` собирают финальную `ComponentMask` один раз, находят архетип и вставляют строку напрямую, без единой миграции. `CreateMany<T…>` дополнительно выносит из цикла словарную выборку архетипа и рост числа чанков (новый `Archetype.Reserve`). Общий приватный примитив `World.PlaceNew` заводит сущность и проставляет её запись; перегрузки лишь пишут свои компоненты.

**Tech Stack:** C# / net8.0, xUnit, существующее ядро UvEcs (World, Archetype, Chunk, ChunkPool, ComponentMask).

## Global Constraints

Копируются дословно из спеки ядра; каждая задача обязана их соблюдать:

- Target framework: `net8.0`. `LangVersion` — `latest`.
- **Рефлексии в рантайме нет.** `Unsafe.SizeOf<T>()` и интринсики JIT разрешены; рефлексия — только в тестах.
- **Все компоненты `unmanaged`.** Ограничение параметров: `where T : unmanaged, IComponent`.
- **Новая строка чанка обнуляется** — это делает `Chunk.AddRow`; полагаться на него, компоненты в `CreateMany` стартуют с `default`.
- Архетипы никогда не удаляются; освобождаются чанки. `ReleaseChunkIfEmpty` удаляет только последний чанк.
- Тесты: xUnit, каждая задача заканчивается зелёным прогоном (`dotnet test`) и коммитом.
- Бенчмарки — не в CI; ручной прогон, методика в `Harness` (общий прогрев, чередование порядка, 25 раундов, медиана, печать разброса).
- Стиль коммита: заканчивать строкой `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

Работа идёт на ветке `feat/fast-spawn-path` (уже создана; на ней лежит дизайн-спека и незакоммиченный `Archetype._chunkWithSpace`).

---

### Task 1: Закоммитить готовый O(1)-кеш чанка

Изменение `Archetype._chunkWithSpace` уже написано и покрыто существующими create/destroy-тестами и фаззером — оно висит незакоммиченным. Первый шаг: убедиться, что дерево зелёное, и зафиксировать его отдельным коммитом, чтобы дальнейшая работа не смешалась с ним.

**Files:**
- Modify: `src/UvEcs/Archetype.cs` (уже изменён в рабочем дереве — коммитим как есть)

**Interfaces:**
- Consumes: ничего.
- Produces: ничего нового (внутренняя оптимизация `GetOrCreateChunkWithSpace`).

- [ ] **Step 1: Прогнать весь набор тестов, убедиться что зелено**

Run: `dotnet test`
Expected: PASS, 0 failed. (В Debug-конфигурации — полный набор, включая §11-гард.)

- [ ] **Step 2: Закоммитить кеш**

```bash
git add src/UvEcs/Archetype.cs
git commit -m "$(cat <<'EOF'
perf(archetype): O(1)-кеш чанка со свободным местом

GetOrCreateChunkWithSpace сканировал список чанков от нуля на каждую вставку —
O(числа чанков) на сущность. Кеш _chunkWithSpace делает общий случай (аппенд в
один чанк, пока не заполнится) O(1); при удалении последнего чанка инвалидируется.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: `Create<T1>` + примитив `PlaceNew`

Заводим новый partial-файл `World.Create.cs` с общим примитивом прямого создания и первой (одно-компонентной) перегрузкой. Арность 1 не может закоротить через промежуточный архетип, поэтому здесь проверяем только размещение и значение; доказательство «нет миграций» — в Task 3, где есть промежуточный архетип, который можно НЕ создать.

**Files:**
- Create: `src/UvEcs/World.Create.cs`
- Test: `tests/UvEcs.Tests/FastSpawnTests.cs`

**Interfaces:**
- Consumes: `World.GetOrCreateArchetype(in ComponentMask)`, `World.Entities` (`EntityStore.Create()`, `GetRecord(Entity)→ref EntityRecord` с полями `ArchetypeId/ChunkIndex/Row`), `World.Pool`, `Archetype.GetOrCreateChunkWithSpace(ChunkPool, out int)`, `Archetype.BumpStructuralVersion()`, `Chunk.AddRow(Entity)→int`, `Chunk.GetRef<T>(int)→ref T`, `ComponentMask.Set(int)`, `ComponentType<T>.Id`.
- Produces:
  - `private Chunk World.PlaceNew(Archetype to, out Entity e, out int row)` — заводит сущность, добавляет строку в `to`, проставляет запись; **не пишет компоненты и не бампает версию**.
  - `private static void World.AssertDistinct(int arity, in ComponentMask mask)` — `[Conditional("DEBUG")]`, бросает `ArgumentException` если `mask.PopCount() != arity`.
  - `public Entity World.Create<T1>(in T1 c1) where T1 : unmanaged, IComponent`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/UvEcs.Tests/FastSpawnTests.cs`:

```csharp
using Xunit;

namespace UvEcs.Tests;

public class FastSpawnTests
{
    [Fact]
    public void Create_with_one_component_places_it_with_its_value()
    {
        var w = new World();
        var e = w.Create(new Position { X = 1, Y = 2, Z = 3 });

        Assert.True(w.Has<Position>(e));
        Assert.Equal(2, w.Get<Position>(e).Y);
        Assert.True(w.IsAlive(e));
    }
}
```

- [ ] **Step 2: Прогнать тест, убедиться что не компилируется/падает**

Run: `dotnet test --filter FastSpawnTests`
Expected: FAIL — нет перегрузки `Create(Position)` (ошибка компиляции).

- [ ] **Step 3: Реализовать `World.Create.cs`**

Создать `src/UvEcs/World.Create.cs`:

```csharp
using System.Diagnostics;

namespace UvEcs;

public sealed partial class World
{
    /// <summary>
    /// Общий примитив прямого создания: заводит сущность и кладёт её строку в <paramref name="to"/>,
    /// сразу проставляя EntityRecord. Компоненты НЕ пишет и версию НЕ бампает — это делает вызывающий
    /// (одиночный Create — на сущность; CreateMany — один раз на пакет).
    /// </summary>
    private Chunk PlaceNew(Archetype to, out Entity e, out int row)
    {
        e = Entities.Create();
        var chunk = to.GetOrCreateChunkWithSpace(Pool, out int chunkIndex);
        row = chunk.AddRow(e);

        ref var rec = ref Entities.GetRecord(e);
        rec.ArchetypeId = to.Id;
        rec.ChunkIndex = chunkIndex;
        rec.Row = row;
        return chunk;
    }

    /// <summary>
    /// В Debug ловит дубликат типа в дженериках (Create&lt;Position,Position&gt;): число битов
    /// в маске обязано равняться арности. В Release вызов вырезается.
    /// </summary>
    [Conditional("DEBUG")]
    private static void AssertDistinct(int arity, in ComponentMask mask)
    {
        if (mask.PopCount() != arity)
            throw new ArgumentException("Повторяющийся тип компонента в Create/CreateMany.");
    }

    public Entity Create<T1>(in T1 c1)
        where T1 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);

        var to = GetOrCreateArchetype(in mask);
        var chunk = PlaceNew(to, out var e, out int row);
        chunk.GetRef<T1>(row) = c1;
        to.BumpStructuralVersion();
        return e;
    }
}
```

- [ ] **Step 4: Прогнать тест, убедиться что зелено**

Run: `dotnet test --filter FastSpawnTests`
Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/UvEcs/World.Create.cs tests/UvEcs.Tests/FastSpawnTests.cs
git commit -m "$(cat <<'EOF'
feat(world): Create<T1>() — прямое создание в финальном архетипе

Вводит примитив PlaceNew и одно-компонентную перегрузку Create. Ноль миграций.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `Create<T1,T2/T3/T4>` + гарантия «нет миграций»

Добавляем перегрузки на 2–4 компонента. Ключевой тест — что промежуточные архетипы НЕ создаются (`Create<Position,Velocity>` добавляет только `{P,V}`, а не `{P}` и `{P,V}`): именно он доказывает отсутствие цепочки миграций. Плюс эквивалентность старому пути и Debug-гард дубликата.

**Files:**
- Modify: `src/UvEcs/World.Create.cs`
- Test: `tests/UvEcs.Tests/FastSpawnTests.cs`

**Interfaces:**
- Consumes: всё из Task 2 (`PlaceNew`, `AssertDistinct`).
- Produces:
  - `public Entity World.Create<T1,T2>(in T1, in T2)`
  - `public Entity World.Create<T1,T2,T3>(in T1, in T2, in T3)`
  - `public Entity World.Create<T1,T2,T3,T4>(in T1, in T2, in T3, in T4)`
  - все с `where Tn : unmanaged, IComponent`.

- [ ] **Step 1: Написать падающие тесты**

Добавить в `tests/UvEcs.Tests/FastSpawnTests.cs`:

```csharp
    [Fact]
    public void Create_with_two_components_creates_only_the_final_archetype()
    {
        var w = new World();            // стартует с архетипом-∅ (Id 0)
        int before = w.ArchetypeCount;  // 1

        w.Create(new Position(), new Velocity());

        // Прямая вставка создаёт ТОЛЬКО {Position,Velocity}. Путь через миграции
        // создал бы ещё и промежуточный {Position} -> before + 2. Это и есть доказательство.
        Assert.Equal(before + 1, w.ArchetypeCount);
    }

    [Fact]
    public void Create_with_two_components_places_both_values()
    {
        var w = new World();
        var e = w.Create(new Position { X = 7 }, new Velocity { X = 4 });

        Assert.Equal(7, w.Get<Position>(e).X);
        Assert.Equal(4, w.Get<Velocity>(e).X);
    }

    [Fact]
    public void Create_with_four_components_places_all_values()
    {
        var w = new World();
        var e = w.Create(
            new Position { X = 1 },
            new Velocity { X = 2 },
            new Health { Current = 3, Max = 30 },
            new Mana { Current = 4 });

        Assert.Equal(1, w.Get<Position>(e).X);
        Assert.Equal(2, w.Get<Velocity>(e).X);
        Assert.Equal(3, w.Get<Health>(e).Current);
        Assert.Equal(4, w.Get<Mana>(e).Current);
    }

    [Fact]
    public void Create_direct_matches_create_then_add()
    {
        var direct = new World();
        var a = direct.Create(new Position { X = 5 }, new Velocity { X = 6 });

        var staged = new World();
        var b = staged.Create();
        staged.Add(b, new Position { X = 5 });
        staged.Add(b, new Velocity { X = 6 });

        // Наблюдаемое состояние совпадает: те же компоненты, те же значения.
        Assert.True(direct.Has<Position>(a) && direct.Has<Velocity>(a));
        Assert.True(staged.Has<Position>(b) && staged.Has<Velocity>(b));
        Assert.Equal(staged.Get<Position>(b).X, direct.Get<Position>(a).X);
        Assert.Equal(staged.Get<Velocity>(b).X, direct.Get<Velocity>(a).X);
    }

#if DEBUG
    [Fact]
    public void Create_with_duplicate_component_type_throws_in_debug()
    {
        var w = new World();
        Assert.Throws<ArgumentException>(() => w.Create(new Position { X = 1 }, new Position { X = 2 }));
    }
#endif
```

- [ ] **Step 2: Прогнать тесты, убедиться что падают**

Run: `dotnet test --filter FastSpawnTests`
Expected: FAIL — нет перегрузок `Create` на 2/4 аргумента (ошибка компиляции).

- [ ] **Step 3: Реализовать перегрузки 2–4**

Добавить в `src/UvEcs/World.Create.cs` внутрь класса, после `Create<T1>`:

```csharp
    public Entity Create<T1, T2>(in T1 c1, in T2 c2)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        AssertDistinct(2, in mask);

        var to = GetOrCreateArchetype(in mask);
        var chunk = PlaceNew(to, out var e, out int row);
        chunk.GetRef<T1>(row) = c1;
        chunk.GetRef<T2>(row) = c2;
        to.BumpStructuralVersion();
        return e;
    }

    public Entity Create<T1, T2, T3>(in T1 c1, in T2 c2, in T3 c3)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        mask.Set(ComponentType<T3>.Id);
        AssertDistinct(3, in mask);

        var to = GetOrCreateArchetype(in mask);
        var chunk = PlaceNew(to, out var e, out int row);
        chunk.GetRef<T1>(row) = c1;
        chunk.GetRef<T2>(row) = c2;
        chunk.GetRef<T3>(row) = c3;
        to.BumpStructuralVersion();
        return e;
    }

    public Entity Create<T1, T2, T3, T4>(in T1 c1, in T2 c2, in T3 c3, in T4 c4)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        mask.Set(ComponentType<T3>.Id);
        mask.Set(ComponentType<T4>.Id);
        AssertDistinct(4, in mask);

        var to = GetOrCreateArchetype(in mask);
        var chunk = PlaceNew(to, out var e, out int row);
        chunk.GetRef<T1>(row) = c1;
        chunk.GetRef<T2>(row) = c2;
        chunk.GetRef<T3>(row) = c3;
        chunk.GetRef<T4>(row) = c4;
        to.BumpStructuralVersion();
        return e;
    }
```

- [ ] **Step 4: Прогнать тесты, убедиться что зелено**

Run: `dotnet test --filter FastSpawnTests`
Expected: PASS (включая `Create_with_duplicate_component_type_throws_in_debug` в Debug).

- [ ] **Step 5: Коммит**

```bash
git add src/UvEcs/World.Create.cs tests/UvEcs.Tests/FastSpawnTests.cs
git commit -m "$(cat <<'EOF'
feat(world): Create<T1..T4>() — прямое создание без промежуточных архетипов

Перегрузки на 2–4 компонента. Тест доказывает, что промежуточные архетипы не
создаются (нет цепочки миграций). Debug-гард ловит дубликат типа.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `Archetype.Reserve` — пред-выделение чанков

Батч-спавн должен резервировать чанки один раз, а не растить их в цикле. Добавляем `Archetype.Reserve`, учитывающий уже свободное место в существующих не-полных чанках, чтобы не аллоцировать лишнего.

**Files:**
- Modify: `src/UvEcs/Archetype.cs`
- Test: `tests/UvEcs.Tests/ArchetypeTests.cs`

**Interfaces:**
- Consumes: `ChunkPool.Rent()→byte[]`, `Chunk(ChunkLayout, byte[])`, `Chunk.Capacity`, `Chunk.Count`, `Layout.Capacity`.
- Produces: `public void Archetype.Reserve(ChunkPool pool, int count)` — гарантирует ёмкость под ещё `count` строк поверх занятого места.

- [ ] **Step 1: Написать падающие тесты**

Добавить в `tests/UvEcs.Tests/ArchetypeTests.cs` (в класс, рядом с другими; хелперы `Make`/`PosVel` уже есть):

```csharp
    [Fact]
    public void Reserve_pre_allocates_capacity_for_count_rows()
    {
        var pool = new ChunkPool();
        var a = PosVel();

        a.Reserve(pool, 1000);

        int capacity = 0;
        foreach (var ch in a.Chunks) capacity += ch.Capacity;
        Assert.True(capacity >= 1000, $"ждали ёмкость >= 1000, получили {capacity}");
    }

    [Fact]
    public void Reserve_counts_existing_free_space_and_does_not_over_allocate()
    {
        var pool = new ChunkPool();
        var a = PosVel();

        a.Reserve(pool, 100);
        int chunksAfterFirst = a.Chunks.Count;

        // Второй резерв в пределах уже свободного места не должен добавлять чанки.
        a.Reserve(pool, 100);
        Assert.Equal(chunksAfterFirst, a.Chunks.Count);
    }

    [Fact]
    public void Reserve_with_non_positive_count_does_nothing()
    {
        var pool = new ChunkPool();
        var a = PosVel();
        a.Reserve(pool, 0);
        a.Reserve(pool, -5);
        Assert.Empty(a.Chunks);
    }
```

(Первый резерв на 100 строк создаёт один чанк ёмкостью в сотни строк для `{Position,Velocity}`; второй резерв на 100 попадает в свободное место того же чанка.)

- [ ] **Step 2: Прогнать тесты, убедиться что падают**

Run: `dotnet test --filter ArchetypeTests`
Expected: FAIL — нет метода `Reserve` (ошибка компиляции).

- [ ] **Step 3: Реализовать `Reserve`**

Добавить в `src/UvEcs/Archetype.cs` метод (например, сразу после `GetOrCreateChunkWithSpace`):

```csharp
    /// <summary>
    /// Пред-выделяет чанки, чтобы вместить ещё <paramref name="count"/> строк поверх уже
    /// занятого места. Батч-создание зовёт это один раз, чтобы цикл заполнения не чередовал
    /// вставку с арендой чанков. Свободное место в существующих не-полных чанках учитывается —
    /// лишнего не аллоцируем. Кеш _chunkWithSpace не трогаем: его подхватит GetOrCreateChunkWithSpace.
    /// </summary>
    public void Reserve(ChunkPool pool, int count)
    {
        if (count <= 0) return;

        int free = 0;
        for (int i = 0; i < _chunks.Count; i++)
            free += _chunks[i].Capacity - _chunks[i].Count;

        int need = count - free;
        if (need <= 0) return;

        int perChunk = Layout.Capacity;
        int newChunks = (need + perChunk - 1) / perChunk;
        for (int i = 0; i < newChunks; i++)
            _chunks.Add(new Chunk(Layout, pool.Rent()));
    }
```

- [ ] **Step 4: Прогнать тесты, убедиться что зелено**

Run: `dotnet test --filter ArchetypeTests`
Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/UvEcs/Archetype.cs tests/UvEcs.Tests/ArchetypeTests.cs
git commit -m "$(cat <<'EOF'
feat(archetype): Reserve(pool, count) — пред-выделение чанков под батч

Учитывает свободное место в существующих чанках, чтобы не аллоцировать лишнего.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: `CreateMany<T…>` — батч-спавн одного архетипа

Публичный батч-спавн: один архетип, один резерв, плотный цикл. Компоненты стартуют с `default` (строки занулены `AddRow`), значения ставит вызывающий. Общий приватный `CreateManyInto` держит валидацию/резерв/цикл; перегрузки лишь собирают маску.

**Files:**
- Modify: `src/UvEcs/World.Create.cs`
- Test: `tests/UvEcs.Tests/FastSpawnTests.cs`

**Interfaces:**
- Consumes: `PlaceNew` (Task 2), `AssertDistinct` (Task 2), `GetOrCreateArchetype`, `Archetype.Reserve` (Task 4), `Archetype.BumpStructuralVersion`.
- Produces:
  - `private void World.CreateManyInto(int count, Span<Entity> dest, in ComponentMask mask, int arity)`
  - `public void World.CreateMany<T1>(int count, Span<Entity> dest)`
  - `public void World.CreateMany<T1,T2>(int count, Span<Entity> dest)`
  - `public void World.CreateMany<T1,T2,T3>(int count, Span<Entity> dest)`
  - `public void World.CreateMany<T1,T2,T3,T4>(int count, Span<Entity> dest)`
  - все `where Tn : unmanaged, IComponent`.

- [ ] **Step 1: Написать падающие тесты**

Добавить в `tests/UvEcs.Tests/FastSpawnTests.cs`:

```csharp
    [Fact]
    public void CreateMany_spawns_all_entities_in_one_archetype()
    {
        var w = new World();
        int before = w.ArchetypeCount;

        var dest = new Entity[500];
        w.CreateMany<Position, Velocity>(500, dest);

        Assert.Equal(500, w.EntityCount);
        Assert.Equal(before + 1, w.ArchetypeCount);   // только {Position,Velocity}
        for (int i = 0; i < 500; i++)
        {
            Assert.True(w.IsAlive(dest[i]));
            Assert.True(w.Has<Position>(dest[i]));
            Assert.True(w.Has<Velocity>(dest[i]));
        }
    }

    [Fact]
    public void CreateMany_ids_are_distinct()
    {
        var w = new World();
        var dest = new Entity[300];
        w.CreateMany<Position>(300, dest);

        var seen = new HashSet<int>();
        for (int i = 0; i < 300; i++)
            Assert.True(seen.Add(dest[i].Id), $"повтор Id на позиции {i}");
    }

    [Fact]
    public void CreateMany_spans_multiple_chunks()
    {
        // 5000 строк заведомо больше вместимости одного 16 КБ чанка -> несколько чанков.
        var w = new World();
        var dest = new Entity[5000];
        w.CreateMany<Position>(5000, dest);

        Assert.Equal(5000, w.EntityCount);
        for (int i = 0; i < 5000; i++)
            Assert.True(w.IsAlive(dest[i]));
    }

    [Fact]
    public void CreateMany_defaults_component_values_to_zero()
    {
        var w = new World();
        var dest = new Entity[10];
        w.CreateMany<Position>(10, dest);

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(0, w.Get<Position>(dest[i]).X);
            Assert.Equal(0, w.Get<Position>(dest[i]).Y);
        }
    }

    [Fact]
    public void CreateMany_with_zero_count_is_a_noop()
    {
        var w = new World();
        var dest = new Entity[4];
        w.CreateMany<Position>(0, dest);
        Assert.Equal(0, w.EntityCount);
    }

    [Fact]
    public void CreateMany_throws_when_dest_is_too_small()
    {
        var w = new World();
        var dest = new Entity[3];
        Assert.Throws<ArgumentException>(() => w.CreateMany<Position>(4, dest));
        Assert.Equal(0, w.EntityCount);   // бросили до любых мутаций
    }

    [Fact]
    public void CreateMany_throws_on_negative_count()
    {
        var w = new World();
        var dest = new Entity[4];
        Assert.Throws<ArgumentOutOfRangeException>(() => w.CreateMany<Position>(-1, dest));
    }
```

- [ ] **Step 2: Прогнать тесты, убедиться что падают**

Run: `dotnet test --filter FastSpawnTests`
Expected: FAIL — нет `CreateMany` (ошибка компиляции).

- [ ] **Step 3: Реализовать `CreateManyInto` + перегрузки**

Добавить в `src/UvEcs/World.Create.cs` внутрь класса:

```csharp
    private void CreateManyInto(int count, Span<Entity> dest, in ComponentMask mask, int arity)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (dest.Length < count) throw new ArgumentException("dest короче count.", nameof(dest));
        AssertDistinct(arity, in mask);
        if (count == 0) return;

        var to = GetOrCreateArchetype(in mask);
        to.Reserve(Pool, count);
        for (int i = 0; i < count; i++)
        {
            PlaceNew(to, out var e, out _);
            dest[i] = e;
        }
        to.BumpStructuralVersion();
    }

    public void CreateMany<T1>(int count, Span<Entity> dest)
        where T1 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        CreateManyInto(count, dest, in mask, 1);
    }

    public void CreateMany<T1, T2>(int count, Span<Entity> dest)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        CreateManyInto(count, dest, in mask, 2);
    }

    public void CreateMany<T1, T2, T3>(int count, Span<Entity> dest)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        mask.Set(ComponentType<T3>.Id);
        CreateManyInto(count, dest, in mask, 3);
    }

    public void CreateMany<T1, T2, T3, T4>(int count, Span<Entity> dest)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        var mask = new ComponentMask();
        mask.Set(ComponentType<T1>.Id);
        mask.Set(ComponentType<T2>.Id);
        mask.Set(ComponentType<T3>.Id);
        mask.Set(ComponentType<T4>.Id);
        CreateManyInto(count, dest, in mask, 4);
    }
```

- [ ] **Step 4: Прогнать тесты, убедиться что зелено**

Run: `dotnet test --filter FastSpawnTests`
Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/UvEcs/World.Create.cs tests/UvEcs.Tests/FastSpawnTests.cs
git commit -m "$(cat <<'EOF'
feat(world): CreateMany<T…>() — батч-спавн одного архетипа

Один архетип, один Reserve, плотный цикл. Компоненты стартуют с default;
валидация (count, длина dest) до любых мутаций.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Расширить фаззер новыми операциями

Инвариантный фаззер уже гоняет create/destroy/add/remove/tag/sparse и проверяет пять структурных инвариантов. Добавляем `Create<T…>` и `CreateMany` как операции, чтобы новый код проверялся против тех же инвариантов на тысячах случайных шагов.

**Files:**
- Modify: `tests/UvEcs.Tests/FuzzInvariantTests.cs`

**Interfaces:**
- Consumes: `World.Create<T1,T2>` (Task 3), `World.CreateMany<T1>` (Task 5).
- Produces: ничего (только тестовое покрытие).

- [ ] **Step 1: Расширить диапазон операций и добавить два case**

В `tests/UvEcs.Tests/FuzzInvariantTests.cs`, в методе `Random_operations_preserve_every_invariant`, заменить `switch (rng.Next(10))` на `switch (rng.Next(12))` и добавить перед закрывающей `}` свича два новых case:

```csharp
                case 10:
                    // Прямое создание в финальном архетипе {Position,Velocity}.
                    alive.Add(w.Create(new Position { X = step }, new Velocity()));
                    break;

                case 11:
                {
                    // Батч-спавн: k сущностей одного архетипа за раз.
                    int k = 1 + rng.Next(4);
                    Span<Entity> buf = stackalloc Entity[k];
                    w.CreateMany<Health>(k, buf);
                    for (int j = 0; j < k; j++) alive.Add(buf[j]);
                    break;
                }
```

Найти строку:

```csharp
            switch (rng.Next(10))
```

и заменить на:

```csharp
            switch (rng.Next(12))
```

- [ ] **Step 2: Прогнать фаззер, убедиться что зелено**

Run: `dotnet test --filter FuzzInvariantTests`
Expected: PASS для всех сидов (1, 42, 1337, 2026) — новые операции не ломают ни один из пяти инвариантов.

- [ ] **Step 3: Коммит**

```bash
git add tests/UvEcs.Tests/FuzzInvariantTests.cs
git commit -m "$(cat <<'EOF'
test(fuzz): Create<T…> и CreateMany как фаззинг-операции

Новый путь спавна проверяется против пяти структурных инвариантов.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Перевести бенчмарк на новый путь + отдельный замер destroy

Переводим сценарий create/destroy на `Create<Position>()`, добавляем вариант `CreateMany` и вариант «только create». Базовый вариант — «только create», поэтому отношения показывают, сколько добавляет destroy (тот самый отдельный замер: destroy = разница между «create+destroy» и «create only»).

**Files:**
- Modify: `bench/UvEcs.Bench/Program.cs`

**Interfaces:**
- Consumes: `World.Create<T1>` (Task 2), `World.CreateMany<T1>` (Task 5), `Harness.Compare(string, int, params (string, Action)[])`.
- Produces: ничего (ручной бенч, не в CI).

- [ ] **Step 1: Заменить `BenchCreateDestroy`**

В `bench/UvEcs.Bench/Program.cs` заменить метод `BenchCreateDestroy` целиком на:

```csharp
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
```

- [ ] **Step 2: Собрать бенч, убедиться что компилируется**

Run: `dotnet build -c Release bench/UvEcs.Bench`
Expected: Build succeeded, 0 Error(s).

- [ ] **Step 3: Коммит**

```bash
git add bench/UvEcs.Bench/Program.cs
git commit -m "$(cat <<'EOF'
bench: create/destroy на Create<Position>() + вариант CreateMany + замер destroy

Базовый вариант «только create» изолирует вклад destroy (разница). Добавлен
вариант батч-спавна CreateMany.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Финальная проверка (после всех задач)

- [ ] **Полный прогон тестов**

Run: `dotnet test`
Expected: PASS, 0 failed. (Существующие 151 + новые из FastSpawnTests/ArchetypeTests; фаззер зелёный на всех сидах.)

- [ ] **Ручная валидация бенчмарком (не блокирует, долгий прогон — минуты)**

Run: `dotnet run -c Release --project bench/UvEcs.Bench`
Что смотреть:
- Санити-строки печатаются (create дал N, destroy обнулил, CreateMany дал N).
- `create<Position> + destroy` относительно `create<Position> only` — во сколько раз destroy удлиняет операцию (стал ли destroy высоким столбом).
- `createMany + destroy` должен быть быстрее `create<Position> + destroy` (батч выносит выборку архетипа и рост чанков из цикла).
- Цель плана: одиночный create в пределах ~1.5× от Arch на той же машине (сравнение с внешним бенчем автора — вне этого репозитория).

---

## Self-Review (заполнено автором плана)

**Покрытие спеки:**
- Прямое создание `Create<T1…T4>` — Task 2 (арность 1) + Task 3 (2–4, доказательство «нет миграций»). ✓
- Батч-спавн `CreateMany<T…>` + `Archetype.Reserve` — Task 4 (Reserve) + Task 5 (CreateMany). ✓
- Гард дубликата типа (Debug) — Task 3. ✓
- `dest` короче `count` / `count<0` / `count==0` — Task 5. ✓
- Занулённые значения по умолчанию — Task 5. ✓
- Эквивалентность старому пути — Task 3. ✓
- Фаззер расширен — Task 6. ✓
- Бенч: перевод на `Create<T…>`, вариант `CreateMany`, отдельный замер destroy — Task 7. ✓
- Первый шаг: коммит O(1)-кеша — Task 1. ✓
- Вне плана (CommandBuffer/§7, публичный Reserve, bulk destroy, починка RemoveFromAllSparseSets) — не входит, соответствует спеке. ✓

**Плейсхолдеры:** нет — весь код и все команды приведены целиком.

**Согласованность типов:** `PlaceNew(Archetype, out Entity, out int)→Chunk`, `AssertDistinct(int, in ComponentMask)`, `CreateManyInto(int, Span<Entity>, in ComponentMask, int)`, `Archetype.Reserve(ChunkPool, int)`, `Create<T…>(in T…)→Entity`, `CreateMany<T…>(int, Span<Entity>)→void` — имена и сигнатуры совпадают между задачами, которые их определяют и потребляют. ✓
