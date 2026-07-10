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

    public int FreeCount => _free.Count;
    public int TotalAllocated => _owned.Count;

    public byte[] Rent()
    {
        if (_free.Count > 0) return _free.Pop();

        // Pinned Object Heap: массив закреплён навсегда, Dispose не нужен, GC его не двигает.
        var buffer = GC.AllocateUninitializedArray<byte>(ChunkBytes + Alignment, pinned: true);
        _owned.Add(buffer);
        return buffer;
    }

    public void Return(byte[] buffer)
    {
        if (!_owned.Contains(buffer))
            throw new ArgumentException("Буфер не принадлежит этому пулу.", nameof(buffer));
        _free.Push(buffer);
    }

    /// <summary>
    /// POH гарантирует выравнивание только на 8 байт, поэтому сдвигаемся внутри буфера.
    /// Массив закреплён, адрес не изменится.
    /// </summary>
    public static unsafe nint AlignedStart(byte[] buffer)
    {
        nint raw = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(buffer));
        nint offset = (Alignment - (raw & (Alignment - 1))) & (Alignment - 1);
        return raw + offset;
    }
}
