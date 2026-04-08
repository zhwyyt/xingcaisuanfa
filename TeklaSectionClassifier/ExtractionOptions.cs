namespace TeklaSectionClassifier;

public sealed class ExtractionOptions
{
    public bool IncludeBolts { get; set; }
    public bool IncludeWelds { get; set; }
    public bool IncludeCutsSummary { get; set; } = true;
    public bool IncludePartConnections { get; set; } = true;
    public bool EmitPerMemberJson { get; set; } = true;
    public bool SelectedOnly { get; set; } = true;
    public string MemberPrefixFilter { get; set; } = string.Empty;
}
