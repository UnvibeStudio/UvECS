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
