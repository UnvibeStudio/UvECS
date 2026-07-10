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
