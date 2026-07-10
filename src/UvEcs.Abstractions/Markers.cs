namespace UvEcs;

/// <summary>Компонент с данными. Живёт колонкой в чанке архетипа.</summary>
public interface IComponent { }

/// <summary>Флаг без данных. Живёт битом в маске чанка, вне идентичности архетипа.</summary>
public interface ITag { }

/// <summary>Компонент с данными у меньшинства сущностей (порог ~25%). Живёт в sparse set.</summary>
public interface ISparse { }
