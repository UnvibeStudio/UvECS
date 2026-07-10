namespace UvEcs.Tests;

public struct Position : IComponent { public float X, Y, Z; }
public struct Velocity : IComponent { public float X, Y, Z; }
public struct Health   : IComponent { public int Current, Max; }
public struct Mana     : IComponent { public int Current; }

public struct Stunned  : ITag { }
public struct InCombat : ITag { }
public struct Dead     : ITag { }

public struct GuildBuff : ISparse { public int Id; public float Until; }
public struct QuestFlag : ISparse { public int Id; }
