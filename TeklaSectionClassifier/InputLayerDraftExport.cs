using System;
using System.Collections.Generic;
using System.Linq;

namespace TeklaSectionClassifier;

public sealed class InputLayerDraftMemberExport
{
    public string SchemaVersion { get; set; } = "new-input-layer-draft/v0";
    public string GeneratedAtUtc { get; set; } = string.Empty;
    public InputLayerDraftMemberInfo Member { get; set; } = new InputLayerDraftMemberInfo();
    public InputLayerDraftLongitudinalPath NormalizedLongitudinalPath { get; set; } = new InputLayerDraftLongitudinalPath();
    public List<InputLayerDraftPartExport> Parts { get; set; } = new List<InputLayerDraftPartExport>();
    public List<string> ExportDiagnostics { get; set; } = new List<string>();
}

public sealed class InputLayerDraftMemberInfo
{
    public string AssemblyId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Material { get; set; } = string.Empty;
    public string ProfileString { get; set; } = string.Empty;
    public AxisDefinition MainAxis { get; set; } = new AxisDefinition();
    public BoundingBox3D BoundingBox { get; set; } = new BoundingBox3D();
    public string LongitudinalAxisKind { get; set; } = string.Empty;
    public double LongitudinalAxisConfidence { get; set; }
    public string LongitudinalAxisSource { get; set; } = string.Empty;
}

public sealed class InputLayerDraftLongitudinalPath
{
    public string PathModelType { get; set; } = "MAIN_AXIS_FALLBACK";
    public string PathSourceKind { get; set; } = string.Empty;
    public double PathConfidence { get; set; }
    public double PathLength { get; set; }
    public bool FallbackUsed { get; set; }
    public List<string> SupportEvidence { get; set; } = new List<string>();
    public List<LongitudinalAxisSegment> PathSegments { get; set; } = new List<LongitudinalAxisSegment>();
}

public sealed class InputLayerDraftPartExport
{
    public string PartId { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PartType { get; set; } = string.Empty;
    public string ProfileString { get; set; } = string.Empty;
    public string Material { get; set; } = string.Empty;
    public string PartModelType { get; set; } = string.Empty;
    public string RepresentationKind { get; set; } = string.Empty;
    public string RepresentationLevel { get; set; } = string.Empty;
    public string DistortionRiskCode { get; set; } = string.Empty;
    public List<string> DistortionRiskReasons { get; set; } = new List<string>();
    public List<string> DegradationReasonCodes { get; set; } = new List<string>();
    public InputLayerDraftRawGeometry RawGeometry { get; set; } = new InputLayerDraftRawGeometry();
    public InputLayerDraftNormalizedShape NormalizedShape { get; set; } = new InputLayerDraftNormalizedShape();
    public InputLayerDraftLongitudinalProjection LongitudinalProjection { get; set; } = new InputLayerDraftLongitudinalProjection();
    public List<string> WarningCodes { get; set; } = new List<string>();
}

public sealed class InputLayerDraftRawGeometry
{
    public Point3D Centroid { get; set; } = new Point3D();
    public BoundingBox3D BoundingBox { get; set; } = new BoundingBox3D();
    public CoordinateSystem3D LocalCoordinateSystem { get; set; } = new CoordinateSystem3D();
    public double? LegacyThickness { get; set; }
    public double? NormalizedThickness { get; set; }
    public string ThicknessSource { get; set; } = string.Empty;
    public string ThicknessSourceDetail { get; set; } = string.Empty;
    public AxisProjection AxisProjection { get; set; } = new AxisProjection();
    public int SolidEdgeCount { get; set; }
    public bool NearMemberStart { get; set; }
    public bool NearMemberEnd { get; set; }
    public double Planarity { get; set; }
    public bool OuterSideCandidate { get; set; }
}

public sealed class InputLayerDraftNormalizedShape
{
    public Point3D ReferenceOrigin { get; set; } = new Point3D();
    public Vector3D LongDirection { get; set; } = new Vector3D(1, 0, 0);
    public Vector3D WidthDirection { get; set; } = new Vector3D(0, 1, 0);
    public Vector3D NormalDirection { get; set; } = new Vector3D(0, 0, 1);
    public double? Thickness { get; set; }
    public string BoundaryLoopKind { get; set; } = string.Empty;
    public string BoundarySource { get; set; } = string.Empty;
    public bool BoundaryClosed { get; set; }
    public string LocalFrameSource { get; set; } = string.Empty;
    public string SectionSource { get; set; } = string.Empty;
    public string SectionProfile { get; set; } = string.Empty;
    public bool HasExplicitBoundary { get; set; }
    public bool HasExplicitThickness { get; set; }
    public bool HasExplicitSection { get; set; }
    public List<Point3D> BoundaryPoints { get; set; } = new List<Point3D>();
}

public sealed class InputLayerDraftLongitudinalProjection
{
    public double Start { get; set; }
    public double End { get; set; }
    public double Length { get; set; }
    public double CoverageRatio { get; set; }
}

public sealed class InputLayerDraftBatchSummary
{
    public string SchemaVersion { get; set; } = "new-input-layer-draft-summary/v0";
    public string GeneratedAtUtc { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public int PartCount { get; set; }
    public List<InputLayerDraftBreakdownItem> PartModelTypeBreakdown { get; set; } = new List<InputLayerDraftBreakdownItem>();
    public List<InputLayerDraftBreakdownItem> RepresentationKindBreakdown { get; set; } = new List<InputLayerDraftBreakdownItem>();
    public List<InputLayerDraftBreakdownItem> RepresentationLevelBreakdown { get; set; } = new List<InputLayerDraftBreakdownItem>();
    public List<InputLayerDraftBreakdownItem> DistortionRiskBreakdown { get; set; } = new List<InputLayerDraftBreakdownItem>();
    public List<InputLayerDraftBreakdownItem> ThicknessSourceBreakdown { get; set; } = new List<InputLayerDraftBreakdownItem>();
    public List<InputLayerDraftBreakdownItem> BoundaryLoopKindBreakdown { get; set; } = new List<InputLayerDraftBreakdownItem>();
    public List<InputLayerDraftBreakdownItem> WarningBreakdown { get; set; } = new List<InputLayerDraftBreakdownItem>();
}

public sealed class InputLayerDraftBreakdownItem
{
    public string Code { get; set; } = string.Empty;
    public int Count { get; set; }
}

internal static class InputLayerDraftExportBuilder
{
    public static InputLayerDraftMemberExport BuildMember(MemberSnapshot member)
    {
        var diagnostics = new List<string>();
        var pathModelType = member.AxisSegments.Count > 0
            ? "RAW_AXIS_SEGMENTS"
            : member.GuidePolyline.Count > 1
                ? "GUIDE_POLYLINE_RECONSTRUCTED"
                : "MAIN_AXIS_FALLBACK";

        if (member.Parts.Any(part => part.InputLayerDraft.WarningCodes.Contains("BOUNDARY_UNRESOLVED")))
        {
            diagnostics.Add("PLATE_BOUNDARY_GAPS_PRESENT");
        }

        if (member.Parts.Any(part => part.InputLayerDraft.ThicknessSource == "PROFILE_PARSE" || part.InputLayerDraft.ThicknessSource == "LOCAL_BBOX_MIN_DIMENSION"))
        {
            diagnostics.Add("THICKNESS_FALLBACK_PRESENT");
        }

        return new InputLayerDraftMemberExport
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            Member = new InputLayerDraftMemberInfo
            {
                AssemblyId = member.Member.AssemblyId,
                MemberId = member.Member.MemberId,
                Guid = member.Member.Guid,
                Name = member.Member.Name,
                Material = member.Member.Material,
                ProfileString = member.Member.ProfileString,
                MainAxis = member.Member.MainAxis,
                BoundingBox = member.Member.BoundingBox,
                LongitudinalAxisKind = member.LongitudinalAxisKind,
                LongitudinalAxisConfidence = member.LongitudinalAxisConfidence,
                LongitudinalAxisSource = member.LongitudinalAxisSource
            },
            NormalizedLongitudinalPath = new InputLayerDraftLongitudinalPath
            {
                PathModelType = pathModelType,
                PathSourceKind = member.LongitudinalAxisSource,
                PathConfidence = member.LongitudinalAxisConfidence,
                PathLength = member.AxisSegments.Count > 0
                    ? member.AxisSegments.Sum(segment => Math.Max(0.0, segment.Length))
                    : member.Member.MainAxis.Length,
                FallbackUsed = pathModelType != "RAW_AXIS_SEGMENTS",
                SupportEvidence = BuildSupportEvidence(member, pathModelType),
                PathSegments = member.AxisSegments.Count > 0
                    ? member.AxisSegments.ToList()
                    : BuildFallbackSegments(member)
            },
            Parts = member.Parts.Select(BuildPart).ToList(),
            ExportDiagnostics = diagnostics
        };
    }

    public static InputLayerDraftBatchSummary BuildSummary(ExtractionBatch batch)
    {
        var parts = batch.Members.SelectMany(member => member.Parts).ToList();
        return new InputLayerDraftBatchSummary
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            MemberCount = batch.Members.Count,
            PartCount = parts.Count,
            PartModelTypeBreakdown = BuildBreakdown(parts.Select(part => part.InputLayerDraft.PartModelType)),
            RepresentationKindBreakdown = BuildBreakdown(parts.Select(part => part.InputLayerDraft.RepresentationKind)),
            RepresentationLevelBreakdown = BuildBreakdown(parts.Select(part => part.InputLayerDraft.RepresentationLevel)),
            DistortionRiskBreakdown = BuildBreakdown(parts.Select(part => part.InputLayerDraft.DistortionRiskCode)),
            ThicknessSourceBreakdown = BuildBreakdown(parts.Select(part => part.InputLayerDraft.ThicknessSource)),
            BoundaryLoopKindBreakdown = BuildBreakdown(parts.Select(part => part.InputLayerDraft.BoundaryLoopKind)),
            WarningBreakdown = BuildBreakdown(parts.SelectMany(part => part.InputLayerDraft.WarningCodes))
        };
    }

    private static InputLayerDraftPartExport BuildPart(PartSnapshot part)
    {
        var metadata = part.InputLayerDraft;
        return new InputLayerDraftPartExport
        {
            PartId = part.PartId,
            Guid = part.Guid,
            Name = part.Name,
            PartType = part.PartType,
            ProfileString = part.ProfileString,
            Material = part.Material,
            PartModelType = metadata.PartModelType,
            RepresentationKind = metadata.RepresentationKind,
            RepresentationLevel = metadata.RepresentationLevel,
            DistortionRiskCode = metadata.DistortionRiskCode,
            DistortionRiskReasons = metadata.DistortionRiskReasons.ToList(),
            DegradationReasonCodes = metadata.DegradationReasonCodes.ToList(),
            RawGeometry = new InputLayerDraftRawGeometry
            {
                Centroid = part.Centroid,
                BoundingBox = part.BoundingBox,
                LocalCoordinateSystem = part.LocalCoordinateSystem,
                LegacyThickness = part.Thickness,
                NormalizedThickness = metadata.Thickness,
                ThicknessSource = metadata.ThicknessSource,
                ThicknessSourceDetail = metadata.ThicknessSourceDetail,
                AxisProjection = part.AxisProjection,
                SolidEdgeCount = part.SolidEdges.Count,
                NearMemberStart = part.EndProximity.NearStart,
                NearMemberEnd = part.EndProximity.NearEnd,
                Planarity = part.GeometryHints.Planarity,
                OuterSideCandidate = part.GeometryHints.OuterSideCandidate
            },
            NormalizedShape = new InputLayerDraftNormalizedShape
            {
                ReferenceOrigin = part.LocalCoordinateSystem.Origin,
                LongDirection = part.LocalCoordinateSystem.X,
                WidthDirection = part.LocalCoordinateSystem.Y,
                NormalDirection = part.LocalCoordinateSystem.Z,
                Thickness = metadata.Thickness,
                BoundaryLoopKind = metadata.BoundaryLoopKind,
                BoundarySource = metadata.BoundarySource,
                BoundaryClosed = metadata.BoundaryClosed,
                LocalFrameSource = metadata.LocalFrameSource,
                SectionSource = metadata.SectionSource,
                SectionProfile = metadata.SectionProfile,
                HasExplicitBoundary = metadata.HasExplicitBoundary,
                HasExplicitThickness = metadata.HasExplicitThickness,
                HasExplicitSection = metadata.HasExplicitSection,
                BoundaryPoints = metadata.BoundaryPoints.ToList()
            },
            LongitudinalProjection = new InputLayerDraftLongitudinalProjection
            {
                Start = part.AxisProjection.Start,
                End = part.AxisProjection.End,
                Length = part.AxisProjection.Length,
                CoverageRatio = part.AxisProjection.CoverageRatio
            },
            WarningCodes = metadata.WarningCodes.ToList()
        };
    }

    private static List<string> BuildSupportEvidence(MemberSnapshot member, string pathModelType)
    {
        var evidence = new List<string>
        {
            "LONGITUDINAL_AXIS_KIND:" + member.LongitudinalAxisKind,
            "LONGITUDINAL_AXIS_SOURCE:" + member.LongitudinalAxisSource
        };

        if (member.AxisSegments.Count > 0)
        {
            evidence.Add("AXIS_SEGMENT_COUNT:" + member.AxisSegments.Count);
        }

        if (member.GuidePolyline.Count > 0)
        {
            evidence.Add("GUIDE_POLYLINE_POINT_COUNT:" + member.GuidePolyline.Count);
        }

        if (pathModelType == "MAIN_AXIS_FALLBACK")
        {
            evidence.Add("MAIN_AXIS_LENGTH:" + member.Member.MainAxis.Length.ToString("0.###"));
        }

        return evidence;
    }

    private static List<LongitudinalAxisSegment> BuildFallbackSegments(MemberSnapshot member)
    {
        var axis = member.Member.MainAxis;
        var normalized = Normalize(axis.Direction);
        var end = new Point3D(
            axis.Origin.X + (normalized.X * axis.Length),
            axis.Origin.Y + (normalized.Y * axis.Length),
            axis.Origin.Z + (normalized.Z * axis.Length));

        return new List<LongitudinalAxisSegment>
        {
            new LongitudinalAxisSegment
            {
                Index = 0,
                Start = axis.Origin,
                End = end,
                Direction = normalized,
                Length = axis.Length,
                CumulativeStart = 0.0,
                CumulativeEnd = axis.Length
            }
        };
    }

    private static Vector3D Normalize(Vector3D vector)
    {
        var length = Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y) + (vector.Z * vector.Z));
        if (length <= 1e-9)
        {
            return new Vector3D(1, 0, 0);
        }

        return new Vector3D(vector.X / length, vector.Y / length, vector.Z / length);
    }

    private static List<InputLayerDraftBreakdownItem> BuildBreakdown(IEnumerable<string> codes)
    {
        return codes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .GroupBy(code => code, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new InputLayerDraftBreakdownItem
            {
                Code = group.Key,
                Count = group.Count()
            })
            .ToList();
    }
}
