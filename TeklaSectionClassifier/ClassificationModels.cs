using System.Collections.Generic;

namespace TeklaSectionClassifier;

public enum MemberClass
{
    Unknown = 0,
    H,
    Box,
    T,
    Cross,
    Angle,
    Pipe,
    Irregular
}

public enum SecondaryClass
{
    Unknown = 0,
    Regular,
    Irregular,
    HRegular,
    HTapered,
    HIrregular,
    HCurved,
    HFolded,
    BoxCurved,
    BoxFolded
}

public sealed class ClassificationResult
{
    public MemberClass MainClass { get; set; } = MemberClass.Unknown;
    public SecondaryClass SecondaryClass { get; set; } = SecondaryClass.Unknown;
    public double Confidence { get; set; }
    public bool NeedsManualReview { get; set; }
    public string ReviewReason { get; set; } = string.Empty;
    public string KeyDimensionsDisplay { get; set; } = string.Empty;
    public List<string> Labels { get; set; } = new List<string>();
    public List<ScoreBreakdown> Scores { get; set; } = new List<ScoreBreakdown>();
    public List<PartRoleAssignment> PartRoles { get; set; } = new List<PartRoleAssignment>();
}

public sealed class ScoreBreakdown
{
    public MemberClass ClassName { get; set; } = MemberClass.Unknown;
    public double Score { get; set; }
    public List<string> Reasons { get; set; } = new List<string>();
}

public sealed class PartRoleAssignment
{
    public string PartId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public double Score { get; set; }
    public string Reason { get; set; } = string.Empty;
}

