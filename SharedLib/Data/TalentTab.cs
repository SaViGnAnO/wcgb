namespace SharedLib;

public readonly record struct TalentTab
{
    public int Id { get; init; }
    public int OrderIndex { get; init; }
    public int ClassMask { get; init; }
}
