namespace UvEcs;

/// <summary>
/// Необобщённое окно в sparse-набор. Нужно в двух местах, где T неизвестен:
/// выбор драйвера итерации и очистка наборов при удалении сущности.
/// </summary>
public interface ISparseSetView
{
    int Count { get; }
    ReadOnlySpan<int> Entities { get; }
    bool Has(int entityId);
    bool Remove(int entityId);
}

/// <summary>
/// Редкий компонент с данными. Индексируется по entityId, поэтому миграция
/// архетипа его не трогает: обновлять нечего (§5 спеки).
/// Окупается, пока носителей меньше ~25% кандидатов запроса (§6 спеки).
/// </summary>
public sealed class SparseSet<T> : ISparseSetView where T : unmanaged, ISparse
{
    private const int Absent = -1;

    private int[] _sparse = new int[64];
    private int[] _dense = new int[16];
    private T[] _values = new T[16];
    private int _count;

    public SparseSet() => Array.Fill(_sparse, Absent);

    public int Count => _count;

    public ReadOnlySpan<int> Entities => _dense.AsSpan(0, _count);
    public Span<T> Values => _values.AsSpan(0, _count);

    public bool Has(int entityId)
        => (uint)entityId < (uint)_sparse.Length && _sparse[entityId] != Absent;

    public void Add(int entityId, T value)
    {
        if (Has(entityId)) throw new InvalidOperationException($"Сущность {entityId} уже в наборе.");

        EnsureSparse(entityId);
        if (_count == _dense.Length)
        {
            Array.Resize(ref _dense, _dense.Length * 2);
            Array.Resize(ref _values, _values.Length * 2);
        }

        _dense[_count] = entityId;
        _values[_count] = value;
        _sparse[entityId] = _count;
        _count++;
    }

    public bool Remove(int entityId)
    {
        if (!Has(entityId)) return false;

        int denseIndex = _sparse[entityId];
        int last = _count - 1;

        if (denseIndex != last)
        {
            int movedEntity = _dense[last];
            _dense[denseIndex] = movedEntity;
            _values[denseIndex] = _values[last];
            _sparse[movedEntity] = denseIndex;
        }

        _sparse[entityId] = Absent;
        _count--;
        return true;
    }

    public ref T GetRef(int entityId)
    {
        if (!Has(entityId)) throw new InvalidOperationException($"Сущности {entityId} нет в наборе.");
        return ref _values[_sparse[entityId]];
    }

    private void EnsureSparse(int entityId)
    {
        if (entityId < _sparse.Length) return;

        int newSize = _sparse.Length;
        while (newSize <= entityId) newSize *= 2;

        int oldSize = _sparse.Length;
        Array.Resize(ref _sparse, newSize);
        Array.Fill(_sparse, Absent, oldSize, newSize - oldSize);
    }
}
