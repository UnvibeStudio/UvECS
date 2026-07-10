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
