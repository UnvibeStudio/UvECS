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
