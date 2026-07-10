namespace UvEcs;

/// <summary>
/// Раскладка чанка: [Entity × Cap][TagMask × Cap][компонент₀ × Cap]...
/// Колонка Entity обязательна: swap-remove обновляет EntityRecord переехавшей строки,
/// а сетевой слой индексирует версии по entityId (§5 спеки).
/// </summary>
public sealed class ChunkLayout
{
    public const int ColumnAlignment = 16;
    private const int EntitySize = 8;   // int Id + uint Version
    private const int TagSize = 8;      // TagMask

    public int Capacity { get; private init; }
    public int EntityOffset { get; private init; }
    public int TagOffset { get; private init; }
    public int[] ComponentIds { get; private init; } = Array.Empty<int>();
    public int[] ColumnOffsets { get; private init; } = Array.Empty<int>();

    private static int Align(int value) => (value + ColumnAlignment - 1) & ~(ColumnAlignment - 1);

    /// <summary>Сколько байт займёт чанк на <paramref name="capacity"/> сущностей. Публично ради тестов.</summary>
    public static int TotalBytesFor(int[] componentIds, int capacity)
    {
        int off = 0;
        off = Align(off + EntitySize * capacity);
        off = Align(off + TagSize * capacity);
        foreach (int id in componentIds)
            off = Align(off + ComponentRegistry.SizeOf(id) * capacity);
        return off;
    }

    /// <param name="componentIds">Отсортированы по возрастанию.</param>
    public static ChunkLayout Create(int[] componentIds)
    {
        int stride = EntitySize + TagSize;
        foreach (int id in componentIds) stride += ComponentRegistry.SizeOf(id);

        int capacity = ChunkPool.ChunkBytes / stride;
        while (capacity > 0 && TotalBytesFor(componentIds, capacity) > ChunkPool.ChunkBytes) capacity--;

        if (capacity == 0)
            throw new InvalidOperationException(
                $"Архетип не помещается в чанк {ChunkPool.ChunkBytes} б: одна сущность требует {stride} б. " +
                "Компонент слишком велик.");

        int off = 0;
        int entityOffset = off;
        off = Align(off + EntitySize * capacity);
        int tagOffset = off;
        off = Align(off + TagSize * capacity);

        var offsets = new int[componentIds.Length];
        for (int i = 0; i < componentIds.Length; i++)
        {
            offsets[i] = off;
            off = Align(off + ComponentRegistry.SizeOf(componentIds[i]) * capacity);
        }

        return new ChunkLayout
        {
            Capacity = capacity,
            EntityOffset = entityOffset,
            TagOffset = tagOffset,
            ComponentIds = componentIds,
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
