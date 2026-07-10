namespace UvEcs;

/// <summary>Generational index. Version == 0 означает невалидную сущность.</summary>
public readonly struct Entity : IEquatable<Entity>
{
    public readonly int Id;
    public readonly uint Version;

    public Entity(int id, uint version)
    {
        Id = id;
        Version = version;
    }

    public static Entity Null => default;

    public bool IsNull => Version == 0;

    public bool Equals(Entity other) => Id == other.Id && Version == other.Version;
    public override bool Equals(object? obj) => obj is Entity e && Equals(e);
    public override int GetHashCode() => HashCode.Combine(Id, Version);
    public override string ToString() => IsNull ? "Entity.Null" : $"Entity({Id}v{Version})";

    public static bool operator ==(Entity a, Entity b) => a.Equals(b);
    public static bool operator !=(Entity a, Entity b) => !a.Equals(b);
}

/// <summary>Где физически лежит сущность. Обновляется при каждой миграции.</summary>
public struct EntityRecord
{
    public int ArchetypeId;
    public int ChunkIndex;
    public int Row;
    public uint Version;
}
