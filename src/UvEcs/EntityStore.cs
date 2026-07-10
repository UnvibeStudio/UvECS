namespace UvEcs;

public sealed class EntityStore
{
    private const int PageBits = 12;
    private const int PageSize = 1 << PageBits;    // 4096 записей на страницу
    private const int PageMask = PageSize - 1;

    /// <remarks>
    /// Страничное, а не плоское: страница никогда не переаллоцируется, поэтому ref,
    /// возвращённый из GetRecord, остаётся валидным после любого числа Create().
    /// Плоский Array.Resize молча оставил бы удержанный ref указывать в старый массив.
    /// </remarks>
    private EntityRecord[][] _pages = { new EntityRecord[PageSize] };

    private int[] _freeIds = new int[256];
    private int _freeCount;
    private int _count;

    public int Capacity => _pages.Length * PageSize;
    public int AliveCount => _count - _freeCount;

    private ref EntityRecord At(int id) => ref _pages[id >> PageBits][id & PageMask];

    private void EnsurePage(int id)
    {
        int page = id >> PageBits;
        if (page < _pages.Length) return;

        int oldLength = _pages.Length;
        Array.Resize(ref _pages, page + 1);                 // растёт только массив ссылок
        for (int i = oldLength; i < _pages.Length; i++)
            _pages[i] = new EntityRecord[PageSize];         // сами страницы неподвижны
    }

    public Entity Create()
    {
        int id;
        if (_freeCount > 0)
        {
            id = _freeIds[--_freeCount];
        }
        else
        {
            id = _count++;
            EnsurePage(id);
        }

        ref var rec = ref At(id);
        rec.Version = rec.Version == 0 ? 1u : rec.Version;   // первая жизнь начинается с 1
        rec.ArchetypeId = -1;
        rec.ChunkIndex = -1;
        rec.Row = -1;
        return new Entity(id, rec.Version);
    }

    public bool IsAlive(Entity e)
        => !e.IsNull && (uint)e.Id < (uint)_count && At(e.Id).Version == e.Version;

    public void Destroy(Entity e)
    {
        if (!IsAlive(e)) throw new InvalidOperationException($"{e} уже удалена или невалидна.");

        ref var rec = ref At(e.Id);
        rec.Version++;                       // протухание всех существующих дескрипторов
        if (rec.Version == 0) rec.Version = 1;   // 0 зарезервирован под Null
        rec.ArchetypeId = -1;
        rec.ChunkIndex = -1;
        rec.Row = -1;

        if (_freeCount == _freeIds.Length) Array.Resize(ref _freeIds, _freeIds.Length * 2);
        _freeIds[_freeCount++] = e.Id;
    }

    /// <summary>
    /// Проверяется и в Release: молча читать чужую память недопустимо (§11 спеки).
    /// Возвращённый ref переживает любое число Create() — страницы неподвижны.
    /// </summary>
    public ref EntityRecord GetRecord(Entity e)
    {
        if (!IsAlive(e)) throw new InvalidOperationException($"{e} протухла или невалидна.");
        return ref At(e.Id);
    }

    /// <summary>Без проверки версии. Только для внутренних путей, где сущность заведомо жива.</summary>
    internal ref EntityRecord RecordRefUnchecked(int id) => ref At(id);
}
