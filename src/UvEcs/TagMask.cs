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
