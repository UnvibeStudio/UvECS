using System.Runtime.CompilerServices;

namespace UvEcs;

/// <summary>То, что видит пользователь внутри foreach по запросу.</summary>
public readonly ref struct ChunkView
{
    private readonly Chunk _chunk;
    private readonly TagMask _tagAll;
    private readonly TagMask _tagNone;

    internal ChunkView(Chunk chunk, TagMask tagAll, TagMask tagNone)
    {
        _chunk = chunk;
        _tagAll = tagAll;
        _tagNone = tagNone;
        AllRowsPass = tagAll.IsEmpty && chunk.TagUnion.HasNone(tagNone);
    }

    public int Count => _chunk.Count;

    /// <summary>Все строки проходят фильтр — проверять построчно не нужно.</summary>
    public bool AllRowsPass { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Passes(int row)
    {
        if (AllRowsPass) return true;
        var tags = _chunk.TagAt(row);
        return tags.HasAll(_tagAll) && tags.HasNone(_tagNone);
    }

    public ReadOnlySpan<Entity> Entities => _chunk.Entities;

    public ReadOnlySpan<T> GetRead<T>() where T : unmanaged, IComponent => _chunk.GetRead<T>();

    public Span<T> GetWrite<T>() where T : unmanaged, IComponent => _chunk.GetWrite<T>();

    public ref T GetRef<T>(int row) where T : unmanaged, IComponent => ref _chunk.GetRef<T>(row);
}
