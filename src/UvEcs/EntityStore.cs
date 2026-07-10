namespace UvEcs;

public sealed class EntityStore
{
    private EntityRecord[] _records = new EntityRecord[1024];
    private int[] _freeIds = new int[256];
    private int _freeCount;
    private int _count;

    public int Capacity => _records.Length;
    public int AliveCount => _count - _freeCount;

    public Entity Create()
    {
        int id;
        if (_freeCount > 0)
        {
            id = _freeIds[--_freeCount];
        }
        else
        {
            if (_count == _records.Length) Array.Resize(ref _records, _records.Length * 2);
            id = _count++;
        }

        ref var rec = ref _records[id];
        rec.Version = rec.Version == 0 ? 1u : rec.Version;   // первая жизнь начинается с 1
        rec.ArchetypeId = -1;
        rec.ChunkIndex = -1;
        rec.Row = -1;
        return new Entity(id, rec.Version);
    }

    public bool IsAlive(Entity e)
        => !e.IsNull && (uint)e.Id < (uint)_count && _records[e.Id].Version == e.Version;

    public void Destroy(Entity e)
    {
        if (!IsAlive(e)) throw new InvalidOperationException($"{e} уже удалена или невалидна.");

        ref var rec = ref _records[e.Id];
        rec.Version++;                       // протухание всех существующих дескрипторов
        if (rec.Version == 0) rec.Version = 1;   // 0 зарезервирован под Null
        rec.ArchetypeId = -1;
        rec.ChunkIndex = -1;
        rec.Row = -1;

        if (_freeCount == _freeIds.Length) Array.Resize(ref _freeIds, _freeIds.Length * 2);
        _freeIds[_freeCount++] = e.Id;
    }

    /// <summary>Проверяется и в Release: молча читать чужую память недопустимо (§11 спеки).</summary>
    public ref EntityRecord GetRecord(Entity e)
    {
        if (!IsAlive(e)) throw new InvalidOperationException($"{e} протухла или невалидна.");
        return ref _records[e.Id];
    }

    /// <summary>Без проверки версии. Только для внутренних путей, где сущность заведомо жива.</summary>
    internal ref EntityRecord RecordRefUnchecked(int id) => ref _records[id];
}
