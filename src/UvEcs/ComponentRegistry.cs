using System.Runtime.CompilerServices;

namespace UvEcs;

/// <summary>
/// Идентификаторы стабильны внутри процесса и НЕСТАБИЛЬНЫ между процессами:
/// они зависят от порядка первого обращения. Наружу — в сеть и в файлы — не выходят никогда.
/// </summary>
public static class ComponentRegistry
{
    private static int _next;
    private static readonly int[] _sizes = new int[ComponentMask.Capacity];

    public static int Count => Volatile.Read(ref _next);

    public static int Register(int size)
    {
        int id = Interlocked.Increment(ref _next) - 1;
        if (id >= ComponentMask.Capacity)
            throw new InvalidOperationException(
                $"Превышен лимит компонентов ({ComponentMask.Capacity}). Увеличьте ComponentMask.Words.");
        _sizes[id] = size;
        return id;
    }

    public static int SizeOf(int componentId) => _sizes[componentId];
}

public static class ComponentType<T> where T : unmanaged, IComponent
{
    /// <remarks>JIT в tier1 сворачивает static readonly в константу — const не даёт выигрыша.</remarks>
    public static readonly int Id = ComponentRegistry.Register(Unsafe.SizeOf<T>());

    public static int Size => Unsafe.SizeOf<T>();
}

public static class SparseRegistry
{
    private static int _next;
    public static int Count => Volatile.Read(ref _next);
    public static int Register() => Interlocked.Increment(ref _next) - 1;
}

public static class SparseType<T> where T : unmanaged, ISparse
{
    public static readonly int Id = SparseRegistry.Register();
}

public static class TagRegistry
{
    private static int _next;
    public static int Count => Volatile.Read(ref _next);

    public static int Register()
    {
        int index = Interlocked.Increment(ref _next) - 1;
        if (index >= TagMask.Capacity)
            throw new InvalidOperationException(
                $"Превышен лимит тегов ({TagMask.Capacity}). Сначала пересмотрите корзины (§5 спеки): " +
                "редкий тег переезжает в ISparse, неизменяемый выражается набором компонентов. " +
                "Только затем расширяйте TagMask до InlineArray(2).");
        return index;
    }
}

public static class TagType<T> where T : unmanaged, ITag
{
    public static readonly int Index = TagRegistry.Register();

    /// <remarks>Возвращает TagMask, а не ulong. ulong не покидает TagMask.</remarks>
    public static readonly TagMask Bit = TagMask.FromIndex(Index);
}
