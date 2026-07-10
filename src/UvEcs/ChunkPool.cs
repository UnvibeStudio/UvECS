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
