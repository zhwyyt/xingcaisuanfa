using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace TeklaSectionClassifier.Runner;

internal static class BodyBracketBundleExporter
{
    private const string ExporterVersion = "xingcai-runner-body-bracket-0.3.3-aligned-span-axis";

    private sealed class PartObjectCache
    {
        public List<BooleanPart> Booleans { get; } = new List<BooleanPart>();
        public List<BoltGroup> Bolts { get; } = new List<BoltGroup>();
        public List<BaseWeld> Welds { get; } = new List<BaseWeld>();
    }

    public static string Write(Model model, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var assemblies = CollectSelectedAssemblies();
        if (assemblies.Count == 0)
        {
            throw new InvalidOperationException("没有从当前选择集中解析出可导出的 assembly。请先在 Tekla 中选择 assembly 或属于该 assembly 的零件。");
        }

        Console.WriteLine("开始导出 Body/Bracket Bundle: " + assemblies.Count.ToString(CultureInfo.InvariantCulture) + " 根装配");
        Console.Out.Flush();

        var indexWatch = Stopwatch.StartNew();
        var bevelByFather = IndexBevelObjectsByFather(model);
        indexWatch.Stop();
        Console.WriteLine(
            "剖口对象索引完成: " +
            bevelByFather.Count.ToString(CultureInfo.InvariantCulture) +
            " 个父零件，耗时 " +
            indexWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
            "s");
        Console.Out.Flush();

        var ordered = assemblies.OrderBy(assembly => assembly.Identifier.ID).ToList();
        var exported = new List<ExportedAssembly>();
        var exportWatch = Stopwatch.StartNew();
        for (var i = 0; i < ordered.Count; i++)
        {
            var assembly = ordered[i];
            var item = BuildAssemblyExport(model, assembly, bevelByFather);
            if (item != null)
            {
                exported.Add(item);
            }

            var count = i + 1;
            if (count == 1 || count % 10 == 0 || count == ordered.Count)
            {
                var label = item != null && item.Metadata != null && !string.IsNullOrWhiteSpace(item.Metadata.AssemblyPosition)
                    ? item.Metadata.AssemblyPosition
                    : assembly.Identifier.ID.ToString(CultureInfo.InvariantCulture);
                Console.WriteLine(
                    "Bundle进度: 已处理 " +
                    count.ToString(CultureInfo.InvariantCulture) +
                    "/" +
                    ordered.Count.ToString(CultureInfo.InvariantCulture) +
                    " 根装配，当前 " +
                    label);
                Console.Out.Flush();
            }
        }

        var bundle = new ExportBundle
        {
            ExportedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Source = new ExportSourceInfo
            {
                ModelPath = TryGetModelPath(model),
                SelectionMode = "selected",
                ExporterVersion = ExporterVersion
            },
            Assemblies = exported
        };

        var exportPath = Path.Combine(outputDirectory, "tekla-body-bracket-export.bundle.json");
        WriteJson(exportPath, bundle);
        exportWatch.Stop();

        var summary = new StringBuilder();
        summary.AppendLine("Tekla body/bracket export completed.");
        summary.AppendLine("Exporter: " + ExporterVersion);
        summary.AppendLine("Assemblies: " + bundle.Assemblies.Count.ToString(CultureInfo.InvariantCulture));
        summary.AppendLine("Bevel index seconds: " + indexWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture));
        summary.AppendLine("Assembly export seconds: " + exportWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture));
        summary.AppendLine("Output: " + exportPath);
        File.WriteAllText(Path.Combine(outputDirectory, "README-body-bracket-export.txt"), summary.ToString(), Encoding.UTF8);

        Console.WriteLine(
            "Bundle 写出完成，装配导出+序列化 " +
            exportWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
            "s");
        Console.Out.Flush();
        return exportPath;
    }

    private static List<Assembly> CollectSelectedAssemblies()
    {
        var selector = new Tekla.Structures.Model.UI.ModelObjectSelector();
        var selected = selector.GetSelectedObjects();
        var assemblies = new Dictionary<int, Assembly>();

        while (selected.MoveNext())
        {
            var assembly = ResolveAssembly(selected.Current as ModelObject);
            if (assembly == null)
            {
                continue;
            }

            assemblies[assembly.Identifier.ID] = assembly;
        }

        return assemblies.Values.ToList();
    }

    private static Assembly ResolveAssembly(ModelObject modelObject)
    {
        if (modelObject is Assembly assembly)
        {
            return assembly;
        }

        if (modelObject is Part part)
        {
            return part.GetAssembly();
        }

        return null;
    }

    private static ExportedAssembly BuildAssemblyExport(
        Model model,
        Assembly assembly,
        IReadOnlyDictionary<int, List<ModelObject>> bevelByFather)
    {
        var mainPart = assembly.GetMainPart() as Part;
        if (mainPart == null)
        {
            return null;
        }

        var parts = GetAssemblyParts(assembly)
            .GroupBy(part => part.Identifier.ID)
            .ToDictionary(group => group.Key, group => group.First());

        var memberAxisEvidence = BuildMemberAxisEvidence(parts.Values.ToList(), mainPart);
        var partCaches = new Dictionary<int, PartObjectCache>();
        var exportedParts = new List<ExportedPart>();
        foreach (var part in parts.Values.OrderBy(item => item.Identifier.ID))
        {
            var cache = LoadPartObjectCache(part);
            partCaches[part.Identifier.ID] = cache;
            var exported = BuildPartExport(model, part, memberAxisEvidence, bevelByFather, cache);
            if (exported != null)
            {
                exportedParts.Add(exported);
            }
        }

        ApplySegmentAdjacencyEvidence(exportedParts);
        var teklaPartList = parts.Values.ToList();
        var boxSectionEvidence = BuildBoxSectionEvidence(teklaPartList, exportedParts, memberAxisEvidence);
        var hBeamSectionEvidence = BuildHBeamSectionEvidence(teklaPartList, exportedParts, memberAxisEvidence);

        return new ExportedAssembly
        {
            AssemblyId = assembly.Identifier.ID.ToString(CultureInfo.InvariantCulture),
            MainPartId = mainPart.Identifier.ID,
            Parts = exportedParts,
            Relationships = BuildRelationships(parts.Values.ToList(), exportedParts, partCaches),
            Metadata = new ExportedAssemblyMetadata
            {
                AssemblyPosition = TryGetStringReportProperty(assembly, "ASSEMBLY_POS") ?? string.Empty,
                MainPartName = mainPart.Name ?? string.Empty,
                MemberAxisEvidence = memberAxisEvidence,
                BoxSectionEvidence = boxSectionEvidence,
                HBeamSectionEvidence = hBeamSectionEvidence,
                ExternalConnectionEvidence = BuildExternalConnectionEvidence(model, parts, partCaches, memberAxisEvidence)
            }
        };
    }

    private static IEnumerable<Part> GetAssemblyParts(Assembly assembly)
    {
        var parts = new List<Part>();
        var mainPart = assembly.GetMainPart() as Part;
        if (mainPart != null)
        {
            parts.Add(mainPart);
        }

        var secondaries = assembly.GetSecondaries();
        foreach (var secondary in secondaries.OfType<Part>())
        {
            parts.Add(secondary);
        }

        return parts
            .Where(part => part != null)
            .GroupBy(part => part.Identifier.ID)
            .Select(group => group.First());
    }

    private static ExportedPart BuildPartExport(
        Model model,
        Part part,
        ExportedMemberAxisEvidence memberAxisEvidence,
        IReadOnlyDictionary<int, List<ModelObject>> bevelByFather,
        PartObjectCache cache)
    {
        var runtimeType = MapRuntimeType(part);
        var profileString = part.Profile != null ? part.Profile.ProfileString ?? string.Empty : string.Empty;
        var isPlateLike = part is ContourPlate || IsPlateProfile(profileString);
        var isSpecialShape =
            runtimeType.IndexOf("bent", StringComparison.OrdinalIgnoreCase) >= 0 ||
            runtimeType.IndexOf("lofted", StringComparison.OrdinalIgnoreCase) >= 0 ||
            runtimeType.Equals("PolyBeam", StringComparison.OrdinalIgnoreCase);

        var coordinateSystem = part.GetCoordinateSystem();
        var axisX = Normalize(ToVector(coordinateSystem.AxisX));
        var axisY = Normalize(ToVector(coordinateSystem.AxisY));
        var axisZ = Normalize(Cross(axisX, axisY));

        var globalBox = GetGlobalBoundingBox(part);
        var localBox = GetLocalBoundingBox(model, part, coordinateSystem);
        var thickness = ResolveThickness(part, profileString, localBox);
        var contourPoints = GetContourPoints(part, coordinateSystem, localBox, isPlateLike, isSpecialShape, thickness);
        var contourSegments = GetContourSegments(part, contourPoints);
        var hasArcContour = contourSegments.Any(IsArcContourSegment);
        var weldStats = GetWeldStrengths(cache.Welds);
        var weldDetails = GetWeldDetails(cache.Welds);
        var booleanCounts = GetBooleanCounts(cache.Booleans);
        var booleanCutDetails = GetBooleanCutDetails(part, cache.Booleans);
        var boltHoles = GetBoltHoles(cache.Bolts);
        var partCoordSys = ToExportedCoordinateSystem(coordinateSystem);
        var center = GetCenter(globalBox);

        ExportedVector3 plateNormal;
        ExportedVector3 plateLongDirection;
        ExportedVector3 plateWidthDirection;
        if (isPlateLike)
        {
            var localLengthX = Math.Abs(localBox.Max.X - localBox.Min.X);
            var localLengthY = Math.Abs(localBox.Max.Y - localBox.Min.Y);
            var useXAsLong = localLengthX >= localLengthY;
            plateNormal = ToExportedVector3(axisZ);
            plateLongDirection = ToExportedVector3(useXAsLong ? axisX : axisY);
            plateWidthDirection = ToExportedVector3(useXAsLong ? axisY : axisX);
        }
        else
        {
            plateNormal = ToExportedVector3(axisZ);
            plateLongDirection = ToExportedVector3(axisX);
            plateWidthDirection = ToExportedVector3(axisY);
        }

        List<ModelObject> extraBevels;
        if (bevelByFather == null || !bevelByFather.TryGetValue(part.Identifier.ID, out extraBevels))
        {
            extraBevels = new List<ModelObject>();
        }

        List<string> booleanObjectTypes;
        int endChamferCount;
        var edgeBevels = GetEdgeBevels(
            part,
            ToVector(plateNormal),
            ToVector(plateLongDirection),
            profileString,
            isPlateLike,
            thickness,
            globalBox,
            extraBevels,
            cache.Booleans,
            out booleanObjectTypes,
            out endChamferCount);
        var bevelCount = 0;
        var bevelBooleanCuts = 0;
        foreach (var item in edgeBevels)
        {
            if (!item.IsBevel)
            {
                continue;
            }

            bevelCount++;
            if (string.Equals(item.Kind, "BOOLEAN_CUT", StringComparison.OrdinalIgnoreCase))
            {
                bevelBooleanCuts++;
            }
        }

        var holeCuts = Math.Max(0, booleanCounts.Cuts - bevelBooleanCuts - endChamferCount);

        return new ExportedPart
        {
            PartId = part.Identifier.ID,
            RuntimeType = runtimeType,
            Name = part.Name ?? string.Empty,
            Material = part.Material != null ? part.Material.MaterialString ?? string.Empty : string.Empty,
            ProfileString = profileString,
            IsPlateLike = isPlateLike,
            IsSpecialShape = isSpecialShape,
            Centroid = ToExportedVector3(center),
            ObbDims = ToExportedVector3(new Vector(localBox.Max.X - localBox.Min.X, localBox.Max.Y - localBox.Min.Y, localBox.Max.Z - localBox.Min.Z)),
            BoundingBox = new ExportedBoundingBox
            {
                Min = ToExportedVector3(globalBox.Min),
                Max = ToExportedVector3(globalBox.Max)
            },
            Volume = TryGetDoubleReportProperty(part, "VOLUME"),
            SurfaceArea = TryGetDoubleReportProperty(part, "AREA"),
            Thickness = thickness,
            PlateNormal = plateNormal,
            PlateLongDirection = plateLongDirection,
            PlateWidthDirection = plateWidthDirection,
            PartPosition = TryGetStringReportProperty(part, "PART_POS") ?? string.Empty,
            ContourVertexCount = contourPoints.Count,
            ConcaveCornerCount = ComputeConcaveCornerCount(contourPoints, coordinateSystem),
            HoleLikeFeatureCount = boltHoles.Count + holeCuts,
            BooleanCutCount = booleanCounts.Cuts,
            BooleanAddCount = booleanCounts.Adds,
            ShopWeldDegree = weldStats.Shop,
            SiteWeldDegree = weldStats.Site,
            ContourPoints = contourPoints,
            // 新增字段
            ContourSegments = contourSegments,
            HasArcContour = hasArcContour,
            BoltHoles = boltHoles,
            BoltHoleCount = boltHoles.Count,
            WeldDetails = weldDetails,
            PartCoordinateSystem = partCoordSys,
            BooleanCutDetails = booleanCutDetails,
            MainMaterialEvidence = BuildMainMaterialEvidence(isPlateLike, profileString, center, globalBox, contourPoints, plateNormal, memberAxisEvidence),
            EdgeBevels = edgeBevels,
            EdgeBevelCount = bevelCount,
            HasEdgeBevel = bevelCount > 0,
            BooleanObjectTypes = booleanObjectTypes,
            EndChamferCount = endChamferCount,
            HasEndChamfer = endChamferCount > 0
        };
    }

    private static ExportedMemberAxisEvidence BuildMemberAxisEvidence(
        IReadOnlyCollection<Part> parts,
        Part fallbackPart)
    {
        var samples = new List<AlignedSpanPartInput>();
        foreach (var part in parts)
        {
            var sample = TeklaPartAxisSampler.TryRead(part);
            if (sample != null)
            {
                samples.Add(sample);
            }
        }

        if (samples.Count == 0 && fallbackPart != null)
        {
            var sample = TeklaPartAxisSampler.TryRead(fallbackPart);
            if (sample != null)
            {
                samples.Add(sample);
            }
        }

        var resolved = AlignedSpanMemberAxis.Resolve(samples);
        if (!resolved.HasAxis)
        {
            return new ExportedMemberAxisEvidence { Source = AlignedSpanMemberAxis.SourceName };
        }

        return new ExportedMemberAxisEvidence
        {
            Origin = ToExportedVector3(new Point(resolved.Axis.Origin.X, resolved.Axis.Origin.Y, resolved.Axis.Origin.Z)),
            Direction = ToExportedVector3(new Vector(resolved.Axis.Direction.X, resolved.Axis.Direction.Y, resolved.Axis.Direction.Z)),
            Length = Math.Round(Math.Max(0.0, resolved.Axis.Length), 6),
            Source = resolved.Source
        };
    }

    private static ExportedMainMaterialEvidence BuildMainMaterialEvidence(
        bool isPlateLike,
        string profileString,
        Point center,
        GlobalBoundingBox globalBox,
        IReadOnlyList<ExportedVector3> contourPoints,
        ExportedVector3 plateNormal,
        ExportedMemberAxisEvidence memberAxisEvidence)
    {
        var axisOrigin = ToPoint(memberAxisEvidence.Origin);
        var axisDirection = Normalize(ToVector(memberAxisEvidence.Direction));
        var centerVector = new Vector(center.X - axisOrigin.X, center.Y - axisOrigin.Y, center.Z - axisOrigin.Z);
        var centerStation = Dot(centerVector, axisDirection);
        var halfProjection = ProjectHalfExtent(globalBox, axisDirection);
        var start = centerStation - halfProjection;
        var end = centerStation + halfProjection;
        var radial = new Vector(
            centerVector.X - (axisDirection.X * centerStation),
            centerVector.Y - (axisDirection.Y * centerStation),
            centerVector.Z - (axisDirection.Z * centerStation));
        var faceId = ResolveRadialFaceId(radial);
        var faceOffset = Math.Sqrt((radial.X * radial.X) + (radial.Y * radial.Y) + (radial.Z * radial.Z));

        return new ExportedMainMaterialEvidence
        {
            AxisStationStart = Math.Round(start, 6),
            AxisStationEnd = Math.Round(end, 6),
            AxisStationLength = Math.Round(Math.Max(0.0, end - start), 6),
            BodyFaceId = faceId,
            BodyFaceOffset = Math.Round(faceOffset, 6),
            IsBodyWallPlateCandidate = isPlateLike && IsPlateProfile(profileString),
            EvidenceSource = "axisProjection+radialFaceBucket+sectionProjection",
            SegmentAdjacency = new ExportedSegmentAdjacencyEvidence
            {
                EvidenceSource = "pending.sameFaceStationSort"
            },
            SectionProjectionEvidence = BuildSectionProjectionEvidence(
                center,
                globalBox,
                contourPoints,
                plateNormal,
                axisOrigin,
                axisDirection)
        };
    }

    private static ExportedSectionProjectionEvidence BuildSectionProjectionEvidence(
        Point center,
        GlobalBoundingBox globalBox,
        IReadOnlyList<ExportedVector3> contourPoints,
        ExportedVector3 plateNormal,
        Point axisOrigin,
        Vector axisDirection)
    {
        var axes = BuildSectionAxes(axisDirection);
        var sourcePoints = contourPoints != null && contourPoints.Count >= 3
            ? contourPoints.Select(ToPoint).ToList()
            : GetBoundingBoxCorners(globalBox);
        var pointSource = contourPoints != null && contourPoints.Count >= 3 ? "contourPoints" : "solidBoundingBoxCorners";
        var projected = sourcePoints
            .Select(point => ProjectToSection(point, axisOrigin, axes.U, axes.V))
            .ToList();

        var centroid = ProjectToSection(center, axisOrigin, axes.U, axes.V);
        var minU = projected.Count == 0 ? centroid.U : projected.Min(point => point.U);
        var minV = projected.Count == 0 ? centroid.V : projected.Min(point => point.V);
        var maxU = projected.Count == 0 ? centroid.U : projected.Max(point => point.U);
        var maxV = projected.Count == 0 ? centroid.V : projected.Max(point => point.V);
        var normal = Normalize(ToVector(plateNormal));
        var normalU = Dot(normal, axes.U);
        var normalV = Dot(normal, axes.V);

        return new ExportedSectionProjectionEvidence
        {
            EvidenceSource = "memberAxisSectionProjection.v1",
            SectionPlaneOrigin = ToExportedVector3(axisOrigin),
            SectionAxisU = ToExportedVector3(axes.U),
            SectionAxisV = ToExportedVector3(axes.V),
            ProjectedCentroid = centroid,
            ProjectedBoundsMin = new ExportedSectionPoint { U = Math.Round(minU, 6), V = Math.Round(minV, 6) },
            ProjectedBoundsMax = new ExportedSectionPoint { U = Math.Round(maxU, 6), V = Math.Round(maxV, 6) },
            ProjectedContour = projected,
            NormalProjection = new ExportedSectionPoint { U = Math.Round(normalU, 6), V = Math.Round(normalV, 6) },
            NormalProjectionMagnitude = Math.Round(Math.Sqrt((normalU * normalU) + (normalV * normalV)), 6),
            PointSource = pointSource
        };
    }

    private static (Vector U, Vector V) BuildSectionAxes(Vector axisDirection)
    {
        var axis = Normalize(axisDirection);
        var reference = Math.Abs(Dot(axis, new Vector(0, 0, 1))) < 0.9 ? new Vector(0, 0, 1) : new Vector(1, 0, 0);
        var axisU = Normalize(Cross(reference, axis));
        var axisV = Normalize(Cross(axis, axisU));
        return (axisU, axisV);
    }

    private static ExportedSectionPoint ProjectToSection(Point point, Point origin, Vector axisU, Vector axisV)
    {
        var delta = new Vector(point.X - origin.X, point.Y - origin.Y, point.Z - origin.Z);
        return new ExportedSectionPoint
        {
            U = Math.Round(Dot(delta, axisU), 6),
            V = Math.Round(Dot(delta, axisV), 6)
        };
    }

    private static List<Point> GetBoundingBoxCorners(GlobalBoundingBox box)
    {
        return new List<Point>
        {
            new Point(box.Min.X, box.Min.Y, box.Min.Z),
            new Point(box.Min.X, box.Min.Y, box.Max.Z),
            new Point(box.Min.X, box.Max.Y, box.Min.Z),
            new Point(box.Min.X, box.Max.Y, box.Max.Z),
            new Point(box.Max.X, box.Min.Y, box.Min.Z),
            new Point(box.Max.X, box.Min.Y, box.Max.Z),
            new Point(box.Max.X, box.Max.Y, box.Min.Z),
            new Point(box.Max.X, box.Max.Y, box.Max.Z)
        };
    }


    private static ExportedBoxSectionEvidence BuildBoxSectionEvidence(
        IReadOnlyList<Part> teklaParts,
        IReadOnlyList<ExportedPart> exportedParts,
        ExportedMemberAxisEvidence memberAxisEvidence)
    {
        var byId = exportedParts.ToDictionary(part => part.PartId, part => part);
        var axisOrigin = ToPoint(memberAxisEvidence.Origin);
        var axisDirection = Normalize(ToVector(memberAxisEvidence.Direction));
        var axes = BuildSectionAxes(axisDirection);
        var stationMax = Math.Max(memberAxisEvidence.Length, exportedParts.Where(part => part.MainMaterialEvidence != null).Select(part => part.MainMaterialEvidence.AxisStationEnd).DefaultIfEmpty(memberAxisEvidence.Length).Max());
        var stations = new[] { 0.25, 0.5, 0.75 }
            .Select(ratio => memberAxisEvidence.Length * ratio)
            .Concat(exportedParts
                .Where(part => part.MainMaterialEvidence != null)
                .SelectMany(part => BuildPartProbeStations(part.MainMaterialEvidence)))
            .Where(station => station > 0 && station < stationMax)
            .OrderBy(station => station)
            .Aggregate(new List<double>(), (acc, station) =>
            {
                if (!acc.Any(existing => Math.Abs(existing - station) <= 25.0))
                {
                    acc.Add(station);
                }
                return acc;
            });
        var stationLoops = new List<ExportedBoxSectionStationLoop>();

        foreach (var station in stations)
        {
            var planeOrigin = new Point(
                axisOrigin.X + axisDirection.X * station,
                axisOrigin.Y + axisDirection.Y * station,
                axisOrigin.Z + axisDirection.Z * station);
            var points = new List<ExportedSectionPoint>();
            var supportPartIds = new HashSet<int>();
            var partLoops = new List<ExportedBoxSectionPartLoop>();

            foreach (var part in teklaParts)
            {
                if (!byId.TryGetValue(part.Identifier.ID, out var exported) ||
                    exported.MainMaterialEvidence == null ||
                    !exported.MainMaterialEvidence.IsBodyWallPlateCandidate ||
                    exported.MainMaterialEvidence.AxisStationStart > station ||
                    exported.MainMaterialEvidence.AxisStationEnd < station)
                {
                    continue;
                }
                var section = IntersectSolidFacesWithPlane(part, planeOrigin, axisDirection, axes.U, axes.V);
                if (section.Points.Count == 0 && section.Segments.Count == 0)
                {
                    continue;
                }

                var partSectionLoops = BuildSectionLoops(section.Segments, "partSegmentGraph.v1");
                var primaryPartLoop = partSectionLoops
                    .OrderByDescending(item => item.Area)
                    .FirstOrDefault();
                var partDiagnostics = new List<string>(section.Diagnostics);
                if (section.Segments.Count == 0)
                {
                    partDiagnostics.Add("NO_SECTION_SEGMENTS");
                }
                if (partSectionLoops.Count == 0)
                {
                    partDiagnostics.Add("NO_CLOSED_SECTION_LOOP");
                }

                supportPartIds.Add(part.Identifier.ID);
                points.AddRange(section.Points);
                partLoops.Add(new ExportedBoxSectionPartLoop
                {
                    PartId = part.Identifier.ID,
                    Points = primaryPartLoop != null && primaryPartLoop.Points.Count >= 3 ? primaryPartLoop.Points : section.Points,
                    Segments = section.Segments,
                    SectionLoops = partSectionLoops,
                    LoopStatus = partSectionLoops.Count > 0 ? "CLOSED" : "OPEN_OR_DEGENERATE",
                    Area = primaryPartLoop != null ? primaryPartLoop.Area : 0.0,
                    IsClosed = partSectionLoops.Count > 0,
                    IsValid = primaryPartLoop != null && primaryPartLoop.IsValid,
                    Bounds = primaryPartLoop != null ? primaryPartLoop.Bounds : BuildSectionBounds(section.Points),
                    Diagnostics = partDiagnostics.Distinct(StringComparer.Ordinal).OrderBy(item => item).ToList()
                });
            }
            var stationSegments = partLoops.SelectMany(item => item.Segments).ToList();
            var stationSectionLoops = BuildSectionLoops(stationSegments, "stationSegmentGraph.v1");
            var loop = stationSectionLoops
                .OrderByDescending(item => item.Area)
                .Select(item => item.Points)
                .FirstOrDefault() ?? BuildConvexHull(points);
            if (loop.Count >= 3)
            {
                var diagnostics = new List<string>();
                if (stationSegments.Count == 0)
                {
                    diagnostics.Add("NO_STATION_SECTION_SEGMENTS");
                }
                if (stationSectionLoops.Count == 0)
                {
                    diagnostics.Add("NO_CLOSED_STATION_SECTION_LOOP");
                }
                if (partLoops.Any(item => !item.IsClosed))
                {
                    diagnostics.Add("HAS_OPEN_OR_DEGENERATE_PART_LOOP");
                }

                stationLoops.Add(new ExportedBoxSectionStationLoop
                {
                    Station = Math.Round(station, 6),
                    OuterLoop = loop,
                    Source = "teklaSolidFaceSectionSegments.v2",
                    SupportPartIds = supportPartIds.OrderBy(id => id).ToList(),
                    PartLoops = partLoops.OrderBy(item => item.PartId).ToList(),
                    SectionSegments = stationSegments,
                    SectionLoops = stationSectionLoops,
                    ClosedLoopCount = stationSectionLoops.Count,
                    OpenChainCount = EstimateOpenChainCount(stationSegments),
                    DegeneratePartLoopCount = partLoops.Count(item => !item.IsValid || item.Area <= 1e-6),
                    Diagnostics = diagnostics.Distinct(StringComparer.Ordinal).OrderBy(item => item).ToList()
                });
            }
        }

        if (stationLoops.Count > 0)
        {
            var representative = stationLoops
                .OrderByDescending(item => PolygonArea(item.OuterLoop))
                .First();
            return new ExportedBoxSectionEvidence
            {
                OuterLoop = representative.OuterLoop,
                StationLoops = stationLoops,
                Source = "teklaSolidFaceSectionSegments.v2"
            };
        }

        var fallbackPoints = exportedParts
            .Where(part => part.MainMaterialEvidence != null && part.MainMaterialEvidence.IsBodyWallPlateCandidate)
            .SelectMany(part =>
            {
                var projection = part.MainMaterialEvidence.SectionProjectionEvidence;
                return projection != null && projection.ProjectedContour != null
                    ? projection.ProjectedContour
                    : new List<ExportedSectionPoint>();
            })
            .Where(point => point != null)
            .Select(point => new ExportedSectionPoint { U = point.U, V = point.V })
            .ToList();

        var fallbackLoop = BuildConvexHull(fallbackPoints);
        if (fallbackLoop.Count < 3)
        {
            return new ExportedBoxSectionEvidence();
        }

        return new ExportedBoxSectionEvidence
        {
            OuterLoop = fallbackLoop,
            Source = "mainWallProjectedContourHull.fallback"
        };
    }
    private static ExportedHBeamSectionEvidence BuildHBeamSectionEvidence(
        IReadOnlyList<Part> teklaParts,
        IReadOnlyList<ExportedPart> exportedParts,
        ExportedMemberAxisEvidence memberAxisEvidence)
    {
        var axisOrigin = ToPoint(memberAxisEvidence.Origin);
        var axisDirection = Normalize(ToVector(memberAxisEvidence.Direction));
        var axes = BuildSectionAxes(axisDirection);
        var candidates = exportedParts
            .Where(part => IsLikelyHBeamMainPlate(part, memberAxisEvidence.Length))
            .ToList();
        if (candidates.Count < 3)
        {
            var profileEvidence = BuildDirectHProfileSectionEvidence(exportedParts, memberAxisEvidence, axes);
            if (profileEvidence != null)
            {
                return profileEvidence;
            }

            return new ExportedHBeamSectionEvidence
            {
                Source = "hBeamStationLocalSolidSection.v1",
                SectionAxisU = ToExportedVector3(axes.U),
                SectionAxisV = ToExportedVector3(axes.V),
                Diagnostics = new List<string> { "INSUFFICIENT_MAIN_PLATE_CANDIDATES" }
            };
        }
        var byId = exportedParts.ToDictionary(part => part.PartId, part => part);
        var teklaById = teklaParts.ToDictionary(part => part.Identifier.ID, part => part);
        var mainCandidateIds = new HashSet<int>(candidates.Select(part => part.PartId));
        var stationMax = Math.Max(memberAxisEvidence.Length, candidates.Select(part => part.MainMaterialEvidence.AxisStationEnd).DefaultIfEmpty(memberAxisEvidence.Length).Max());
        var stations = new[] { 0.25, 0.5, 0.75 }
            .Select(ratio => memberAxisEvidence.Length * ratio)
            .Concat(exportedParts
                .Where(part => part.MainMaterialEvidence != null)
                .SelectMany(part => BuildPartProbeStations(part.MainMaterialEvidence)))
            .Where(station => station > 0 && station < stationMax)
            .OrderBy(station => station)
            .Aggregate(new List<double>(), (acc, station) =>
            {
                if (!acc.Any(existing => Math.Abs(existing - station) <= 25.0))
                {
                    acc.Add(station);
                }
                return acc;
            });

        var frames = new List<ExportedHBeamSectionStationFrame>();
        var diagnostics = new List<string>();
        foreach (var station in stations)
        {
            var planeOrigin = new Point(
                axisOrigin.X + axisDirection.X * station,
                axisOrigin.Y + axisDirection.Y * station,
                axisOrigin.Z + axisDirection.Z * station);
            var slices = new List<ExportedHBeamSectionPartSlice>();

            foreach (var part in teklaParts)
            {
                if (!byId.TryGetValue(part.Identifier.ID, out var exported) || exported.MainMaterialEvidence == null)
                {
                    continue;
                }
                if (exported.MainMaterialEvidence.AxisStationStart > station || exported.MainMaterialEvidence.AxisStationEnd < station)
                {
                    continue;
                }

                var section = IntersectSolidFacesWithPlane(part, planeOrigin, axisDirection, axes.U, axes.V);
                if (section.Points.Count == 0 && section.Segments.Count == 0)
                {
                    continue;
                }

                var points = section.Points.Count > 0 ? section.Points : section.Segments.SelectMany(segment => new[] { segment.Start, segment.End }).ToList();
                var bounds = BuildSectionBounds(points);
                var spanU = Math.Max(0.0, bounds.Max.U - bounds.Min.U);
                var spanV = Math.Max(0.0, bounds.Max.V - bounds.Min.V);
                var loops = BuildSectionLoops(section.Segments, "hBeamPartSegmentGraph.v1");
                var thickness = ResolveHBeamSliceThickness(exported, spanU, spanV);
                var sliceDiagnostics = new List<string>(section.Diagnostics);
                if (section.Segments.Count == 0)
                {
                    sliceDiagnostics.Add("NO_SECTION_SEGMENTS");
                }
                if (loops.Count == 0)
                {
                    sliceDiagnostics.Add("NO_CLOSED_SECTION_LOOP");
                }

                slices.Add(new ExportedHBeamSectionPartSlice
                {
                    PartId = exported.PartId,
                    PartPosition = exported.PartPosition ?? string.Empty,
                    RoleHint = mainCandidateIds.Contains(exported.PartId) ? InferHBeamSliceRoleHint(exported, spanU, spanV) : "attachment_candidate",
                    Center = new ExportedSectionPoint
                    {
                        U = Math.Round((bounds.Min.U + bounds.Max.U) / 2.0, 6),
                        V = Math.Round((bounds.Min.V + bounds.Max.V) / 2.0, 6)
                    },
                    Bounds = bounds,
                    SpanU = Math.Round(spanU, 6),
                    SpanV = Math.Round(spanV, 6),
                    Thickness = Math.Round(thickness, 6),
                    Points = points,
                    Segments = section.Segments,
                    SectionLoops = loops,
                    Diagnostics = sliceDiagnostics.Distinct(StringComparer.Ordinal).OrderBy(item => item).ToList()
                });
            }

            var frame = BuildHBeamStationFrame(station, slices);
            if (frame != null)
            {
                frames.Add(frame);
            }
        }

        if (frames.Count == 0)
        {
            diagnostics.Add("NO_VALID_H_BEAM_STATION_FRAME");
        }

        return new ExportedHBeamSectionEvidence
        {
            Source = "hBeamStationLocalSolidSection.v1",
            SectionAxisU = ToExportedVector3(axes.U),
            SectionAxisV = ToExportedVector3(axes.V),
            StationFrames = frames,
            Diagnostics = diagnostics
        };
    }


    private sealed class DirectHProfileDimensions
    {
        public double Height { get; set; }
        public double Width { get; set; }
        public double WebThickness { get; set; }
        public double FlangeThickness { get; set; }
        public string Source { get; set; } = string.Empty;
    }

    private static ExportedHBeamSectionEvidence? BuildDirectHProfileSectionEvidence(
        IReadOnlyList<ExportedPart> exportedParts,
        ExportedMemberAxisEvidence memberAxisEvidence,
        (Vector U, Vector V) axes)
    {
        var directParts = exportedParts
            .Select(part => new { Part = part, Dimensions = ParseDirectHProfileDimensions(part.ProfileString) })
            .Where(item => item.Dimensions != null)
            .OrderByDescending(item => item.Part.MainMaterialEvidence != null ? item.Part.MainMaterialEvidence.AxisStationLength : 0.0)
            .ToList();
        if (directParts.Count == 0)
        {
            return null;
        }

        var selected = directParts[0];
        var dimensions = selected.Dimensions!;
        var evidence = selected.Part.MainMaterialEvidence;
        var start = evidence != null ? evidence.AxisStationStart : 0.0;
        var end = evidence != null ? evidence.AxisStationEnd : memberAxisEvidence.Length;
        if (end < start)
        {
            var temp = start;
            start = end;
            end = temp;
        }

        var length = Math.Max(0.0, end - start);
        if (length <= 0.0)
        {
            length = memberAxisEvidence.Length;
            start = 0.0;
            end = length;
        }
        if (length <= 0.0)
        {
            return null;
        }

        var frames = BuildDirectHProfileStations(start, end)
            .Select(station => BuildDirectHProfileStationFrame(station, selected.Part, dimensions))
            .ToList();
        return new ExportedHBeamSectionEvidence
        {
            Source = "directHProfileSectionFrame.v1",
            SectionAxisU = ToExportedVector3(axes.U),
            SectionAxisV = ToExportedVector3(axes.V),
            StationFrames = frames,
            Diagnostics = new List<string>
            {
                "DIRECT_H_PROFILE_FRAME",
                "PROFILE=" + (selected.Part.ProfileString ?? string.Empty),
                "PROFILE_SOURCE=" + dimensions.Source
            }
        };
    }

    private static IEnumerable<double> BuildDirectHProfileStations(double start, double end)
    {
        var length = Math.Max(0.0, end - start);
        if (length <= 0.0)
        {
            yield break;
        }

        var offset = Math.Min(25.0, length * 0.1);
        yield return start + offset;
        yield return start + (length * 0.5);
        yield return end - offset;
    }

    private static ExportedHBeamSectionStationFrame BuildDirectHProfileStationFrame(
        double station,
        ExportedPart part,
        DirectHProfileDimensions dimensions)
    {
        var halfWidth = dimensions.Width / 2.0;
        var halfHeight = dimensions.Height / 2.0;
        var halfWeb = dimensions.WebThickness / 2.0;
        var flangeThickness = dimensions.FlangeThickness;
        var top = new ExportedHBeamSectionPartSlice
        {
            PartId = part.PartId,
            PartPosition = part.PartPosition ?? string.Empty,
            RoleHint = "flange_candidate",
            Center = new ExportedSectionPoint { U = 0.0, V = Math.Round(halfHeight - (flangeThickness / 2.0), 6) },
            Bounds = new ExportedSectionBounds
            {
                Min = new ExportedSectionPoint { U = Math.Round(-halfWidth, 6), V = Math.Round(halfHeight - flangeThickness, 6) },
                Max = new ExportedSectionPoint { U = Math.Round(halfWidth, 6), V = Math.Round(halfHeight, 6) }
            },
            SpanU = Math.Round(dimensions.Width, 6),
            SpanV = Math.Round(flangeThickness, 6),
            Thickness = Math.Round(flangeThickness, 6),
            Diagnostics = new List<string> { "DIRECT_H_PROFILE_SYNTHETIC_SLICE", "TOP_FLANGE" }
        };
        var bottom = new ExportedHBeamSectionPartSlice
        {
            PartId = part.PartId,
            PartPosition = part.PartPosition ?? string.Empty,
            RoleHint = "flange_candidate",
            Center = new ExportedSectionPoint { U = 0.0, V = Math.Round(-halfHeight + (flangeThickness / 2.0), 6) },
            Bounds = new ExportedSectionBounds
            {
                Min = new ExportedSectionPoint { U = Math.Round(-halfWidth, 6), V = Math.Round(-halfHeight, 6) },
                Max = new ExportedSectionPoint { U = Math.Round(halfWidth, 6), V = Math.Round(-halfHeight + flangeThickness, 6) }
            },
            SpanU = Math.Round(dimensions.Width, 6),
            SpanV = Math.Round(flangeThickness, 6),
            Thickness = Math.Round(flangeThickness, 6),
            Diagnostics = new List<string> { "DIRECT_H_PROFILE_SYNTHETIC_SLICE", "BOTTOM_FLANGE" }
        };
        var web = new ExportedHBeamSectionPartSlice
        {
            PartId = part.PartId,
            PartPosition = part.PartPosition ?? string.Empty,
            RoleHint = "web_candidate",
            Center = new ExportedSectionPoint { U = 0.0, V = 0.0 },
            Bounds = new ExportedSectionBounds
            {
                Min = new ExportedSectionPoint { U = Math.Round(-halfWeb, 6), V = Math.Round(-halfHeight + flangeThickness, 6) },
                Max = new ExportedSectionPoint { U = Math.Round(halfWeb, 6), V = Math.Round(halfHeight - flangeThickness, 6) }
            },
            SpanU = Math.Round(dimensions.WebThickness, 6),
            SpanV = Math.Round(Math.Max(0.0, dimensions.Height - (2.0 * flangeThickness)), 6),
            Thickness = Math.Round(dimensions.WebThickness, 6),
            Diagnostics = new List<string> { "DIRECT_H_PROFILE_SYNTHETIC_SLICE", "WEB" }
        };

        return new ExportedHBeamSectionStationFrame
        {
            Station = Math.Round(station, 6),
            WebCenterU = 0.0,
            WebHalfThickness = Math.Round(halfWeb, 6),
            TopOuterV = Math.Round(halfHeight, 6),
            BottomOuterV = Math.Round(-halfHeight, 6),
            WebTopV = Math.Round(halfHeight - flangeThickness, 6),
            WebBottomV = Math.Round(-halfHeight + flangeThickness, 6),
            TopFlangePartId = part.PartId,
            BottomFlangePartId = part.PartId,
            WebPartId = part.PartId,
            PartSlices = new List<ExportedHBeamSectionPartSlice> { bottom, web, top },
            Diagnostics = new List<string> { "DIRECT_H_PROFILE_FRAME" }
        };
    }

    private static DirectHProfileDimensions? ParseDirectHProfileDimensions(string? profileString)
    {
        if (string.IsNullOrWhiteSpace(profileString))
        {
            return null;
        }

        var normalized = profileString.Trim().ToUpperInvariant().Replace(" ", string.Empty);
        var bh = Regex.Match(normalized, @"^(?:BH|H)([0-9]+(?:\.[0-9]+)?)\*([0-9]+(?:\.[0-9]+)?)\*([0-9]+(?:\.[0-9]+)?)\*([0-9]+(?:\.[0-9]+)?)$");
        if (bh.Success)
        {
            return new DirectHProfileDimensions
            {
                Height = ParseInvariantDouble(bh.Groups[1].Value),
                Width = ParseInvariantDouble(bh.Groups[2].Value),
                WebThickness = ParseInvariantDouble(bh.Groups[3].Value),
                FlangeThickness = ParseInvariantDouble(bh.Groups[4].Value),
                Source = "BH/H"
            };
        }

        var hi = Regex.Match(normalized, @"^HI([0-9]+(?:\.[0-9]+)?)-([0-9]+(?:\.[0-9]+)?)-([0-9]+(?:\.[0-9]+)?)\*([0-9]+(?:\.[0-9]+)?)-([0-9]+(?:\.[0-9]+)?)\*([0-9]+(?:\.[0-9]+)?)$");
        if (hi.Success)
        {
            return new DirectHProfileDimensions
            {
                Height = ParseInvariantDouble(hi.Groups[1].Value),
                WebThickness = ParseInvariantDouble(hi.Groups[2].Value),
                FlangeThickness = ParseInvariantDouble(hi.Groups[3].Value),
                Width = Math.Max(ParseInvariantDouble(hi.Groups[4].Value), ParseInvariantDouble(hi.Groups[6].Value)),
                Source = "HI"
            };
        }

        return null;
    }

    private static double ParseInvariantDouble(string value)
    {
        double parsed;
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed) ? parsed : 0.0;
    }
    private static bool IsLikelyHBeamMainPlate(ExportedPart part, double memberLength)
    {
        if (part.MainMaterialEvidence == null || !part.MainMaterialEvidence.IsBodyWallPlateCandidate)
        {
            return false;
        }
        var length = Math.Max(0.0, part.MainMaterialEvidence.AxisStationLength);
        var minLength = Math.Max(300.0, memberLength * 0.45);
        return length >= minLength;
    }
    private static ExportedHBeamSectionStationFrame BuildHBeamStationFrame(double station, IReadOnlyList<ExportedHBeamSectionPartSlice> slices)
    {
        var mainSlices = slices
            .Where(slice => slice.RoleHint == "flange_candidate" || slice.RoleHint == "web_candidate" || slice.RoleHint == "wall_candidate")
            .ToList();
        var flangeCandidates = mainSlices
            .Where(slice => slice.RoleHint == "flange_candidate" || slice.SpanU >= slice.SpanV * 2.0)
            .OrderBy(slice => slice.Center.V)
            .ToList();
        var webCandidates = mainSlices
            .Where(slice => slice.RoleHint == "web_candidate" || slice.RoleHint == "wall_candidate" || slice.SpanV >= slice.SpanU * 2.0)
            .OrderByDescending(slice => slice.SpanV)
            .ThenBy(slice => Math.Abs(slice.Center.U))
            .ToList();

        if (flangeCandidates.Count < 2 || webCandidates.Count == 0)
        {
            return null;
        }

        var bottom = flangeCandidates.First();
        var top = flangeCandidates.Last();
        var web = webCandidates.First();
        var webHalfThickness = ResolveHBeamWebHalfThickness(web);
        var diagnostics = new List<string>();
        if (web.SpanU > Math.Max(20.0, web.Thickness * 4.0))
        {
            diagnostics.Add("WEB_SLICE_U_SPAN_LARGE_CHECK_CURVED_MEMBER_LOCAL_FRAME");
        }

        return new ExportedHBeamSectionStationFrame
        {
            Station = Math.Round(station, 6),
            WebCenterU = Math.Round(web.Center.U, 6),
            WebHalfThickness = Math.Round(webHalfThickness, 6),
            TopOuterV = Math.Round(top.Bounds.Max.V, 6),
            BottomOuterV = Math.Round(bottom.Bounds.Min.V, 6),
            WebTopV = Math.Round(web.Bounds.Max.V, 6),
            WebBottomV = Math.Round(web.Bounds.Min.V, 6),
            TopFlangePartId = top.PartId,
            BottomFlangePartId = bottom.PartId,
            WebPartId = web.PartId,
            PartSlices = slices.OrderBy(slice => slice.PartId).ToList(),
            Diagnostics = diagnostics
        };
    }

    private static string InferHBeamSliceRoleHint(ExportedPart part, double spanU, double spanV)
    {
        if (part.MainMaterialEvidence == null || !part.MainMaterialEvidence.IsBodyWallPlateCandidate)
        {
            return "attachment_candidate";
        }
        if (spanU >= spanV * 2.0)
        {
            return "flange_candidate";
        }
        if (spanV >= spanU * 2.0)
        {
            return "web_candidate";
        }
        return "wall_candidate";
    }

    private static double ResolveHBeamSliceThickness(ExportedPart part, double spanU, double spanV)
    {
        if (part.Thickness.HasValue && part.Thickness.Value > 0)
        {
            return part.Thickness.Value;
        }
        var spans = new[] { spanU, spanV }.Where(value => value > 1e-6).OrderBy(value => value).ToArray();
        return spans.Length == 0 ? 0.0 : spans[0];
    }

    private static double ResolveHBeamWebHalfThickness(ExportedHBeamSectionPartSlice web)
    {
        if (web.Thickness > 0)
        {
            return Math.Max(1.0, web.Thickness / 2.0);
        }
        var span = Math.Min(web.SpanU, web.SpanV);
        return span > 0 ? Math.Max(1.0, span / 2.0) : 1.0;
    }



    private static IEnumerable<double> BuildPartProbeStations(ExportedMainMaterialEvidence evidence)
    {
        var start = evidence.AxisStationStart;
        var end = evidence.AxisStationEnd;
        if (end < start)
        {
            var temp = start;
            start = end;
            end = temp;
        }

        var length = Math.Max(0.0, end - start);
        var offset = Math.Min(25.0, length * 0.1);
        yield return start + offset;
        yield return (start + end) / 2.0;
        yield return end - offset;
    }

    private sealed class SectionIntersectionResult
    {
        public List<ExportedSectionPoint> Points { get; } = new List<ExportedSectionPoint>();
        public List<ExportedSectionSegment> Segments { get; } = new List<ExportedSectionSegment>();
        public List<string> Diagnostics { get; } = new List<string>();
    }

    private sealed class PlaneEdgeHit
    {
        public bool IsSegment { get; set; }
        public Point Start { get; set; } = new Point();
        public Point End { get; set; } = new Point();
        public string Kind { get; set; } = string.Empty;
    }

    private static SectionIntersectionResult IntersectSolidFacesWithPlane(Part part, Point planeOrigin, Vector planeNormal, Vector axisU, Vector axisV)
    {
        var result = new SectionIntersectionResult();
        try
        {
            var solid = part.GetSolid();
            var faceEnumerator = solid.GetFaceEnumerator();
            var faceIndex = 0;
            while (faceEnumerator.MoveNext())
            {
                var face = faceEnumerator.Current;
                if (face == null)
                {
                    faceIndex++;
                    continue;
                }

                var loopEnumerator = face.GetLoopEnumerator();
                var loopIndex = 0;
                while (loopEnumerator.MoveNext())
                {
                    var loop = loopEnumerator.Current;
                    if (loop == null)
                    {
                        loopIndex++;
                        continue;
                    }

                    var vertices = new List<Point>();
                    var vertexEnumerator = loop.GetVertexEnumerator();
                    while (vertexEnumerator.MoveNext())
                    {
                        var point = vertexEnumerator.Current;
                        if (point != null)
                        {
                            vertices.Add(point);
                        }
                    }

                    if (vertices.Count < 2)
                    {
                        loopIndex++;
                        continue;
                    }

                    var loopPointHits = new List<ExportedSectionPoint>();
                    for (var edgeIndex = 0; edgeIndex < vertices.Count; edgeIndex++)
                    {
                        var start = vertices[edgeIndex];
                        var end = vertices[(edgeIndex + 1) % vertices.Count];
                        var hit = IntersectSegmentWithPlane(start, end, planeOrigin, planeNormal);
                        if (hit == null)
                        {
                            continue;
                        }

                        if (hit.IsSegment)
                        {
                            AddSectionSegment(result.Segments, ProjectToSection(hit.Start, planeOrigin, axisU, axisV), ProjectToSection(hit.End, planeOrigin, axisU, axisV), part.Identifier.ID, faceIndex, loopIndex, edgeIndex, hit.Kind);
                        }
                        else
                        {
                            AddDistinctSectionPoint(loopPointHits, ProjectToSection(hit.Start, planeOrigin, axisU, axisV));
                        }
                    }

                    foreach (var point in loopPointHits)
                    {
                        AddDistinctSectionPoint(result.Points, point);
                    }

                    if (loopPointHits.Count >= 2)
                    {
                        var distinctHits = loopPointHits.GroupBy(point => SectionPointKey(point)).Select(group => group.First()).ToList();
                        if (distinctHits.Count >= 2)
                        {
                            AddSectionSegment(result.Segments, distinctHits[0], distinctHits[1], part.Identifier.ID, faceIndex, loopIndex, -1, "FACE_LOOP_PLANE_CROSSING");
                        }
                        if (distinctHits.Count > 2)
                        {
                            result.Diagnostics.Add("FACE_LOOP_HAS_MORE_THAN_TWO_PLANE_HITS");
                        }
                    }

                    loopIndex++;
                }

                faceIndex++;
            }
        }
        catch (Exception ex)
        {
            result.Diagnostics.Add("SOLID_SECTION_INTERSECTION_FAILED:" + ex.GetType().Name);
        }

        foreach (var segment in result.Segments)
        {
            AddDistinctSectionPoint(result.Points, segment.Start);
            AddDistinctSectionPoint(result.Points, segment.End);
        }

        return result;
    }

    private static PlaneEdgeHit IntersectSegmentWithPlane(Point start, Point end, Point planeOrigin, Vector planeNormal)
    {
        const double tolerance = 1e-6;
        var startDistance = SignedDistanceToPlane(start, planeOrigin, planeNormal);
        var endDistance = SignedDistanceToPlane(end, planeOrigin, planeNormal);

        if (Math.Abs(startDistance) <= tolerance && Math.Abs(endDistance) <= tolerance)
        {
            return new PlaneEdgeHit { IsSegment = true, Start = start, End = end, Kind = "EDGE_ON_PLANE" };
        }
        if (Math.Abs(startDistance) <= tolerance)
        {
            return new PlaneEdgeHit { IsSegment = false, Start = start, Kind = "START_ON_PLANE" };
        }
        if (Math.Abs(endDistance) <= tolerance)
        {
            return new PlaneEdgeHit { IsSegment = false, Start = end, Kind = "END_ON_PLANE" };
        }
        if ((startDistance < 0 && endDistance < 0) || (startDistance > 0 && endDistance > 0))
        {
            return null;
        }

        var denominator = startDistance - endDistance;
        if (Math.Abs(denominator) <= tolerance)
        {
            return null;
        }
        var t = startDistance / denominator;
        if (t < -tolerance || t > 1.0 + tolerance)
        {
            return null;
        }

        t = Math.Max(0.0, Math.Min(1.0, t));
        var intersection = new Point(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t, start.Z + (end.Z - start.Z) * t);
        return new PlaneEdgeHit { IsSegment = false, Start = intersection, Kind = "CROSSING" };
    }

    private static double SignedDistanceToPlane(Point point, Point planeOrigin, Vector planeNormal)
    {
        return Dot(new Vector(point.X - planeOrigin.X, point.Y - planeOrigin.Y, point.Z - planeOrigin.Z), planeNormal);
    }

    private static void AddDistinctSectionPoint(List<ExportedSectionPoint> points, ExportedSectionPoint candidate)
    {
        if (points.Any(point => Distance2D(point, candidate) <= 1.0))
        {
            return;
        }

        points.Add(candidate);
    }

    private static double Distance2D(ExportedSectionPoint left, ExportedSectionPoint right)
    {
        var du = left.U - right.U;
        var dv = left.V - right.V;
        return Math.Sqrt(du * du + dv * dv);
    }

    private static void AddSectionSegment(
        List<ExportedSectionSegment> segments,
        ExportedSectionPoint start,
        ExportedSectionPoint end,
        int sourcePartId,
        int sourceFaceIndex,
        int sourceLoopIndex,
        int sourceEdgeIndex,
        string intersectionKind)
    {
        var length = Distance2D(start, end);
        if (length <= 1.0)
        {
            return;
        }

        var key = SectionSegmentKey(start, end);
        if (segments.Any(segment => SectionSegmentKey(segment.Start, segment.End) == key))
        {
            return;
        }

        segments.Add(new ExportedSectionSegment
        {
            Start = start,
            End = end,
            Length = Math.Round(length, 6),
            SourcePartId = sourcePartId,
            SourceFaceIndex = sourceFaceIndex,
            SourceLoopIndex = sourceLoopIndex,
            SourceEdgeIndex = sourceEdgeIndex,
            IntersectionKind = intersectionKind
        });
    }

    private static List<ExportedSectionLoop> BuildSectionLoops(IReadOnlyList<ExportedSectionSegment> segments, string source)
    {
        var loops = new List<ExportedSectionLoop>();
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var points = new Dictionary<string, ExportedSectionPoint>(StringComparer.Ordinal);

        foreach (var segment in segments)
        {
            var startKey = SectionPointKey(segment.Start);
            var endKey = SectionPointKey(segment.End);
            if (startKey == endKey)
            {
                continue;
            }

            points[startKey] = segment.Start;
            points[endKey] = segment.End;
            AddNeighbor(adjacency, startKey, endKey);
            AddNeighbor(adjacency, endKey, startKey);
        }

        var visitedEdges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var startKey in adjacency.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            foreach (var nextKey in adjacency[startKey].OrderBy(key => key, StringComparer.Ordinal))
            {
                var edgeKey = EdgeKey(startKey, nextKey);
                if (visitedEdges.Contains(edgeKey))
                {
                    continue;
                }

                var loopKeys = TraceClosedLoop(adjacency, startKey, nextKey, visitedEdges);
                if (loopKeys.Count < 3 || loopKeys[0] != loopKeys[loopKeys.Count - 1])
                {
                    continue;
                }

                var loopPoints = loopKeys.Take(loopKeys.Count - 1)
                    .Select(key => points[key])
                    .ToList();
                var area = PolygonArea(loopPoints);
                if (area <= 1e-6)
                {
                    continue;
                }

                loops.Add(new ExportedSectionLoop
                {
                    Points = EnsureCounterClockwise(loopPoints),
                    Area = Math.Round(area, 6),
                    IsClosed = true,
                    IsValid = true,
                    Bounds = BuildSectionBounds(loopPoints),
                    Source = source
                });
            }
        }

        return loops
            .GroupBy(loop => string.Join("|", loop.Points.Select(SectionPointKey)))
            .Select(group => group.First())
            .OrderByDescending(loop => loop.Area)
            .ToList();
    }

    private static List<string> TraceClosedLoop(Dictionary<string, List<string>> adjacency, string startKey, string nextKey, HashSet<string> visitedEdges)
    {
        var loop = new List<string> { startKey };
        var previous = startKey;
        var current = nextKey;
        var guard = Math.Max(8, adjacency.Count * 4);

        while (guard-- > 0)
        {
            visitedEdges.Add(EdgeKey(previous, current));
            loop.Add(current);
            if (current == startKey)
            {
                return loop;
            }

            if (!adjacency.TryGetValue(current, out var neighbors))
            {
                return new List<string>();
            }

            var candidates = neighbors
                .Where(candidate => candidate != previous)
                .OrderBy(candidate => candidate, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count == 0)
            {
                return new List<string>();
            }

            var next = candidates.FirstOrDefault(candidate => !visitedEdges.Contains(EdgeKey(current, candidate))) ?? candidates[0];
            previous = current;
            current = next;
        }

        return new List<string>();
    }

    private static int EstimateOpenChainCount(IReadOnlyList<ExportedSectionSegment> segments)
    {
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            IncrementDegree(degree, SectionPointKey(segment.Start));
            IncrementDegree(degree, SectionPointKey(segment.End));
        }

        return degree.Values.Count(value => value == 1) / 2;
    }

    private static ExportedSectionBounds BuildSectionBounds(IReadOnlyList<ExportedSectionPoint> points)
    {
        if (points.Count == 0)
        {
            return new ExportedSectionBounds();
        }

        return new ExportedSectionBounds
        {
            Min = new ExportedSectionPoint
            {
                U = Math.Round(points.Min(point => point.U), 6),
                V = Math.Round(points.Min(point => point.V), 6)
            },
            Max = new ExportedSectionPoint
            {
                U = Math.Round(points.Max(point => point.U), 6),
                V = Math.Round(points.Max(point => point.V), 6)
            }
        };
    }

    private static List<ExportedSectionPoint> EnsureCounterClockwise(List<ExportedSectionPoint> points)
    {
        return SignedPolygonArea(points) < 0
            ? points.AsEnumerable().Reverse().ToList()
            : points;
    }

    private static double SignedPolygonArea(IReadOnlyList<ExportedSectionPoint> points)
    {
        if (points.Count < 3)
        {
            return 0.0;
        }

        var area = 0.0;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            area += current.U * next.V - next.U * current.V;
        }

        return area / 2.0;
    }

    private static void AddNeighbor(Dictionary<string, List<string>> adjacency, string key, string neighbor)
    {
        if (!adjacency.TryGetValue(key, out var neighbors))
        {
            neighbors = new List<string>();
            adjacency[key] = neighbors;
        }

        if (!neighbors.Contains(neighbor, StringComparer.Ordinal))
        {
            neighbors.Add(neighbor);
        }
    }

    private static void IncrementDegree(Dictionary<string, int> degree, string key)
    {
        degree[key] = degree.TryGetValue(key, out var value) ? value + 1 : 1;
    }

    private static string SectionPointKey(ExportedSectionPoint point)
    {
        return Math.Round(point.U, 3).ToString("0.###", CultureInfo.InvariantCulture) + ":" + Math.Round(point.V, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string SectionSegmentKey(ExportedSectionPoint start, ExportedSectionPoint end)
    {
        var startKey = SectionPointKey(start);
        var endKey = SectionPointKey(end);
        return string.CompareOrdinal(startKey, endKey) <= 0 ? startKey + "|" + endKey : endKey + "|" + startKey;
    }

    private static string EdgeKey(string startKey, string endKey)
    {
        return string.CompareOrdinal(startKey, endKey) <= 0 ? startKey + "|" + endKey : endKey + "|" + startKey;
    }

    private static double PolygonArea(IReadOnlyList<ExportedSectionPoint> points)
    {
        if (points.Count < 3)
        {
            return 0.0;
        }

        var area = 0.0;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            area += current.U * next.V - next.U * current.V;
        }

        return Math.Abs(area) / 2.0;
    }

    private static List<ExportedSectionPoint> BuildConvexHull(IReadOnlyList<ExportedSectionPoint> points)
    {
        var distinct = points
            .GroupBy(point => point.U.ToString("0.######", CultureInfo.InvariantCulture) + ":" + point.V.ToString("0.######", CultureInfo.InvariantCulture))
            .Select(group => group.First())
            .OrderBy(point => point.U)
            .ThenBy(point => point.V)
            .ToList();
        if (distinct.Count <= 3)
        {
            return distinct;
        }

        var lower = new List<ExportedSectionPoint>();
        foreach (var point in distinct)
        {
            while (lower.Count >= 2 && Cross2D(lower[lower.Count - 2], lower[lower.Count - 1], point) <= 0)
            {
                lower.RemoveAt(lower.Count - 1);
            }
            lower.Add(point);
        }

        var upper = new List<ExportedSectionPoint>();
        for (var index = distinct.Count - 1; index >= 0; index--)
        {
            var point = distinct[index];
            while (upper.Count >= 2 && Cross2D(upper[upper.Count - 2], upper[upper.Count - 1], point) <= 0)
            {
                upper.RemoveAt(upper.Count - 1);
            }
            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        return lower.Concat(upper)
            .Select(point => new ExportedSectionPoint { U = Math.Round(point.U, 6), V = Math.Round(point.V, 6) })
            .ToList();
    }

    private static double Cross2D(ExportedSectionPoint origin, ExportedSectionPoint left, ExportedSectionPoint right)
    {
        return ((left.U - origin.U) * (right.V - origin.V)) - ((left.V - origin.V) * (right.U - origin.U));
    }
    private static void ApplySegmentAdjacencyEvidence(IReadOnlyList<ExportedPart> exportedParts)
    {
        var candidates = exportedParts
            .Where(part => part.MainMaterialEvidence != null && part.MainMaterialEvidence.IsBodyWallPlateCandidate)
            .GroupBy(part => part.MainMaterialEvidence.BodyFaceId ?? string.Empty);

        foreach (var group in candidates)
        {
            var ordered = group
                .OrderBy(part => part.MainMaterialEvidence.AxisStationStart)
                .ThenBy(part => part.PartId)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                var current = ordered[index];
                var previous = index > 0 ? ordered[index - 1] : null;
                var next = index + 1 < ordered.Count ? ordered[index + 1] : null;
                current.MainMaterialEvidence.SegmentAdjacency = new ExportedSegmentAdjacencyEvidence
                {
                    PreviousPartId = previous != null ? previous.PartId : 0,
                    NextPartId = next != null ? next.PartId : 0,
                    PreviousGap = previous != null ? Math.Round(current.MainMaterialEvidence.AxisStationStart - previous.MainMaterialEvidence.AxisStationEnd, 6) : 0.0,
                    NextGap = next != null ? Math.Round(next.MainMaterialEvidence.AxisStationStart - current.MainMaterialEvidence.AxisStationEnd, 6) : 0.0,
                    EvidenceSource = "sameFaceStationSort"
                };
            }
        }
    }

    private static double ProjectHalfExtent(GlobalBoundingBox box, Vector direction)
    {
        var halfX = Math.Abs(box.Max.X - box.Min.X) / 2.0;
        var halfY = Math.Abs(box.Max.Y - box.Min.Y) / 2.0;
        var halfZ = Math.Abs(box.Max.Z - box.Min.Z) / 2.0;
        return (Math.Abs(direction.X) * halfX) + (Math.Abs(direction.Y) * halfY) + (Math.Abs(direction.Z) * halfZ);
    }

    private static string ResolveRadialFaceId(Vector radial)
    {
        var absX = Math.Abs(radial.X);
        var absY = Math.Abs(radial.Y);
        var absZ = Math.Abs(radial.Z);
        if (absX >= absY && absX >= absZ)
        {
            return radial.X >= 0 ? "RADIAL_X_POS" : "RADIAL_X_NEG";
        }

        if (absY >= absX && absY >= absZ)
        {
            return radial.Y >= 0 ? "RADIAL_Y_POS" : "RADIAL_Y_NEG";
        }

        return radial.Z >= 0 ? "RADIAL_Z_POS" : "RADIAL_Z_NEG";
    }
    private static ExportedExternalConnectionEvidence BuildExternalConnectionEvidence(
        Model model,
        IReadOnlyDictionary<int, Part> localParts,
        IReadOnlyDictionary<int, PartObjectCache> partCaches,
        ExportedMemberAxisEvidence memberAxisEvidence)
    {
        const double axisLocationToleranceMm = 50.0;
        const double axisLocationLengthRatio = 0.15;
        var links = new List<ExportedExternalPartLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<string>();
        var axisOrigin = ToPoint(memberAxisEvidence.Origin);
        var axisDirection = Normalize(ToVector(memberAxisEvidence.Direction));
        var axisLength = memberAxisEvidence.Length;

        foreach (var part in localParts.Values)
        {
            PartObjectCache cache;
            if (partCaches == null || !partCaches.TryGetValue(part.Identifier.ID, out cache))
            {
                continue;
            }

            foreach (var weld in cache.Welds)
            {
                AddExternalWeldLink(model, part, weld, localParts, axisOrigin, axisDirection, axisLength, axisLocationToleranceMm, axisLocationLengthRatio, seen, links, diagnostics);
            }

            foreach (var bolt in cache.Bolts)
            {
                AddExternalBoltLinks(model, part, bolt, localParts, axisOrigin, axisDirection, axisLength, axisLocationToleranceMm, axisLocationLengthRatio, seen, links, diagnostics);
            }
        }

        return new ExportedExternalConnectionEvidence
        {
            Source = "part.weld-and-bolt.other-assembly",
            AxisLocationToleranceMm = axisLocationToleranceMm,
            AxisLocationLengthRatio = axisLocationLengthRatio,
            Links = links
                .OrderBy(item => item.LocalPartId)
                .ThenBy(item => item.OtherAssemblyId)
                .ThenBy(item => item.OtherPartId)
                .ThenBy(item => item.Relation, StringComparer.Ordinal)
                .ToList(),
            Diagnostics = diagnostics
        };
    }

    private static void AddExternalWeldLink(
        Model model,
        Part localPart,
        BaseWeld weld,
        IReadOnlyDictionary<int, Part> localParts,
        Point axisOrigin,
        Vector axisDirection,
        double axisLength,
        double toleranceMm,
        double lengthRatio,
        HashSet<string> seen,
        List<ExportedExternalPartLink> links,
        List<string> diagnostics)
    {
        if (weld == null)
        {
            return;
        }

        var main = weld.MainObject as Part;
        var secondary = weld.SecondaryObject as Part;
        if (main == null || secondary == null)
        {
            diagnostics.Add("external-weld-unresolved:" + SafeGetIdentifier(weld).ToString(CultureInfo.InvariantCulture));
            return;
        }

        var other = localParts.ContainsKey(main.Identifier.ID) ? secondary : main;
        if (localParts.ContainsKey(main.Identifier.ID) && localParts.ContainsKey(secondary.Identifier.ID))
        {
            return;
        }

        if (localParts.ContainsKey(other.Identifier.ID))
        {
            return;
        }

        var otherAssemblyId = TryGetAssemblyId(other);
        if (otherAssemblyId == 0)
        {
            diagnostics.Add("external-weld-no-assembly:" + other.Identifier.ID.ToString(CultureInfo.InvariantCulture));
            return;
        }

        var key = string.Format(
            CultureInfo.InvariantCulture,
            "Weld:{0}:{1}:{2}",
            localPart.Identifier.ID,
            other.Identifier.ID,
            otherAssemblyId);
        if (!seen.Add(key))
        {
            return;
        }

        var location = ResolveAxisLocation(localPart, axisOrigin, axisDirection, axisLength, toleranceMm, lengthRatio);
        var otherAxis = TryGetAssemblyAxis(model, other, diagnostics);
        links.Add(new ExportedExternalPartLink
        {
            LocalPartId = localPart.Identifier.ID,
            OtherPartId = other.Identifier.ID,
            OtherAssemblyId = otherAssemblyId,
            Relation = "Weld",
            ShopOrSite = weld.ShopWeld ? "shop" : "site",
            AxisLocation = location.Location,
            AxisStation = location.Station,
            OtherMainPartId = otherAxis.MainPartId,
            OtherAxisDirection = otherAxis.Direction,
            OtherAxisPose = otherAxis.Pose
        });
    }

    private static void AddExternalBoltLinks(
        Model model,
        Part localPart,
        BoltGroup bolt,
        IReadOnlyDictionary<int, Part> localParts,
        Point axisOrigin,
        Vector axisDirection,
        double axisLength,
        double toleranceMm,
        double lengthRatio,
        HashSet<string> seen,
        List<ExportedExternalPartLink> links,
        List<string> diagnostics)
    {
        if (bolt == null)
        {
            return;
        }

        var connected = new List<Part>();
        if (bolt.PartToBoltTo != null)
        {
            connected.Add(bolt.PartToBoltTo);
        }

        if (bolt.PartToBeBolted != null)
        {
            connected.Add(bolt.PartToBeBolted);
        }

        var others = bolt.OtherPartsToBolt;
        if (others != null)
        {
            foreach (var item in others)
            {
                var otherPart = item as Part;
                if (otherPart != null)
                {
                    connected.Add(otherPart);
                }
            }
        }

        if (connected.Count == 0)
        {
            diagnostics.Add("external-bolt-unresolved:" + SafeGetIdentifier(bolt).ToString(CultureInfo.InvariantCulture));
            return;
        }

        var external = connected
            .Where(part => part != null && !localParts.ContainsKey(part.Identifier.ID))
            .GroupBy(part => part.Identifier.ID)
            .Select(group => group.First())
            .ToList();
        if (external.Count == 0 || !connected.Any(part => localParts.ContainsKey(part.Identifier.ID)))
        {
            return;
        }

        var location = ResolveAxisLocation(localPart, axisOrigin, axisDirection, axisLength, toleranceMm, lengthRatio);
        foreach (var other in external)
        {
            var otherAssemblyId = TryGetAssemblyId(other);
            if (otherAssemblyId == 0)
            {
                diagnostics.Add("external-bolt-no-assembly:" + other.Identifier.ID.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            var key = string.Format(
                CultureInfo.InvariantCulture,
                "Bolt:{0}:{1}:{2}",
                localPart.Identifier.ID,
                other.Identifier.ID,
                otherAssemblyId);
            if (!seen.Add(key))
            {
                continue;
            }

            var otherAxis = TryGetAssemblyAxis(model, other, diagnostics);
            links.Add(new ExportedExternalPartLink
            {
                LocalPartId = localPart.Identifier.ID,
                OtherPartId = other.Identifier.ID,
                OtherAssemblyId = otherAssemblyId,
                Relation = "Bolt",
                ShopOrSite = bolt.Bolt ? "site" : "shop",
                AxisLocation = location.Location,
                AxisStation = location.Station,
                BoltSize = Math.Round(bolt.BoltSize, 3),
                OtherMainPartId = otherAxis.MainPartId,
                OtherAxisDirection = otherAxis.Direction,
                OtherAxisPose = otherAxis.Pose
            });
        }
    }

    private static (int MainPartId, ExportedVector3 Direction, string Pose) TryGetAssemblyAxis(
        Model model,
        Part part,
        List<string> diagnostics)
    {
        try
        {
            var assembly = part.GetAssembly();
            var mainPart = assembly != null ? assembly.GetMainPart() as Part : null;
            if (mainPart == null)
            {
                return (0, new ExportedVector3(), "unknown");
            }

            var assemblyParts = assembly != null ? GetAssemblyParts(assembly).ToList() : new List<Part>();
            var axis = BuildMemberAxisEvidence(assemblyParts, mainPart);
            return (mainPart.Identifier.ID, axis.Direction, PoseOf(axis.Direction));
        }
        catch
        {
            diagnostics.Add("external-axis-unresolved:" + part.Identifier.ID.ToString(CultureInfo.InvariantCulture));
            return (0, new ExportedVector3(), "unknown");
        }
    }

    private static string PoseOf(ExportedVector3 direction)
    {
        var length = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y) + (direction.Z * direction.Z));
        if (length <= 1e-9)
        {
            return "unknown";
        }

        var absZ = Math.Abs(direction.Z / length);
        if (absZ >= Math.Cos(10.0 * Math.PI / 180.0))
        {
            return "vertical";
        }

        if (absZ <= Math.Sin(10.0 * Math.PI / 180.0))
        {
            return "horizontal";
        }

        return "inclined";
    }

    private static int TryGetAssemblyId(Part part)
    {
        try
        {
            var assembly = part.GetAssembly();
            return assembly != null ? assembly.Identifier.ID : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static (string Location, double Station) ResolveAxisLocation(
        Part part,
        Point axisOrigin,
        Vector axisDirection,
        double axisLength,
        double toleranceMm,
        double lengthRatio)
    {
        var box = GetGlobalBoundingBox(part);
        var center = GetCenter(box);
        var offset = new Vector(center.X - axisOrigin.X, center.Y - axisOrigin.Y, center.Z - axisOrigin.Z);
        var station = Dot(offset, axisDirection);
        var endBand = Math.Max(toleranceMm, axisLength * lengthRatio);
        var location = "middle";
        if (station <= endBand)
        {
            location = "start";
        }
        else if (station >= axisLength - endBand)
        {
            location = "end";
        }

        return (location, Math.Round(station, 3));
    }

    private static List<ExportedRelationship> BuildRelationships(
        IReadOnlyList<Part> parts,
        IReadOnlyList<ExportedPart> exportedParts,
        IReadOnlyDictionary<int, PartObjectCache> partCaches)
    {
        var partIds = new HashSet<int>(parts.Select(part => part.Identifier.ID));
        var relationships = new List<ExportedRelationship>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var part in parts)
        {
            PartObjectCache cache;
            if (partCaches == null || !partCaches.TryGetValue(part.Identifier.ID, out cache))
            {
                cache = LoadPartObjectCache(part);
            }

            foreach (var weld in cache.Welds)
            {
                var main = weld.MainObject as Part;
                var secondary = weld.SecondaryObject as Part;
                if (main == null || secondary == null)
                {
                    continue;
                }

                if (!partIds.Contains(main.Identifier.ID) || !partIds.Contains(secondary.Identifier.ID))
                {
                    continue;
                }

                var strength = 0.5 + (Math.Max(weld.SizeAbove, weld.SizeBelow) / 20.0);
                var meta = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|shop={1}|sizeAbove={2:0.###}|sizeBelow={3:0.###}|around={4}",
                    weld.GetType().Name,
                    weld.ShopWeld,
                    weld.SizeAbove,
                    weld.SizeBelow,
                    weld.AroundWeld);
                AddRelationship(seen, relationships, main.Identifier.ID, secondary.Identifier.ID, "Weld", strength, 1.0, meta);
            }

            foreach (var booleanPart in cache.Booleans)
            {
                var operativePart = booleanPart.OperativePart;
                if (operativePart == null)
                {
                    continue;
                }

                if (!partIds.Contains(part.Identifier.ID) || !partIds.Contains(operativePart.Identifier.ID))
                {
                    continue;
                }

                AddRelationship(
                    seen,
                    relationships,
                    part.Identifier.ID,
                    operativePart.Identifier.ID,
                    "Boolean",
                    0.8,
                    1.0,
                    booleanPart.Type.ToString());
            }

            foreach (var bolt in cache.Bolts)
            {
                var partToBoltTo = bolt.PartToBoltTo;
                var partToBeBolted = bolt.PartToBeBolted;
                if (partToBoltTo == null || partToBeBolted == null)
                {
                    continue;
                }

                if (!partIds.Contains(partToBoltTo.Identifier.ID) || !partIds.Contains(partToBeBolted.Identifier.ID))
                {
                    continue;
                }

                AddRelationship(
                    seen,
                    relationships,
                    partToBoltTo.Identifier.ID,
                    partToBeBolted.Identifier.ID,
                    "Bolt",
                    0.45,
                    0.9,
                    string.Format(CultureInfo.InvariantCulture, "bolt={0}|size={1:0.###}", bolt.Bolt, bolt.BoltSize));
            }
        }

        AddContactRelationships(exportedParts, seen, relationships);

        return relationships
            .OrderBy(item => item.PartIdA)
            .ThenBy(item => item.PartIdB)
            .ThenBy(item => item.EdgeType)
            .ToList();
    }

    private static void AddContactRelationships(
        IReadOnlyList<ExportedPart> exportedParts,
        HashSet<string> seen,
        List<ExportedRelationship> relationships)
    {
        const double contactMarginMm = 3.0;

        for (var i = 0; i < exportedParts.Count; i++)
        {
            for (var j = i + 1; j < exportedParts.Count; j++)
            {
                var left = exportedParts[i];
                var right = exportedParts[j];
                if (!BoxesIntersect(left.BoundingBox, right.BoundingBox, contactMarginMm))
                {
                    continue;
                }

                AddRelationship(
                    seen,
                    relationships,
                    left.PartId,
                    right.PartId,
                    "Contact",
                    0.35,
                    0.75,
                    "bbox-contact|margin=" + contactMarginMm.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }
    }

    private static bool BoxesIntersect(ExportedBoundingBox left, ExportedBoundingBox right, double margin)
    {
        return (left.Min.X - margin) <= right.Max.X &&
               (left.Max.X + margin) >= right.Min.X &&
               (left.Min.Y - margin) <= right.Max.Y &&
               (left.Max.Y + margin) >= right.Min.Y &&
               (left.Min.Z - margin) <= right.Max.Z &&
               (left.Max.Z + margin) >= right.Min.Z;
    }

    private static void AddRelationship(
        HashSet<string> seen,
        List<ExportedRelationship> relationships,
        int partIdA,
        int partIdB,
        string edgeType,
        double strength,
        double geometricSupport,
        string meta)
    {
        var left = Math.Min(partIdA, partIdB);
        var right = Math.Max(partIdA, partIdB);
        var key = string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2}", edgeType, left, right);
        if (!seen.Add(key))
        {
            return;
        }

        relationships.Add(new ExportedRelationship
        {
            PartIdA = left,
            PartIdB = right,
            EdgeType = edgeType,
            Strength = Math.Round(strength, 4),
            GeometricSupport = Math.Round(geometricSupport, 4),
            Meta = meta
        });
    }

    private static (double Shop, double Site) GetWeldStrengths(IReadOnlyList<BaseWeld> welds)
    {
        var seen = new HashSet<int>();
        double shop = 0.0;
        double site = 0.0;

        if (welds == null)
        {
            return (0.0, 0.0);
        }

        foreach (var weld in welds)
        {
            if (weld == null || !seen.Add(weld.Identifier.ID))
            {
                continue;
            }

            var magnitude = 1.0 + (Math.Max(weld.SizeAbove, weld.SizeBelow) / 10.0);
            if (weld.ShopWeld)
            {
                shop += magnitude;
            }
            else
            {
                site += magnitude;
            }
        }

        return (Math.Round(shop, 4), Math.Round(site, 4));
    }

    private static (int Cuts, int Adds) GetBooleanCounts(IReadOnlyList<BooleanPart> booleans)
    {
        var seen = new HashSet<int>();
        var cuts = 0;
        var adds = 0;

        if (booleans == null)
        {
            return (0, 0);
        }

        foreach (var booleanPart in booleans)
        {
            if (booleanPart == null || !seen.Add(booleanPart.Identifier.ID))
            {
                continue;
            }

            var typeText = booleanPart.Type.ToString();
            if (typeText.IndexOf("WELDPREP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            if (typeText.IndexOf("cut", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                cuts++;
            }
            else
            {
                adds++;
            }
        }

        return (cuts, adds);
    }

    private static List<ExportedVector3> GetContourPoints(
        Part part,
        CoordinateSystem coordinateSystem,
        LocalBoundingBox localBox,
        bool isPlateLike,
        bool isSpecialShape,
        double? thickness)
    {
        var contourPlate = part as ContourPlate;
        if (contourPlate != null && contourPlate.Contour != null)
        {
            var points = new List<ExportedVector3>();
            foreach (var item in contourPlate.Contour.ContourPoints.Cast<object>())
            {
                var contourPoint = item as ContourPoint;
                if (contourPoint == null)
                {
                    continue;
                }

                points.Add(new ExportedVector3
                {
                    X = Math.Round(contourPoint.X, 6),
                    Y = Math.Round(contourPoint.Y, 6),
                    Z = Math.Round(contourPoint.Z, 6)
                });
            }

            if (points.Count >= 3)
            {
                return points;
            }
        }

        if (isPlateLike && !isSpecialShape && part is Beam)
        {
            return PlateFaceCorners(coordinateSystem, localBox, thickness);
        }

        return new List<ExportedVector3>();
    }

    private static List<ExportedVector3> PlateFaceCorners(
        CoordinateSystem coordinateSystem,
        LocalBoundingBox localBox,
        double? thickness)
    {
        if (coordinateSystem == null || localBox == null)
        {
            return new List<ExportedVector3>();
        }

        var min = localBox.Min;
        var max = localBox.Max;
        var dx = Math.Abs(max.X - min.X);
        var dy = Math.Abs(max.Y - min.Y);
        var dz = Math.Abs(max.Z - min.Z);
        var plateT = thickness.HasValue && thickness.Value > 0
            ? thickness.Value
            : new[] { dx, dy, dz }.Where(value => value > 1e-6).DefaultIfEmpty(0.0).Min();
        var errX = Math.Abs(dx - plateT);
        var errY = Math.Abs(dy - plateT);
        var errZ = Math.Abs(dz - plateT);
        var tAxis = 0;
        var best = errX;
        if (errY < best)
        {
            tAxis = 1;
            best = errY;
        }

        if (errZ < best)
        {
            tAxis = 2;
        }

        var midX = (min.X + max.X) / 2.0;
        var midY = (min.Y + max.Y) / 2.0;
        var midZ = (min.Z + max.Z) / 2.0;
        Point[] local;
        if (tAxis == 2)
        {
            local = new[]
            {
                new Point(min.X, min.Y, midZ),
                new Point(max.X, min.Y, midZ),
                new Point(max.X, max.Y, midZ),
                new Point(min.X, max.Y, midZ)
            };
        }
        else if (tAxis == 1)
        {
            local = new[]
            {
                new Point(min.X, midY, min.Z),
                new Point(max.X, midY, min.Z),
                new Point(max.X, midY, max.Z),
                new Point(min.X, midY, max.Z)
            };
        }
        else
        {
            local = new[]
            {
                new Point(midX, min.Y, min.Z),
                new Point(midX, max.Y, min.Z),
                new Point(midX, max.Y, max.Z),
                new Point(midX, min.Y, max.Z)
            };
        }

        var result = new List<ExportedVector3>(4);
        foreach (var point in local)
        {
            result.Add(ToExportedVector3(LocalToGlobal(coordinateSystem, point)));
        }

        return result;
    }

    private static Point LocalToGlobal(CoordinateSystem coordinateSystem, Point local)
    {
        var origin = coordinateSystem.Origin;
        var axisX = Normalize(ToVector(coordinateSystem.AxisX));
        var axisY = Normalize(ToVector(coordinateSystem.AxisY));
        var axisZ = Normalize(Cross(axisX, axisY));
        return new Point(
            origin.X + (local.X * axisX.X) + (local.Y * axisY.X) + (local.Z * axisZ.X),
            origin.Y + (local.X * axisX.Y) + (local.Y * axisY.Y) + (local.Z * axisZ.Y),
            origin.Z + (local.X * axisX.Z) + (local.Y * axisY.Z) + (local.Z * axisZ.Z));
    }

    private static int ComputeConcaveCornerCount(List<ExportedVector3> contourPoints, CoordinateSystem coordinateSystem)
    {
        if (contourPoints == null || contourPoints.Count < 4)
        {
            return 0;
        }

        var local = MatrixFactory.ToCoordinateSystem(coordinateSystem);
        var polygon = contourPoints
            .Select(point => local.Transform(new Point(point.X, point.Y, point.Z)))
            .ToList();

        var signedArea = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Count];
            signedArea += (current.X * next.Y) - (next.X * current.Y);
        }

        var winding = signedArea >= 0 ? 1.0 : -1.0;
        var concave = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var prev = polygon[(i - 1 + polygon.Count) % polygon.Count];
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Count];
            var cross = ((current.X - prev.X) * (next.Y - current.Y)) - ((current.Y - prev.Y) * (next.X - current.X));
            if ((cross * winding) < -1e-6)
            {
                concave++;
            }
        }

        return concave;
    }

    private static double? ResolveThickness(Part part, string profileString, LocalBoundingBox localBox)
    {
        var reportThickness = TryGetNullableDoubleReportProperty(part, "DIM_A")
            ?? TryGetNullableDoubleReportProperty(part, "PROFILE.WIDTH")
            ?? TryGetNullableDoubleReportProperty(part, "PLATE_THICKNESS");
        if (reportThickness.HasValue && reportThickness.Value > 0)
        {
            return reportThickness.Value;
        }

        var parsed = ParsePlateThickness(profileString);
        if (parsed.HasValue && parsed.Value > 0)
        {
            return parsed.Value;
        }

        var dims = new[]
        {
            Math.Abs(localBox.Max.X - localBox.Min.X),
            Math.Abs(localBox.Max.Y - localBox.Min.Y),
            Math.Abs(localBox.Max.Z - localBox.Min.Z)
        }
        .Where(value => value > 0)
        .OrderBy(value => value)
        .ToArray();

        return dims.Length == 0 ? (double?)null : dims[0];
    }

    private static double? ParsePlateThickness(string profileString)
    {
        if (string.IsNullOrWhiteSpace(profileString))
        {
            return null;
        }

        var match = Regex.Match(profileString, @"PL\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        double value;
        return double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ? value : (double?)null;
    }

    private static bool IsPlateProfile(string profileString)
    {
        if (string.IsNullOrWhiteSpace(profileString))
        {
            return false;
        }

        return profileString.StartsWith("PL", StringComparison.OrdinalIgnoreCase) ||
               profileString.StartsWith("FLAT", StringComparison.OrdinalIgnoreCase);
    }

    private static string MapRuntimeType(Part part)
    {
        if (part is ContourPlate)
        {
            return "ContourPlate";
        }

        if (part is Beam)
        {
            return "Beam";
        }

        if (part is PolyBeam)
        {
            return "PolyBeam";
        }

        return part.GetType().Name;
    }

    private static GlobalBoundingBox GetGlobalBoundingBox(Part part)
    {
        var solid = part.GetSolid();
        return new GlobalBoundingBox(solid.MinimumPoint, solid.MaximumPoint);
    }

    private static LocalBoundingBox GetLocalBoundingBox(Model model, Part part, CoordinateSystem coordinateSystem)
    {
        var handler = model.GetWorkPlaneHandler();
        var current = handler.GetCurrentTransformationPlane();
        try
        {
            handler.SetCurrentTransformationPlane(new TransformationPlane(coordinateSystem));
            var solid = part.GetSolid();
            return new LocalBoundingBox(solid.MinimumPoint, solid.MaximumPoint);
        }
        finally
        {
            handler.SetCurrentTransformationPlane(current);
        }
    }

    private static string TryGetModelPath(Model model)
    {
        try
        {
            return model.GetInfo().ModelPath ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryGetStringReportProperty(ModelObject modelObject, string propertyName)
    {
        var value = string.Empty;
        return modelObject.GetReportProperty(propertyName, ref value) ? value : null;
    }

    private static double TryGetDoubleReportProperty(ModelObject modelObject, string propertyName)
    {
        double value = 0.0;
        return modelObject.GetReportProperty(propertyName, ref value) ? Math.Round(value, 6) : 0.0;
    }

    private static double? TryGetNullableDoubleReportProperty(ModelObject modelObject, string propertyName)
    {
        double value = 0.0;
        return modelObject.GetReportProperty(propertyName, ref value) ? value : (double?)null;
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }

    // === 新增方法: 轮廓段详情（含ChamferType） ===
    private static List<ExportedContourSegment> GetContourSegments(Part part, List<ExportedVector3> contourPoints)
    {
        var segments = new List<ExportedContourSegment>();
        var contourPlate = part as ContourPlate;
        if (contourPlate == null || contourPlate.Contour == null)
        {
            return SegmentsFromPoints(contourPoints);
        }

        var sourcePoints = contourPlate.Contour.ContourPoints.Cast<object>().ToList();
        for (int i = 0; i < sourcePoints.Count; i++)
        {
            var current = sourcePoints[i] as ContourPoint;
            var next = sourcePoints[(i + 1) % sourcePoints.Count] as ContourPoint;
            if (current == null || next == null) continue;

            var chamferType = "None";
            double chamferX = 0, chamferY = 0;

            try
            {
                var chamfer = current.Chamfer;
                if (chamfer != null)
                {
                    chamferX = chamfer.X;
                    chamferY = chamfer.Y;
                    chamferType = chamfer.Type.ToString();
                }
            }
            catch
            {
                // 部分零件可能无法获取 Chamfer 信息
            }

            var startPt = new Point(current.X, current.Y, current.Z);
            var endPt = new Point(next.X, next.Y, next.Z);
            var length = Math.Sqrt(
                Math.Pow(endPt.X - startPt.X, 2) +
                Math.Pow(endPt.Y - startPt.Y, 2) +
                Math.Pow(endPt.Z - startPt.Z, 2));

            segments.Add(new ExportedContourSegment
            {
                StartPoint = ToExportedVector3(startPt),
                EndPoint = ToExportedVector3(endPt),
                ChamferType = chamferType,
                ChamferX = Math.Round(chamferX, 4),
                ChamferY = Math.Round(chamferY, 4),
                Length = Math.Round(length, 4)
            });
        }

        return segments;
    }

    private static List<ExportedContourSegment> SegmentsFromPoints(List<ExportedVector3> contourPoints)
    {
        var segments = new List<ExportedContourSegment>();
        if (contourPoints == null || contourPoints.Count < 3)
        {
            return segments;
        }

        for (var i = 0; i < contourPoints.Count; i++)
        {
            var start = contourPoints[i];
            var end = contourPoints[(i + 1) % contourPoints.Count];
            var length = Math.Sqrt(
                Math.Pow(end.X - start.X, 2) +
                Math.Pow(end.Y - start.Y, 2) +
                Math.Pow(end.Z - start.Z, 2));
            segments.Add(new ExportedContourSegment
            {
                StartPoint = start,
                EndPoint = end,
                ChamferType = "None",
                ChamferX = 0,
                ChamferY = 0,
                Length = Math.Round(length, 4)
            });
        }

        return segments;
    }

    // === 新增方法: 螺栓孔详情 ===
    private static List<ExportedBoltHole> GetBoltHoles(IReadOnlyList<BoltGroup> bolts)
    {
        var holes = new List<ExportedBoltHole>();
        var seen = new HashSet<int>();
        if (bolts == null)
        {
            return holes;
        }

        foreach (var bolt in bolts)
        {
            if (bolt == null || !seen.Add(bolt.Identifier.ID)) continue;

            var partToBoltTo = bolt.PartToBoltTo;
            var partToBeBolted = bolt.PartToBeBolted;

            // BoltGroup 在 Tekla 2017 中没有直接的3D坐标，用solid包围盒中心
            Point pos;
            try
            {
                var boltSolid = bolt.GetSolid();
                pos = new Point(
                    (boltSolid.MinimumPoint.X + boltSolid.MaximumPoint.X) / 2.0,
                    (boltSolid.MinimumPoint.Y + boltSolid.MaximumPoint.Y) / 2.0,
                    (boltSolid.MinimumPoint.Z + boltSolid.MaximumPoint.Z) / 2.0);
            }
            catch
            {
                pos = new Point(0, 0, 0);
            }

            holes.Add(new ExportedBoltHole
            {
                Position = ToExportedVector3(pos),
                Diameter = bolt.BoltSize,
                BoltGroupType = bolt.BoltStandard ?? bolt.BoltType.ToString(),
                BoltGroupPartIdA = partToBoltTo != null ? partToBoltTo.Identifier.ID : 0,
                BoltGroupPartIdB = partToBeBolted != null ? partToBeBolted.Identifier.ID : 0
            });
        }

        return holes;
    }

    // === 新增方法: 焊缝详情 ===
    private static List<ExportedWeldDetail> GetWeldDetails(IReadOnlyList<BaseWeld> welds)
    {
        var details = new List<ExportedWeldDetail>();
        var seen = new HashSet<int>();
        if (welds == null)
        {
            return details;
        }

        foreach (var weld in welds)
        {
            if (weld == null || !seen.Add(weld.Identifier.ID)) continue;

            var main = weld.MainObject as Part;
            var secondary = weld.SecondaryObject as Part;

            double weldLengthAbove = 0;
            double weldLengthBelow = 0;
            try
            {
                weldLengthAbove = weld.LengthAbove;
                weldLengthBelow = weld.LengthBelow;
            }
            catch { }

            details.Add(new ExportedWeldDetail
            {
                MainPartId = main != null ? main.Identifier.ID : 0,
                SecondaryPartId = secondary != null ? secondary.Identifier.ID : 0,
                WeldType = weld.GetType().Name,
                SizeAbove = Math.Round(weld.SizeAbove, 4),
                SizeBelow = Math.Round(weld.SizeBelow, 4),
                Length = Math.Round(Math.Max(weldLengthAbove, weldLengthBelow), 4),
                ShopWeld = weld.ShopWeld,
                AroundWeld = weld.AroundWeld
            });
        }

        return details;
    }

    // === 新增方法: Boolean切割详情 ===
    private static List<ExportedBooleanCut> GetBooleanCutDetails(Part father, IReadOnlyList<BooleanPart> booleans)
    {
        var details = new List<ExportedBooleanCut>();
        var seen = new HashSet<int>();
        if (booleans == null)
        {
            return details;
        }

        var fatherRaw = TryGetRawSolid(father);
        foreach (var booleanPart in booleans)
        {
            if (booleanPart == null || !seen.Add(booleanPart.Identifier.ID)) continue;

            var operativePart = booleanPart.OperativePart;
            if (operativePart == null) continue;

            var typeText = booleanPart.Type.ToString();
            // 只导出Cut类型
            if (typeText.IndexOf("cut", StringComparison.OrdinalIgnoreCase) < 0) continue;

            Point min;
            Point max;
            try
            {
                var opSolid = operativePart.GetSolid();
                min = opSolid.MinimumPoint;
                max = opSolid.MaximumPoint;
            }
            catch
            {
                continue;
            }

            string proof;
            var cutsFather = CutterRemovesMaterial(fatherRaw, operativePart, out proof);
            details.Add(new ExportedBooleanCut
            {
                OperativePartId = operativePart.Identifier.ID,
                CutType = typeText,
                BoundingBox = new ExportedBoundingBox
                {
                    Min = ToExportedVector3(min),
                    Max = ToExportedVector3(max)
                },
                CutsFatherSolid = cutsFather,
                CutProof = proof
            });
        }

        return details;
    }

    private const double BevelTiltMinDeg = 8.0;
    private const double BevelTiltMaxDeg = 82.0;

    private static Dictionary<int, List<ModelObject>> IndexBevelObjectsByFather(Model model)
    {
        var map = new Dictionary<int, List<ModelObject>>();
        if (model == null)
        {
            return map;
        }

        ModelObjectSelector selector;
        try
        {
            selector = model.GetModelObjectSelector();
        }
        catch
        {
            return map;
        }

        var types = new[]
        {
            ModelObject.ModelObjectEnum.EDGE_CHAMFER,
            ModelObject.ModelObjectEnum.FITTING,
            ModelObject.ModelObjectEnum.CUTPLANE
        };

        foreach (var type in types)
        {
            ModelObjectEnumerator objects;
            try
            {
                objects = selector.GetAllObjectsWithType(type);
            }
            catch
            {
                continue;
            }

            while (objects != null && objects.MoveNext())
            {
                var current = objects.Current as Tekla.Structures.Model.Boolean;
                if (current == null)
                {
                    continue;
                }

                Part father;
                try
                {
                    father = current.Father as Part;
                }
                catch
                {
                    continue;
                }

                if (father == null)
                {
                    continue;
                }

                var fatherId = SafeGetIdentifier(father);
                if (fatherId == 0)
                {
                    continue;
                }

                List<ModelObject> list;
                if (!map.TryGetValue(fatherId, out list))
                {
                    list = new List<ModelObject>();
                    map[fatherId] = list;
                }

                list.Add(current);
            }
        }

        return map;
    }

    private static List<ExportedEdgeBevel> GetEdgeBevels(
        Part part,
        Vector plateNormal,
        Vector longAxis,
        string profileString,
        bool isPlateLike,
        double? partThickness,
        GlobalBoundingBox plateBox,
        List<ModelObject> extra,
        IReadOnlyList<BooleanPart> partBooleans,
        out List<string> objectTypes,
        out int endChamferCount)
    {
        var thicknessCandidates = BevelThicknessCandidates(profileString, partThickness, isPlateLike);
        var details = new List<ExportedEdgeBevel>();
        var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();
        var objects = new List<ModelObject>();
        endChamferCount = 0;

        var fatherRaw = TryGetRawSolid(part);
        if (partBooleans != null)
        {
            foreach (var booleanPart in partBooleans)
            {
                AddBooleanLike(booleanPart, seen, objects, typeNames);
            }
        }

        CollectBooleanLikeRecursive(part, seen, objects, typeNames, 0);
        if (extra != null)
        {
            foreach (var item in extra)
            {
                AddBooleanLike(item, seen, objects, typeNames);
            }
        }

        foreach (var current in objects)
        {
            try
            {
                var edgeChamfer = current as EdgeChamfer;
                if (edgeChamfer != null)
                {
                    details.Add(FromEdgeChamfer(edgeChamfer));
                    continue;
                }

                var fitting = current as Fitting;
                if (fitting != null)
                {
                    details.Add(FromPlaneBoolean("FITTING", fitting, fitting.Plane, plateNormal));
                    continue;
                }

                var cutPlane = current as CutPlane;
                if (cutPlane != null)
                {
                    details.Add(FromPlaneBoolean("CUT_PLANE", cutPlane, cutPlane.Plane, plateNormal));
                    continue;
                }

                var booleanPart = current as BooleanPart;
                if (booleanPart == null)
                {
                    continue;
                }

                var typeText = booleanPart.Type.ToString();
                if (typeText.IndexOf("WELDPREP", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    details.Add(FromWeldPrep(booleanPart));
                    continue;
                }

                if (typeText.IndexOf("cut", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var bevel = FromBooleanCutIfBevel(
                        booleanPart,
                        fatherRaw,
                        plateNormal,
                        longAxis,
                        isPlateLike,
                        thicknessCandidates,
                        plateBox);
                    if (bevel != null)
                    {
                        details.Add(bevel);
                    }
                    else if (!isPlateLike && IsBooleanCutEndChamfer(
                        booleanPart,
                        fatherRaw,
                        plateNormal,
                        longAxis,
                        thicknessCandidates,
                        plateBox))
                    {
                        endChamferCount++;
                    }
                }
            }
            catch
            {
            }
        }

        objectTypes = typeNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        return details;
    }

    private static PartObjectCache LoadPartObjectCache(Part part)
    {
        var cache = new PartObjectCache();
        if (part == null)
        {
            return cache;
        }

        var booleanSeen = new HashSet<int>();
        var booleans = TryGetBooleans(part);
        while (booleans != null && booleans.MoveNext())
        {
            var booleanPart = booleans.Current as BooleanPart;
            if (booleanPart != null && booleanSeen.Add(SafeGetIdentifier(booleanPart)))
            {
                cache.Booleans.Add(booleanPart);
            }
        }

        try
        {
            var bolts = part.GetBolts();
            var boltSeen = new HashSet<int>();
            while (bolts != null && bolts.MoveNext())
            {
                var bolt = bolts.Current as BoltGroup;
                if (bolt != null && boltSeen.Add(SafeGetIdentifier(bolt)))
                {
                    cache.Bolts.Add(bolt);
                }
            }
        }
        catch
        {
        }

        try
        {
            var welds = part.GetWelds();
            var weldSeen = new HashSet<int>();
            while (welds != null && welds.MoveNext())
            {
                var weld = welds.Current as BaseWeld;
                if (weld != null && weldSeen.Add(SafeGetIdentifier(weld)))
                {
                    cache.Welds.Add(weld);
                }
            }
        }
        catch
        {
        }

        return cache;
    }

    private static ModelObjectEnumerator TryGetBooleans(Part part)
    {
        try
        {
            return part.GetBooleans();
        }
        catch
        {
            return null;
        }
    }

    private static Tekla.Structures.Model.Solid TryGetRawSolid(Part part)
    {
        if (part == null)
        {
            return null;
        }

        try
        {
            return part.GetSolid(Tekla.Structures.Model.Solid.SolidCreationTypeEnum.RAW);
        }
        catch
        {
            try
            {
                return part.GetSolid();
            }
            catch
            {
                return null;
            }
        }
    }

    private static bool CutterRemovesMaterial(Tekla.Structures.Model.Solid fatherRaw, Part operative, out string proof)
    {
        proof = "SOLID_UNAVAILABLE";
        if (fatherRaw == null || operative == null)
        {
            return false;
        }

        Tekla.Structures.Model.Solid cutter;
        try
        {
            cutter = operative.GetSolid();
        }
        catch
        {
            return false;
        }

        if (cutter == null)
        {
            return false;
        }

        if (!SolidAabbsOverlap(fatherRaw, cutter))
        {
            proof = "AABB_MISS";
            return false;
        }

        try
        {
            var shells = fatherRaw.GetCutPart(cutter);
            while (shells != null && shells.MoveNext())
            {
                var shell = shells.Current as Tekla.Structures.Solid.Shell;
                if (shell == null)
                {
                    continue;
                }

                var faces = shell.GetFaceEnumerator();
                if (faces != null && faces.MoveNext() && faces.Current != null)
                {
                    proof = "GET_CUT_PART";
                    return true;
                }
            }

            proof = "NO_SHELL";
            return false;
        }
        catch
        {
            proof = "GET_CUT_PART_FAILED";
            return false;
        }
    }

    private static bool SolidAabbsOverlap(Tekla.Structures.Model.Solid left, Tekla.Structures.Model.Solid right)
    {
        const double margin = 0.5;
        try
        {
            var leftMin = left.MinimumPoint;
            var leftMax = left.MaximumPoint;
            var rightMin = right.MinimumPoint;
            var rightMax = right.MaximumPoint;
            return leftMin.X <= rightMax.X + margin && leftMax.X >= rightMin.X - margin
                && leftMin.Y <= rightMax.Y + margin && leftMax.Y >= rightMin.Y - margin
                && leftMin.Z <= rightMax.Z + margin && leftMax.Z >= rightMin.Z - margin;
        }
        catch
        {
            return false;
        }
    }

    private static void CollectBooleanLikeRecursive(
        ModelObject parent,
        HashSet<int> seen,
        List<ModelObject> sink,
        HashSet<string> typeNames,
        int depth)
    {
        if (parent == null || depth > 3)
        {
            return;
        }

        ModelObjectEnumerator children;
        try
        {
            children = parent.GetChildren();
        }
        catch
        {
            return;
        }

        while (children != null && children.MoveNext())
        {
            var child = children.Current as ModelObject;
            if (child == null)
            {
                continue;
            }

            AddBooleanLike(child, seen, sink, typeNames);
            if (child is Part)
            {
                continue;
            }

            CollectBooleanLikeRecursive(child, seen, sink, typeNames, depth + 1);
        }
    }

    private static void AppendEnumerator(
        ModelObjectEnumerator enumerator,
        HashSet<int> seen,
        List<ModelObject> sink,
        HashSet<string> typeNames)
    {
        while (enumerator != null && enumerator.MoveNext())
        {
            AddBooleanLike(enumerator.Current as ModelObject, seen, sink, typeNames);
        }
    }

    private static void AddBooleanLike(
        ModelObject current,
        HashSet<int> seen,
        List<ModelObject> sink,
        HashSet<string> typeNames)
    {
        if (current == null)
        {
            return;
        }

        var id = SafeGetIdentifier(current);
        if (id != 0 && !seen.Add(id))
        {
            return;
        }

        try
        {
            typeNames.Add(current.GetType().Name);
        }
        catch
        {
        }

        if (current is EdgeChamfer || current is Fitting || current is CutPlane || current is BooleanPart)
        {
            sink.Add(current);
        }
    }

    private static ExportedEdgeBevel FromEdgeChamfer(EdgeChamfer edgeChamfer)
    {
        var item = new ExportedEdgeBevel
        {
            BooleanId = SafeGetIdentifier(edgeChamfer),
            Kind = "EDGE_CHAMFER",
            Name = edgeChamfer.Name ?? string.Empty,
            IsBevel = true
        };

        try
        {
            item.FirstEnd = ToExportedVector3(edgeChamfer.FirstEnd);
            item.SecondEnd = ToExportedVector3(edgeChamfer.SecondEnd);
            item.FirstBevelDimension = Math.Round(edgeChamfer.FirstBevelDimension, 4);
            item.SecondBevelDimension = Math.Round(edgeChamfer.SecondBevelDimension, 4);
            item.FirstEndType = edgeChamfer.FirstChamferEndType.ToString();
            item.SecondEndType = edgeChamfer.SecondChamferEndType.ToString();
        }
        catch
        {
        }

        try
        {
            var chamfer = edgeChamfer.Chamfer;
            if (chamfer != null)
            {
                item.ChamferType = chamfer.Type.ToString();
                item.ChamferX = Math.Round(chamfer.X, 4);
                item.ChamferY = Math.Round(chamfer.Y, 4);
                item.Dz1 = Math.Round(chamfer.DZ1, 4);
                item.Dz2 = Math.Round(chamfer.DZ2, 4);
            }
        }
        catch
        {
        }

        return item;
    }

    private static ExportedEdgeBevel FromWeldPrep(BooleanPart booleanPart)
    {
        var item = new ExportedEdgeBevel
        {
            BooleanId = SafeGetIdentifier(booleanPart),
            Kind = "WELD_PREP",
            ChamferType = booleanPart.Type.ToString(),
            IsBevel = true
        };

        try
        {
            var operative = booleanPart.OperativePart;
            if (operative != null)
            {
                item.OperativePartId = operative.Identifier.ID;
            }
        }
        catch
        {
        }

        return item;
    }

    private static ExportedEdgeBevel FromPlaneBoolean(string kind, ModelObject source, Plane plane, Vector plateNormal)
    {
        var item = new ExportedEdgeBevel
        {
            BooleanId = SafeGetIdentifier(source),
            Kind = kind,
            IsBevel = false
        };

        if (plane == null)
        {
            return item;
        }

        var planeNormal = Normalize(Cross(ToVector(plane.AxisX), ToVector(plane.AxisY)));
        var angle = AngleBetweenAbsDeg(planeNormal, plateNormal);
        item.PlaneOrigin = ToExportedVector3(plane.Origin);
        item.PlaneNormal = ToExportedVector3(planeNormal);
        item.AngleToPlateNormalDeg = Math.Round(angle, 4);
        item.IsBevel = angle >= BevelTiltMinDeg && angle <= BevelTiltMaxDeg;
        return item;
    }

    private static ExportedEdgeBevel FromBooleanCutIfBevel(
        BooleanPart booleanPart,
        Tekla.Structures.Model.Solid fatherRaw,
        Vector plateNormal,
        Vector longAxis,
        bool isPlateLike,
        List<double> thicknesses,
        GlobalBoundingBox plateBox)
    {
        Part operative;
        try
        {
            operative = booleanPart.OperativePart;
        }
        catch
        {
            return null;
        }

        if (operative == null)
        {
            return null;
        }

        Point min;
        Point max;
        try
        {
            var solid = operative.GetSolid();
            min = solid.MinimumPoint;
            max = solid.MaximumPoint;
        }
        catch
        {
            return null;
        }

        string proof;
        var cutsFather = CutterRemovesMaterial(fatherRaw, operative, out proof);
        if (!cutsFather)
        {
            return null;
        }

        var cutBox = new GlobalBoundingBox(min, max);
        if (!IsBooleanCutBevel(plateBox, cutBox, plateNormal, longAxis, isPlateLike, thicknesses))
        {
            return null;
        }

        var size = BoxSize(cutBox);
        return new ExportedEdgeBevel
        {
            BooleanId = SafeGetIdentifier(booleanPart),
            Kind = "BOOLEAN_CUT",
            OperativePartId = operative.Identifier.ID,
            ChamferX = Math.Round(Math.Abs(size.X), 4),
            ChamferY = Math.Round(Math.Abs(size.Y), 4),
            Dz1 = Math.Round(Math.Abs(size.Z), 4),
            PlaneOrigin = ToExportedVector3(GetCenter(cutBox)),
            IsBevel = true,
            BoundingBox = new ExportedBoundingBox
            {
                Min = ToExportedVector3(min),
                Max = ToExportedVector3(max)
            },
            CutsFatherSolid = true,
            CutProof = proof
        };
    }

    private static bool IsBooleanCutBevel(
        GlobalBoundingBox plate,
        GlobalBoundingBox cut,
        Vector plateNormal,
        Vector longAxis,
        bool isPlateLike,
        List<double> thicknesses)
    {
        if (thicknesses != null)
        {
            foreach (var thickness in thicknesses)
            {
                if (IsRimWedgeCut(plate, cut, plateNormal, thickness))
                {
                    return true;
                }
            }
        }

        return !isPlateLike && IsMemberEndBevelCut(plate, cut, longAxis, thicknesses);
    }

    private static bool IsRimWedgeCut(
        GlobalBoundingBox plate,
        GlobalBoundingBox cut,
        Vector plateNormal,
        double thickness)
    {
        if (plate == null || cut == null || thickness <= 1e-6)
        {
            return false;
        }

        var size = BoxSize(cut);
        var dims = new[] { Math.Abs(size.X), Math.Abs(size.Y), Math.Abs(size.Z) };
        Array.Sort(dims);
        var thinLimit = (thickness * 2.5) + 8.0;
        var shortLimit = (thickness * 4.0) + 10.0;
        var longMin = Math.Max(thickness * 8.0, 80.0);
        if (dims[0] > thinLimit || dims[1] > shortLimit || dims[2] < longMin)
        {
            return false;
        }

        var normal = Normalize(plateNormal);
        var cutCenter = GetCenter(cut);
        var rimTol = Math.Max(thickness * 2.0, 10.0);
        var nComp = new[] { normal.X, normal.Y, normal.Z };
        var plateMin = new[] { plate.Min.X, plate.Min.Y, plate.Min.Z };
        var plateMax = new[] { plate.Max.X, plate.Max.Y, plate.Max.Z };
        var cutC = new[] { cutCenter.X, cutCenter.Y, cutCenter.Z };
        for (var i = 0; i < 3; i++)
        {
            if (Math.Abs(nComp[i]) > 0.75)
            {
                continue;
            }

            var dist = Math.Min(Math.Abs(cutC[i] - plateMin[i]), Math.Abs(cutC[i] - plateMax[i]));
            if (dist <= rimTol)
            {
                return true;
            }
        }

        return false;
    }

    private static Vector BoxSize(GlobalBoundingBox box)
    {
        return new Vector(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z);
    }

    private static bool IsMemberEndBevelCut(
        GlobalBoundingBox plate,
        GlobalBoundingBox cut,
        Vector longAxis,
        List<double> thicknesses)
    {
        if (plate == null || cut == null || thicknesses == null || thicknesses.Count == 0)
        {
            return false;
        }

        var tMin = thicknesses[0];
        var tMax = thicknesses[0];
        foreach (var thickness in thicknesses)
        {
            if (thickness < tMin) tMin = thickness;
            if (thickness > tMax) tMax = thickness;
        }

        if (tMin <= 1e-6)
        {
            return false;
        }

        var along = ExtentAlong(cut, longAxis);
        var alongMax = Math.Max(tMax * 8.0, 180.0);
        if (along < (tMin * 0.4) || along > alongMax)
        {
            return false;
        }

        double plateMin;
        double plateMax;
        StationRange(plate, longAxis, out plateMin, out plateMax);
        var cutStation = Station(GetCenter(cut), longAxis);
        var endDist = Math.Min(Math.Abs(cutStation - plateMin), Math.Abs(cutStation - plateMax));
        var endTol = Math.Max(tMax * 8.0, 250.0);
        return endDist <= endTol;
    }

    private static bool IsBooleanCutEndChamfer(
        BooleanPart booleanPart,
        Tekla.Structures.Model.Solid fatherRaw,
        Vector plateNormal,
        Vector longAxis,
        List<double> thicknesses,
        GlobalBoundingBox plateBox)
    {
        Part operative;
        try
        {
            operative = booleanPart.OperativePart;
        }
        catch
        {
            return false;
        }

        if (operative == null)
        {
            return false;
        }

        string proof;
        if (!CutterRemovesMaterial(fatherRaw, operative, out proof))
        {
            return false;
        }

        Point min;
        Point max;
        try
        {
            var solid = operative.GetSolid();
            min = solid.MinimumPoint;
            max = solid.MaximumPoint;
        }
        catch
        {
            return false;
        }

        return IsMemberEndChamferCut(plateBox, new GlobalBoundingBox(min, max), plateNormal, longAxis, thicknesses);
    }

    private static bool IsMemberEndChamferCut(
        GlobalBoundingBox plate,
        GlobalBoundingBox cut,
        Vector plateNormal,
        Vector longAxis,
        List<double> thicknesses)
    {
        if (plate == null || cut == null)
        {
            return false;
        }

        var tMax = 10.0;
        if (thicknesses != null && thicknesses.Count > 0)
        {
            tMax = thicknesses[0];
            foreach (var thickness in thicknesses)
            {
                if (thickness > tMax) tMax = thickness;
            }
        }

        if (tMax <= 1e-6)
        {
            return false;
        }

        var memberLen = ExtentAlong(plate, longAxis);
        var along = ExtentAlong(cut, longAxis);
        if (along < tMax || along > Math.Max(memberLen * 0.35, 80.0) || along > memberLen * 0.45)
        {
            return false;
        }

        double plateMin;
        double plateMax;
        StationRange(plate, longAxis, out plateMin, out plateMax);
        var cutStation = Station(GetCenter(cut), longAxis);
        var endDist = Math.Min(Math.Abs(cutStation - plateMin), Math.Abs(cutStation - plateMax));
        var endTol = Math.Max(tMax * 8.0, 250.0);
        if (endDist > endTol)
        {
            return false;
        }

        var cutCenter = GetCenter(cut);
        var rimTol = Math.Max(tMax * 3.0, 40.0);
        var normal = Normalize(plateNormal);
        var nComp = new[] { normal.X, normal.Y, normal.Z };
        var axis = Normalize(longAxis);
        var aComp = new[] { axis.X, axis.Y, axis.Z };
        var plateMinXYZ = new[] { plate.Min.X, plate.Min.Y, plate.Min.Z };
        var plateMaxXYZ = new[] { plate.Max.X, plate.Max.Y, plate.Max.Z };
        var cutC = new[] { cutCenter.X, cutCenter.Y, cutCenter.Z };
        for (var i = 0; i < 3; i++)
        {
            if (Math.Abs(aComp[i]) > 0.75 || Math.Abs(nComp[i]) > 0.75)
            {
                continue;
            }

            var dist = Math.Min(Math.Abs(cutC[i] - plateMinXYZ[i]), Math.Abs(cutC[i] - plateMaxXYZ[i]));
            if (dist <= rimTol)
            {
                return true;
            }
        }

        return false;
    }

    private static double ExtentAlong(GlobalBoundingBox box, Vector axis)
    {
        var direction = Normalize(axis);
        var size = BoxSize(box);
        return (Math.Abs(direction.X) * Math.Abs(size.X))
            + (Math.Abs(direction.Y) * Math.Abs(size.Y))
            + (Math.Abs(direction.Z) * Math.Abs(size.Z));
    }

    private static double Station(Point point, Vector axis)
    {
        var direction = Normalize(axis);
        return Dot(new Vector(point.X, point.Y, point.Z), direction);
    }

    private static void StationRange(GlobalBoundingBox box, Vector axis, out double minStation, out double maxStation)
    {
        var xs = new[] { box.Min.X, box.Max.X };
        var ys = new[] { box.Min.Y, box.Max.Y };
        var zs = new[] { box.Min.Z, box.Max.Z };
        minStation = double.PositiveInfinity;
        maxStation = double.NegativeInfinity;
        foreach (var x in xs)
        {
            foreach (var y in ys)
            {
                foreach (var z in zs)
                {
                    var value = Station(new Point(x, y, z), axis);
                    if (value < minStation) minStation = value;
                    if (value > maxStation) maxStation = value;
                }
            }
        }
    }

    private static List<double> BevelThicknessCandidates(string profileString, double? partThickness, bool isPlateLike)
    {
        var result = new List<double>();
        var spec = (profileString ?? string.Empty).Trim().ToUpperInvariant();
        var nums = ParseProfileNumbers(spec);

        if (spec.StartsWith("PL", StringComparison.Ordinal) || spec.StartsWith("FLAT", StringComparison.Ordinal))
        {
            if (nums.Count >= 1)
            {
                AddThicknessCandidate(result, nums[0]);
            }
        }
        else
        {
            AddSectionWallThicknesses(result, nums);
        }

        if (isPlateLike && partThickness.HasValue)
        {
            AddThicknessCandidate(result, partThickness.Value);
        }

        if (result.Count == 0 && partThickness.HasValue && partThickness.Value <= 80.0)
        {
            AddThicknessCandidate(result, partThickness.Value);
        }

        return result;
    }

    private static void AddSectionWallThicknesses(List<double> result, List<double> nums)
    {
        if (nums == null || nums.Count == 0)
        {
            return;
        }

        AddThicknessCandidate(result, nums[nums.Count - 1]);
        if (nums.Count >= 4)
        {
            AddThicknessCandidate(result, nums[2]);
            AddThicknessCandidate(result, nums[3]);
            return;
        }

        if (nums.Count >= 2)
        {
            var last = nums[nums.Count - 1];
            var prev = nums[nums.Count - 2];
            if (prev <= (last * 4.0) + 10.0)
            {
                AddThicknessCandidate(result, prev);
            }
        }
    }

    private static void AddThicknessCandidate(List<double> result, double thickness)
    {
        if (thickness <= 2.0 || thickness > 80.0)
        {
            return;
        }

        foreach (var existing in result)
        {
            if (Math.Abs(existing - thickness) <= 0.4)
            {
                return;
            }
        }

        result.Add(thickness);
    }

    private static List<double> ParseProfileNumbers(string profileString)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(profileString))
        {
            return result;
        }

        foreach (Match match in Regex.Matches(profileString, @"[0-9]+(?:\.[0-9]+)?"))
        {
            double value;
            if (double.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static bool IsArcContourSegment(ExportedContourSegment segment)
    {
        var text = segment != null ? segment.ChamferType ?? string.Empty : string.Empty;
        return text.IndexOf("ARC", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("ROUNDING", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static double AngleBetweenAbsDeg(Vector left, Vector right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        var denom = Math.Sqrt(Dot(a, a) * Dot(b, b));
        if (denom <= 1e-9)
        {
            return 90.0;
        }

        var cos = Math.Abs(Dot(a, b) / denom);
        if (cos > 1.0)
        {
            cos = 1.0;
        }

        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    // === 新增方法: 零件坐标系 ===
    private static ExportedCoordinateSystem ToExportedCoordinateSystem(CoordinateSystem coordinateSystem)
    {
        var origin = coordinateSystem.Origin;
        var axisX = Normalize(ToVector(coordinateSystem.AxisX));
        var axisY = Normalize(ToVector(coordinateSystem.AxisY));
        var axisZ = Cross(axisX, axisY);

        return new ExportedCoordinateSystem
        {
            Origin = ToExportedVector3(origin),
            AxisX = ToExportedVector3(axisX),
            AxisY = ToExportedVector3(axisY),
            AxisZ = ToExportedVector3(axisZ)
        };
    }

    private static void WriteJson<T>(string path, T value)
    {
        using (var stream = File.Create(path))
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            serializer.WriteObject(stream, value);
        }
    }

    private static int SafeGetIdentifier(ModelObject modelObject)
    {
        try
        {
            return modelObject.Identifier.ID;
        }
        catch
        {
            return 0;
        }
    }

    private static ExportedVector3 ToExportedVector3(Point point)
    {
        return new ExportedVector3
        {
            X = Math.Round(point.X, 6),
            Y = Math.Round(point.Y, 6),
            Z = Math.Round(point.Z, 6)
        };
    }

    private static ExportedVector3 ToExportedVector3(Vector vector)
    {
        return new ExportedVector3
        {
            X = Math.Round(vector.X, 6),
            Y = Math.Round(vector.Y, 6),
            Z = Math.Round(vector.Z, 6)
        };
    }

    private static Point ToPoint(ExportedVector3 vector)
    {
        return new Point(vector.X, vector.Y, vector.Z);
    }

    private static Vector ToVector(ExportedVector3 vector)
    {
        return new Vector(vector.X, vector.Y, vector.Z);
    }

    private static double Dot(Vector left, Vector right)
    {
        return (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);
    }
    private static Point GetCenter(GlobalBoundingBox box)
    {
        return new Point(
            (box.Min.X + box.Max.X) / 2.0,
            (box.Min.Y + box.Max.Y) / 2.0,
            (box.Min.Z + box.Max.Z) / 2.0);
    }

    private static Vector ToVector(Vector vector)
    {
        return new Vector(vector.X, vector.Y, vector.Z);
    }

    private static Vector Normalize(Vector vector)
    {
        var length = Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y) + (vector.Z * vector.Z));
        return length <= 1e-9 ? new Vector(0, 0, 0) : new Vector(vector.X / length, vector.Y / length, vector.Z / length);
    }

    private static Vector Cross(Vector left, Vector right)
    {
        return new Vector(
            (left.Y * right.Z) - (left.Z * right.Y),
            (left.Z * right.X) - (left.X * right.Z),
            (left.X * right.Y) - (left.Y * right.X));
    }

    private sealed class GlobalBoundingBox
    {
        public GlobalBoundingBox(Point min, Point max)
        {
            Min = min;
            Max = max;
        }

        public Point Min { get; private set; }

        public Point Max { get; private set; }
    }

    private sealed class LocalBoundingBox
    {
        public LocalBoundingBox(Point min, Point max)
        {
            Min = min;
            Max = max;
        }

        public Point Min { get; private set; }

        public Point Max { get; private set; }
    }
}















