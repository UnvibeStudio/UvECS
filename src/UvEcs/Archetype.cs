namespace UvEcs;

/// <summary>
/// Точное множество компонентов. Архетипы никогда не удаляются: граф переходов
/// и кеши запросов держат на них индексы. Освобождаются чанки, а не архетипы.
/// </summary>
public sealed class Archetype
{
    private readonly List<Chunk> _chunks = new();
    private readonly Dictionary<int, Archetype> _addEdges = new();
    private readonly Dictionary<int, Archetype> _removeEdges = new();

    // Кеш индекса чанка со свободным местом. -1 — искать заново.
    // Вставки идут аппендом в один и тот же чанк, пока он не заполнится, поэтому
    // почти всегда это O(1)-попадание. Без кеша GetOrCreateChunkWithSpace сканировал
    // список чанков от нуля на КАЖДУЮ вставку: O(числа чанков) на сущность, что и было
    // главной ценой create/миграции на растущем архетипе.
    private int _chunkWithSpace = -1;

    public int Id { get; }
    public ComponentMask Mask { get; }
    public ChunkLayout Layout { get; }
    public IReadOnlyList<Chunk> Chunks => _chunks;

    /// <summary>Меняется при каждой миграции. Итератор чанков сверяет его в Debug (§11 спеки).</summary>
    public int StructuralVersion { get; private set; }

    public Archetype(int id, ComponentMask mask, int[] sortedComponentIds)
    {
        Id = id;
        Mask = mask;
        Layout = ChunkLayout.Create(sortedComponentIds);
    }

    public int EntityCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _chunks.Count; i++) n += _chunks[i].Count;
            return n;
        }
    }

    public Chunk GetOrCreateChunkWithSpace(ChunkPool pool, out int chunkIndex)
    {
        // Быстрый путь: кешированный чанк ещё существует и не полон.
        if ((uint)_chunkWithSpace < (uint)_chunks.Count && !_chunks[_chunkWithSpace].IsFull)
        {
            chunkIndex = _chunkWithSpace;
            return _chunks[_chunkWithSpace];
        }

        for (int i = 0; i < _chunks.Count; i++)
        {
            if (!_chunks[i].IsFull)
            {
                chunkIndex = _chunkWithSpace = i;
                return _chunks[i];
            }
        }

        var chunk = new Chunk(Layout, pool.Rent());
        _chunks.Add(chunk);
        chunkIndex = _chunkWithSpace = _chunks.Count - 1;
        return chunk;
    }

    /// <summary>
    /// Гистерезис: один пустой чанк остаётся про запас. Иначе сущность, прыгающая
    /// через границу чанка, устраивает пинг-понг аренды на каждой операции.
    /// </summary>
    public void ReleaseChunkIfEmpty(int chunkIndex, ChunkPool pool)
    {
        var chunk = _chunks[chunkIndex];
        if (!chunk.IsEmpty) return;

        int emptyCount = 0;
        for (int i = 0; i < _chunks.Count; i++) if (_chunks[i].IsEmpty) emptyCount++;
        if (emptyCount <= 1) return;

        // Убираем только последний чанк: иначе поедут ChunkIndex в EntityRecord.
        if (chunkIndex != _chunks.Count - 1) return;

        _chunks.RemoveAt(chunkIndex);
        _chunkWithSpace = -1;   // индексы могли сдвинуться — пусть перескан найдёт заново
        chunk.Reset();
        pool.Return(chunk.Buffer);
    }

    public bool TryGetAddEdge(int componentId, out Archetype target) => _addEdges.TryGetValue(componentId, out target!);
    public void SetAddEdge(int componentId, Archetype target) => _addEdges[componentId] = target;

    public bool TryGetRemoveEdge(int componentId, out Archetype target) => _removeEdges.TryGetValue(componentId, out target!);
    public void SetRemoveEdge(int componentId, Archetype target) => _removeEdges[componentId] = target;

    internal void BumpStructuralVersion() => StructuralVersion++;
}
