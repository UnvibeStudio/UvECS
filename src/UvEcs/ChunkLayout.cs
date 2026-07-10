using System.Runtime.CompilerServices;

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
