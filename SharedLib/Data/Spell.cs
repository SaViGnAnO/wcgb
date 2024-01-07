namespace SharedLib;

public readonly record struct Spell
{
    public int Id { get; init; }
    public string Name { get; init; }
    public int Level { get; init; }
}
