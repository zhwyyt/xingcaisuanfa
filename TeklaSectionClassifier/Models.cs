using System;
using System.Collections.Generic;

namespace TeklaSectionClassifier;

public sealed class ExtractionBatch
{
    public string SchemaVersion { get; set; } = "1.1";
    public ModelInfo ModelInfo { get; set; } = new ModelInfo();
    public List<MemberSnapshot> Members { get; set; } = new List<MemberSnapshot>();
}

public sealed class ModelInfo
{
    public string TeklaVersion { get; set; } = "2017";
    public string ModelPath { get; set; } = string.Empty;
    public DateTimeOffset ExtractTime { get; set; } = DateTimeOffset.Now;
}

public sealed class MemberSnapshot
{
    public MemberIdentity Member { get; set; } = new MemberIdentity();
    public string LongitudinalAxisKind { get; set; } = "STRAIGHT";
    public List<Point3D> GuidePolyline { get; set; } = new List<Point3D>();
    public List<LongitudinalAxisSegment> AxisSegments { get; set; } = new List<LongitudinalAxisSegment>();
    public double LongitudinalAxisConfidence { get; set; } = 1.0;
    public string LongitudinalAxisSource { get; set; } = "MAIN_AXIS";
    public List<PartSnapshot> Parts { get; set; } = new List<PartSnapshot>();
    public List<SectionSample> Samples { get; set; } = new List<SectionSample>();
    public PreClassificationSnapshot PreClassification { get; set; } = new PreClassificationSnapshot();
    public ClassificationResult? Classification { get; set; }
}

public sealed class MemberIdentity
{
    public string MemberId { get; set; } = string.Empty;
    public string AssemblyId { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public string Material { get; set; } = string.Empty;
    public string ProfileString { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public AxisDefinition MainAxis { get; set; } = new AxisDefinition();
    public BoundingBox3D BoundingBox { get; set; } = new BoundingBox3D();
}

public sealed class AxisDefinition
{
    public Point3D Origin { get; set; } = new Point3D();
    public Vector3D Direction { get; set; } = new Vector3D(1, 0, 0);
    public double Length { get; set; }
}

public sealed class LongitudinalAxisSegment
{
    public int Index { get; set; }
    public Point3D Start { get; set; } = new Point3D();
    public Point3D End { get; set; } = new Point3D();
    public Vector3D Direction { get; set; } = new Vector3D(1, 0, 0);
    public double Length { get; set; }
    public double CumulativeStart { get; set; }
    public double CumulativeEnd { get; set; }
}

public sealed class PartSnapshot
{
    public string PartId { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PartType { get; set; } = string.Empty;
    public string ProfileString { get; set; } = string.Empty;
    public string Material { get; set; } = string.Empty;
    public double Thickness { get; set; }
    public double Area { get; set; }
    public double Volume { get; set; }
    public Point3D Centroid { get; set; } = new Point3D();
    public CoordinateSystem3D LocalCoordinateSystem { get; set; } = new CoordinateSystem3D();
    public BoundingBox3D BoundingBox { get; set; } = new BoundingBox3D();
    public List<LineSegment3D> SolidEdges { get; set; } = new List<LineSegment3D>();
    public AxisProjection AxisProjection { get; set; } = new AxisProjection();
    public EndProximity EndProximity { get; set; } = new EndProximity();
    public GeometryHints GeometryHints { get; set; } = new GeometryHints();
    public TopologyHints TopologyHints { get; set; } = new TopologyHints();
    internal PartInputLayerDraftMetadata InputLayerDraft { get; set; } = new PartInputLayerDraftMetadata();
}

public sealed class AxisProjection
{
    public double Start { get; set; }
    public double End { get; set; }
    public double Length { get; set; }
    public double CoverageRatio { get; set; }
}

public sealed class EndProximity
{
    public bool NearStart { get; set; }
    public bool NearEnd { get; set; }
}

public sealed class GeometryHints
{
    public double Planarity { get; set; }
    public bool IsBentLike { get; set; }
    public bool IsFoldedLike { get; set; }
    public bool OuterSideCandidate { get; set; }
}

public sealed class TopologyHints
{
    public List<string> ConnectedPartIds { get; set; } = new List<string>();
    public int IntersectionCount { get; set; }
    public bool SymmetricCandidate { get; set; }
}

public sealed class SectionSample
{
    public string SampleId { get; set; } = string.Empty;
    public double Ratio { get; set; }
    public double Station { get; set; }
    public bool IsAbnormal { get; set; }
    public string AbnormalReason { get; set; } = string.Empty;
    public List<SectionPartHit> SectionParts { get; set; } = new List<SectionPartHit>();
    public SectionFeatures SectionFeatures { get; set; } = new SectionFeatures();
}

public sealed class SectionPartHit
{
    public string PartId { get; set; } = string.Empty;
    public string IntersectionType { get; set; } = string.Empty;
    public int SegmentCount { get; set; }
    public double TotalCutLength { get; set; }
    public double CutSpanX { get; set; }
    public double CutSpanY { get; set; }
    public double Thickness { get; set; }
    public Point2D Center2D { get; set; } = new Point2D();
    public Point2D Direction2D { get; set; } = new Point2D(1, 0);
    public string RoleHint { get; set; } = string.Empty;
}

public sealed class SectionFeatures
{
    public int ClosedLoops { get; set; }
    public int MajorPlateCount { get; set; }
    public double OuterWidth { get; set; }
    public double OuterHeight { get; set; }
    public int CavityCount { get; set; }
    public double HorizontalSymmetryScore { get; set; }
    public double VerticalSymmetryScore { get; set; }
    public int CentralVerticalPlateCount { get; set; }
    public int CentralHorizontalPlateCount { get; set; }
    public double EnclosureScore { get; set; }
    public List<Point2D> OuterLoop { get; set; } = new List<Point2D>();
    public List<List<Point2D>> InnerLoops { get; set; } = new List<List<Point2D>>();
    public string SectionLoopEvidenceSource { get; set; } = string.Empty;
}

public sealed class PreClassificationSnapshot
{
    public List<PartRoleCandidate> PartRoleCandidates { get; set; } = new List<PartRoleCandidate>();
    public List<MemberClassCandidate> MemberClassCandidates { get; set; } = new List<MemberClassCandidate>();
}

public sealed class PartRoleCandidate
{
    public string PartId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public double Score { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class MemberClassCandidate
{
    public string ClassName { get; set; } = string.Empty;
    public double Score { get; set; }
    public string Reason { get; set; } = string.Empty;
}

internal sealed class PartInputLayerDraftMetadata
{
    public string PartModelType { get; set; } = "PATH_SECTION_SWEEP";
    public string RepresentationKind { get; set; } = "PATH_SECTION_SWEEP";
    public string RepresentationLevel { get; set; } = "EXACT";
    public string DistortionRiskCode { get; set; } = "LOW";
    public double? Thickness { get; set; }
    public string ThicknessSource { get; set; } = "UNKNOWN";
    public string ThicknessSourceDetail { get; set; } = string.Empty;
    public string BoundaryLoopKind { get; set; } = "UNRESOLVED";
    public string BoundarySource { get; set; } = "NONE";
    public bool BoundaryClosed { get; set; }
    public string LocalFrameSource { get; set; } = "PART_COORDINATE_SYSTEM";
    public string SectionSource { get; set; } = string.Empty;
    public string SectionProfile { get; set; } = string.Empty;
    public bool HasExplicitBoundary { get; set; }
    public bool HasExplicitThickness { get; set; }
    public bool HasExplicitSection { get; set; }
    public List<Point3D> BoundaryPoints { get; } = new List<Point3D>();
    public List<string> WarningCodes { get; } = new List<string>();
    public List<string> DistortionRiskReasons { get; } = new List<string>();
    public List<string> DegradationReasonCodes { get; } = new List<string>();
}




