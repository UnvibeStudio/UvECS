namespace UvEcs;

public sealed class QueryBuilder
{
    private readonly Query _query;
    private readonly List<int> _sparseAll = new();

    internal QueryBuilder(World world) => _query = new Query(world);

    public QueryBuilder All<T>() where T : unmanaged, IComponent
    {
        _query.All.Set(ComponentType<T>.Id);
        return this;
    }

    public QueryBuilder All<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        => All<T1>().All<T2>();

    public QueryBuilder All<T1, T2, T3>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        => All<T1>().All<T2>().All<T3>();

    public QueryBuilder None<T>() where T : unmanaged, IComponent
    {
        _query.None.Set(ComponentType<T>.Id);
        return this;
    }

    public QueryBuilder WithTag<T>() where T : unmanaged, ITag
    {
        _query.TagAll = _query.TagAll.Or(TagType<T>.Bit);
        return this;
    }

    public QueryBuilder WithoutTag<T>() where T : unmanaged, ITag
    {
        _query.TagNone = _query.TagNone.Or(TagType<T>.Bit);
        return this;
    }

    /// <summary>Обязательный sparse-компонент. Драйвером итерации становится sparse-набор.</summary>
    public QueryBuilder AllSparse<T>() where T : unmanaged, ISparse
    {
        _sparseAll.Add(SparseType<T>.Id);
        return this;
    }

    public Query Build()
    {
        _query.SparseAll = _sparseAll.ToArray();
        return _query;
    }
}
