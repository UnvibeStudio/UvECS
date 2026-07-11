# UvEcs Core Storage — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Рабочее ядро ECS: сущности, архетипные чанки, теги-битмаски, sparse set и запросы с чанковой итерацией.

**Architecture:** Три хранилища. Основное — архетипы: сущности с одинаковым набором компонентов лежат в чанках по 16 КБ, колонками (SoA). Теги — бит в маске, колонка `TagMask` в том же чанке, вне идентичности архетипа. Редкие компоненты с данными — sparse set. Запрос матчит архетипы по битовой маске один раз и кеширует список инкрементально.

**Tech Stack:** .NET 8, C# 12, xUnit. Никаких внешних зависимостей в ядре.

**Источник:** `docs/superpowers/specs/2026-07-10-uv-ecs-design.md`. Все числа и обоснования там.

## Что НЕ входит в этот план

- `repVersion` и сериализация (§9 спеки) — зависят от `[NetComponent]` и кодогена, идут отдельным планом. Здесь `GetWrite<T>()` просто возвращает `Span<T>` без штампа версии.
- Command buffer, системы, стадии тика, параллелизм (§7, §8) — отдельный план.
- Предсказание и rollback (§10) — отдельный план.

Ядро хранилища тестируется и работает без единой строчки про сеть.

## Global Constraints

- Target framework: `net8.0`. `LangVersion` — `latest`.
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` в `UvEcs.csproj` (нужен для `fixed` в чанке).
- **Рефлексии в рантайме нет.** `Unsafe.SizeOf<T>()` и `RuntimeHelpers.IsReferenceOrContainsReferences<T>()` — интринсики JIT, не рефлексия, разрешены. В тестах рефлексия допустима.
- **Все компоненты `unmanaged`.** Managed-компонентов не существует (§4 спеки: `IManaged` отвергнут).
- **Маска тегов никогда не выражается как `ulong` вне `TagMask`.** `TagType<T>.Bit` возвращает `TagMask`; ни чанк, ни query, ни системы не видят `ulong`. (Внутри `ComponentMask` `ulong` использовать можно — это другая маска, и переносить её на 512 бит меняет `Words`, а не тип.)
- **Новая строка чанка обнуляется.** Буферы переиспользуются из пула, `AllocateUninitializedArray` их не чистит, поэтому `AddRow` обязан затирать свою строку во всех колонках. Иначе сущность наследует данные покойника, а тесты зеленеют случайно — свежие страницы ОС отдаёт нулевыми.
- Ядро не ссылается ни на GodotSharp, ни на сетевые библиотеки.
- Размер чанка: ровно `16384` байта данных. Буфер аллоцируется как `16384 + 64` для сдвига до границы 64.
- Максимум компонентов: `256` (`ComponentMask.Capacity`). Максимум тегов: `64`.
- Тесты: xUnit, каждая задача заканчивается зелёным прогоном и коммитом.
- Бенчмарки — не в CI. Методика в §13 спеки: общий прогрев, чередование порядка, ≥25 раундов, медиана, печать собственного разброса, проверка что варианты делают одну работу.

---

## File Structure

```
UvEcs.sln
src/UvEcs.Abstractions/UvEcs.Abstractions.csproj
    Markers.cs              IComponent, ITag, ISparse — пустые маркеры
src/UvEcs/UvEcs.csproj
    TagMask.cs              обёртка над ulong; единственное место, где ulong виден
    ComponentMask.cs        [InlineArray(4)], 256 бит
    ComponentRegistry.cs    ComponentRegistry, ComponentType<T>, TagRegistry, TagType<T>
    Entity.cs               Entity (generational index)
    EntityStore.cs          EntityRecord[], free-list, версии
    ChunkPool.cs            пул pinned byte[16384+64]
    ChunkLayout.cs          расчёт Cap и смещений колонок (чистая арифметика)
    Chunk.cs                данные чанка, колонки, AddRow/SwapRemove, TagUnion
    Archetype.cs            маска, layout, список чанков, граф переходов
    SparseSet.cs            SparseSet<T>
    World.cs                Create/Destroy/Has/Get/Set + реестр архетипов
    World.Structural.cs     Add/Remove + миграция между архетипами
    World.Tags.cs           SetTag/UnsetTag/HasTag + пересчёт TagUnion
    Query.cs                Query, кеш архетипов, матчинг
    QueryBuilder.cs         билдер
    ChunkView.cs            то, что видит пользователь в foreach: Count, Passes(row), GetRead/GetWrite
tests/UvEcs.Tests/UvEcs.Tests.csproj
    TagMaskTests.cs
    ComponentMaskTests.cs
    RegistryTests.cs
    EntityStoreTests.cs
    ChunkLayoutTests.cs
    ChunkTests.cs
    ArchetypeTests.cs
    WorldTests.cs
    StructuralTests.cs
    TagTests.cs
    SparseSetTests.cs
    QueryTests.cs
    SparseDriverTests.cs
    FuzzInvariantTests.cs
    TestComponents.cs       общие тестовые компоненты и теги
bench/UvEcs.Bench/UvEcs.Bench.csproj
    Harness.cs              методика §13
    Benchmarks.cs
```

---

## Task 1: Solution, проекты, маркерные интерфейсы

**Files:**
- Create: `UvEcs.sln`
- Create: `src/UvEcs.Abstractions/UvEcs.Abstractions.csproj`
- Create: `src/UvEcs.Abstractions/Markers.cs`
- Create: `src/UvEcs/UvEcs.csproj`
- Create: `tests/UvEcs.Tests/UvEcs.Tests.csproj`
- Test: `tests/UvEcs.Tests/TestComponents.cs`

**Interfaces:**
- Consumes: ничего.
- Produces: `UvEcs.IComponent`, `UvEcs.ITag`, `UvEcs.ISparse` — пустые интерфейсы-маркеры. Тестовые типы `Position`, `Velocity`, `Health` (`IComponent`), `Stunned`, `InCombat`, `Dead` (`ITag`), `GuildBuff` (`ISparse`).

- [ ] **Step 1: Создать solution и проекты**

```bash
cd /home/dev/projects/uv-ecs
dotnet new sln -n UvEcs
dotnet new classlib -o src/UvEcs.Abstractions -f net8.0 --force
dotnet new classlib -o src/UvEcs -f net8.0 --force
dotnet new xunit  -o tests/UvEcs.Tests -f net8.0 --force
rm -f src/UvEcs.Abstractions/Class1.cs src/UvEcs/Class1.cs tests/UvEcs.Tests/UnitTest1.cs
dotnet sln add src/UvEcs.Abstractions src/UvEcs tests/UvEcs.Tests
dotnet add src/UvEcs reference src/UvEcs.Abstractions
dotnet add tests/UvEcs.Tests reference src/UvEcs
```

- [ ] **Step 2: Настроить csproj**

`src/UvEcs/UvEcs.csproj` — заменить `<PropertyGroup>` целиком и добавить `<ItemGroup>`:

```xml
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <!-- Тесты обращаются к World.Entities, World.ArchetypeById, Chunk.TagAt — они internal. -->
    <InternalsVisibleTo Include="UvEcs.Tests" />
  </ItemGroup>
```

`src/UvEcs.Abstractions/UvEcs.Abstractions.csproj` — то же, но без `AllowUnsafeBlocks` и без `InternalsVisibleTo`.

`tests/UvEcs.Tests/UvEcs.Tests.csproj` — добавить в `<PropertyGroup>` (тест выравнивания использует `fixed`):

```xml
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
```

- [ ] **Step 3: Написать маркеры**

`src/UvEcs.Abstractions/Markers.cs`:

```csharp
namespace UvEcs;

/// <summary>Компонент с данными. Живёт колонкой в чанке архетипа.</summary>
public interface IComponent { }

/// <summary>Флаг без данных. Живёт битом в маске чанка, вне идентичности архетипа.</summary>
public interface ITag { }

/// <summary>Компонент с данными у меньшинства сущностей (порог ~25%). Живёт в sparse set.</summary>
public interface ISparse { }
```

- [ ] **Step 4: Написать тестовые типы**

`tests/UvEcs.Tests/TestComponents.cs`:

```csharp
namespace UvEcs.Tests;

public struct Position : IComponent { public float X, Y, Z; }
public struct Velocity : IComponent { public float X, Y, Z; }
public struct Health   : IComponent { public int Current, Max; }
public struct Mana     : IComponent { public int Current; }

public struct Stunned  : ITag { }
public struct InCombat : ITag { }
public struct Dead     : ITag { }

public struct GuildBuff : ISparse { public int Id; public float Until; }
public struct QuestFlag : ISparse { public int Id; }
```

- [ ] **Step 5: Написать тест, что констрейнты компилируются и маркер ничего не стоит**

`tests/UvEcs.Tests/MarkerTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using Xunit;

namespace UvEcs.Tests;

public class MarkerTests
{
    private static int SizeOfComponent<T>() where T : unmanaged, IComponent => Unsafe.SizeOf<T>();

    [Fact]
    public void Constraint_accepts_component_and_reports_size()
    {
        Assert.Equal(12, SizeOfComponent<Position>());
        Assert.Equal(8, SizeOfComponent<Health>());
    }

    [Fact]
    public void Components_are_unmanaged()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<Position>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<GuildBuff>());
    }
}
```

- [ ] **Step 6: Прогнать тесты**

Run: `dotnet test`
Expected: PASS, 2 теста.

- [ ] **Step 7: Коммит**

```bash
git add -A
git commit -m "feat: solution, проекты, маркерные интерфейсы хранилищ"
```

---

## Task 2: TagMask

**Files:**
- Create: `src/UvEcs/TagMask.cs`
- Test: `tests/UvEcs.Tests/TagMaskTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces: `readonly struct TagMask` с `TagMask.Empty`, `TagMask.FromIndex(int)`, `bool IsEmpty`, `bool HasAll(TagMask)`, `bool HasAny(TagMask)`, `bool HasNone(TagMask)`, `TagMask Or(TagMask)`, `TagMask AndNot(TagMask)`, `const int Capacity = 64`. Реализует `IEquatable<TagMask>`.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/TagMaskTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using Xunit;

namespace UvEcs.Tests;

public class TagMaskTests
{
    [Fact]
    public void Size_matches_ulong()
    {
        Assert.Equal(sizeof(ulong), Unsafe.SizeOf<TagMask>());
        Assert.Equal(64, TagMask.Capacity);
    }

    [Fact]
    public void Empty_has_nothing_and_is_subset_of_everything()
    {
        var empty = TagMask.Empty;
        Assert.True(empty.IsEmpty);
        Assert.False(empty.HasAny(TagMask.FromIndex(0)));
        Assert.True(TagMask.FromIndex(3).HasAll(empty));
    }

    [Fact]
    public void Or_sets_bits_and_HasAll_requires_all_of_them()
    {
        var m = TagMask.FromIndex(3).Or(TagMask.FromIndex(5));
        Assert.True(m.HasAll(TagMask.FromIndex(3)));
        Assert.True(m.HasAll(TagMask.FromIndex(3).Or(TagMask.FromIndex(5))));
        Assert.False(m.HasAll(TagMask.FromIndex(3).Or(TagMask.FromIndex(4))));
    }

    [Fact]
    public void HasAny_and_HasNone_are_opposites()
    {
        var m = TagMask.FromIndex(1);
        Assert.True(m.HasAny(TagMask.FromIndex(1)));
        Assert.False(m.HasNone(TagMask.FromIndex(1)));
        Assert.True(m.HasNone(TagMask.FromIndex(2)));
    }

    [Fact]
    public void AndNot_clears_bits()
    {
        var m = TagMask.FromIndex(3).Or(TagMask.FromIndex(5)).AndNot(TagMask.FromIndex(3));
        Assert.False(m.HasAny(TagMask.FromIndex(3)));
        Assert.True(m.HasAll(TagMask.FromIndex(5)));
    }

    [Fact]
    public void Boundary_bits_work()
    {
        Assert.True(TagMask.FromIndex(63).HasAll(TagMask.FromIndex(63)));
        Assert.False(TagMask.FromIndex(63).HasAny(TagMask.FromIndex(0)));
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(TagMask.FromIndex(7), TagMask.FromIndex(7));
        Assert.NotEqual(TagMask.FromIndex(7), TagMask.FromIndex(8));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    public void FromIndex_rejects_out_of_range(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TagMask.FromIndex(index));
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter TagMaskTests`
Expected: FAIL — `The type or namespace name 'TagMask' could not be found`.

- [ ] **Step 3: Реализовать**

`src/UvEcs/TagMask.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace UvEcs;

/// <summary>
/// Маска тегов. Единственное место в проекте, где встречается ulong.
/// Расширение до 128 тегов — правка только этого файла: поле становится [InlineArray(2)].
/// </summary>
public readonly struct TagMask : IEquatable<TagMask>
{
    public const int Capacity = 64;

    private readonly ulong _bits;

    private TagMask(ulong bits) => _bits = bits;

    public static TagMask Empty => default;

    public static TagMask FromIndex(int index)
    {
        if ((uint)index >= Capacity) throw new ArgumentOutOfRangeException(nameof(index));
        return new TagMask(1UL << index);
    }

    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bits == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAll(TagMask required) => (_bits & required._bits) == required._bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAny(TagMask any) => (_bits & any._bits) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasNone(TagMask none) => (_bits & none._bits) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TagMask Or(TagMask other) => new(_bits | other._bits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TagMask AndNot(TagMask other) => new(_bits & ~other._bits);

    public bool Equals(TagMask other) => _bits == other._bits;
    public override bool Equals(object? obj) => obj is TagMask other && Equals(other);
    public override int GetHashCode() => _bits.GetHashCode();
    public override string ToString() => $"TagMask(0x{_bits:X16})";

    public static bool operator ==(TagMask a, TagMask b) => a.Equals(b);
    public static bool operator !=(TagMask a, TagMask b) => !a.Equals(b);
}
```

- [ ] **Step 4: Прогнать**

Run: `dotnet test --filter TagMaskTests`
Expected: PASS, 8 тестов (`Theory` даёт 2).

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: TagMask — обёртка над ulong, делающая переход на 128 тегов правкой одного файла"
```

---

## Task 3: ComponentMask

**Files:**
- Create: `src/UvEcs/ComponentMask.cs`
- Test: `tests/UvEcs.Tests/ComponentMaskTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces: `struct ComponentMask` с `const int Words = 4`, `const int Capacity = 256`, методами `void Set(int id)`, `void Unset(int id)`, `readonly bool Get(int id)`, `readonly bool HasAll(in ComponentMask)`, `readonly bool HasNone(in ComponentMask)`, `readonly bool HasAny(in ComponentMask)`, `readonly bool IsEmpty`, `readonly int PopCount()`. Реализует `IEquatable<ComponentMask>`.

Проверено на прототипе: `[InlineArray]` допускает индексатор `this[i]` внутри собственных методов, включая `readonly`. `Unsafe.SizeOf<ComponentMask>() == 32`.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/ComponentMaskTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using Xunit;

namespace UvEcs.Tests;

public class ComponentMaskTests
{
    [Fact]
    public void Size_is_32_bytes_and_capacity_is_256()
    {
        Assert.Equal(32, Unsafe.SizeOf<ComponentMask>());
        Assert.Equal(256, ComponentMask.Capacity);
    }

    [Fact]
    public void Set_and_Get_cross_word_boundaries()
    {
        var m = new ComponentMask();
        foreach (var id in new[] { 0, 63, 64, 127, 128, 255 }) m.Set(id);
        foreach (var id in new[] { 0, 63, 64, 127, 128, 255 }) Assert.True(m.Get(id), $"bit {id}");
        foreach (var id in new[] { 1, 62, 65, 254 }) Assert.False(m.Get(id), $"bit {id}");
    }

    [Fact]
    public void Unset_clears_only_that_bit()
    {
        var m = new ComponentMask();
        m.Set(64); m.Set(65);
        m.Unset(64);
        Assert.False(m.Get(64));
        Assert.True(m.Get(65));
    }

    [Fact]
    public void Empty_mask_is_empty_and_has_zero_popcount()
    {
        var m = new ComponentMask();
        Assert.True(m.IsEmpty);
        Assert.Equal(0, m.PopCount());
    }

    [Fact]
    public void HasAll_requires_every_bit()
    {
        var m = new ComponentMask(); m.Set(1); m.Set(200);
        var req = new ComponentMask(); req.Set(1); req.Set(200);
        Assert.True(m.HasAll(in req));
        req.Set(2);
        Assert.False(m.HasAll(in req));
    }

    [Fact]
    public void HasAll_of_empty_is_always_true()
    {
        var m = new ComponentMask(); m.Set(5);
        var empty = new ComponentMask();
        Assert.True(m.HasAll(in empty));
    }

    [Fact]
    public void HasNone_and_HasAny_are_opposites()
    {
        var m = new ComponentMask(); m.Set(10);
        var probe = new ComponentMask(); probe.Set(10);
        Assert.True(m.HasAny(in probe));
        Assert.False(m.HasNone(in probe));

        var other = new ComponentMask(); other.Set(11);
        Assert.False(m.HasAny(in other));
        Assert.True(m.HasNone(in other));
    }

    [Fact]
    public void PopCount_counts_all_words()
    {
        var m = new ComponentMask();
        m.Set(0); m.Set(64); m.Set(128); m.Set(192);
        Assert.Equal(4, m.PopCount());
    }

    [Fact]
    public void Equality_is_by_value()
    {
        var a = new ComponentMask(); a.Set(3);
        var b = new ComponentMask(); b.Set(3);
        var c = new ComponentMask(); c.Set(4);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Set_rejects_out_of_range(int id)
    {
        var m = new ComponentMask();
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Set(id));
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter ComponentMaskTests`
Expected: FAIL — тип `ComponentMask` не найден.

- [ ] **Step 3: Реализовать**

`src/UvEcs/ComponentMask.cs`:

```csharp
using System.Numerics;
using System.Runtime.CompilerServices;

namespace UvEcs;

/// <summary>
/// Битовая маска компонентов, 256 бит. Плоская, а не категорийная:
/// матчинг 1000 архетипов плоской маской 1.07 мкс, категорийной 9.25 мкс (см. §4 спеки).
/// </summary>
[InlineArray(Words)]
public struct ComponentMask : IEquatable<ComponentMask>
{
    public const int Words = 4;
    public const int Capacity = Words * 64;

    private ulong _element0;

    private static void CheckRange(int id)
    {
        if ((uint)id >= Capacity) throw new ArgumentOutOfRangeException(nameof(id));
    }

    public void Set(int id)
    {
        CheckRange(id);
        this[id >> 6] |= 1UL << (id & 63);
    }

    public void Unset(int id)
    {
        CheckRange(id);
        this[id >> 6] &= ~(1UL << (id & 63));
    }

    public readonly bool Get(int id)
    {
        CheckRange(id);
        return (this[id >> 6] & (1UL << (id & 63))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool HasAll(in ComponentMask required)
    {
        for (int i = 0; i < Words; i++)
            if ((this[i] & required[i]) != required[i]) return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool HasNone(in ComponentMask forbidden)
    {
        for (int i = 0; i < Words; i++)
            if ((this[i] & forbidden[i]) != 0) return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool HasAny(in ComponentMask any)
    {
        for (int i = 0; i < Words; i++)
            if ((this[i] & any[i]) != 0) return true;
        return false;
    }

    public readonly bool IsEmpty
    {
        get
        {
            for (int i = 0; i < Words; i++) if (this[i] != 0) return false;
            return true;
        }
    }

    public readonly int PopCount()
    {
        int n = 0;
        for (int i = 0; i < Words; i++) n += BitOperations.PopCount(this[i]);
        return n;
    }

    public readonly bool Equals(ComponentMask other)
    {
        for (int i = 0; i < Words; i++) if (this[i] != other[i]) return false;
        return true;
    }

    public readonly override bool Equals(object? obj) => obj is ComponentMask m && Equals(m);

    public readonly override int GetHashCode()
    {
        var hc = new HashCode();
        for (int i = 0; i < Words; i++) hc.Add(this[i]);
        return hc.ToHashCode();
    }
}
```

- [ ] **Step 4: Прогнать**

Run: `dotnet test --filter ComponentMaskTests`
Expected: PASS, 11 тестов.

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: ComponentMask — плоская битовая маска на 256 компонентов через InlineArray"
```

---

## Task 4: Реестры компонентов и тегов

**Files:**
- Create: `src/UvEcs/ComponentRegistry.cs`
- Test: `tests/UvEcs.Tests/RegistryTests.cs`

**Interfaces:**
- Consumes: `ComponentMask.Capacity`, `TagMask.Capacity`, `TagMask.FromIndex`.
- Produces:
  - `static class ComponentRegistry` — `static int Register(int size)`, `static int Count { get; }`, `static int SizeOf(int componentId)`.
  - `static class ComponentType<T> where T : unmanaged, IComponent` — `static readonly int Id`, `static int Size { get; }`.
  - `static class SparseRegistry` — `static int Register()`, `static int Count { get; }`.
  - `static class SparseType<T> where T : unmanaged, ISparse` — `static readonly int Id`.
  - `static class TagRegistry` — `static int Register()`, `static int Count { get; }`.
  - `static class TagType<T> where T : unmanaged, ITag` — `static readonly int Index`, `static readonly TagMask Bit`.

**Важно про тестируемость:** регистрация статична на процесс, поэтому абсолютные значения `Id` зависят от порядка загрузки типов и от того, какие ещё тесты успели выполниться. **Тесты обязаны проверять уникальность и стабильность, а не конкретные числа.**

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/RegistryTests.cs`:

```csharp
using Xunit;

namespace UvEcs.Tests;

public class RegistryTests
{
    [Fact]
    public void Component_id_is_stable_across_calls()
    {
        Assert.Equal(ComponentType<Position>.Id, ComponentType<Position>.Id);
    }

    [Fact]
    public void Different_components_get_different_ids()
    {
        Assert.NotEqual(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
        Assert.NotEqual(ComponentType<Position>.Id, ComponentType<Health>.Id);
    }

    [Fact]
    public void Component_size_comes_from_the_type()
    {
        Assert.Equal(12, ComponentType<Position>.Size);
        Assert.Equal(8, ComponentType<Health>.Size);
        Assert.Equal(ComponentType<Position>.Size, ComponentRegistry.SizeOf(ComponentType<Position>.Id));
    }

    [Fact]
    public void Component_ids_fit_the_mask()
    {
        Assert.InRange(ComponentType<Position>.Id, 0, ComponentMask.Capacity - 1);
        Assert.True(ComponentRegistry.Count <= ComponentMask.Capacity);
    }

    [Fact]
    public void Tag_bits_are_distinct_and_stable()
    {
        Assert.Equal(TagType<Stunned>.Bit, TagType<Stunned>.Bit);
        Assert.NotEqual(TagType<Stunned>.Bit, TagType<InCombat>.Bit);
        Assert.False(TagType<Stunned>.Bit.HasAny(TagType<InCombat>.Bit));
    }

    [Fact]
    public void Tag_index_fits_64()
    {
        Assert.InRange(TagType<Dead>.Index, 0, TagMask.Capacity - 1);
    }

    [Fact]
    public void Sparse_ids_are_distinct()
    {
        Assert.NotEqual(SparseType<GuildBuff>.Id, SparseType<QuestFlag>.Id);
        Assert.Equal(SparseType<GuildBuff>.Id, SparseType<GuildBuff>.Id);
    }

    [Fact]
    public void Component_and_tag_id_spaces_are_independent()
    {
        // Оба могут быть нулём одновременно — это разные пространства.
        Assert.True(ComponentRegistry.Count > 0);
        Assert.True(TagRegistry.Count > 0);
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter RegistryTests`
Expected: FAIL — `ComponentType<>` не найден.

- [ ] **Step 3: Реализовать**

`src/UvEcs/ComponentRegistry.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace UvEcs;

/// <summary>
/// Идентификаторы стабильны внутри процесса и НЕСТАБИЛЬНЫ между процессами:
/// они зависят от порядка первого обращения. Наружу — в сеть и в файлы — не выходят никогда.
/// </summary>
public static class ComponentRegistry
{
    private static int _next;
    private static readonly int[] _sizes = new int[ComponentMask.Capacity];

    public static int Count => Volatile.Read(ref _next);

    public static int Register(int size)
    {
        int id = Interlocked.Increment(ref _next) - 1;
        if (id >= ComponentMask.Capacity)
            throw new InvalidOperationException(
                $"Превышен лимит компонентов ({ComponentMask.Capacity}). Увеличьте ComponentMask.Words.");
        _sizes[id] = size;
        return id;
    }

    public static int SizeOf(int componentId) => _sizes[componentId];
}

public static class ComponentType<T> where T : unmanaged, IComponent
{
    /// <remarks>JIT в tier1 сворачивает static readonly в константу — const не даёт выигрыша.</remarks>
    public static readonly int Id = ComponentRegistry.Register(Unsafe.SizeOf<T>());

    public static int Size => Unsafe.SizeOf<T>();
}

public static class SparseRegistry
{
    private static int _next;
    public static int Count => Volatile.Read(ref _next);
    public static int Register() => Interlocked.Increment(ref _next) - 1;
}

public static class SparseType<T> where T : unmanaged, ISparse
{
    public static readonly int Id = SparseRegistry.Register();
}

public static class TagRegistry
{
    private static int _next;
    public static int Count => Volatile.Read(ref _next);

    public static int Register()
    {
        int index = Interlocked.Increment(ref _next) - 1;
        if (index >= TagMask.Capacity)
            throw new InvalidOperationException(
                $"Превышен лимит тегов ({TagMask.Capacity}). Сначала пересмотрите корзины (§5 спеки): " +
                "редкий тег переезжает в ISparse, неизменяемый выражается набором компонентов. " +
                "Только затем расширяйте TagMask до InlineArray(2).");
        return index;
    }
}

public static class TagType<T> where T : unmanaged, ITag
{
    public static readonly int Index = TagRegistry.Register();

    /// <remarks>Возвращает TagMask, а не ulong. ulong не покидает TagMask.</remarks>
    public static readonly TagMask Bit = TagMask.FromIndex(Index);
}
```

- [ ] **Step 4: Прогнать**

Run: `dotnet test --filter RegistryTests`
Expected: PASS, 8 тестов.

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: реестры компонентов, тегов и sparse; id стабильны в процессе, но не между процессами"
```

---

## Task 5: Entity и EntityStore

**Files:**
- Create: `src/UvEcs/Entity.cs`
- Create: `src/UvEcs/EntityStore.cs`
- Test: `tests/UvEcs.Tests/EntityStoreTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces:
  - `readonly struct Entity` — поля `int Id`, `uint Version`; `Entity.Null`; `bool IsNull`; `IEquatable<Entity>`.
  - `struct EntityRecord` (public, нужен `Chunk`/`World`) — поля `int ArchetypeId`, `int ChunkIndex`, `int Row`, `uint Version`.
  - `sealed class EntityStore` — `Entity Create()`, `bool IsAlive(Entity)`, `void Destroy(Entity)`, `ref EntityRecord GetRecord(Entity)` (бросает на протухшей), `ref EntityRecord RecordRefUnchecked(int id)`, `int Capacity { get; }`.

**Контракт:** `Version == 0` означает «невалидная сущность». Живые версии начинаются с 1. Протухшая `Entity` ловится сравнением версий — одно сравнение по уже загруженной кеш-линии (§11 спеки), проверяется **и в Release**.

**Хранилище записей — страничное, а не плоский массив.** `EntityRecord[][]`, страницы по 4096 записей, страница никогда не переаллоцируется. Причина: `GetRecord` возвращает `ref` внутрь массива, а `Array.Resize` на плоском массиве заменил бы его новым — и удержанный `ref` молча писал бы в выброшенную копию. В ядре такой путь пока недостижим, но `CommandBuffer.Playback` из плана систем создаёт сущности в цикле, и там это выстрелит без единого исключения.

Цена измерена: случайный доступ к 10k записям — 5.4 мкс плоским массивом против 8.1 мкс страничным (в 1.5 раза), то есть **+2.7 мкс из 50 000 мкс бюджета тика**. Бьёт по `GetRef(Entity)` и sparse-драйверу, не по чанковой итерации. Так устроены `flecs` и `EnTT`.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/EntityStoreTests.cs`:

```csharp
using Xunit;

namespace UvEcs.Tests;

public class EntityStoreTests
{
    [Fact]
    public void Default_entity_is_null()
    {
        Assert.True(default(Entity).IsNull);
        Assert.True(Entity.Null.IsNull);
    }

    [Fact]
    public void Created_entities_are_alive_and_distinct()
    {
        var store = new EntityStore();
        var a = store.Create();
        var b = store.Create();

        Assert.NotEqual(a.Id, b.Id);
        Assert.False(a.IsNull);
        Assert.True(store.IsAlive(a));
        Assert.True(store.IsAlive(b));
    }

    [Fact]
    public void Destroyed_entity_is_not_alive()
    {
        var store = new EntityStore();
        var e = store.Create();
        store.Destroy(e);
        Assert.False(store.IsAlive(e));
    }

    [Fact]
    public void Destroyed_id_is_reused_with_a_new_version()
    {
        var store = new EntityStore();
        var first = store.Create();
        store.Destroy(first);
        var second = store.Create();

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Version, second.Version);
        Assert.False(store.IsAlive(first));
        Assert.True(store.IsAlive(second));
    }

    [Fact]
    public void GetRecord_throws_on_stale_entity()
    {
        var store = new EntityStore();
        var e = store.Create();
        store.Destroy(e);
        Assert.Throws<InvalidOperationException>(() => store.GetRecord(e));
    }

    [Fact]
    public void GetRecord_returns_writable_reference()
    {
        var store = new EntityStore();
        var e = store.Create();
        store.GetRecord(e).Row = 42;
        Assert.Equal(42, store.GetRecord(e).Row);
    }

    [Fact]
    public void Destroying_twice_throws()
    {
        var store = new EntityStore();
        var e = store.Create();
        store.Destroy(e);
        Assert.Throws<InvalidOperationException>(() => store.Destroy(e));
    }

    [Fact]
    public void Store_grows_beyond_initial_capacity()
    {
        var store = new EntityStore();
        var created = new List<Entity>();
        for (int i = 0; i < 5000; i++) created.Add(store.Create());

        Assert.All(created, e => Assert.True(store.IsAlive(e)));
        Assert.Equal(5000, created.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Free_list_is_lifo_and_does_not_leak_ids()
    {
        var store = new EntityStore();
        var a = store.Create();
        var b = store.Create();
        store.Destroy(a);
        store.Destroy(b);

        var c = store.Create();
        var d = store.Create();
        var e = store.Create();

        Assert.Equal(b.Id, c.Id);   // LIFO: последний уничтоженный отдаётся первым
        Assert.Equal(a.Id, d.Id);
        Assert.Equal(2, e.Id);      // свежий id, свободных больше нет
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter EntityStoreTests`
Expected: FAIL — `Entity` / `EntityStore` не найдены.

- [ ] **Step 3: Реализовать Entity**

`src/UvEcs/Entity.cs`:

```csharp
namespace UvEcs;

/// <summary>Generational index. Version == 0 означает невалидную сущность.</summary>
public readonly struct Entity : IEquatable<Entity>
{
    public readonly int Id;
    public readonly uint Version;

    public Entity(int id, uint version)
    {
        Id = id;
        Version = version;
    }

    public static Entity Null => default;

    public bool IsNull => Version == 0;

    public bool Equals(Entity other) => Id == other.Id && Version == other.Version;
    public override bool Equals(object? obj) => obj is Entity e && Equals(e);
    public override int GetHashCode() => HashCode.Combine(Id, Version);
    public override string ToString() => IsNull ? "Entity.Null" : $"Entity({Id}v{Version})";

    public static bool operator ==(Entity a, Entity b) => a.Equals(b);
    public static bool operator !=(Entity a, Entity b) => !a.Equals(b);
}

/// <summary>Где физически лежит сущность. Обновляется при каждой миграции.</summary>
public struct EntityRecord
{
    public int ArchetypeId;
    public int ChunkIndex;
    public int Row;
    public uint Version;
}
```

- [ ] **Step 4: Реализовать EntityStore**

`src/UvEcs/EntityStore.cs`:

```csharp
namespace UvEcs;

public sealed class EntityStore
{
    private const int PageBits = 12;
    private const int PageSize = 1 << PageBits;    // 4096 записей на страницу
    private const int PageMask = PageSize - 1;

    /// <remarks>
    /// Страничное, а не плоское: страница никогда не переаллоцируется, поэтому ref,
    /// возвращённый из GetRecord, остаётся валидным после любого числа Create().
    /// Плоский Array.Resize молча оставил бы удержанный ref указывать в старый массив.
    /// </remarks>
    private EntityRecord[][] _pages = { new EntityRecord[PageSize] };
    private int[] _freeIds = new int[256];
    private int _freeCount;
    private int _count;

    public int Capacity => _pages.Length * PageSize;
    public int AliveCount => _count - _freeCount;

    private ref EntityRecord At(int id) => ref _pages[id >> PageBits][id & PageMask];

    private void EnsurePage(int id)
    {
        int page = id >> PageBits;
        if (page < _pages.Length) return;

        int oldLength = _pages.Length;
        Array.Resize(ref _pages, page + 1);                 // растёт только массив ссылок
        for (int i = oldLength; i < _pages.Length; i++)
            _pages[i] = new EntityRecord[PageSize];         // сами страницы неподвижны
    }

    public Entity Create()
    {
        int id;
        if (_freeCount > 0)
        {
            id = _freeIds[--_freeCount];
        }
        else
        {
            id = _count++;
            EnsurePage(id);
        }

        ref var rec = ref At(id);
        rec.Version = rec.Version == 0 ? 1u : rec.Version;   // первая жизнь начинается с 1
        rec.ArchetypeId = -1;
        rec.ChunkIndex = -1;
        rec.Row = -1;
        return new Entity(id, rec.Version);
    }

    public bool IsAlive(Entity e)
        => !e.IsNull && (uint)e.Id < (uint)_count && At(e.Id).Version == e.Version;

    public void Destroy(Entity e)
    {
        if (!IsAlive(e)) throw new InvalidOperationException($"{e} уже удалена или невалидна.");

        ref var rec = ref At(e.Id);
        rec.Version++;                       // протухание всех существующих дескрипторов
        if (rec.Version == 0) rec.Version = 1;   // 0 зарезервирован под Null
        rec.ArchetypeId = -1;
        rec.ChunkIndex = -1;
        rec.Row = -1;

        if (_freeCount == _freeIds.Length) Array.Resize(ref _freeIds, _freeIds.Length * 2);
        _freeIds[_freeCount++] = e.Id;
    }

    /// <summary>
    /// Проверяется и в Release: молча читать чужую память недопустимо (§11 спеки).
    /// Возвращённый ref переживает любое число Create() — страницы неподвижны.
    /// </summary>
    public ref EntityRecord GetRecord(Entity e)
    {
        if (!IsAlive(e)) throw new InvalidOperationException($"{e} протухла или невалидна.");
        return ref At(e.Id);
    }

    /// <summary>Без проверки версии. Только для внутренних путей, где сущность заведомо жива.</summary>
    internal ref EntityRecord RecordRefUnchecked(int id) => ref At(id);
}
```

Тест, закрепляющий главное свойство:

```csharp
    [Fact]
    public void Record_ref_survives_growth_caused_by_later_creates()
    {
        var store = new EntityStore();
        var first = store.Create();

        ref var rec = ref store.GetRecord(first);
        rec.Row = 111;

        for (int i = 0; i < 20_000; i++) store.Create();   // перешагиваем несколько страниц

        rec.Row = 222;                                     // пишем через ref, взятый до роста
        Assert.Equal(222, store.GetRecord(first).Row);     // запись видна через свежий поиск
    }
```

- [ ] **Step 5: Прогнать**

Run: `dotnet test --filter EntityStoreTests`
Expected: PASS, 9 тестов.

- [ ] **Step 6: Коммит**

```bash
git add -A
git commit -m "feat: Entity как generational index и EntityStore с free-list"
```

---

## Task 6: ChunkPool

**Files:**
- Create: `src/UvEcs/ChunkPool.cs`
- Test: `tests/UvEcs.Tests/ChunkPoolTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces: `sealed class ChunkPool` — `const int ChunkBytes = 16384`, `const int Alignment = 64`, `byte[] Rent()`, `void Return(byte[])`, `int FreeCount { get; }`, `int TotalAllocated { get; }`; `internal static nint AlignedStart(byte[] buffer)` — возвращает выровненный на 64 адрес внутри буфера.

**Две защиты, без которых пул опасен:**

`AlignedStart` — **`internal`, а не `public`**. Он законен исключительно потому, что буфер пиннут на POH; вызванный на обычном массиве, он вернёт адрес, который протухнет после ближайшей сборки мусора. Публичный метод, принимающий любой `byte[]`, — приглашение к этому. `Chunk` живёт в той же сборке, тестам открыт `InternalsVisibleTo`.

`Return` **отвергает повторный возврат**. Без этого `Return(buf); Return(buf);` кладёт одну ссылку в стек дважды, и следующие два `Rent()` отдают её двум владельцам: два архетипа пишут в одну память, считая её своей, без единого исключения. Проверка `_owned.Contains` этого не ловит — членство в `_owned` навсегда. Нужно отдельное множество «сейчас свободных».

**Обоснование (§5 спеки):** POH-массив закреплён навсегда, `Dispose` не нужен. Но POH выравнивает только на 8 байт (из 200 массивов на границу 64 попали 25), поэтому указатель сдвигается внутри буфера. Отсюда размер `16384 + 64`. Все чанки одного размера, поэтому пул один на мир и чанки взаимозаменяемы между архетипами.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/ChunkPoolTests.cs`:

```csharp
using Xunit;

namespace UvEcs.Tests;

public class ChunkPoolTests
{
    [Fact]
    public void Rented_buffer_has_room_for_chunk_plus_alignment()
    {
        var pool = new ChunkPool();
        var buf = pool.Rent();
        Assert.Equal(ChunkPool.ChunkBytes + ChunkPool.Alignment, buf.Length);
    }

    [Fact]
    public unsafe void Aligned_start_is_64_byte_aligned_and_leaves_a_full_chunk()
    {
        var pool = new ChunkPool();

        // POH выравнивает лишь на 8 байт, поэтому сдвиг обязателен и должен работать для любого буфера.
        for (int i = 0; i < 50; i++)
        {
            var buf = pool.Rent();
            nint start = ChunkPool.AlignedStart(buf);

            Assert.Equal(0, (int)(start & (ChunkPool.Alignment - 1)));

            fixed (byte* raw = buf)
            {
                int shift = (int)(start - (nint)raw);
                Assert.InRange(shift, 0, ChunkPool.Alignment - 1);
                Assert.True(shift + ChunkPool.ChunkBytes <= buf.Length,
                    "после сдвига в буфере не осталось места на полный чанк");
            }
        }
    }

    [Fact]
    public void Returned_buffer_is_reused()
    {
        var pool = new ChunkPool();
        var a = pool.Rent();
        pool.Return(a);
        var b = pool.Rent();

        Assert.Same(a, b);
        Assert.Equal(1, pool.TotalAllocated);
    }

    [Fact]
    public void Pool_counts_free_buffers()
    {
        var pool = new ChunkPool();
        var a = pool.Rent();
        var b = pool.Rent();
        Assert.Equal(0, pool.FreeCount);

        pool.Return(a);
        pool.Return(b);
        Assert.Equal(2, pool.FreeCount);
        Assert.Equal(2, pool.TotalAllocated);
    }

    [Fact]
    public void Returning_a_foreign_buffer_throws()
    {
        var pool = new ChunkPool();
        Assert.Throws<ArgumentException>(() => pool.Return(new byte[10]));
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter ChunkPoolTests`
Expected: FAIL — `ChunkPool` не найден.

- [ ] **Step 3: Реализовать**

`src/UvEcs/ChunkPool.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UvEcs;

/// <summary>
/// Пул чанков. Все чанки одного размера, поэтому пул один на мир и буферы
/// взаимозаменяемы между архетипами. Память ОС не отдаётся никогда.
/// </summary>
public sealed class ChunkPool
{
    public const int ChunkBytes = 16384;
    public const int Alignment = 64;

    private readonly Stack<byte[]> _free = new();
    private readonly HashSet<byte[]> _owned = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<byte[]> _currentlyFree = new(ReferenceEqualityComparer.Instance);

    public int FreeCount => _free.Count;
    public int TotalAllocated => _owned.Count;

    public byte[] Rent()
    {
        if (_free.Count > 0)
        {
            var reused = _free.Pop();
            _currentlyFree.Remove(reused);
            return reused;
        }

        // Pinned Object Heap: массив закреплён навсегда, Dispose не нужен, GC его не двигает.
        var buffer = GC.AllocateUninitializedArray<byte>(ChunkBytes + Alignment, pinned: true);
        _owned.Add(buffer);
        return buffer;
    }

    /// <summary>
    /// Повторный возврат — ошибка, а не мелочь: он положил бы одну ссылку в стек дважды,
    /// и следующие два Rent() отдали бы её двум владельцам. Два архетипа писали бы в одну
    /// память, считая её своей, без единого исключения.
    /// </summary>
    public void Return(byte[] buffer)
    {
        if (!_owned.Contains(buffer))
            throw new ArgumentException("Буфер не принадлежит этому пулу.", nameof(buffer));

        if (!_currentlyFree.Add(buffer))
            throw new InvalidOperationException("Буфер уже возвращён в пул.");

        _free.Push(buffer);
    }

    /// <summary>
    /// POH гарантирует выравнивание только на 8 байт, поэтому сдвигаемся внутри буфера.
    /// </summary>
    /// <remarks>
    /// internal, а не public, и это принципиально: метод законен только для буфера,
    /// выделенного с pinned: true. На обычном массиве он вернёт адрес, который протухнет
    /// после ближайшей сборки мусора, и ни один тест этого не поймает.
    /// </remarks>
    internal static unsafe nint AlignedStart(byte[] buffer)
    {
        nint raw = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(buffer));
        nint offset = (Alignment - (raw & (Alignment - 1))) & (Alignment - 1);
        return raw + offset;
    }
}
```

Тесты, закрепляющие обе защиты:

```csharp
    [Fact]
    public void Returning_the_same_buffer_twice_throws()
    {
        // Иначе следующие два Rent() отдадут одну память двум владельцам — молча.
        var pool = new ChunkPool();
        var buf = pool.Rent();
        pool.Return(buf);

        Assert.Throws<InvalidOperationException>(() => pool.Return(buf));
    }

    [Fact]
    public void Rent_never_hands_out_the_same_buffer_to_two_owners()
    {
        var pool = new ChunkPool();
        var a = pool.Rent();
        pool.Return(a);

        var b = pool.Rent();
        var c = pool.Rent();

        Assert.Same(a, b);
        Assert.NotSame(b, c);
    }
```

- [ ] **Step 4: Прогнать**

Run: `dotnet test --filter ChunkPoolTests`
Expected: PASS, 5 тестов.

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: ChunkPool на Pinned Object Heap с ручным выравниванием до 64"
```

---

## Task 7: ChunkLayout

**Files:**
- Create: `src/UvEcs/ChunkLayout.cs`
- Test: `tests/UvEcs.Tests/ChunkLayoutTests.cs`

**Interfaces:**
- Consumes: `ChunkPool.ChunkBytes`.
- Produces: `sealed class ChunkLayout` — `static ChunkLayout Create(int[] componentIds)` (ids отсортированы по возрастанию), свойства `int Capacity`, `int EntityOffset`, `int TagOffset`, `int[] ColumnOffsets` (параллелен `componentIds`), `int[] ComponentIds`, `int ColumnOf(int componentId)` (возвращает `-1`, если нет), `const int ColumnAlignment = 16`.

Раскладка (§5 спеки):

```
[ Entity × Cap ][ TagMask × Cap ][ компонент₀ × Cap ][ компонент₁ × Cap ] ...
```

Каждая колонка начинается на границе 16 байт. `Capacity` — максимальное число сущностей, при котором всё влезает в `ChunkBytes`.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/ChunkLayoutTests.cs`:

```csharp
using Xunit;

namespace UvEcs.Tests;

public class ChunkLayoutTests
{
    private static int[] Ids(params int[] ids) { Array.Sort(ids); return ids; }

    [Fact]
    public void Empty_archetype_holds_only_entity_and_tag_columns()
    {
        var layout = ChunkLayout.Create(Array.Empty<int>());
        // 8 байт Entity + 8 байт TagMask = 16 на сущность
        Assert.Equal(ChunkPool.ChunkBytes / 16, layout.Capacity);
        Assert.Equal(0, layout.EntityOffset);
        Assert.Empty(layout.ColumnOffsets);
    }

    [Fact]
    public void Columns_are_16_byte_aligned()
    {
        var layout = ChunkLayout.Create(Ids(ComponentType<Position>.Id, ComponentType<Health>.Id));
        Assert.Equal(0, layout.EntityOffset % ChunkLayout.ColumnAlignment);
        Assert.Equal(0, layout.TagOffset % ChunkLayout.ColumnAlignment);
        Assert.All(layout.ColumnOffsets, off => Assert.Equal(0, off % ChunkLayout.ColumnAlignment));
    }

    [Fact]
    public void Everything_fits_inside_the_chunk()
    {
        var layout = ChunkLayout.Create(Ids(ComponentType<Position>.Id, ComponentType<Velocity>.Id, ComponentType<Health>.Id));
        int cap = layout.Capacity;

        Assert.True(layout.TagOffset + 8 * cap <= ChunkPool.ChunkBytes);
        for (int i = 0; i < layout.ColumnOffsets.Length; i++)
        {
            int size = ComponentRegistry.SizeOf(layout.ComponentIds[i]);
            Assert.True(layout.ColumnOffsets[i] + size * cap <= ChunkPool.ChunkBytes,
                $"колонка {i} вылезает за чанк");
        }
    }

    [Fact]
    public void Capacity_is_maximal_one_more_entity_would_not_fit()
    {
        var ids = Ids(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
        var layout = ChunkLayout.Create(ids);
        Assert.True(ChunkLayout.TotalBytesFor(ids, layout.Capacity) <= ChunkPool.ChunkBytes);
        Assert.True(ChunkLayout.TotalBytesFor(ids, layout.Capacity + 1) > ChunkPool.ChunkBytes);
    }

    // Независимый оракул. Предыдущий тест круговой: Create сам вызывает TotalBytesFor
    // в цикле поиска ёмкости, поэтому его постусловие выполняется тождественно.
    // Здесь ожидание посчитано руками и не зависит от кода.
    [Fact]
    public void Capacity_matches_a_hand_computed_oracle()
    {
        // Position(12) + Velocity(12) + Health(8) = 32; шаг = 8(Entity) + 8(TagMask) + 32 = 48.
        // 16384 / 48 = 341, но при 341: Entity 2728 -> Align 2736, Tag -> 5472, Position -> 9568,
        // Velocity -> 13664, Health -> 16392 > 16384. Не влезает.
        // При 340 все колонки кратны 16 без добивки: 2720, 5440, 9520, 13600, 16320 <= 16384.
        var ids = Ids(ComponentType<Position>.Id, ComponentType<Velocity>.Id, ComponentType<Health>.Id);
        Assert.Equal(340, ChunkLayout.Create(ids).Capacity);

        // Только Position: шаг 28. 16384/28 = 585 -> 16396 > 16384. При 584: 4672+4672+7008 = 16352.
        var onlyPosition = Ids(ComponentType<Position>.Id);
        Assert.Equal(584, ChunkLayout.Create(onlyPosition).Capacity);
    }

    [Fact]
    public void ColumnOf_returns_minus_one_on_an_empty_archetype()
    {
        var layout = ChunkLayout.Create(Array.Empty<int>());
        Assert.Equal(-1, layout.ColumnOf(ComponentType<Position>.Id));
    }

    [Fact]
    public void Layout_does_not_alias_the_caller_array()
    {
        var ids = Ids(ComponentType<Position>.Id, ComponentType<Health>.Id);
        var layout = ChunkLayout.Create(ids);
        int probe = ids[0];

        ids[0] = 999;   // портим массив вызывающего: сортировка сломана

        Assert.Equal(probe, layout.ComponentIds[0]);           // копия не пострадала
        Assert.InRange(layout.ColumnOf(probe), 0, 1);          // бинарный поиск по-прежнему работает
    }

    [Fact]
    public void Columns_do_not_overlap()
    {
        var ids = Ids(ComponentType<Position>.Id, ComponentType<Velocity>.Id, ComponentType<Health>.Id);
        var layout = ChunkLayout.Create(ids);
        int cap = layout.Capacity;

        var ranges = new List<(int start, int end)> { (layout.EntityOffset, layout.EntityOffset + 8 * cap), (layout.TagOffset, layout.TagOffset + 8 * cap) };
        for (int i = 0; i < ids.Length; i++)
            ranges.Add((layout.ColumnOffsets[i], layout.ColumnOffsets[i] + ComponentRegistry.SizeOf(ids[i]) * cap));

        ranges.Sort((a, b) => a.start.CompareTo(b.start));
        for (int i = 1; i < ranges.Count; i++)
            Assert.True(ranges[i].start >= ranges[i - 1].end, $"колонки {i - 1} и {i} пересекаются");
    }

    [Fact]
    public void ColumnOf_finds_registered_components_and_rejects_others()
    {
        var layout = ChunkLayout.Create(Ids(ComponentType<Position>.Id, ComponentType<Health>.Id));
        Assert.InRange(layout.ColumnOf(ComponentType<Position>.Id), 0, 1);
        Assert.InRange(layout.ColumnOf(ComponentType<Health>.Id), 0, 1);
        Assert.NotEqual(layout.ColumnOf(ComponentType<Position>.Id), layout.ColumnOf(ComponentType<Health>.Id));
        Assert.Equal(-1, layout.ColumnOf(ComponentType<Mana>.Id));
    }

    [Fact]
    public void Component_larger_than_chunk_is_rejected()
    {
        int fakeId = ComponentRegistry.Register(ChunkPool.ChunkBytes + 1);
        var ex = Assert.Throws<InvalidOperationException>(() => ChunkLayout.Create(new[] { fakeId }));
        Assert.Contains("не помещается", ex.Message);
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter ChunkLayoutTests`
Expected: FAIL — `ChunkLayout` не найден.

- [ ] **Step 3: Реализовать**

`src/UvEcs/ChunkLayout.cs`:

```csharp
namespace UvEcs;

/// <summary>
/// Раскладка чанка: [Entity × Cap][TagMask × Cap][компонент₀ × Cap]...
/// Колонка Entity обязательна: swap-remove обновляет EntityRecord переехавшей строки,
/// а сетевой слой индексирует версии по entityId (§5 спеки).
/// </summary>
public sealed class ChunkLayout
{
    public const int ColumnAlignment = 16;

    // Берём из типов, а не константой: Chunk адресует строки тем же Unsafe.SizeOf,
    // и разъехаться они не должны. Прибитая восьмёрка в двух местах — это два места,
    // где надо не забыть, если Entity вырастет.
    private static readonly int EntitySize = Unsafe.SizeOf<Entity>();
    private static readonly int TagSize = Unsafe.SizeOf<TagMask>();

    public int Capacity { get; private init; }
    public int EntityOffset { get; private init; }
    public int TagOffset { get; private init; }
    public int[] ComponentIds { get; private init; } = Array.Empty<int>();
    public int[] ColumnOffsets { get; private init; } = Array.Empty<int>();

    private static int Align(int value) => (value + ColumnAlignment - 1) & ~(ColumnAlignment - 1);

    /// <summary>
    /// Единственное место, где известна раскладка колонок. И расчёт ёмкости, и построение
    /// смещений идут через него, поэтому разойтись они не могут.
    /// </summary>
    /// <param name="offsets">Куда записать смещения колонок компонентов. <c>null</c> — только посчитать размер.</param>
    /// <returns>Полный размер чанка в байтах при данной ёмкости.</returns>
    private static int WalkColumns(int[] componentIds, int capacity, int[]? offsets,
                                   out int entityOffset, out int tagOffset)
    {
        int off = 0;

        entityOffset = off;
        off = Align(off + EntitySize * capacity);

        tagOffset = off;
        off = Align(off + TagSize * capacity);

        for (int i = 0; i < componentIds.Length; i++)
        {
            if (offsets is not null) offsets[i] = off;
            off = Align(off + ComponentRegistry.SizeOf(componentIds[i]) * capacity);
        }

        return off;
    }

    /// <summary>Сколько байт займёт чанк на <paramref name="capacity"/> сущностей. Публично ради тестов.</summary>
    public static int TotalBytesFor(int[] componentIds, int capacity)
        => WalkColumns(componentIds, capacity, null, out _, out _);

    /// <param name="componentIds">Отсортированы по возрастанию.</param>
    public static ChunkLayout Create(int[] componentIds)
    {
        int stride = EntitySize + TagSize;
        foreach (int id in componentIds) stride += ComponentRegistry.SizeOf(id);

        // Выравнивание только добавляет байты, поэтому ChunkBytes/stride — верхняя оценка ёмкости.
        int capacity = ChunkPool.ChunkBytes / stride;
        while (capacity > 0 && TotalBytesFor(componentIds, capacity) > ChunkPool.ChunkBytes) capacity--;

        if (capacity == 0)
            throw new InvalidOperationException(
                $"Архетип не помещается в чанк {ChunkPool.ChunkBytes} б: одна сущность требует {stride} б. " +
                "Компонент слишком велик.");

        var offsets = new int[componentIds.Length];
        WalkColumns(componentIds, capacity, offsets, out int entityOffset, out int tagOffset);

        return new ChunkLayout
        {
            Capacity = capacity,
            EntityOffset = entityOffset,
            TagOffset = tagOffset,
            ComponentIds = (int[])componentIds.Clone(),   // ColumnOf полагается на сортировку
            ColumnOffsets = offsets,
        };
    }

    /// <remarks>Вызывается раз на чанк, не на сущность, поэтому бинарного поиска достаточно.</remarks>
    public int ColumnOf(int componentId)
    {
        int lo = 0, hi = ComponentIds.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int v = ComponentIds[mid];
            if (v == componentId) return mid;
            if (v < componentId) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }
}
```

- [ ] **Step 4: Прогнать**

Run: `dotnet test --filter ChunkLayoutTests`
Expected: PASS, 7 тестов.

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: ChunkLayout — расчёт ёмкости и выровненных смещений колонок"
```

---

## Task 8: Chunk

**Files:**
- Create: `src/UvEcs/Chunk.cs`
- Test: `tests/UvEcs.Tests/ChunkTests.cs`

**Interfaces:**
- Consumes: `ChunkLayout`, `ChunkPool.AlignedStart`, `Entity`, `TagMask`, `ComponentType<T>.Id`, `ComponentRegistry.SizeOf`.
- Produces: `sealed unsafe class Chunk` —
  - конструктор `Chunk(ChunkLayout layout, byte[] buffer)`;
  - `ChunkLayout Layout { get; }`, `byte[] Buffer { get; }` (internal), `int Count { get; }`, `int Capacity { get; }`, `bool IsFull { get; }`, `bool IsEmpty { get; }`;
  - `TagMask TagUnion { get; internal set; }`, `bool TagsDirty { get; internal set; }`;
  - `Span<Entity> Entities { get; }`, `Span<TagMask> Tags { get; }`;
  - `ref Entity EntityAt(int row)`, `ref TagMask TagAt(int row)`;
  - `ReadOnlySpan<T> GetRead<T>()`, `Span<T> GetWrite<T>()`, `ref T GetRef<T>(int row)` — все `where T : unmanaged, IComponent`;
  - `int AddRow(Entity e)`;
  - `Entity SwapRemove(int row)` — возвращает сущность, **переехавшую** в `row`, либо `Entity.Null`, если удалялась последняя строка;
  - `internal void CopyRowTo(int row, Chunk dest, int destRow)` — копирует общие колонки и маску тегов.

**Контракт `GetWrite`:** в этом плане он просто возвращает `Span<T>`. Штамп `repVersion` добавит план сети — это будет правка одного метода.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/ChunkTests.cs`:

```csharp
using Xunit;

namespace UvEcs.Tests;

public class ChunkTests
{
    private static readonly ChunkPool Pool = new();

    private static Chunk NewChunk(params int[] componentIds)
    {
        Array.Sort(componentIds);
        var layout = ChunkLayout.Create(componentIds);
        return new Chunk(layout, Pool.Rent());
    }

    private static Chunk PosVelChunk() => NewChunk(ComponentType<Position>.Id, ComponentType<Velocity>.Id);

    [Fact]
    public void New_chunk_is_empty()
    {
        var c = PosVelChunk();
        Assert.Equal(0, c.Count);
        Assert.True(c.IsEmpty);
        Assert.False(c.IsFull);
        Assert.True(c.Capacity > 0);
    }

    [Fact]
    public void AddRow_appends_entity_with_empty_tags()
    {
        var c = PosVelChunk();
        var e = new Entity(7, 1);
        int row = c.AddRow(e);

        Assert.Equal(0, row);
        Assert.Equal(1, c.Count);
        Assert.Equal(e, c.Entities[0]);
        Assert.True(c.Tags[0].IsEmpty);
    }

    [Fact]
    public void Spans_are_sized_by_count_not_capacity()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        c.AddRow(new Entity(2, 1));

        Assert.Equal(2, c.Entities.Length);
        Assert.Equal(2, c.Tags.Length);
        Assert.Equal(2, c.GetRead<Position>().Length);
        Assert.Equal(2, c.GetWrite<Position>().Length);
    }

    [Fact]
    public void Component_data_round_trips()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        c.AddRow(new Entity(2, 1));

        var pos = c.GetWrite<Position>();
        pos[0] = new Position { X = 1, Y = 2, Z = 3 };
        pos[1] = new Position { X = 4, Y = 5, Z = 6 };

        var vel = c.GetWrite<Velocity>();
        vel[1] = new Velocity { X = 9 };

        Assert.Equal(1, c.GetRead<Position>()[0].X);
        Assert.Equal(6, c.GetRead<Position>()[1].Z);
        Assert.Equal(9, c.GetRead<Velocity>()[1].X);
        Assert.Equal(0, c.GetRead<Velocity>()[0].X);   // AddRow обнулил строку
    }

    // Имя честное: различить sizeof(long) и Unsafe.SizeOf<Entity>() тестом нельзя,
    // пока обе величины равны восьми. Этот тест проверяет ровно то, что умеет —
    // согласованность трёх способов добраться до строки.
    [Fact]
    public void Multi_row_addressing_stays_consistent_across_accessors()
    {
        var c = PosVelChunk();
        for (int i = 0; i < 5; i++) c.AddRow(new Entity(100 + i, 1));

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(100 + i, c.EntityAt(i).Id);
            Assert.Equal(c.Entities[i], c.EntityAt(i));
        }

        c.TagAt(3) = TagType<Dead>.Bit;
        Assert.True(c.Tags[3].HasAll(TagType<Dead>.Bit));
        Assert.True(c.Tags[2].IsEmpty);   // соседняя строка не задета
    }

    // А вот это ловит настоящий рассинхрон: если ChunkLayout зарезервирует под строку
    // не столько байт, сколько Chunk отсчитывает, колонки наедут друг на друга.
    [Fact]
    public void Layout_reserves_room_for_every_row_at_the_types_stride()
    {
        var layout = ChunkLayout.Create(Ids(ComponentType<Position>.Id));

        int entityBytes = Unsafe.SizeOf<Entity>() * layout.Capacity;
        int tagBytes = Unsafe.SizeOf<TagMask>() * layout.Capacity;

        Assert.True(layout.TagOffset >= layout.EntityOffset + entityBytes,
            "колонка тегов начинается внутри колонки сущностей");
        Assert.True(layout.ColumnOffsets[0] >= layout.TagOffset + tagBytes,
            "первая колонка компонента начинается внутри колонки тегов");
    }

    [Fact]
    public void EntityAt_and_TagAt_reject_rows_outside_count()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = c.EntityAt(1).Id; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = c.EntityAt(-1).Id; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = c.TagAt(1).IsEmpty; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = c.TagAt(999_999).IsEmpty; });
    }

    [Fact]
    public void AddRow_zeroes_the_row_even_when_the_buffer_was_reused()
    {
        // Пул отдаёт буфер с данными предыдущего чанка. Без затирания строки
        // сущность унаследовала бы значения покойника, а на свежем буфере
        // тест зеленел бы случайно — ОС отдаёт новые страницы нулевыми.
        var pool = new ChunkPool();
        var layout = ChunkLayout.Create(new[] { ComponentType<Position>.Id });

        var buffer = pool.Rent();
        var first = new Chunk(layout, buffer);
        first.AddRow(new Entity(1, 1));
        first.GetWrite<Position>()[0] = new Position { X = 1234.5f, Y = 1, Z = 2 };
        first.SwapRemove(0);
        pool.Return(buffer);

        var second = new Chunk(layout, pool.Rent());   // тот же буфер
        second.AddRow(new Entity(2, 1));

        Assert.Equal(0, second.GetRead<Position>()[0].X);
        Assert.Equal(0, second.GetRead<Position>()[0].Y);
        Assert.Equal(0, second.GetRead<Position>()[0].Z);
    }

    [Fact]
    public void GetRef_gives_writable_reference()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        c.GetRef<Position>(0).X = 12.5f;
        Assert.Equal(12.5f, c.GetRead<Position>()[0].X);
    }

    [Fact]
    public void Accessing_absent_component_throws()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        Assert.Throws<InvalidOperationException>(() => c.GetRead<Health>());
    }

    [Fact]
    public void AddRow_beyond_capacity_throws()
    {
        var c = PosVelChunk();
        for (int i = 0; i < c.Capacity; i++) c.AddRow(new Entity(i, 1));
        Assert.True(c.IsFull);
        Assert.Throws<InvalidOperationException>(() => c.AddRow(new Entity(9999, 1)));
    }

    [Fact]
    public void SwapRemove_from_middle_moves_last_row_and_reports_it()
    {
        var c = PosVelChunk();
        var e0 = new Entity(10, 1);
        var e1 = new Entity(11, 1);
        var e2 = new Entity(12, 1);
        c.AddRow(e0); c.AddRow(e1); c.AddRow(e2);
        c.GetWrite<Position>()[2] = new Position { X = 99 };
        c.TagAt(2) = TagType<Stunned>.Bit;

        Entity moved = c.SwapRemove(0);

        Assert.Equal(e2, moved);
        Assert.Equal(2, c.Count);
        Assert.Equal(e2, c.Entities[0]);
        Assert.Equal(99, c.GetRead<Position>()[0].X);
        Assert.True(c.Tags[0].HasAll(TagType<Stunned>.Bit));
    }

    [Fact]
    public void SwapRemove_of_last_row_moves_nothing()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        c.AddRow(new Entity(2, 1));

        Entity moved = c.SwapRemove(1);

        Assert.True(moved.IsNull);
        Assert.Equal(1, c.Count);
    }

    [Fact]
    public void SwapRemove_of_only_row_empties_chunk()
    {
        var c = PosVelChunk();
        c.AddRow(new Entity(1, 1));
        Assert.True(c.SwapRemove(0).IsNull);
        Assert.True(c.IsEmpty);
    }

    [Fact]
    public void CopyRowTo_transfers_shared_columns_and_tags()
    {
        var src = NewChunk(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
        var dst = NewChunk(ComponentType<Position>.Id, ComponentType<Health>.Id);

        src.AddRow(new Entity(5, 1));
        src.GetWrite<Position>()[0] = new Position { X = 7, Y = 8, Z = 9 };
        src.GetWrite<Velocity>()[0] = new Velocity { X = 1 };
        src.TagAt(0) = TagType<InCombat>.Bit;

        int destRow = dst.AddRow(new Entity(5, 1));
        src.CopyRowTo(0, dst, destRow);

        Assert.Equal(7, dst.GetRead<Position>()[0].X);   // общая колонка перенеслась
        Assert.Equal(9, dst.GetRead<Position>()[0].Z);
        Assert.True(dst.Tags[0].HasAll(TagType<InCombat>.Bit));   // теги перенеслись
        Assert.Equal(0, dst.GetRead<Health>()[0].Current);        // новая колонка не тронута
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter ChunkTests`
Expected: FAIL — `Chunk` не найден.

- [ ] **Step 3: Реализовать**

`src/UvEcs/Chunk.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace UvEcs;

/// <summary>
/// 16 КБ данных, SoA. Первые две колонки служебные: Entity и TagMask.
/// Единица версионирования и параллельной работы.
/// </summary>
public sealed unsafe class Chunk
{
    private readonly nint _data;

    public ChunkLayout Layout { get; }
    internal byte[] Buffer { get; }

    public int Count { get; private set; }
    public int Capacity => Layout.Capacity;
    public bool IsFull => Count == Capacity;
    public bool IsEmpty => Count == 0;

    /// <summary>OR всех масок чанка. Консервативна: может быть шире правды (§5 спеки).</summary>
    public TagMask TagUnion { get; internal set; }

    /// <summary>Маска менялась — TagUnion пересчитывается в конце тика.</summary>
    public bool TagsDirty { get; internal set; }

    public Chunk(ChunkLayout layout, byte[] buffer)
    {
        Layout = layout;
        Buffer = buffer;
        _data = ChunkPool.AlignedStart(buffer);
    }

    public Span<Entity> Entities => new((void*)(_data + Layout.EntityOffset), Count);
    public Span<TagMask> Tags => new((void*)(_data + Layout.TagOffset), Count);

    /// <remarks>
    /// Шаг строки берётся из типа, а не из литерала. Восьмёрка была бы верна по совпадению:
    /// Entity — это int + uint. Добавь кто-нибудь поле, и адресация поехала бы молча,
    /// без единого падающего теста. Тот же приём уже используется в GetRef&lt;T&gt;.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Entity EntityAt(int row)
    {
        if ((uint)row >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(row));
        return ref Unsafe.AsRef<Entity>((void*)(_data + Layout.EntityOffset + (nint)row * Unsafe.SizeOf<Entity>()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TagMask TagAt(int row)
    {
        if ((uint)row >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(row));
        return ref Unsafe.AsRef<TagMask>((void*)(_data + Layout.TagOffset + (nint)row * Unsafe.SizeOf<TagMask>()));
    }

    private int ColumnOrThrow<T>() where T : unmanaged, IComponent
    {
        int col = Layout.ColumnOf(ComponentType<T>.Id);
        if (col < 0) throw new InvalidOperationException($"Компонента {typeof(T).Name} нет в этом архетипе.");
        return col;
    }

    public ReadOnlySpan<T> GetRead<T>() where T : unmanaged, IComponent
        => new((void*)(_data + Layout.ColumnOffsets[ColumnOrThrow<T>()]), Count);

    /// <remarks>План сети добавит сюда штамп repVersion для всех Count строк.</remarks>
    public Span<T> GetWrite<T>() where T : unmanaged, IComponent
        => new((void*)(_data + Layout.ColumnOffsets[ColumnOrThrow<T>()]), Count);

    public ref T GetRef<T>(int row) where T : unmanaged, IComponent
    {
        if ((uint)row >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(row));
        return ref Unsafe.AsRef<T>((void*)(_data + Layout.ColumnOffsets[ColumnOrThrow<T>()] + row * Unsafe.SizeOf<T>()));
    }

    /// <summary>
    /// Обнуляет строку во всех колонках. Буферы переиспользуются из пула, а
    /// AllocateUninitializedArray их не чистит — без затирания сущность унаследовала бы
    /// данные покойника, лежавшего на этой строке.
    /// </summary>
    public int AddRow(Entity e)
    {
        if (IsFull) throw new InvalidOperationException("Чанк заполнен.");
        int row = Count++;

        EntityAt(row) = e;
        TagAt(row) = TagMask.Empty;

        for (int c = 0; c < Layout.ComponentIds.Length; c++)
        {
            int size = ComponentRegistry.SizeOf(Layout.ComponentIds[c]);
            byte* col = (byte*)(_data + Layout.ColumnOffsets[c]);
            new Span<byte>(col + (long)row * size, size).Clear();
        }

        return row;
    }

    /// <returns>Сущность, переехавшая в <paramref name="row"/>, либо Entity.Null.</returns>
    public Entity SwapRemove(int row)
    {
        if ((uint)row >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(row));

        int last = Count - 1;
        Entity moved = Entity.Null;

        if (row != last)
        {
            EntityAt(row) = EntityAt(last);
            TagAt(row) = TagAt(last);

            for (int c = 0; c < Layout.ComponentIds.Length; c++)
            {
                int size = ComponentRegistry.SizeOf(Layout.ComponentIds[c]);
                byte* col = (byte*)(_data + Layout.ColumnOffsets[c]);
                System.Buffer.MemoryCopy(col + (long)last * size, col + (long)row * size, size, size);
            }

            moved = EntityAt(row);
        }

        Count--;
        return moved;
    }

    /// <summary>Копирует общие колонки и маску тегов. Колонки, которых нет в приёмнике, игнорируются.</summary>
    internal void CopyRowTo(int row, Chunk dest, int destRow)
    {
        dest.TagAt(destRow) = TagAt(row);

        for (int c = 0; c < Layout.ComponentIds.Length; c++)
        {
            int componentId = Layout.ComponentIds[c];
            int destCol = dest.Layout.ColumnOf(componentId);
            if (destCol < 0) continue;

            int size = ComponentRegistry.SizeOf(componentId);
            byte* srcCol = (byte*)(_data + Layout.ColumnOffsets[c]);
            byte* dstCol = (byte*)(dest._data + dest.Layout.ColumnOffsets[destCol]);
            System.Buffer.MemoryCopy(srcCol + (long)row * size, dstCol + (long)destRow * size, size, size);
        }
    }

    internal void Reset()
    {
        Count = 0;
        TagUnion = TagMask.Empty;
        TagsDirty = false;
    }
}
```

- [ ] **Step 4: Прогнать**

Run: `dotnet test --filter ChunkTests`
Expected: PASS, 12 тестов.

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: Chunk — SoA-колонки, swap-remove, перенос строки, обнуление новой строки"
```

---

## Task 9: Archetype

**Files:**
- Create: `src/UvEcs/Archetype.cs`
- Test: `tests/UvEcs.Tests/ArchetypeTests.cs`

**Interfaces:**
- Consumes: `ComponentMask`, `ChunkLayout`, `Chunk`, `ChunkPool`.
- Produces: `sealed class Archetype` —
  - `Archetype(int id, ComponentMask mask, int[] sortedComponentIds)`;
  - `int Id { get; }`, `ComponentMask Mask { get; }`, `ChunkLayout Layout { get; }`, `IReadOnlyList<Chunk> Chunks { get; }`, `int EntityCount { get; }`;
  - `int StructuralVersion { get; }` — инкрементится при любой миграции, ловит инвалидацию итератора в Debug;
  - `Chunk GetOrCreateChunkWithSpace(ChunkPool pool, out int chunkIndex)`;
  - `void ReleaseChunkIfEmpty(int chunkIndex, ChunkPool pool)` — гистерезис: один пустой чанк остаётся;
  - `bool TryGetAddEdge(int componentId, out Archetype target)`, `void SetAddEdge(int componentId, Archetype target)`;
  - `bool TryGetRemoveEdge(int componentId, out Archetype target)`, `void SetRemoveEdge(int componentId, Archetype target)`;
  - `internal void BumpStructuralVersion()`.

**Обоснование (§5 спеки):** архетипы никогда не удаляются — граф переходов и кеши запросов держат на них индексы. Освобождаются чанки, а не архетипы. Пустой чанк отдаётся в пул с гистерезисом, иначе сущность, прыгающая через границу чанка, устраивает пинг-понг.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/ArchetypeTests.cs`:

```csharp
using Xunit;

namespace UvEcs.Tests;

public class ArchetypeTests
{
    private static Archetype Make(int id, params int[] componentIds)
    {
        Array.Sort(componentIds);
        var mask = new ComponentMask();
        foreach (int c in componentIds) mask.Set(c);
        return new Archetype(id, mask, componentIds);
    }

    private static Archetype PosVel(int id = 0) => Make(id, ComponentType<Position>.Id, ComponentType<Velocity>.Id);

    [Fact]
    public void Archetype_exposes_mask_and_layout()
    {
        var a = PosVel();
        Assert.True(a.Mask.Get(ComponentType<Position>.Id));
        Assert.True(a.Mask.Get(ComponentType<Velocity>.Id));
        Assert.False(a.Mask.Get(ComponentType<Health>.Id));
        Assert.True(a.Layout.Capacity > 0);
        Assert.Equal(0, a.EntityCount);
    }

    [Fact]
    public void First_chunk_is_created_on_demand()
    {
        var pool = new ChunkPool();
        var a = PosVel();
        Assert.Empty(a.Chunks);

        var chunk = a.GetOrCreateChunkWithSpace(pool, out int index);
        Assert.Equal(0, index);
        Assert.Single(a.Chunks);
        Assert.Same(chunk, a.Chunks[0]);
    }

    [Fact]
    public void Full_chunk_forces_a_new_one()
    {
        var pool = new ChunkPool();
        var a = PosVel();

        var first = a.GetOrCreateChunkWithSpace(pool, out int i0);
        for (int i = 0; i < first.Capacity; i++) first.AddRow(new Entity(i, 1));

        var second = a.GetOrCreateChunkWithSpace(pool, out int i1);
        Assert.NotSame(first, second);
        Assert.Equal(1, i1);
        Assert.Equal(2, a.Chunks.Count);
    }

    [Fact]
    public void EntityCount_sums_all_chunks()
    {
        var pool = new ChunkPool();
        var a = PosVel();
        var c = a.GetOrCreateChunkWithSpace(pool, out _);
        c.AddRow(new Entity(1, 1));
        c.AddRow(new Entity(2, 1));
        Assert.Equal(2, a.EntityCount);
    }

    [Fact]
    public void Empty_chunk_is_kept_as_spare_but_second_one_is_returned()
    {
        var pool = new ChunkPool();
        var a = PosVel();

        a.GetOrCreateChunkWithSpace(pool, out int i0);
        var c0 = a.Chunks[0];
        for (int i = 0; i < c0.Capacity; i++) c0.AddRow(new Entity(i, 1));
        a.GetOrCreateChunkWithSpace(pool, out int i1);

        // оба пусты -> первый остаётся про запас, второй уходит в пул
        while (!c0.IsEmpty) c0.SwapRemove(c0.Count - 1);
        a.ReleaseChunkIfEmpty(i1, pool);
        Assert.Equal(1, pool.FreeCount);

        a.ReleaseChunkIfEmpty(i0, pool);
        Assert.Equal(1, pool.FreeCount);       // гистерезис: последний пустой не отдаём
        Assert.Single(a.Chunks);
    }

    [Fact]
    public void A_non_last_empty_chunk_is_never_removed()
    {
        // Ровно та защита, ради которой ReleaseChunkIfEmpty удаляет только последний чанк:
        // удаление из середины сдвинуло бы ChunkIndex в EntityRecord соседних чанков.
        // Без этого теста будущая «оптимизация» вернула бы удаление из середины молча.
        var pool = new ChunkPool();
        var a = PosVel();

        // Три чанка: [полный c0][полный c1][пустой c2]
        a.GetOrCreateChunkWithSpace(pool, out _);
        var c0 = a.Chunks[0];
        for (int i = 0; i < c0.Capacity; i++) c0.AddRow(new Entity(i, 1));

        a.GetOrCreateChunkWithSpace(pool, out _);
        var c1 = a.Chunks[1];
        for (int i = 0; i < c1.Capacity; i++) c1.AddRow(new Entity(10_000 + i, 1));

        a.GetOrCreateChunkWithSpace(pool, out int i2);   // c2 пустой
        Assert.Equal(3, a.Chunks.Count);

        // Опустошаем СРЕДНИЙ чанк и пробуем освободить его.
        while (!c1.IsEmpty) c1.SwapRemove(c1.Count - 1);
        a.ReleaseChunkIfEmpty(1, pool);

        // c1 не последний -> остаётся на месте, порядок и число чанков не меняются.
        Assert.Equal(3, a.Chunks.Count);
        Assert.Same(c0, a.Chunks[0]);
        Assert.Same(c1, a.Chunks[1]);
    }

    [Fact]
    public void Add_and_remove_edges_are_stored_and_found()
    {
        var a = PosVel(0);
        var b = Make(1, ComponentType<Position>.Id, ComponentType<Velocity>.Id, ComponentType<Health>.Id);

        Assert.False(a.TryGetAddEdge(ComponentType<Health>.Id, out _));

        a.SetAddEdge(ComponentType<Health>.Id, b);
        b.SetRemoveEdge(ComponentType<Health>.Id, a);

        Assert.True(a.TryGetAddEdge(ComponentType<Health>.Id, out var found));
        Assert.Same(b, found);
        Assert.True(b.TryGetRemoveEdge(ComponentType<Health>.Id, out var back));
        Assert.Same(a, back);
    }

    [Fact]
    public void StructuralVersion_changes_on_bump()
    {
        var a = PosVel();
        int before = a.StructuralVersion;
        a.BumpStructuralVersion();
        Assert.NotEqual(before, a.StructuralVersion);
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter ArchetypeTests`
Expected: FAIL — `Archetype` не найден.

- [ ] **Step 3: Реализовать**

`src/UvEcs/Archetype.cs`:

```csharp
namespace UvEcs;

/// <summary>
/// Точное множество компонентов. Архетипы никогда не удаляются: граф переходов
/// и кеши запросов держат на них индексы. Освобождаются чанки, а не архетипы.
/// </summary>
public sealed class Archetype
{
    private readonly List<Chunk> _chunks = new();
    private readonly Dictionary<int, Archetype> _addEdges = new();
    private readonly Dictionary<int, Archetype> _removeEdges = new();

    public int Id { get; }
    public ComponentMask Mask { get; }
    public ChunkLayout Layout { get; }
    public IReadOnlyList<Chunk> Chunks => _chunks;

    /// <summary>Меняется при каждой миграции. Итератор чанков сверяет его в Debug (§11 спеки).</summary>
    public int StructuralVersion { get; private set; }

    public Archetype(int id, ComponentMask mask, int[] sortedComponentIds)
    {
        Id = id;
        Mask = mask;
        Layout = ChunkLayout.Create(sortedComponentIds);
    }

    public int EntityCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _chunks.Count; i++) n += _chunks[i].Count;
            return n;
        }
    }

    public Chunk GetOrCreateChunkWithSpace(ChunkPool pool, out int chunkIndex)
    {
        for (int i = 0; i < _chunks.Count; i++)
        {
            if (!_chunks[i].IsFull)
            {
                chunkIndex = i;
                return _chunks[i];
            }
        }

        var chunk = new Chunk(Layout, pool.Rent());
        _chunks.Add(chunk);
        chunkIndex = _chunks.Count - 1;
        return chunk;
    }

    /// <summary>
    /// Гистерезис: один пустой чанк остаётся про запас. Иначе сущность, прыгающая
    /// через границу чанка, устраивает пинг-понг аренды на каждой операции.
    /// </summary>
    public void ReleaseChunkIfEmpty(int chunkIndex, ChunkPool pool)
    {
        var chunk = _chunks[chunkIndex];
        if (!chunk.IsEmpty) return;

        int emptyCount = 0;
        for (int i = 0; i < _chunks.Count; i++) if (_chunks[i].IsEmpty) emptyCount++;
        if (emptyCount <= 1) return;

        // Убираем только последний чанк: иначе поедут ChunkIndex в EntityRecord.
        if (chunkIndex != _chunks.Count - 1) return;

        _chunks.RemoveAt(chunkIndex);
        chunk.Reset();
        pool.Return(chunk.Buffer);
    }

    public bool TryGetAddEdge(int componentId, out Archetype target) => _addEdges.TryGetValue(componentId, out target!);
    public void SetAddEdge(int componentId, Archetype target) => _addEdges[componentId] = target;

    public bool TryGetRemoveEdge(int componentId, out Archetype target) => _removeEdges.TryGetValue(componentId, out target!);
    public void SetRemoveEdge(int componentId, Archetype target) => _removeEdges[componentId] = target;

    internal void BumpStructuralVersion() => StructuralVersion++;
}
```

- [ ] **Step 4: Прогнать**

Run: `dotnet test --filter ArchetypeTests`
Expected: PASS, 7 тестов.

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: Archetype с графом переходов и пулом чанков с гистерезисом"
```

---

## Task 10: World — создание, удаление, чтение, запись

**Files:**
- Create: `src/UvEcs/World.cs`
- Test: `tests/UvEcs.Tests/WorldTests.cs`

**Interfaces:**
- Consumes: `EntityStore`, `ChunkPool`, `Archetype`, `Chunk`, `ComponentMask`, `ComponentType<T>`.
- Produces: `sealed class World` —
  - `Entity Create()` — сущность без компонентов, попадает в пустой архетип;
  - `void Destroy(Entity e)`;
  - `bool IsAlive(Entity e)`;
  - `bool Has<T>(Entity e) where T : unmanaged, IComponent`;
  - `ref T GetRef<T>(Entity e)`, `T Get<T>(Entity e)`, `void Set<T>(Entity e, T value)`;
  - `int ArchetypeCount { get; }`, `int EntityCount { get; }`;
  - `internal Archetype ArchetypeById(int id)`;
  - `internal Archetype GetOrCreateArchetype(in ComponentMask mask)`;
  - `internal ChunkPool Pool { get; }`;
  - `internal EntityStore Entities { get; }`.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/WorldTests.cs`:

```csharp
using Xunit;

namespace UvEcs.Tests;

public class WorldTests
{
    [Fact]
    public void New_world_has_the_empty_archetype_only()
    {
        var w = new World();
        Assert.Equal(1, w.ArchetypeCount);
        Assert.Equal(0, w.EntityCount);
    }

    [Fact]
    public void Created_entity_is_alive_and_has_no_components()
    {
        var w = new World();
        var e = w.Create();

        Assert.True(w.IsAlive(e));
        Assert.False(w.Has<Position>(e));
        Assert.Equal(1, w.EntityCount);
    }

    [Fact]
    public void Destroy_removes_the_entity()
    {
        var w = new World();
        var e = w.Create();
        w.Destroy(e);

        Assert.False(w.IsAlive(e));
        Assert.Equal(0, w.EntityCount);
    }

    [Fact]
    public void Destroying_from_the_middle_keeps_other_entities_findable()
    {
        var w = new World();
        var a = w.Create();
        var b = w.Create();
        var c = w.Create();

        w.Destroy(a);   // b или c переезжает на освободившуюся строку

        Assert.True(w.IsAlive(b));
        Assert.True(w.IsAlive(c));
        Assert.Equal(2, w.EntityCount);
    }

    [Fact]
    public void Destroying_many_entities_in_random_order_keeps_records_consistent()
    {
        var w = new World();
        var entities = new List<Entity>();
        for (int i = 0; i < 500; i++) entities.Add(w.Create());

        var rng = new Random(42);
        var shuffled = entities.OrderBy(_ => rng.Next()).ToList();
        foreach (var e in shuffled.Take(250)) w.Destroy(e);

        foreach (var e in shuffled.Skip(250)) Assert.True(w.IsAlive(e), $"{e} должна быть жива");
        Assert.Equal(250, w.EntityCount);
    }

    [Fact]
    public void Get_on_absent_component_throws()
    {
        var w = new World();
        var e = w.Create();
        Assert.Throws<InvalidOperationException>(() => w.Get<Position>(e));
    }

    [Fact]
    public void Get_on_dead_entity_throws()
    {
        var w = new World();
        var e = w.Create();
        w.Destroy(e);
        Assert.Throws<InvalidOperationException>(() => w.Has<Position>(e));
    }

    [Fact]
    public void Destroying_the_middle_fixes_the_moved_entitys_row()
    {
        // Единственный тест, который отличает починенный Row от протухшего.
        // Удали строку movedRec.Row = row в World, и упадёт только он: IsAlive и
        // EntityCount смотрят на Version, а не на Row. Читаем Row напрямую —
        // World.Entities открыт internal ровно для этого.
        var w = new World();
        var a = w.Create();
        var b = w.Create();
        var c = w.Create();            // строки 0, 1, 2 в одном чанке

        w.Destroy(a);                  // последняя строка (c) переезжает в строку 0

        Assert.Equal(0, w.Entities.GetRecord(c).Row);   // Row починен
        Assert.Equal(1, w.Entities.GetRecord(b).Row);   // соседа не тронули
        Assert.True(w.IsAlive(b));
        Assert.True(w.IsAlive(c));
    }

    [Fact]
    public void Same_component_set_produces_the_same_archetype()
    {
        var w = new World();
        var mask = new ComponentMask();
        mask.Set(ComponentType<Position>.Id);

        var first = w.GetOrCreateArchetype(in mask);
        var second = w.GetOrCreateArchetype(in mask);

        Assert.Same(first, second);
        Assert.Equal(2, w.ArchetypeCount);   // пустой + этот
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter WorldTests`
Expected: FAIL — `World` не найден.

- [ ] **Step 3: Реализовать**

`src/UvEcs/World.cs`:

```csharp
namespace UvEcs;

public sealed partial class World
{
    private readonly List<Archetype> _archetypes = new();
    private readonly Dictionary<ComponentMask, Archetype> _byMask = new();

    internal EntityStore Entities { get; } = new();
    internal ChunkPool Pool { get; } = new();

    public int ArchetypeCount => _archetypes.Count;
    public int EntityCount => Entities.AliveCount;

    public World()
    {
        GetOrCreateArchetype(new ComponentMask());   // архетип без компонентов, Id == 0
    }

    internal Archetype ArchetypeById(int id) => _archetypes[id];

    private static int[] IdsFromMask(in ComponentMask mask)
    {
        var ids = new int[mask.PopCount()];
        int n = 0;
        for (int i = 0; i < ComponentMask.Capacity; i++)
            if (mask.Get(i)) ids[n++] = i;
        return ids;   // уже отсортированы по возрастанию
    }

    internal Archetype GetOrCreateArchetype(in ComponentMask mask)
    {
        if (_byMask.TryGetValue(mask, out var existing)) return existing;

        var archetype = new Archetype(_archetypes.Count, mask, IdsFromMask(in mask));
        _archetypes.Add(archetype);
        _byMask[mask] = archetype;
        return archetype;
    }

    public Entity Create()
    {
        var e = Entities.Create();
        var archetype = _archetypes[0];
        var chunk = archetype.GetOrCreateChunkWithSpace(Pool, out int chunkIndex);
        int row = chunk.AddRow(e);

        ref var rec = ref Entities.GetRecord(e);
        rec.ArchetypeId = archetype.Id;
        rec.ChunkIndex = chunkIndex;
        rec.Row = row;
        archetype.BumpStructuralVersion();
        return e;
    }

    public bool IsAlive(Entity e) => Entities.IsAlive(e);

    public void Destroy(Entity e)
    {
        ref var rec = ref Entities.GetRecord(e);
        RemoveFromChunk(rec.ArchetypeId, rec.ChunkIndex, rec.Row);
        Entities.Destroy(e);
    }

    /// <summary>Swap-remove + починка записи переехавшей сущности.</summary>
    private void RemoveFromChunk(int archetypeId, int chunkIndex, int row)
    {
        var archetype = _archetypes[archetypeId];
        var chunk = archetype.Chunks[chunkIndex];

        Entity moved = chunk.SwapRemove(row);
        if (!moved.IsNull)
        {
            ref var movedRec = ref Entities.RecordRefUnchecked(moved.Id);
            movedRec.Row = row;   // архетип и чанк те же
        }

        archetype.ReleaseChunkIfEmpty(chunkIndex, Pool);
        archetype.BumpStructuralVersion();
    }

    public bool Has<T>(Entity e) where T : unmanaged, IComponent
    {
        ref var rec = ref Entities.GetRecord(e);
        return _archetypes[rec.ArchetypeId].Mask.Get(ComponentType<T>.Id);
    }

    public ref T GetRef<T>(Entity e) where T : unmanaged, IComponent
    {
        ref var rec = ref Entities.GetRecord(e);
        var archetype = _archetypes[rec.ArchetypeId];
        if (!archetype.Mask.Get(ComponentType<T>.Id))
            throw new InvalidOperationException($"У {e} нет компонента {typeof(T).Name}.");
        return ref archetype.Chunks[rec.ChunkIndex].GetRef<T>(rec.Row);
    }

    public T Get<T>(Entity e) where T : unmanaged, IComponent => GetRef<T>(e);

    public void Set<T>(Entity e, T value) where T : unmanaged, IComponent => GetRef<T>(e) = value;
}
```

- [ ] **Step 4: Прогнать**

Run: `dotnet test --filter WorldTests`
Expected: PASS, 8 тестов.

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: World — создание, удаление, доступ к компонентам, реестр архетипов"
```

---

## Task 11: Add и Remove с миграцией между архетипами

**Files:**
- Create: `src/UvEcs/World.Structural.cs`
- Test: `tests/UvEcs.Tests/StructuralTests.cs`

**Interfaces:**
- Consumes: всё из Task 10, плюс `Archetype.TryGetAddEdge/SetAddEdge/TryGetRemoveEdge/SetRemoveEdge`, `Chunk.CopyRowTo`.
- Produces: на `World` —
  - `void Add<T>(Entity e, T value) where T : unmanaged, IComponent`;
  - `void Remove<T>(Entity e) where T : unmanaged, IComponent`;
  - `internal void Migrate(Entity e, ref EntityRecord rec, Archetype from, Archetype to)`.

**Механика (§5 спеки):** миграция — это явные операции. Строка вставляется в чанк нового архетипа, общие колонки и маска тегов копируются, затем строка выбывает из старого чанка через swap-remove, и запись переехавшей сущности чинится. Сущность в каждый момент находится ровно в одном архетипе.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/StructuralTests.cs`:

```csharp
using Xunit;

namespace UvEcs.Tests;

public class StructuralTests
{
    [Fact]
    public void Add_puts_the_component_and_the_value()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 1, Y = 2, Z = 3 });

        Assert.True(w.Has<Position>(e));
        Assert.Equal(2, w.Get<Position>(e).Y);
    }

    [Fact]
    public void Add_of_existing_component_overwrites_without_migrating()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 1 });
        int archetypes = w.ArchetypeCount;

        w.Add(e, new Position { X = 5 });

        Assert.Equal(5, w.Get<Position>(e).X);
        Assert.Equal(archetypes, w.ArchetypeCount);
    }

    [Fact]
    public void Adding_a_second_component_preserves_the_first()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 7, Y = 8, Z = 9 });
        w.Add(e, new Velocity { X = 4 });

        Assert.Equal(7, w.Get<Position>(e).X);
        Assert.Equal(9, w.Get<Position>(e).Z);
        Assert.Equal(4, w.Get<Velocity>(e).X);
    }

    [Fact]
    public void Remove_drops_the_component_and_keeps_the_rest()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 7 });
        w.Add(e, new Velocity { X = 4 });

        w.Remove<Velocity>(e);

        Assert.False(w.Has<Velocity>(e));
        Assert.True(w.Has<Position>(e));
        Assert.Equal(7, w.Get<Position>(e).X);
    }

    [Fact]
    public void Removing_an_absent_component_throws()
    {
        var w = new World();
        var e = w.Create();
        Assert.Throws<InvalidOperationException>(() => w.Remove<Position>(e));
    }

    [Fact]
    public void Migration_fixes_the_record_of_the_entity_that_moved_into_the_hole()
    {
        var w = new World();
        var a = w.Create();
        var b = w.Create();
        w.Add(a, new Position { X = 1 });
        w.Add(b, new Position { X = 2 });

        // a уезжает в архетип {Position,Velocity}; b должна остаться читаемой
        w.Add(a, new Velocity { X = 9 });

        Assert.Equal(2, w.Get<Position>(b).X);
        Assert.Equal(1, w.Get<Position>(a).X);
        Assert.Equal(9, w.Get<Velocity>(a).X);
    }

    [Fact]
    public void Tags_survive_migration()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 1 });

        // ставим тег напрямую в чанке (SetTag появится в Task 12)
        ref var rec = ref w.Entities.GetRecord(e);
        w.ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex].TagAt(rec.Row) = TagType<Stunned>.Bit;

        w.Add(e, new Velocity { X = 1 });

        ref var after = ref w.Entities.GetRecord(e);
        var chunk = w.ArchetypeById(after.ArchetypeId).Chunks[after.ChunkIndex];
        Assert.True(chunk.TagAt(after.Row).HasAll(TagType<Stunned>.Bit));
    }

    [Fact]
    public void Archetype_graph_stops_growing_after_warmup()
    {
        var w = new World();
        for (int i = 0; i < 100; i++)
        {
            var e = w.Create();
            w.Add(e, new Position());
            w.Add(e, new Velocity());
        }

        int afterWarmup = w.ArchetypeCount;   // {}, {P}, {P,V}

        for (int i = 0; i < 100; i++)
        {
            var e = w.Create();
            w.Add(e, new Position());
            w.Add(e, new Velocity());
        }

        Assert.Equal(afterWarmup, w.ArchetypeCount);
        Assert.Equal(3, afterWarmup);
    }

    [Fact]
    public void Add_then_remove_returns_to_the_original_archetype()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position { X = 3 });
        int posOnly = w.Entities.GetRecord(e).ArchetypeId;

        w.Add(e, new Velocity());
        w.Remove<Velocity>(e);

        Assert.Equal(posOnly, w.Entities.GetRecord(e).ArchetypeId);
        Assert.Equal(3, w.Get<Position>(e).X);
    }

    [Fact]
    public void Structural_version_bumps_on_migration()
    {
        var w = new World();
        var e = w.Create();
        var empty = w.ArchetypeById(0);
        int before = empty.StructuralVersion;

        w.Add(e, new Position());

        Assert.NotEqual(before, empty.StructuralVersion);
    }

    [Fact]
    public void Many_entities_migrating_keep_all_data_intact()
    {
        var w = new World();
        var entities = new List<Entity>();
        for (int i = 0; i < 1000; i++)
        {
            var e = w.Create();
            w.Add(e, new Position { X = i });
            entities.Add(e);
        }

        for (int i = 0; i < entities.Count; i += 2)
            w.Add(entities[i], new Velocity { X = i * 10 });

        for (int i = 0; i < entities.Count; i++)
        {
            Assert.Equal(i, w.Get<Position>(entities[i]).X);
            if (i % 2 == 0) Assert.Equal(i * 10, w.Get<Velocity>(entities[i]).X);
            else Assert.False(w.Has<Velocity>(entities[i]));
        }
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter StructuralTests`
Expected: FAIL — `Add` / `Remove` не найдены.

- [ ] **Step 3: Реализовать**

`src/UvEcs/World.Structural.cs`:

```csharp
namespace UvEcs;

public sealed partial class World
{
    public void Add<T>(Entity e, T value) where T : unmanaged, IComponent
    {
        ref var rec = ref Entities.GetRecord(e);
        var from = ArchetypeById(rec.ArchetypeId);
        int componentId = ComponentType<T>.Id;

        if (from.Mask.Get(componentId))
        {
            from.Chunks[rec.ChunkIndex].GetRef<T>(rec.Row) = value;   // уже есть — просто перезапись
            return;
        }

        if (!from.TryGetAddEdge(componentId, out var to))
        {
            var mask = from.Mask;
            mask.Set(componentId);
            to = GetOrCreateArchetype(in mask);
            from.SetAddEdge(componentId, to);
            to.SetRemoveEdge(componentId, from);
        }

        Migrate(e, ref rec, from, to);
        ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex].GetRef<T>(rec.Row) = value;
    }

    public void Remove<T>(Entity e) where T : unmanaged, IComponent
    {
        ref var rec = ref Entities.GetRecord(e);
        var from = ArchetypeById(rec.ArchetypeId);
        int componentId = ComponentType<T>.Id;

        if (!from.Mask.Get(componentId))
            throw new InvalidOperationException($"У {e} нет компонента {typeof(T).Name}.");

        if (!from.TryGetRemoveEdge(componentId, out var to))
        {
            var mask = from.Mask;
            mask.Unset(componentId);
            to = GetOrCreateArchetype(in mask);
            from.SetRemoveEdge(componentId, to);
            to.SetAddEdge(componentId, from);
        }

        Migrate(e, ref rec, from, to);
    }

    /// <summary>
    /// Вставляем в приёмник, копируем общие колонки и теги, затем swap-remove из источника
    /// и чиним запись сущности, переехавшей на освободившуюся строку.
    /// </summary>
    internal void Migrate(Entity e, ref EntityRecord rec, Archetype from, Archetype to)
    {
        var fromChunk = from.Chunks[rec.ChunkIndex];
        int fromRow = rec.Row;
        int fromChunkIndex = rec.ChunkIndex;

        var toChunk = to.GetOrCreateChunkWithSpace(Pool, out int toChunkIndex);
        int toRow = toChunk.AddRow(e);

        fromChunk.CopyRowTo(fromRow, toChunk, toRow);
        toChunk.TagUnion = toChunk.TagUnion.Or(toChunk.TagAt(toRow));

        Entity moved = fromChunk.SwapRemove(fromRow);
        if (!moved.IsNull)
            Entities.RecordRefUnchecked(moved.Id).Row = fromRow;

        from.ReleaseChunkIfEmpty(fromChunkIndex, Pool);

        rec.ArchetypeId = to.Id;
        rec.ChunkIndex = toChunkIndex;
        rec.Row = toRow;

        from.BumpStructuralVersion();
        to.BumpStructuralVersion();
    }
}
```

- [ ] **Step 4: Прогнать**

Run: `dotnet test --filter StructuralTests`
Expected: PASS, 11 тестов.

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: Add/Remove с миграцией по графу переходов и починкой записей"
```

---

## Task 12: Теги

**Files:**
- Create: `src/UvEcs/World.Tags.cs`
- Test: `tests/UvEcs.Tests/TagTests.cs`

**Interfaces:**
- Consumes: `World`, `Chunk.TagAt`, `Chunk.TagUnion`, `Chunk.TagsDirty`, `TagType<T>.Bit`.
- Produces: на `World` —
  - `void SetTag<T>(Entity e) where T : unmanaged, ITag`;
  - `void UnsetTag<T>(Entity e) where T : unmanaged, ITag`;
  - `bool HasTag<T>(Entity e) where T : unmanaged, ITag`;
  - `void RecomputeTagUnions()` — вызывается в конце тика.

**Механика (§5 спеки):** тег — бит в колонке чанка, архетип не меняется, миграции нет. `TagUnion` расширяется при установке бита и **не сужается** при снятии: оценка консервативна, из-за неё мы можем лишь не пропустить чанк, который могли бы. Точное значение восстанавливает `RecomputeTagUnions` для чанков с `TagsDirty`.

`TagIntersection` в дизайне отсутствует намеренно — см. §5 спеки.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/TagTests.cs`:

```csharp
using Xunit;

namespace UvEcs.Tests;

public class TagTests
{
    private static Chunk ChunkOf(World w, Entity e)
    {
        ref var rec = ref w.Entities.GetRecord(e);
        return w.ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex];
    }

    [Fact]
    public void Tag_round_trips()
    {
        var w = new World();
        var e = w.Create();

        Assert.False(w.HasTag<Stunned>(e));
        w.SetTag<Stunned>(e);
        Assert.True(w.HasTag<Stunned>(e));
        w.UnsetTag<Stunned>(e);
        Assert.False(w.HasTag<Stunned>(e));
    }

    [Fact]
    public void Setting_a_tag_twice_is_idempotent()
    {
        var w = new World();
        var e = w.Create();
        w.SetTag<Stunned>(e);
        w.SetTag<Stunned>(e);
        Assert.True(w.HasTag<Stunned>(e));
        w.UnsetTag<Stunned>(e);
        Assert.False(w.HasTag<Stunned>(e));
    }

    [Fact]
    public void Tags_do_not_change_the_archetype()
    {
        var w = new World();
        var e = w.Create();
        w.Add(e, new Position());
        int archetypes = w.ArchetypeCount;
        int archetypeId = w.Entities.GetRecord(e).ArchetypeId;

        w.SetTag<Stunned>(e);
        w.SetTag<InCombat>(e);

        Assert.Equal(archetypes, w.ArchetypeCount);
        Assert.Equal(archetypeId, w.Entities.GetRecord(e).ArchetypeId);
    }

    [Fact]
    public void Tags_are_independent_of_each_other()
    {
        var w = new World();
        var e = w.Create();
        w.SetTag<Stunned>(e);
        w.SetTag<Dead>(e);
        w.UnsetTag<Stunned>(e);

        Assert.False(w.HasTag<Stunned>(e));
        Assert.True(w.HasTag<Dead>(e));
    }

    [Fact]
    public void TagUnion_grows_when_a_tag_is_set()
    {
        var w = new World();
        var e = w.Create();
        var chunk = ChunkOf(w, e);
        Assert.True(chunk.TagUnion.IsEmpty);

        w.SetTag<InCombat>(e);
        Assert.True(chunk.TagUnion.HasAll(TagType<InCombat>.Bit));
        Assert.True(chunk.TagsDirty);
    }

    [Fact]
    public void TagUnion_does_not_shrink_on_unset_it_is_conservative()
    {
        var w = new World();
        var e = w.Create();
        var chunk = ChunkOf(w, e);

        w.SetTag<InCombat>(e);
        w.UnsetTag<InCombat>(e);

        // консервативность: шире правды — безопасно, мы лишь не пропустим чанк
        Assert.True(chunk.TagUnion.HasAll(TagType<InCombat>.Bit));
        Assert.False(w.HasTag<InCombat>(e));
    }

    [Fact]
    public void RecomputeTagUnions_restores_the_exact_value()
    {
        var w = new World();
        var e = w.Create();
        var chunk = ChunkOf(w, e);

        w.SetTag<InCombat>(e);
        w.UnsetTag<InCombat>(e);
        w.RecomputeTagUnions();

        Assert.True(chunk.TagUnion.IsEmpty);
        Assert.False(chunk.TagsDirty);
    }

    [Fact]
    public void RecomputeTagUnions_keeps_tags_that_are_still_set()
    {
        var w = new World();
        var a = w.Create();
        var b = w.Create();
        var chunk = ChunkOf(w, a);

        w.SetTag<InCombat>(a);
        w.SetTag<Dead>(b);
        w.UnsetTag<InCombat>(a);
        w.RecomputeTagUnions();

        Assert.False(chunk.TagUnion.HasAny(TagType<InCombat>.Bit));
        Assert.True(chunk.TagUnion.HasAll(TagType<Dead>.Bit));
    }

    [Fact]
    public void Tag_survives_component_migration()
    {
        var w = new World();
        var e = w.Create();
        w.SetTag<Dead>(e);
        w.Add(e, new Position());
        Assert.True(w.HasTag<Dead>(e));
    }

    [Fact]
    public void Tag_of_a_swapped_entity_follows_it()
    {
        var w = new World();
        var a = w.Create();
        var b = w.Create();
        w.SetTag<Dead>(b);

        w.Destroy(a);   // b переезжает на строку a

        Assert.True(w.HasTag<Dead>(b));
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter TagTests`
Expected: FAIL — `SetTag` не найден.

- [ ] **Step 3: Реализовать**

`src/UvEcs/World.Tags.cs`:

```csharp
namespace UvEcs;

public sealed partial class World
{
    /// <summary>Не структурная операция: архетип не меняется, миграции нет.</summary>
    public void SetTag<T>(Entity e) where T : unmanaged, ITag
    {
        ref var rec = ref Entities.GetRecord(e);
        var chunk = ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex];

        ref var mask = ref chunk.TagAt(rec.Row);
        mask = mask.Or(TagType<T>.Bit);

        chunk.TagUnion = chunk.TagUnion.Or(TagType<T>.Bit);   // расширяем — всегда корректно
        chunk.TagsDirty = true;
    }

    /// <remarks>TagUnion намеренно не сужается: оценка консервативна (§5 спеки).</remarks>
    public void UnsetTag<T>(Entity e) where T : unmanaged, ITag
    {
        ref var rec = ref Entities.GetRecord(e);
        var chunk = ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex];

        ref var mask = ref chunk.TagAt(rec.Row);
        mask = mask.AndNot(TagType<T>.Bit);

        chunk.TagsDirty = true;
    }

    public bool HasTag<T>(Entity e) where T : unmanaged, ITag
    {
        ref var rec = ref Entities.GetRecord(e);
        var chunk = ArchetypeById(rec.ArchetypeId).Chunks[rec.ChunkIndex];
        return chunk.TagAt(rec.Row).HasAll(TagType<T>.Bit);
    }

    /// <summary>Вызывается в конце тика. OR по колонке, микросекунды.</summary>
    public void RecomputeTagUnions()
    {
        for (int a = 0; a < ArchetypeCount; a++)
        {
            var archetype = ArchetypeById(a);
            for (int c = 0; c < archetype.Chunks.Count; c++)
            {
                var chunk = archetype.Chunks[c];
                if (!chunk.TagsDirty) continue;

                var union = TagMask.Empty;
                var tags = chunk.Tags;
                for (int i = 0; i < tags.Length; i++) union = union.Or(tags[i]);

                chunk.TagUnion = union;
                chunk.TagsDirty = false;
            }
        }
    }
}
```

- [ ] **Step 4: Прогнать**

Run: `dotnet test --filter TagTests`
Expected: PASS, 10 тестов.

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: теги как бит в маске чанка, с консервативным TagUnion и пересчётом"
```

---

## Task 13: SparseSet

**Files:**
- Create: `src/UvEcs/SparseSet.cs`
- Test: `tests/UvEcs.Tests/SparseSetTests.cs`

**Interfaces:**
- Consumes: `ISparse`.
- Produces: `sealed class SparseSet<T> where T : unmanaged, ISparse` —
  - `int Count { get; }`;
  - `bool Has(int entityId)`;
  - `void Add(int entityId, T value)` (бросает, если уже есть);
  - `bool Remove(int entityId)` (возвращает `false`, если не было);
  - `ref T GetRef(int entityId)` (бросает, если нет);
  - `ReadOnlySpan<int> Entities { get; }` — плотный список носителей;
  - `Span<T> Values { get; }` — параллелен `Entities`.

**Обоснование (§6 спеки):** sparse-набор умеет быть драйвером итерации. Индексируется по `entityId`, поэтому миграция архетипа его **не трогает вообще** — порядка обновления не существует, потому что обновления нет.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/SparseSetTests.cs`:

```csharp
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
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter SparseSetTests`
Expected: FAIL — `SparseSet<>` не найден.

- [ ] **Step 3: Реализовать**

`src/UvEcs/SparseSet.cs`:

```csharp
namespace UvEcs;

/// <summary>
/// Редкий компонент с данными. Индексируется по entityId, поэтому миграция
/// архетипа его не трогает: обновлять нечего (§5 спеки).
/// Окупается, пока носителей меньше ~25% кандидатов запроса (§6 спеки).
/// </summary>
public sealed class SparseSet<T> where T : unmanaged, ISparse
{
    private const int Absent = -1;

    private int[] _sparse = new int[64];
    private int[] _dense = new int[16];
    private T[] _values = new T[16];
    private int _count;

    public SparseSet() => Array.Fill(_sparse, Absent);

    public int Count => _count;

    public ReadOnlySpan<int> Entities => _dense.AsSpan(0, _count);
    public Span<T> Values => _values.AsSpan(0, _count);

    public bool Has(int entityId)
        => (uint)entityId < (uint)_sparse.Length && _sparse[entityId] != Absent;

    public void Add(int entityId, T value)
    {
        if (Has(entityId)) throw new InvalidOperationException($"Сущность {entityId} уже в наборе.");

        EnsureSparse(entityId);
        if (_count == _dense.Length)
        {
            Array.Resize(ref _dense, _dense.Length * 2);
            Array.Resize(ref _values, _values.Length * 2);
        }

        _dense[_count] = entityId;
        _values[_count] = value;
        _sparse[entityId] = _count;
        _count++;
    }

    public bool Remove(int entityId)
    {
        if (!Has(entityId)) return false;

        int denseIndex = _sparse[entityId];
        int last = _count - 1;

        if (denseIndex != last)
        {
            int movedEntity = _dense[last];
            _dense[denseIndex] = movedEntity;
            _values[denseIndex] = _values[last];
            _sparse[movedEntity] = denseIndex;
        }

        _sparse[entityId] = Absent;
        _count--;
        return true;
    }

    public ref T GetRef(int entityId)
    {
        if (!Has(entityId)) throw new InvalidOperationException($"Сущности {entityId} нет в наборе.");
        return ref _values[_sparse[entityId]];
    }

    private void EnsureSparse(int entityId)
    {
        if (entityId < _sparse.Length) return;

        int newSize = _sparse.Length;
        while (newSize <= entityId) newSize *= 2;

        int oldSize = _sparse.Length;
        Array.Resize(ref _sparse, newSize);
        Array.Fill(_sparse, Absent, oldSize, newSize - oldSize);
    }
}
```

- [ ] **Step 4: Прогнать**

Run: `dotnet test --filter SparseSetTests`
Expected: PASS, 11 тестов.

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "feat: SparseSet с плотными массивами и swap-remove"
```

---

## Task 14: Query и чанковая итерация

**Files:**
- Create: `src/UvEcs/ChunkView.cs`
- Create: `src/UvEcs/Query.cs`
- Create: `src/UvEcs/QueryBuilder.cs`
- Test: `tests/UvEcs.Tests/QueryTests.cs`

**Interfaces:**
- Consumes: `World.ArchetypeCount`, `World.ArchetypeById`, `Archetype.Mask`, `Archetype.Chunks`, `Chunk.TagUnion`, `Chunk.Tags`.
- Produces:
  - `readonly ref struct ChunkView` — `int Count`, `bool AllRowsPass`, `bool Passes(int row)`, `ReadOnlySpan<Entity> Entities`, `ReadOnlySpan<T> GetRead<T>()`, `Span<T> GetWrite<T>()`, `ref T GetRef<T>(int row)`.
  - `sealed class Query` — `ChunkEnumerator GetEnumerator()`, `int MatchedArchetypeCount { get; }`, `internal void Refresh()`.
  - `struct ChunkEnumerator` — `bool MoveNext()`, `ChunkView Current { get; }`.
  - `sealed class QueryBuilder` — `All<T>()`, `All<T1,T2>()`, `All<T1,T2,T3>()`, `None<T>()`, `WithTag<T>()`, `WithoutTag<T>()`, `Query Build()`.
  - На `World`: `QueryBuilder Query()`.

**Кеш архетипов (§6 спеки):** инвалидируется по счётчику `World.ArchetypeCount`. Если счётчик вырос — доматчиваются **только новые** архетипы с запомненной позиции. Полного пересканирования не бывает.

**Пропуск чанка по тегам:** если `!chunk.TagUnion.HasAll(tagAll)` — требуемого тега нет ни у кого, чанк пропускается целиком. `TagUnion` консервативна (шире правды), поэтому ложных пропусков не бывает.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/QueryTests.cs`:

```csharp
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

    private static List<Entity> PassingEntities(Query q)
    {
        var seen = new List<Entity>();
        foreach (var chunk in q)
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Passes(i)) seen.Add(chunk.Entities[i]);
        return seen;
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
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter QueryTests`
Expected: FAIL — `Query()` не найден.

- [ ] **Step 3: Реализовать ChunkView**

`src/UvEcs/ChunkView.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace UvEcs;

/// <summary>То, что видит пользователь внутри foreach по запросу.</summary>
public readonly ref struct ChunkView
{
    private readonly Chunk _chunk;
    private readonly TagMask _tagAll;
    private readonly TagMask _tagNone;

    internal ChunkView(Chunk chunk, TagMask tagAll, TagMask tagNone)
    {
        _chunk = chunk;
        _tagAll = tagAll;
        _tagNone = tagNone;
        AllRowsPass = tagAll.IsEmpty && chunk.TagUnion.HasNone(tagNone);
    }

    public int Count => _chunk.Count;

    /// <summary>Все строки проходят фильтр — проверять построчно не нужно.</summary>
    public bool AllRowsPass { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Passes(int row)
    {
        if (AllRowsPass) return true;
        var tags = _chunk.TagAt(row);
        return tags.HasAll(_tagAll) && tags.HasNone(_tagNone);
    }

    public ReadOnlySpan<Entity> Entities => _chunk.Entities;

    public ReadOnlySpan<T> GetRead<T>() where T : unmanaged, IComponent => _chunk.GetRead<T>();

    public Span<T> GetWrite<T>() where T : unmanaged, IComponent => _chunk.GetWrite<T>();

    public ref T GetRef<T>(int row) where T : unmanaged, IComponent => ref _chunk.GetRef<T>(row);
}
```

- [ ] **Step 4: Реализовать Query и QueryBuilder**

`src/UvEcs/Query.cs`:

```csharp
namespace UvEcs;

public sealed class Query
{
    private readonly World _world;
    private readonly List<Archetype> _matched = new();
    private int _scannedArchetypes;

    internal ComponentMask All;
    internal ComponentMask None;
    internal TagMask TagAll;
    internal TagMask TagNone;
    internal int[] SparseAll = Array.Empty<int>();

    internal Query(World world) => _world = world;

    public int MatchedArchetypeCount
    {
        get { Refresh(); return _matched.Count; }
    }

    /// <summary>Доматчивает только архетипы, появившиеся с прошлого раза (§6 спеки).</summary>
    internal void Refresh()
    {
        int total = _world.ArchetypeCount;
        for (; _scannedArchetypes < total; _scannedArchetypes++)
        {
            var archetype = _world.ArchetypeById(_scannedArchetypes);
            if (archetype.Mask.HasAll(in All) && archetype.Mask.HasNone(in None))
                _matched.Add(archetype);
        }
    }

    public ChunkEnumerator GetEnumerator()
    {
        if (SparseAll.Length > 0)
            throw new InvalidOperationException(
                "Запрос с AllSparse<T>() итерируется через BySparse(): носители размазаны по чанкам, " +
                "Span на них не натянуть (§6 спеки).");

        Refresh();
        return new ChunkEnumerator(this, _matched);
    }

    internal List<Archetype> MatchedArchetypes { get { Refresh(); return _matched; } }
    internal World World => _world;
}

public struct ChunkEnumerator
{
    private readonly Query _query;
    private readonly List<Archetype> _archetypes;
    private int _archetypeIndex;
    private int _chunkIndex;
    private Chunk? _current;

    internal ChunkEnumerator(Query query, List<Archetype> archetypes)
    {
        _query = query;
        _archetypes = archetypes;
        _archetypeIndex = 0;
        _chunkIndex = -1;
        _current = null;
    }

    public ChunkView Current => new(_current!, _query.TagAll, _query.TagNone);

    public bool MoveNext()
    {
        while (_archetypeIndex < _archetypes.Count)
        {
            var archetype = _archetypes[_archetypeIndex];
            _chunkIndex++;

            if (_chunkIndex >= archetype.Chunks.Count)
            {
                _archetypeIndex++;
                _chunkIndex = -1;
                continue;
            }

            var chunk = archetype.Chunks[_chunkIndex];
            if (chunk.IsEmpty) continue;

            // требуемого тега нет ни у кого в этом чанке — пропускаем целиком
            if (!chunk.TagUnion.HasAll(_query.TagAll)) continue;

            _current = chunk;
            return true;
        }
        return false;
    }
}
```

`src/UvEcs/QueryBuilder.cs`:

```csharp
namespace UvEcs;

public sealed class QueryBuilder
{
    private readonly Query _query;
    private readonly List<int> _sparseAll = new();

    internal QueryBuilder(World world) => _query = new Query(world);

    public QueryBuilder All<T>() where T : unmanaged, IComponent
    {
        _query.All.Set(ComponentType<T>.Id);
        return this;
    }

    public QueryBuilder All<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        => All<T1>().All<T2>();

    public QueryBuilder All<T1, T2, T3>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        => All<T1>().All<T2>().All<T3>();

    public QueryBuilder None<T>() where T : unmanaged, IComponent
    {
        _query.None.Set(ComponentType<T>.Id);
        return this;
    }

    public QueryBuilder WithTag<T>() where T : unmanaged, ITag
    {
        _query.TagAll = _query.TagAll.Or(TagType<T>.Bit);
        return this;
    }

    public QueryBuilder WithoutTag<T>() where T : unmanaged, ITag
    {
        _query.TagNone = _query.TagNone.Or(TagType<T>.Bit);
        return this;
    }

    /// <summary>Обязательный sparse-компонент. Драйвером итерации становится sparse-набор.</summary>
    public QueryBuilder AllSparse<T>() where T : unmanaged, ISparse
    {
        _sparseAll.Add(SparseType<T>.Id);
        return this;
    }

    public Query Build()
    {
        _query.SparseAll = _sparseAll.ToArray();
        return _query;
    }
}
```

Добавить в `src/UvEcs/World.cs` (в тело класса):

```csharp
    public QueryBuilder Query() => new(this);
```

- [ ] **Step 5: Прогнать**

Run: `dotnet test --filter QueryTests`
Expected: PASS, 11 тестов.

- [ ] **Step 6: Коммит**

```bash
git add -A
git commit -m "feat: Query с инкрементальным кешем архетипов и чанковой итерацией"
```

---

## Task 15: Sparse API мира и sparse-драйвер запроса

**Files:**
- Create: `src/UvEcs/World.Sparse.cs`
- Modify: `src/UvEcs/Query.cs` (добавить `BySparse()` и `SparseHit`)
- Test: `tests/UvEcs.Tests/SparseDriverTests.cs`

**Interfaces:**
- Consumes: `SparseSet<T>`, `SparseType<T>.Id`, `Query.SparseAll`, `EntityStore.RecordRefUnchecked`.
- Produces:
  - На `World`: `void AddSparse<T>(Entity e, T value)`, `bool RemoveSparse<T>(Entity e)`, `bool HasSparse<T>(Entity e)`, `ref T GetSparseRef<T>(Entity e)`, `SparseSet<T> SparseSetOf<T>()`, `internal object? SparseSetById(int sparseId)` — все `where T : unmanaged, ISparse`.
  - На `Query`: `SparseEnumerable BySparse()`; `readonly struct SparseHit { Entity Entity; Chunk Chunk; int Row; }`.
  - `interface ISparseSetView { int Count; ReadOnlySpan<int> Entities; bool Has(int entityId); }` — реализуется `SparseSet<T>`, нужен для выбора драйвера без generic-диспетчеризации.

**Драйвер (§6 спеки):** если запрос требует sparse-компонент, драйвером становится **наименьший** из обязательных наборов. Обходим его носителей, для каждого берём чанк и строку из `EntityRecord`. Порог применимости: sparse окупается до ~25% носителей от кандидатов запроса.

- [ ] **Step 1: Написать падающий тест**

`tests/UvEcs.Tests/SparseDriverTests.cs`:

```csharp
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
        w.Destroy(e);   // sparse-набор о смерти не знает — драйвер обязан проверять

        var q = w.Query().All<Position>().AllSparse<GuildBuff>().Build();
        int seen = 0;
        foreach (var _ in q.BySparse()) seen++;
        Assert.Equal(0, seen);
    }
}
```

- [ ] **Step 2: Прогнать, убедиться что падает**

Run: `dotnet test --filter SparseDriverTests`
Expected: FAIL — `AddSparse` не найден.

- [ ] **Step 3: Добавить ISparseSetView в `src/UvEcs/SparseSet.cs`**

Вставить перед объявлением `SparseSet<T>`:

```csharp
/// <summary>
/// Необобщённое окно в sparse-набор. Нужно в двух местах, где T неизвестен:
/// выбор драйвера итерации и очистка наборов при удалении сущности.
/// </summary>
public interface ISparseSetView
{
    int Count { get; }
    ReadOnlySpan<int> Entities { get; }
    bool Has(int entityId);
    bool Remove(int entityId);
}
```

И изменить объявление класса на:

```csharp
public sealed class SparseSet<T> : ISparseSetView where T : unmanaged, ISparse
```

Существующие `Count`, `Entities`, `Has`, `Remove` уже подходят под интерфейс — менять их тела не нужно.

- [ ] **Step 4: Реализовать sparse API мира**

`src/UvEcs/World.Sparse.cs`:

```csharp
namespace UvEcs;

public sealed partial class World
{
    private object?[] _sparseSets = new object?[16];

    public SparseSet<T> SparseSetOf<T>() where T : unmanaged, ISparse
    {
        int id = SparseType<T>.Id;
        if (id >= _sparseSets.Length) Array.Resize(ref _sparseSets, Math.Max(id + 1, _sparseSets.Length * 2));
        return (SparseSet<T>)(_sparseSets[id] ??= new SparseSet<T>());
    }

    internal ISparseSetView? SparseSetById(int sparseId)
        => sparseId < _sparseSets.Length ? (ISparseSetView?)_sparseSets[sparseId] : null;

    /// <summary>Не структурная операция: архетип не меняется.</summary>
    public void AddSparse<T>(Entity e, T value) where T : unmanaged, ISparse
    {
        _ = Entities.GetRecord(e);   // проверка живости
        SparseSetOf<T>().Add(e.Id, value);
    }

    public bool RemoveSparse<T>(Entity e) where T : unmanaged, ISparse
    {
        _ = Entities.GetRecord(e);
        return SparseSetOf<T>().Remove(e.Id);
    }

    public bool HasSparse<T>(Entity e) where T : unmanaged, ISparse
    {
        _ = Entities.GetRecord(e);
        return SparseSetOf<T>().Has(e.Id);
    }

    public ref T GetSparseRef<T>(Entity e) where T : unmanaged, ISparse
    {
        _ = Entities.GetRecord(e);
        return ref SparseSetOf<T>().GetRef(e.Id);
    }

    /// <summary>
    /// Вызывается из Destroy. Без этого EntityStore переиспользует Id, и новая сущность
    /// унаследует sparse-компоненты покойника.
    /// </summary>
    internal void RemoveFromAllSparseSets(int entityId)
    {
        for (int i = 0; i < _sparseSets.Length; i++)
            (_sparseSets[i] as ISparseSetView)?.Remove(entityId);
    }
}
```

- [ ] **Step 5: Подключить очистку в `World.Destroy` (`src/UvEcs/World.cs`)**

Заменить тело `Destroy`:

```csharp
    public void Destroy(Entity e)
    {
        ref var rec = ref Entities.GetRecord(e);
        RemoveFromChunk(rec.ArchetypeId, rec.ChunkIndex, rec.Row);
        RemoveFromAllSparseSets(e.Id);   // иначе sparse переживёт сущность и достанется её преемнику
        Entities.Destroy(e);
    }
```

Добавить тест в `tests/UvEcs.Tests/SparseDriverTests.cs`:

```csharp
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
```

- [ ] **Step 6: Добавить BySparse в `src/UvEcs/Query.cs`**

Дописать в конец файла:

```csharp
public readonly struct SparseHit
{
    public readonly Entity Entity;
    public readonly Chunk Chunk;
    public readonly int Row;

    internal SparseHit(Entity entity, Chunk chunk, int row)
    {
        Entity = entity;
        Chunk = chunk;
        Row = row;
    }
}

public readonly struct SparseEnumerable
{
    private readonly Query _query;
    internal SparseEnumerable(Query query) => _query = query;
    public SparseEnumerator GetEnumerator() => new(_query);
}

public struct SparseEnumerator
{
    private readonly Query _query;
    private readonly ISparseSetView _driver;
    private readonly int[] _otherSparse;
    private int _index;
    private SparseHit _current;

    internal SparseEnumerator(Query query)
    {
        _query = query;
        _index = -1;
        _current = default;

        // Драйвер — наименьший из обязательных наборов (§6 спеки).
        // Отсутствующий набор означает ноль носителей, то есть пустой результат.
        int driverId = -1;
        int driverCount = int.MaxValue;

        foreach (int id in query.SparseAll)
        {
            int count = query.World.SparseSetById(id)?.Count ?? 0;
            if (count < driverCount)
            {
                driverCount = count;
                driverId = id;
            }
        }

        _driver = query.World.SparseSetById(driverId) ?? EmptySparseSetView.Instance;
        _otherSparse = query.SparseAll.Where(id => id != driverId).ToArray();
    }

    public SparseHit Current => _current;

    public bool MoveNext()
    {
        var world = _query.World;
        var entities = _driver.Entities;

        while (++_index < entities.Length)
        {
            int entityId = entities[_index];

            ref var rec = ref world.Entities.RecordRefUnchecked(entityId);
            if (rec.ArchetypeId < 0) continue;                     // сущность мертва

            var archetype = world.ArchetypeById(rec.ArchetypeId);
            if (!archetype.Mask.HasAll(in _query.All)) continue;
            if (!archetype.Mask.HasNone(in _query.None)) continue;

            bool hasAllSparse = true;
            for (int i = 0; i < _otherSparse.Length; i++)
            {
                var other = world.SparseSetById(_otherSparse[i]);
                if (other is null || !other.Has(entityId)) { hasAllSparse = false; break; }
            }
            if (!hasAllSparse) continue;

            var chunk = archetype.Chunks[rec.ChunkIndex];
            var tags = chunk.TagAt(rec.Row);
            if (!tags.HasAll(_query.TagAll) || !tags.HasNone(_query.TagNone)) continue;

            _current = new SparseHit(chunk.EntityAt(rec.Row), chunk, rec.Row);
            return true;
        }
        return false;
    }
}

internal sealed class EmptySparseSetView : ISparseSetView
{
    public static readonly EmptySparseSetView Instance = new();
    public int Count => 0;
    public ReadOnlySpan<int> Entities => ReadOnlySpan<int>.Empty;
    public bool Has(int entityId) => false;
}
```

И добавить метод в тело класса `Query`:

```csharp
    public SparseEnumerable BySparse()
    {
        if (SparseAll.Length == 0)
            throw new InvalidOperationException("BySparse() требует хотя бы одного AllSparse<T>().");
        return new SparseEnumerable(this);
    }
```

- [ ] **Step 7: Прогнать**

Run: `dotnet test --filter SparseDriverTests`
Expected: PASS, 11 тестов.

- [ ] **Step 8: Коммит**

```bash
git add -A
git commit -m "feat: sparse API мира, драйвер по наименьшему набору, очистка при Destroy"
```

---

## Task 16: Фаззинг с проверкой инвариантов

**Files:**
- Create: `tests/UvEcs.Tests/WorldInvariants.cs`
- Create: `tests/UvEcs.Tests/FuzzInvariantTests.cs`

**Interfaces:**
- Consumes: весь публичный и internal API `World`.
- Produces: `static class WorldInvariants` — `static void Check(World w)`.

**Обоснование (§12 спеки):** основной инструмент проверки ECS. Ловит swap-remove, забывший обновить `EntityRecord`, миграцию, потерявшую тег, sparse set с висячим индексом — то, что руками не придумаешь.

- [ ] **Step 1: Написать проверку инвариантов**

`tests/UvEcs.Tests/WorldInvariants.cs`:

```csharp
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
    /// (EntityCount == totalRows выше) пропускает компенсирующие ошибки:
    /// +1 в одном чанке и -1 в другом дают ту же сумму. Источник истины здесь —
    /// таблица записей (через список живых), а не сам chunk.Count: иначе проверка
    /// выродилась бы в Assert.Equal(Count, Count), потому что forward-scan
    /// ограничен Count. Заодно ловит утёкшую сущность (в чанке, но не в alive)
    /// по-чанково.
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
            // 5. dense[sparse[e]] == e
            Assert.True(set.Has(entities[i]), $"висячий индекс для сущности {entities[i]}");
        }
        Assert.Equal(entities.Length, set.Count);
    }
}
```

- [ ] **Step 2: Написать фаззер**

`tests/UvEcs.Tests/FuzzInvariantTests.cs`:

```csharp
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
}
```

- [ ] **Step 3: Прогнать**

Run: `dotnet test --filter FuzzInvariantTests`
Expected: PASS, 5 тестов (`Theory` даёт 4).

Если падает — это настоящий баг, а не проблема теста. Смотреть на инвариант, который сломался: №2 указывает на swap-remove без починки записи, №3 — на потерю тега при миграции, №5 — на висячий sparse-индекс.

- [ ] **Step 4: Прогнать всё**

Run: `dotnet test`
Expected: PASS, все тесты.

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "test: фаззинг случайными операциями с проверкой пяти инвариантов мира"
```

---

## Task 17: Бенчмарки

**Files:**
- Create: `bench/UvEcs.Bench/UvEcs.Bench.csproj`
- Create: `bench/UvEcs.Bench/Harness.cs`
- Create: `bench/UvEcs.Bench/Program.cs`

**Interfaces:**
- Consumes: `World`, `Query`.
- Produces: консольное приложение, печатающее таблицу с медианами и собственным разбросом.

**Методика (§13 спеки), обязательна.** За время дизайна бенчмарк выдал перевёрнутый результат трижды. Правила: общий прогрев всех вариантов вместе, чередование порядка между раундами, ≥25 раундов, медиана, печать собственного разброса, проверка что варианты делают одну работу. Если разница между вариантами меньше разброса внутри варианта — разницы нет.

**Не в CI.** Запуск руками перед мержем крупных изменений.

- [ ] **Step 1: Создать проект**

```bash
cd /home/dev/projects/uv-ecs
dotnet new console -o bench/UvEcs.Bench -f net8.0 --force
dotnet sln add bench/UvEcs.Bench
dotnet add bench/UvEcs.Bench reference src/UvEcs
```

В `bench/UvEcs.Bench/UvEcs.Bench.csproj` добавить в `<PropertyGroup>`:

```xml
    <Optimize>true</Optimize>
    <ServerGarbageCollection>true</ServerGarbageCollection>
```

- [ ] **Step 2: Написать харнесс**

`bench/UvEcs.Bench/Harness.cs`:

```csharp
using System.Diagnostics;

namespace UvEcs.Bench;

public static class Harness
{
    private const int Rounds = 25;

    /// <summary>
    /// Общий прогрев всех вариантов, чередование порядка, медиана, печать разброса.
    /// Разница меньше собственного разброса варианта — не разница (§13 спеки).
    /// </summary>
    public static void Compare(string title, int iterations, params (string Name, Action Body)[] variants)
    {
        Console.WriteLine($"\n=== {title} ===");

        for (int i = 0; i < 20_000; i++)
            foreach (var v in variants) v.Body();

        var samples = new double[variants.Length][];
        for (int i = 0; i < variants.Length; i++) samples[i] = new double[Rounds];

        for (int round = 0; round < Rounds; round++)
        {
            // чередуем порядок: первый замер систематически хуже
            bool forward = (round & 1) == 0;
            for (int k = 0; k < variants.Length; k++)
            {
                int i = forward ? k : variants.Length - 1 - k;
                samples[i][round] = Measure(variants[i].Body, iterations);
            }
        }

        double? baseline = null;
        double baselineSpread = 0;

        for (int i = 0; i < variants.Length; i++)
        {
            var s = samples[i];
            Array.Sort(s);
            double min = s[0], med = s[Rounds / 2], max = s[^1];
            double spread = max / min - 1;

            Console.WriteLine($"  {variants[i].Name,-28} min {min,7:F2}  med {med,7:F2}  max {max,7:F2} мкс  (разброс {spread * 100,4:F0}%)");

            if (i == 0) { baseline = med; baselineSpread = spread; }
            else
            {
                double ratio = med / baseline!.Value;
                bool significant = Math.Abs(ratio - 1) > baselineSpread;
                Console.WriteLine($"  {"",-28} -> {ratio:F3}x относительно «{variants[0].Name}» " +
                                  (significant ? "(значимо)" : "(В ПРЕДЕЛАХ ШУМА — разницы нет)"));
            }
        }
    }

    private static double Measure(Action body, int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) body();
        sw.Stop();
        return sw.Elapsed.TotalMicroseconds / iterations;
    }
}
```

- [ ] **Step 3: Написать бенчмарки**

`bench/UvEcs.Bench/Program.cs`:

```csharp
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
```

- [ ] **Step 4: Запустить и записать базовые числа**

Run: `dotnet run -c Release --project bench/UvEcs.Bench`
Expected: три таблицы с медианами и разбросом.

Записать полученные числа в §13 спеки в строки «миграция архетипа» и «create/destroy 10k», которые сейчас помечены как «не измерено».

- [ ] **Step 5: Коммит**

```bash
git add -A
git commit -m "bench: харнесс по методике спеки и три базовых сценария"
```

---

## Self-Review

**1. Покрытие спеки.**

| раздел спеки | задача |
|---|---|
| §3 пакеты (`Abstractions`, `UvEcs`) | Task 1 |
| §4 маркеры хранилищ | Task 1 |
| §4 `ComponentType<T>.Id`, `TagType<T>.Bit` | Task 4 |
| §4 `ComponentMask` `[InlineArray(4)]` | Task 3 |
| §5 `TagMask` как обёртка, 64 тега | Task 2 |
| §5 чанк 16 КБ, POH, сдвиг до 64 | Task 6 |
| §5 раскладка, колонка `Entity`, `Cap` | Task 7 |
| §5 пул с гистерезисом | Task 9 |
| §5 `TagUnion` консервативна, пересчёт | Task 12 |
| §5 архетипы не удаляются, граф переходов | Task 9, 11 |
| §5 миграция как явные remove+insert | Task 11 |
| §6 инкрементальный кеш архетипов | Task 14 |
| §6 чанковая итерация, `GetRead`/`GetWrite` | Task 8, 14 |
| §6 sparse-драйвер, наименьший набор | Task 15 |
| §6 `Chunks()` запрещён при `AllSparse` | Task 15 |
| §11 протухшая `Entity` ловится и в Release | Task 5 |
| §11 `StructuralVersion` | Task 9, 11 |
| §12 фаззинг с пятью инвариантами | Task 16 |
| §13 методика бенчмарков | Task 17 |

Не покрыто и вынесено в другие планы явно: `repVersion` (§9), command buffer и системы (§7), параллелизм (§8), предсказание (§10), кодоген. `OptionalSparse` из §6 не реализуется здесь — он нужен только вместе с чанковой итерацией по запросу со sparse-фильтром, а её потребитель появится в плане систем; сейчас это был бы код без вызывающей стороны (YAGNI).

**2. Найденные дефекты — исправить перед исполнением.**

- **`InternalsVisibleTo` отсутствует.** Тесты в задачах 11, 12, 15, 16 обращаются к `World.Entities`, `World.ArchetypeById`, `Chunk.TagAt` — они `internal`. Правка вносится в Task 1 Step 2.
- **`World.Destroy` не чистит sparse-наборы.** Это настоящий баг, а не мелочь: `EntityStore` переиспользует `Id`, поэтому новая сущность унаследовала бы `GuildBuff` покойника. Требует `Remove(int)` в `ISparseSetView` и цикла в `Destroy`. Правка вносится в Task 15.
- **`ChunkPoolTests.Aligned_start_...`** содержит неиспользуемую переменную `bufStart` и локальную функцию, вычисляющую то же, что и проверка. Переписать.
- **`SparseEnumerator`** выбирает драйвер циклом с `break` и `smallest = null` — логика запутанная. Упростить.

**3. Согласованность типов.** `Entity`, `EntityRecord`, `TagMask`, `ComponentMask`, `ChunkLayout`, `Chunk`, `Archetype`, `World`, `Query`, `SparseSet<T>`, `ISparseSetView`, `ChunkView`, `SparseHit` — имена и сигнатуры совпадают между блоками `Produces` и телами задач. `GetWrite<T>()` возвращает `Span<T>` во всех местах; `SwapRemove` возвращает `Entity` (переехавшую либо `Null`) во всех местах.
