using System.Runtime.CompilerServices;
using Xunit;

namespace UvEcs.Tests;

public class MarkerTests
{
    private static int SizeOfComponent<T>() where T : unmanaged, IComponent => Unsafe.SizeOf<T>();

    [Fact]
    public void Constraint_accepts_component_and_reports_size()
    {
        Assert.Equal(12, SizeOfComponent<Position>());
        Assert.Equal(8, SizeOfComponent<Health>());
    }

    [Fact]
    public void Components_are_unmanaged()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<Position>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<GuildBuff>());
    }
}
