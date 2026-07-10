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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Entity EntityAt(int row) => ref Unsafe.AsRef<Entity>((void*)(_data + Layout.EntityOffset + row * sizeof(long)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TagMask TagAt(int row) => ref Unsafe.AsRef<TagMask>((void*)(_data + Layout.TagOffset + row * sizeof(long)));

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
