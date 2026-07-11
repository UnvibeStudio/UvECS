using Xunit;

namespace UvEcs.Tests;

public class FastSpawnTests
{
    [Fact]
    public void Create_with_one_component_places_it_with_its_value()
    {
        var w = new World();
        var e = w.Create(new Position { X = 1, Y = 2, Z = 3 });

        Assert.True(w.Has<Position>(e));
        Assert.Equal(2, w.Get<Position>(e).Y);
        Assert.True(w.IsAlive(e));
    }
}
