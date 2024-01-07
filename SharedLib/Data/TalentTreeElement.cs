namespace SharedLib;

public readonly record struct TalentTreeElement
{
    public int TierID { get; init; }
    public int ColumnIndex { get; init; }
    public int TabID { get; init; }
    public int[] SpellIds { get; init; }
}
