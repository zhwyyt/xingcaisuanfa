using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TeklaSectionClassifier.Runner;

[DataContract]
internal sealed class ExportBundle
{
    [DataMember(Name = "schemaVersion", Order = 1)]
    public string SchemaVersion { get; set; } = "tekla-body-bracket-export.v2-plate-face";

    [DataMember(Name = "exportedAtUtc", Order = 2)]
    public string ExportedAtUtc { get; set; } = string.Empty;

    [DataMember(Name = "source", Order = 3)]
    public ExportSourceInfo Source { get; set; } = new ExportSourceInfo();

    [DataMember(Name = "assemblies", Order = 4)]
    public List<ExportedAssembly> Assemblies { get; set; } = new List<ExportedAssembly>();
}

[DataContract]
internal sealed class ExportSourceInfo
{
    [DataMember(Name = "modelPath", Order = 1)]
    public string ModelPath { get; set; } = string.Empty;

    [DataMember(Name = "selectionMode", Order = 2)]
    public string SelectionMode { get; set; } = "selected";

    [DataMember(Name = "exporterVersion", Order = 3)]
    public string ExporterVersion { get; set; } = "xingcai-runner-body-bracket-0.3.3-aligned-span-axis";
}

[DataContract]
internal sealed class ExportedAssembly
{
    [DataMember(Name = "assemblyId", Order = 1)]
    public string AssemblyId { get; set; } = string.Empty;

    [DataMember(Name = "mainPartId", Order = 2)]
    public int MainPartId { get; set; }

    [DataMember(Name = "parts", Order = 3)]
    public List<ExportedPart> Parts { get; set; } = new List<ExportedPart>();

    [DataMember(Name = "relationships", Order = 4)]
    public List<ExportedRelationship> Relationships { get; set; } = new List<ExportedRelationship>();

    [DataMember(Name = "metadata", Order = 5)]
    public ExportedAssemblyMetadata Metadata { get; set; } = new ExportedAssemblyMetadata();
}

[DataContract]
internal sealed class ExportedAssemblyMetadata
{
    [DataMember(Name = "assemblyPosition", EmitDefaultValue = false, Order = 1)]
    public string AssemblyPosition { get; set; } = string.Empty;

    [DataMember(Name = "mainPartName", EmitDefaultValue = false, Order = 2)]
    public string MainPartName { get; set; } = string.Empty;

    [DataMember(Name = "memberAxisEvidence", EmitDefaultValue = false, Order = 3)]
    public ExportedMemberAxisEvidence MemberAxisEvidence { get; set; } = new ExportedMemberAxisEvidence();

    [DataMember(Name = "boxSectionEvidence", EmitDefaultValue = false, Order = 4)]
    public ExportedBoxSectionEvidence BoxSectionEvidence { get; set; } = new ExportedBoxSectionEvidence();

    [DataMember(Name = "hBeamSectionEvidence", EmitDefaultValue = false, Order = 5)]
    public ExportedHBeamSectionEvidence HBeamSectionEvidence { get; set; } = new ExportedHBeamSectionEvidence();

    [DataMember(Name = "externalConnectionEvidence", EmitDefaultValue = false, Order = 6)]
    public ExportedExternalConnectionEvidence ExternalConnectionEvidence { get; set; } = new ExportedExternalConnectionEvidence();
}

[DataContract]
internal sealed class ExportedExternalConnectionEvidence
{
    [DataMember(Name = "source", EmitDefaultValue = false, Order = 1)]
    public string Source { get; set; } = string.Empty;

    [DataMember(Name = "axisLocationToleranceMm", Order = 2)]
    public double AxisLocationToleranceMm { get; set; }

    [DataMember(Name = "axisLocationLengthRatio", Order = 3)]
    public double AxisLocationLengthRatio { get; set; }

    [DataMember(Name = "links", EmitDefaultValue = false, Order = 4)]
    public List<ExportedExternalPartLink> Links { get; set; } = new List<ExportedExternalPartLink>();

    [DataMember(Name = "diagnostics", EmitDefaultValue = false, Order = 5)]
    public List<string> Diagnostics { get; set; } = new List<string>();
}

[DataContract]
internal sealed class ExportedExternalPartLink
{
    [DataMember(Name = "localPartId", Order = 1)]
    public int LocalPartId { get; set; }

    [DataMember(Name = "otherPartId", Order = 2)]
    public int OtherPartId { get; set; }

    [DataMember(Name = "otherAssemblyId", Order = 3)]
    public int OtherAssemblyId { get; set; }

    [DataMember(Name = "relation", Order = 4)]
    public string Relation { get; set; } = string.Empty;

    [DataMember(Name = "shopOrSite", EmitDefaultValue = false, Order = 5)]
    public string ShopOrSite { get; set; } = string.Empty;

    [DataMember(Name = "axisLocation", EmitDefaultValue = false, Order = 6)]
    public string AxisLocation { get; set; } = string.Empty;

    [DataMember(Name = "axisStation", Order = 7)]
    public double AxisStation { get; set; }

    [DataMember(Name = "boltSize", EmitDefaultValue = false, Order = 8)]
    public double BoltSize { get; set; }

    [DataMember(Name = "otherMainPartId", EmitDefaultValue = false, Order = 9)]
    public int OtherMainPartId { get; set; }

    [DataMember(Name = "otherAxisDirection", EmitDefaultValue = false, Order = 10)]
    public ExportedVector3 OtherAxisDirection { get; set; } = new ExportedVector3();

    [DataMember(Name = "otherAxisPose", EmitDefaultValue = false, Order = 11)]
    public string OtherAxisPose { get; set; } = string.Empty;
}


[DataContract]
internal sealed class ExportedHBeamSectionEvidence
{
    [DataMember(Name = "source", EmitDefaultValue = false, Order = 1)]
    public string Source { get; set; } = string.Empty;

    [DataMember(Name = "sectionAxisU", Order = 2)]
    public ExportedVector3 SectionAxisU { get; set; } = new ExportedVector3();

    [DataMember(Name = "sectionAxisV", Order = 3)]
    public ExportedVector3 SectionAxisV { get; set; } = new ExportedVector3();

    [DataMember(Name = "stationFrames", EmitDefaultValue = false, Order = 4)]
    public List<ExportedHBeamSectionStationFrame> StationFrames { get; set; } = new List<ExportedHBeamSectionStationFrame>();

    [DataMember(Name = "diagnostics", EmitDefaultValue = false, Order = 5)]
    public List<string> Diagnostics { get; set; } = new List<string>();
}

[DataContract]
internal sealed class ExportedHBeamSectionStationFrame
{
    [DataMember(Name = "station", Order = 1)]
    public double Station { get; set; }

    [DataMember(Name = "webCenterU", Order = 2)]
    public double WebCenterU { get; set; }

    [DataMember(Name = "webHalfThickness", Order = 3)]
    public double WebHalfThickness { get; set; }

    [DataMember(Name = "topOuterV", Order = 4)]
    public double TopOuterV { get; set; }

    [DataMember(Name = "bottomOuterV", Order = 5)]
    public double BottomOuterV { get; set; }

    [DataMember(Name = "webTopV", Order = 6)]
    public double WebTopV { get; set; }

    [DataMember(Name = "webBottomV", Order = 7)]
    public double WebBottomV { get; set; }

    [DataMember(Name = "topFlangePartId", Order = 8)]
    public int TopFlangePartId { get; set; }

    [DataMember(Name = "bottomFlangePartId", Order = 9)]
    public int BottomFlangePartId { get; set; }

    [DataMember(Name = "webPartId", Order = 10)]
    public int WebPartId { get; set; }

    [DataMember(Name = "partSlices", EmitDefaultValue = false, Order = 11)]
    public List<ExportedHBeamSectionPartSlice> PartSlices { get; set; } = new List<ExportedHBeamSectionPartSlice>();

    [DataMember(Name = "diagnostics", EmitDefaultValue = false, Order = 12)]
    public List<string> Diagnostics { get; set; } = new List<string>();
}

[DataContract]
internal sealed class ExportedHBeamSectionPartSlice
{
    [DataMember(Name = "partId", Order = 1)]
    public int PartId { get; set; }

    [DataMember(Name = "partPosition", EmitDefaultValue = false, Order = 2)]
    public string PartPosition { get; set; } = string.Empty;

    [DataMember(Name = "roleHint", EmitDefaultValue = false, Order = 3)]
    public string RoleHint { get; set; } = string.Empty;

    [DataMember(Name = "center", Order = 4)]
    public ExportedSectionPoint Center { get; set; } = new ExportedSectionPoint();

    [DataMember(Name = "bounds", Order = 5)]
    public ExportedSectionBounds Bounds { get; set; } = new ExportedSectionBounds();

    [DataMember(Name = "spanU", Order = 6)]
    public double SpanU { get; set; }

    [DataMember(Name = "spanV", Order = 7)]
    public double SpanV { get; set; }

    [DataMember(Name = "thickness", Order = 8)]
    public double Thickness { get; set; }

    [DataMember(Name = "points", EmitDefaultValue = false, Order = 9)]
    public List<ExportedSectionPoint> Points { get; set; } = new List<ExportedSectionPoint>();

    [DataMember(Name = "segments", EmitDefaultValue = false, Order = 10)]
    public List<ExportedSectionSegment> Segments { get; set; } = new List<ExportedSectionSegment>();

    [DataMember(Name = "sectionLoops", EmitDefaultValue = false, Order = 11)]
    public List<ExportedSectionLoop> SectionLoops { get; set; } = new List<ExportedSectionLoop>();

    [DataMember(Name = "diagnostics", EmitDefaultValue = false, Order = 12)]
    public List<string> Diagnostics { get; set; } = new List<string>();
}

[DataContract]
internal sealed class ExportedBoxSectionEvidence
{
    [DataMember(Name = "outerLoop", EmitDefaultValue = false, Order = 1)]
    public List<ExportedSectionPoint> OuterLoop { get; set; } = new List<ExportedSectionPoint>();

    [DataMember(Name = "innerLoops", EmitDefaultValue = false, Order = 2)]
    public List<List<ExportedSectionPoint>> InnerLoops { get; set; } = new List<List<ExportedSectionPoint>>();

    [DataMember(Name = "source", EmitDefaultValue = false, Order = 3)]
    public string Source { get; set; } = string.Empty;

    [DataMember(Name = "stationLoops", EmitDefaultValue = false, Order = 4)]
    public List<ExportedBoxSectionStationLoop> StationLoops { get; set; } = new List<ExportedBoxSectionStationLoop>();
}

[DataContract]
internal sealed class ExportedBoxSectionStationLoop
{
    [DataMember(Name = "station", Order = 1)]
    public double Station { get; set; }

    [DataMember(Name = "outerLoop", EmitDefaultValue = false, Order = 2)]
    public List<ExportedSectionPoint> OuterLoop { get; set; } = new List<ExportedSectionPoint>();

    [DataMember(Name = "source", EmitDefaultValue = false, Order = 3)]
    public string Source { get; set; } = string.Empty;

    [DataMember(Name = "supportPartIds", EmitDefaultValue = false, Order = 4)]
    public List<int> SupportPartIds { get; set; } = new List<int>();

    [DataMember(Name = "partLoops", EmitDefaultValue = false, Order = 5)]
    public List<ExportedBoxSectionPartLoop> PartLoops { get; set; } = new List<ExportedBoxSectionPartLoop>();

    [DataMember(Name = "sectionSegments", EmitDefaultValue = false, Order = 6)]
    public List<ExportedSectionSegment> SectionSegments { get; set; } = new List<ExportedSectionSegment>();

    [DataMember(Name = "sectionLoops", EmitDefaultValue = false, Order = 7)]
    public List<ExportedSectionLoop> SectionLoops { get; set; } = new List<ExportedSectionLoop>();

    [DataMember(Name = "closedLoopCount", Order = 8)]
    public int ClosedLoopCount { get; set; }

    [DataMember(Name = "openChainCount", Order = 9)]
    public int OpenChainCount { get; set; }

    [DataMember(Name = "degeneratePartLoopCount", Order = 10)]
    public int DegeneratePartLoopCount { get; set; }

    [DataMember(Name = "diagnostics", EmitDefaultValue = false, Order = 11)]
    public List<string> Diagnostics { get; set; } = new List<string>();
}

[DataContract]
internal sealed class ExportedBoxSectionPartLoop
{
    [DataMember(Name = "partId", Order = 1)]
    public int PartId { get; set; }

    [DataMember(Name = "points", EmitDefaultValue = false, Order = 2)]
    public List<ExportedSectionPoint> Points { get; set; } = new List<ExportedSectionPoint>();

    [DataMember(Name = "segments", EmitDefaultValue = false, Order = 3)]
    public List<ExportedSectionSegment> Segments { get; set; } = new List<ExportedSectionSegment>();

    [DataMember(Name = "sectionLoops", EmitDefaultValue = false, Order = 4)]
    public List<ExportedSectionLoop> SectionLoops { get; set; } = new List<ExportedSectionLoop>();

    [DataMember(Name = "loopStatus", EmitDefaultValue = false, Order = 5)]
    public string LoopStatus { get; set; } = string.Empty;

    [DataMember(Name = "area", Order = 6)]
    public double Area { get; set; }

    [DataMember(Name = "isClosed", Order = 7)]
    public bool IsClosed { get; set; }

    [DataMember(Name = "isValid", Order = 8)]
    public bool IsValid { get; set; }

    [DataMember(Name = "bounds", EmitDefaultValue = false, Order = 9)]
    public ExportedSectionBounds Bounds { get; set; } = new ExportedSectionBounds();

    [DataMember(Name = "diagnostics", EmitDefaultValue = false, Order = 10)]
    public List<string> Diagnostics { get; set; } = new List<string>();
}

[DataContract]
internal sealed class ExportedSectionSegment
{
    [DataMember(Name = "start", Order = 1)]
    public ExportedSectionPoint Start { get; set; } = new ExportedSectionPoint();

    [DataMember(Name = "end", Order = 2)]
    public ExportedSectionPoint End { get; set; } = new ExportedSectionPoint();

    [DataMember(Name = "length", Order = 3)]
    public double Length { get; set; }

    [DataMember(Name = "sourcePartId", Order = 4)]
    public int SourcePartId { get; set; }

    [DataMember(Name = "sourceFaceIndex", Order = 5)]
    public int SourceFaceIndex { get; set; }

    [DataMember(Name = "sourceLoopIndex", Order = 6)]
    public int SourceLoopIndex { get; set; }

    [DataMember(Name = "sourceEdgeIndex", Order = 7)]
    public int SourceEdgeIndex { get; set; }

    [DataMember(Name = "intersectionKind", EmitDefaultValue = false, Order = 8)]
    public string IntersectionKind { get; set; } = string.Empty;
}

[DataContract]
internal sealed class ExportedSectionLoop
{
    [DataMember(Name = "points", EmitDefaultValue = false, Order = 1)]
    public List<ExportedSectionPoint> Points { get; set; } = new List<ExportedSectionPoint>();

    [DataMember(Name = "area", Order = 2)]
    public double Area { get; set; }

    [DataMember(Name = "isClosed", Order = 3)]
    public bool IsClosed { get; set; }

    [DataMember(Name = "isValid", Order = 4)]
    public bool IsValid { get; set; }

    [DataMember(Name = "bounds", EmitDefaultValue = false, Order = 5)]
    public ExportedSectionBounds Bounds { get; set; } = new ExportedSectionBounds();

    [DataMember(Name = "source", EmitDefaultValue = false, Order = 6)]
    public string Source { get; set; } = string.Empty;
}

[DataContract]
internal sealed class ExportedSectionBounds
{
    [DataMember(Name = "min", Order = 1)]
    public ExportedSectionPoint Min { get; set; } = new ExportedSectionPoint();

    [DataMember(Name = "max", Order = 2)]
    public ExportedSectionPoint Max { get; set; } = new ExportedSectionPoint();
}

[DataContract]
internal sealed class ExportedPart
{
    [DataMember(Name = "partId", Order = 1)]
    public int PartId { get; set; }

    [DataMember(Name = "runtimeType", Order = 2)]
    public string RuntimeType { get; set; } = string.Empty;

    [DataMember(Name = "name", Order = 3)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "material", Order = 4)]
    public string Material { get; set; } = string.Empty;

    [DataMember(Name = "profileString", Order = 5)]
    public string ProfileString { get; set; } = string.Empty;

    [DataMember(Name = "isPlateLike", Order = 6)]
    public bool IsPlateLike { get; set; }

    [DataMember(Name = "isSpecialShape", Order = 7)]
    public bool IsSpecialShape { get; set; }

    [DataMember(Name = "centroid", Order = 8)]
    public ExportedVector3 Centroid { get; set; } = new ExportedVector3();

    [DataMember(Name = "obbDims", Order = 9)]
    public ExportedVector3 ObbDims { get; set; } = new ExportedVector3();

    [DataMember(Name = "boundingBox", Order = 10)]
    public ExportedBoundingBox BoundingBox { get; set; } = new ExportedBoundingBox();

    [DataMember(Name = "volume", Order = 11)]
    public double Volume { get; set; }

    [DataMember(Name = "surfaceArea", Order = 12)]
    public double SurfaceArea { get; set; }

    [DataMember(Name = "thickness", EmitDefaultValue = false, Order = 13)]
    public double? Thickness { get; set; }

    [DataMember(Name = "plateNormal", Order = 14)]
    public ExportedVector3 PlateNormal { get; set; } = new ExportedVector3();

    [DataMember(Name = "plateLongDirection", Order = 15)]
    public ExportedVector3 PlateLongDirection { get; set; } = new ExportedVector3();

    [DataMember(Name = "plateWidthDirection", Order = 16)]
    public ExportedVector3 PlateWidthDirection { get; set; } = new ExportedVector3();

    [DataMember(Name = "partPosition", EmitDefaultValue = false, Order = 17)]
    public string PartPosition { get; set; } = string.Empty;

    [DataMember(Name = "contourVertexCount", Order = 18)]
    public int ContourVertexCount { get; set; }

    [DataMember(Name = "concaveCornerCount", Order = 18)]
    public int ConcaveCornerCount { get; set; }

    [DataMember(Name = "holeLikeFeatureCount", Order = 19)]
    public int HoleLikeFeatureCount { get; set; }

    [DataMember(Name = "booleanCutCount", Order = 20)]
    public int BooleanCutCount { get; set; }

    [DataMember(Name = "booleanAddCount", Order = 21)]
    public int BooleanAddCount { get; set; }

    [DataMember(Name = "shopWeldDegree", Order = 22)]
    public double ShopWeldDegree { get; set; }

    [DataMember(Name = "siteWeldDegree", Order = 23)]
    public double SiteWeldDegree { get; set; }

    [DataMember(Name = "contourPoints", EmitDefaultValue = false, Order = 24)]
    public List<ExportedVector3> ContourPoints { get; set; } = new List<ExportedVector3>();

    // === P0: 轮廓段详情（含类型：Line/Arc） ===
    [DataMember(Name = "contourSegments", EmitDefaultValue = false, Order = 25)]
    public List<ExportedContourSegment> ContourSegments { get; set; } = new List<ExportedContourSegment>();

    // === P0: 螺栓孔详情 ===
    [DataMember(Name = "boltHoles", EmitDefaultValue = false, Order = 26)]
    public List<ExportedBoltHole> BoltHoles { get; set; } = new List<ExportedBoltHole>();

    // === P0: 螺栓孔总数（含直径信息） ===
    [DataMember(Name = "boltHoleCount", Order = 27)]
    public int BoltHoleCount { get; set; }

    // === P1: 焊缝详情 ===
    [DataMember(Name = "weldDetails", EmitDefaultValue = false, Order = 28)]
    public List<ExportedWeldDetail> WeldDetails { get; set; } = new List<ExportedWeldDetail>();

    // === P1: 零件坐标系 ===
    [DataMember(Name = "partCoordinateSystem", EmitDefaultValue = false, Order = 29)]
    public ExportedCoordinateSystem PartCoordinateSystem { get; set; }

    // === P1: Boolean切割详情 ===
    [DataMember(Name = "booleanCutDetails", EmitDefaultValue = false, Order = 30)]
    public List<ExportedBooleanCut> BooleanCutDetails { get; set; } = new List<ExportedBooleanCut>();

    // === P2: 自定义属性 ===
    [DataMember(Name = "customProperties", EmitDefaultValue = false, Order = 31)]
    public Dictionary<string, string> CustomProperties { get; set; } = new Dictionary<string, string>();

    // === 辅助：零件是否有弧线段轮廓 ===
    [DataMember(Name = "hasArcContour", Order = 32)]
    public bool HasArcContour { get; set; }

    [DataMember(Name = "mainMaterialEvidence", EmitDefaultValue = false, Order = 33)]
    public ExportedMainMaterialEvidence MainMaterialEvidence { get; set; } = new ExportedMainMaterialEvidence();

    // 剖口：EdgeChamfer / 焊缝坡口 / 相对板厚倾斜的 Fitting、CutPlane。不是轮廓倒角。
    [DataMember(Name = "edgeBevels", EmitDefaultValue = false, Order = 34)]
    public List<ExportedEdgeBevel> EdgeBevels { get; set; } = new List<ExportedEdgeBevel>();

    [DataMember(Name = "edgeBevelCount", Order = 35)]
    public int EdgeBevelCount { get; set; }

    [DataMember(Name = "hasEdgeBevel", Order = 36)]
    public bool HasEdgeBevel { get; set; }

    [DataMember(Name = "booleanObjectTypes", EmitDefaultValue = false, Order = 37)]
    public List<string> BooleanObjectTypes { get; set; } = new List<string>();

    [DataMember(Name = "endChamferCount", Order = 38)]
    public int EndChamferCount { get; set; }

    [DataMember(Name = "hasEndChamfer", Order = 39)]
    public bool HasEndChamfer { get; set; }
}


[DataContract]
internal sealed class ExportedMemberAxisEvidence
{
    [DataMember(Name = "origin", Order = 1)]
    public ExportedVector3 Origin { get; set; } = new ExportedVector3();

    [DataMember(Name = "direction", Order = 2)]
    public ExportedVector3 Direction { get; set; } = new ExportedVector3();

    [DataMember(Name = "length", Order = 3)]
    public double Length { get; set; }

    [DataMember(Name = "source", EmitDefaultValue = false, Order = 4)]
    public string Source { get; set; } = string.Empty;
}

[DataContract]
internal sealed class ExportedMainMaterialEvidence
{
    [DataMember(Name = "axisStationStart", Order = 1)]
    public double AxisStationStart { get; set; }

    [DataMember(Name = "axisStationEnd", Order = 2)]
    public double AxisStationEnd { get; set; }

    [DataMember(Name = "axisStationLength", Order = 3)]
    public double AxisStationLength { get; set; }

    [DataMember(Name = "bodyFaceId", EmitDefaultValue = false, Order = 4)]
    public string BodyFaceId { get; set; } = string.Empty;

    [DataMember(Name = "bodyFaceOffset", Order = 5)]
    public double BodyFaceOffset { get; set; }

    [DataMember(Name = "isBodyWallPlateCandidate", Order = 6)]
    public bool IsBodyWallPlateCandidate { get; set; }

    [DataMember(Name = "evidenceSource", EmitDefaultValue = false, Order = 7)]
    public string EvidenceSource { get; set; } = string.Empty;

    [DataMember(Name = "segmentAdjacency", EmitDefaultValue = false, Order = 8)]
    public ExportedSegmentAdjacencyEvidence SegmentAdjacency { get; set; } = new ExportedSegmentAdjacencyEvidence();

    [DataMember(Name = "sectionProjectionEvidence", EmitDefaultValue = false, Order = 9)]
    public ExportedSectionProjectionEvidence SectionProjectionEvidence { get; set; } = new ExportedSectionProjectionEvidence();
}

[DataContract]
internal sealed class ExportedSectionProjectionEvidence
{
    [DataMember(Name = "evidenceSource", EmitDefaultValue = false, Order = 1)]
    public string EvidenceSource { get; set; } = string.Empty;

    [DataMember(Name = "sectionPlaneOrigin", Order = 2)]
    public ExportedVector3 SectionPlaneOrigin { get; set; } = new ExportedVector3();

    [DataMember(Name = "sectionAxisU", Order = 3)]
    public ExportedVector3 SectionAxisU { get; set; } = new ExportedVector3();

    [DataMember(Name = "sectionAxisV", Order = 4)]
    public ExportedVector3 SectionAxisV { get; set; } = new ExportedVector3();

    [DataMember(Name = "projectedCentroid", Order = 5)]
    public ExportedSectionPoint ProjectedCentroid { get; set; } = new ExportedSectionPoint();

    [DataMember(Name = "projectedBoundsMin", Order = 6)]
    public ExportedSectionPoint ProjectedBoundsMin { get; set; } = new ExportedSectionPoint();

    [DataMember(Name = "projectedBoundsMax", Order = 7)]
    public ExportedSectionPoint ProjectedBoundsMax { get; set; } = new ExportedSectionPoint();

    [DataMember(Name = "projectedContour", EmitDefaultValue = false, Order = 8)]
    public List<ExportedSectionPoint> ProjectedContour { get; set; } = new List<ExportedSectionPoint>();

    [DataMember(Name = "normalProjection", Order = 9)]
    public ExportedSectionPoint NormalProjection { get; set; } = new ExportedSectionPoint();

    [DataMember(Name = "normalProjectionMagnitude", Order = 10)]
    public double NormalProjectionMagnitude { get; set; }

    [DataMember(Name = "pointSource", EmitDefaultValue = false, Order = 11)]
    public string PointSource { get; set; } = string.Empty;
}

[DataContract]
internal sealed class ExportedSectionPoint
{
    [DataMember(Name = "u", Order = 1)]
    public double U { get; set; }

    [DataMember(Name = "v", Order = 2)]
    public double V { get; set; }
}

[DataContract]
internal sealed class ExportedSegmentAdjacencyEvidence
{
    [DataMember(Name = "previousPartId", Order = 1)]
    public int PreviousPartId { get; set; }

    [DataMember(Name = "nextPartId", Order = 2)]
    public int NextPartId { get; set; }

    [DataMember(Name = "previousGap", Order = 3)]
    public double PreviousGap { get; set; }

    [DataMember(Name = "nextGap", Order = 4)]
    public double NextGap { get; set; }

    [DataMember(Name = "evidenceSource", EmitDefaultValue = false, Order = 5)]
    public string EvidenceSource { get; set; } = string.Empty;
}
[DataContract]
internal sealed class ExportedRelationship
{
    [DataMember(Name = "partIdA", Order = 1)]
    public int PartIdA { get; set; }

    [DataMember(Name = "partIdB", Order = 2)]
    public int PartIdB { get; set; }

    [DataMember(Name = "edgeType", Order = 3)]
    public string EdgeType { get; set; } = string.Empty;

    [DataMember(Name = "strength", Order = 4)]
    public double Strength { get; set; }

    [DataMember(Name = "geometricSupport", Order = 5)]
    public double GeometricSupport { get; set; }

    [DataMember(Name = "meta", EmitDefaultValue = false, Order = 6)]
    public string Meta { get; set; } = string.Empty;
}

[DataContract]
internal sealed class ExportedBoundingBox
{
    [DataMember(Name = "min", Order = 1)]
    public ExportedVector3 Min { get; set; } = new ExportedVector3();

    [DataMember(Name = "max", Order = 2)]
    public ExportedVector3 Max { get; set; } = new ExportedVector3();
}

[DataContract]
internal sealed class ExportedVector3
{
    [DataMember(Name = "x", Order = 1)]
    public double X { get; set; }

    [DataMember(Name = "y", Order = 2)]
    public double Y { get; set; }

    [DataMember(Name = "z", Order = 3)]
    public double Z { get; set; }
}

[DataContract]
internal sealed class AssemblyLabelTemplate
{
    [DataMember(Name = "assemblyId", Order = 1)]
    public string AssemblyId { get; set; } = string.Empty;

    [DataMember(Name = "expectedBodyFamily", EmitDefaultValue = false, Order = 2)]
    public string ExpectedBodyFamily { get; set; } = string.Empty;

    [DataMember(Name = "expectedCoreBodyPartIds", Order = 3)]
    public List<int> ExpectedCoreBodyPartIds { get; set; } = new List<int>();

    [DataMember(Name = "expectedAccessoryPartIds", Order = 4)]
    public List<int> ExpectedAccessoryPartIds { get; set; } = new List<int>();

    [DataMember(Name = "expectedBrackets", Order = 5)]
    public List<ExpectedBracketLabel> ExpectedBrackets { get; set; } = new List<ExpectedBracketLabel>();

    [DataMember(Name = "notes", EmitDefaultValue = false, Order = 6)]
    public string Notes { get; set; } = string.Empty;
}

[DataContract]
internal sealed class ExpectedBracketLabel
{
    [DataMember(Name = "type", EmitDefaultValue = false, Order = 1)]
    public string Type { get; set; } = string.Empty;

    [DataMember(Name = "partIds", Order = 2)]
    public List<int> PartIds { get; set; } = new List<int>();
}

// === 新增DTO: 轮廓段详情 ===
[DataContract]
internal sealed class ExportedContourSegment
{
    [DataMember(Name = "startPoint", Order = 1)]
    public ExportedVector3 StartPoint { get; set; } = new ExportedVector3();

    [DataMember(Name = "endPoint", Order = 2)]
    public ExportedVector3 EndPoint { get; set; } = new ExportedVector3();

    [DataMember(Name = "chamferType", Order = 3)]
    public string ChamferType { get; set; } = "None";  // None=直线, Line=直线倒角, Arc=弧线, Fillet=圆角

    [DataMember(Name = "chamferX", Order = 4)]
    public double ChamferX { get; set; }

    [DataMember(Name = "chamferY", Order = 5)]
    public double ChamferY { get; set; }

    [DataMember(Name = "length", Order = 6)]
    public double Length { get; set; }
}

// === 新增DTO: 螺栓孔详情 ===
[DataContract]
internal sealed class ExportedBoltHole
{
    [DataMember(Name = "position", Order = 1)]
    public ExportedVector3 Position { get; set; } = new ExportedVector3();

    [DataMember(Name = "diameter", Order = 2)]
    public double Diameter { get; set; }

    [DataMember(Name = "boltGroupType", Order = 3)]
    public string BoltGroupType { get; set; } = string.Empty;  // 锚栓/高强螺栓/普通螺栓

    [DataMember(Name = "boltGroupPartIdA", Order = 4)]
    public int BoltGroupPartIdA { get; set; }

    [DataMember(Name = "boltGroupPartIdB", Order = 5)]
    public int BoltGroupPartIdB { get; set; }
}

// === 新增DTO: 焊缝详情 ===
[DataContract]
internal sealed class ExportedWeldDetail
{
    [DataMember(Name = "mainPartId", Order = 1)]
    public int MainPartId { get; set; }

    [DataMember(Name = "secondaryPartId", Order = 2)]
    public int SecondaryPartId { get; set; }

    [DataMember(Name = "weldType", Order = 3)]
    public string WeldType { get; set; } = string.Empty;

    [DataMember(Name = "sizeAbove", Order = 4)]
    public double SizeAbove { get; set; }

    [DataMember(Name = "sizeBelow", Order = 5)]
    public double SizeBelow { get; set; }

    [DataMember(Name = "length", Order = 6)]
    public double Length { get; set; }

    [DataMember(Name = "shopWeld", Order = 7)]
    public bool ShopWeld { get; set; }

    [DataMember(Name = "aroundWeld", Order = 8)]
    public bool AroundWeld { get; set; }
}

// === 新增DTO: 坐标系 ===
[DataContract]
internal sealed class ExportedCoordinateSystem
{
    [DataMember(Name = "origin", Order = 1)]
    public ExportedVector3 Origin { get; set; } = new ExportedVector3();

    [DataMember(Name = "axisX", Order = 2)]
    public ExportedVector3 AxisX { get; set; } = new ExportedVector3();

    [DataMember(Name = "axisY", Order = 3)]
    public ExportedVector3 AxisY { get; set; } = new ExportedVector3();

    [DataMember(Name = "axisZ", Order = 4)]
    public ExportedVector3 AxisZ { get; set; } = new ExportedVector3();
}

// === 新增DTO: Boolean切割详情 ===
[DataContract]
internal sealed class ExportedBooleanCut
{
    [DataMember(Name = "operativePartId", Order = 1)]
    public int OperativePartId { get; set; }

    [DataMember(Name = "cutType", Order = 2)]
    public string CutType { get; set; } = string.Empty;  // Cut/Fit

    [DataMember(Name = "boundingBox", Order = 3)]
    public ExportedBoundingBox BoundingBox { get; set; } = new ExportedBoundingBox();

    [DataMember(Name = "cutsFatherSolid", Order = 4)]
    public bool CutsFatherSolid { get; set; }

    [DataMember(Name = "cutProof", EmitDefaultValue = false, Order = 5)]
    public string CutProof { get; set; } = string.Empty;
}

[DataContract]
internal sealed class ExportedEdgeBevel
{
    [DataMember(Name = "booleanId", Order = 1)]
    public int BooleanId { get; set; }

    [DataMember(Name = "kind", Order = 2)]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "operativePartId", Order = 3)]
    public int OperativePartId { get; set; }

    [DataMember(Name = "name", EmitDefaultValue = false, Order = 4)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "chamferType", EmitDefaultValue = false, Order = 5)]
    public string ChamferType { get; set; } = string.Empty;

    [DataMember(Name = "chamferX", Order = 6)]
    public double ChamferX { get; set; }

    [DataMember(Name = "chamferY", Order = 7)]
    public double ChamferY { get; set; }

    [DataMember(Name = "dz1", Order = 8)]
    public double Dz1 { get; set; }

    [DataMember(Name = "dz2", Order = 9)]
    public double Dz2 { get; set; }

    [DataMember(Name = "firstBevelDimension", Order = 10)]
    public double FirstBevelDimension { get; set; }

    [DataMember(Name = "secondBevelDimension", Order = 11)]
    public double SecondBevelDimension { get; set; }

    [DataMember(Name = "firstEndType", EmitDefaultValue = false, Order = 12)]
    public string FirstEndType { get; set; } = string.Empty;

    [DataMember(Name = "secondEndType", EmitDefaultValue = false, Order = 13)]
    public string SecondEndType { get; set; } = string.Empty;

    [DataMember(Name = "firstEnd", EmitDefaultValue = false, Order = 14)]
    public ExportedVector3 FirstEnd { get; set; } = new ExportedVector3();

    [DataMember(Name = "secondEnd", EmitDefaultValue = false, Order = 15)]
    public ExportedVector3 SecondEnd { get; set; } = new ExportedVector3();

    [DataMember(Name = "planeOrigin", EmitDefaultValue = false, Order = 16)]
    public ExportedVector3 PlaneOrigin { get; set; } = new ExportedVector3();

    [DataMember(Name = "planeNormal", EmitDefaultValue = false, Order = 17)]
    public ExportedVector3 PlaneNormal { get; set; } = new ExportedVector3();

    [DataMember(Name = "angleToPlateNormalDeg", Order = 18)]
    public double AngleToPlateNormalDeg { get; set; }

    [DataMember(Name = "isBevel", Order = 19)]
    public bool IsBevel { get; set; }

    [DataMember(Name = "boundingBox", EmitDefaultValue = false, Order = 20)]
    public ExportedBoundingBox BoundingBox { get; set; } = new ExportedBoundingBox();

    [DataMember(Name = "cutsFatherSolid", Order = 21)]
    public bool CutsFatherSolid { get; set; }

    [DataMember(Name = "cutProof", EmitDefaultValue = false, Order = 22)]
    public string CutProof { get; set; } = string.Empty;
}








