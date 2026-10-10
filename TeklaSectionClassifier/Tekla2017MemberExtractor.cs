using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace TeklaSectionClassifier;

public sealed class Tekla2017MemberExtractor : IMemberExtractor
{
    private readonly Model _model;
    private readonly ExtractionOptions _options;
    private readonly ISectionSampler _sectionSampler;

    public Tekla2017MemberExtractor(Model model, ExtractionOptions options, ISectionSampler sectionSampler)
    {
        _model = model;
        _options = options;
        _sectionSampler = sectionSampler;
    }

    public IEnumerable<MemberSnapshot> ExtractMembers()
    {
        if (!_model.GetConnectionStatus())
        {
            throw new InvalidOperationException("Tekla model is not connected.");
        }

        foreach (var assembly in GetTargetAssemblies())
        {
            var member = TryExtractAssembly(assembly);
            if (member == null)
            {
                continue;
            }

            member.Samples.AddRange(_sectionSampler.BuildSamples(member));
            yield return member;
        }
    }

    private IEnumerable<Assembly> GetTargetAssemblies()
    {
        return _options.SelectedOnly ? GetSelectedAssemblies() : GetAllAssemblies();
    }

    private IEnumerable<Assembly> GetAllAssemblies()
    {
        var selector = _model.GetModelObjectSelector();
        var enumerator = selector.GetAllObjectsWithType(ModelObject.ModelObjectEnum.ASSEMBLY);
        while (enumerator.MoveNext())
        {
            if (enumerator.Current is Assembly assembly)
            {
                yield return assembly;
            }
        }
    }

    private IEnumerable<Assembly> GetSelectedAssemblies()
    {
        var selector = new Tekla.Structures.Model.UI.ModelObjectSelector();
        var selected = selector.GetSelectedObjects();
        var seen = new HashSet<int>();

        while (selected.MoveNext())
        {
            var assembly = ResolveAssembly(selected.Current as ModelObject);
            if (assembly == null)
            {
                continue;
            }

            var id = assembly.Identifier.ID;
            if (seen.Add(id))
            {
                yield return assembly;
            }
        }
    }

    private static Assembly? ResolveAssembly(ModelObject? modelObject)
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

    private MemberSnapshot? TryExtractAssembly(Assembly assembly)
    {
        var assemblyMainPart = assembly.GetMainPart() as Part;
        if (assemblyMainPart == null)
        {
            return null;
        }

        var allParts = GetAssemblyParts(assembly).ToList();
        var referencePart = SelectReferencePart(allParts, assemblyMainPart);
        var memberId = ReadMemberId(assembly, assemblyMainPart);
        if (!string.IsNullOrWhiteSpace(_options.MemberPrefixFilter) &&
            !memberId.StartsWith(_options.MemberPrefixFilter, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var referenceAxis = BuildAxisDefinition(referencePart);
        var snapshot = new MemberSnapshot
        {
            Member = BuildMemberIdentity(assembly, referencePart, referenceAxis, memberId)
        };

        foreach (var part in allParts)
        {
            snapshot.Parts.Add(BuildPartSnapshot(part, snapshot.Member.MainAxis));
        }

        var resolved = AlignedSpanMemberAxis.Resolve(AlignedSpanMemberAxis.FromSnapshots(snapshot.Parts));
        if (resolved.HasAxis)
        {
            ApplyAlignedSpanAxis(snapshot, resolved);
        }
        else
        {
            var longitudinalAxis = BuildLongitudinalAxis(snapshot.Parts, snapshot.Member.MainAxis);
            snapshot.LongitudinalAxisKind = longitudinalAxis.Kind;
            snapshot.GuidePolyline = longitudinalAxis.GuidePolyline;
            snapshot.AxisSegments = longitudinalAxis.AxisSegments;
            snapshot.LongitudinalAxisConfidence = longitudinalAxis.Confidence;
            snapshot.LongitudinalAxisSource = longitudinalAxis.Source;
        }

        return snapshot;
    }

    private static void ApplyAlignedSpanAxis(MemberSnapshot snapshot, AlignedSpanAxisResult resolved)
    {
        snapshot.Member.MainAxis = resolved.Axis;
        foreach (var part in snapshot.Parts)
        {
            var projection = BuildAxisProjection(part.BoundingBox, resolved.Axis);
            part.AxisProjection = projection;
            part.EndProximity = new EndProximity
            {
                NearStart = projection.Start <= resolved.Axis.Length * 0.05,
                NearEnd = projection.End >= resolved.Axis.Length * 0.95
            };
        }

        snapshot.LongitudinalAxisKind = resolved.Kind;
        snapshot.GuidePolyline = resolved.GuidePolyline;
        snapshot.AxisSegments = resolved.Segments;
        snapshot.LongitudinalAxisConfidence = resolved.Confidence;
        snapshot.LongitudinalAxisSource = resolved.Source;
    }

    private MemberIdentity BuildMemberIdentity(Assembly assembly, Part mainPart, AxisDefinition mainAxis, string memberId)
    {
        return new MemberIdentity
        {
            MemberId = memberId,
            AssemblyId = assembly.Identifier.ID.ToString(),
            Guid = mainPart.Identifier.GUID.ToString(),
            Name = mainPart.Name ?? string.Empty,
            Position = ReadPosition(mainPart),
            Material = ReadMaterial(mainPart),
            ProfileString = ReadProfile(mainPart),
            Class = mainPart.Class ?? string.Empty,
            Phase = ReadPhase(mainPart),
            MainAxis = mainAxis,
            BoundingBox = BuildBoundingBox(mainPart)
        };
    }

    private Part SelectReferencePart(IReadOnlyList<Part> parts, Part fallback)
    {
        if (parts.Count == 0)
        {
            return fallback;
        }

        var best = parts
            .Select(part =>
            {
                var axis = BuildAxisDefinition(part);
                var box = BuildBoundingBox(part);
                var dx = Math.Abs(box.Max.X - box.Min.X);
                var dy = Math.Abs(box.Max.Y - box.Min.Y);
                var dz = Math.Abs(box.Max.Z - box.Min.Z);
                var secondary = new[] { dx, dy, dz }
                    .OrderByDescending(value => value)
                    .Skip(1)
                    .FirstOrDefault();
                return new
                {
                    Part = part,
                    AxisLength = axis.Length,
                    SecondarySpan = secondary,
                    Thickness = ReadThickness(part)
                };
            })
            .OrderByDescending(item => item.AxisLength)
            .ThenByDescending(item => item.SecondarySpan)
            .ThenByDescending(item => item.Thickness)
            .FirstOrDefault();

        return best?.Part ?? fallback;
    }

    private PartSnapshot BuildPartSnapshot(Part part, AxisDefinition axis)
    {
        var profileString = ReadProfile(part);
        var thickness = ReadThickness(part);
        var thicknessInfo = ResolveThicknessInfo(part, profileString);
        var solid = SafeGetSolid(part);
        var bbox = solid != null ? BuildBoundingBox(solid) : new BoundingBox3D();
        var centroid = Midpoint(bbox.Min, bbox.Max);
        var axisProjection = BuildAxisProjection(bbox, axis);
        var coordinateSystem = SafeGetCoordinateSystem(part);
        var solidEdges = solid != null ? ExtractSolidEdges(solid) : new List<LineSegment3D>();
        var contourPoints = ExtractContourPoints(part);
        var inputLayerDraft = BuildInputLayerDraftMetadata(part, profileString, contourPoints, thicknessInfo);

        return new PartSnapshot
        {
            PartId = part.Identifier.ID.ToString(),
            Guid = part.Identifier.GUID.ToString(),
            Name = part.Name ?? string.Empty,
            PartType = part.GetType().Name,
            ProfileString = profileString,
            Material = ReadMaterial(part),
            Thickness = thickness,
            Area = 0.0,
            Volume = 0.0,
            Centroid = centroid,
            LocalCoordinateSystem = coordinateSystem,
            BoundingBox = bbox,
            SolidEdges = solidEdges,
            AxisProjection = axisProjection,
            EndProximity = new EndProximity
            {
                NearStart = axisProjection.Start <= axis.Length * 0.05,
                NearEnd = axisProjection.End >= axis.Length * 0.95
            },
            GeometryHints = new GeometryHints
            {
                Planarity = InferPlanarity(part),
                IsBentLike = part is BentPlate,
                IsFoldedLike = part is ContourPlate && InferFoldedLike(part),
                OuterSideCandidate = true
            },
            TopologyHints = new TopologyHints(),
            InputLayerDraft = inputLayerDraft
        };
    }

    private static object? SafeGetSolid(Part part)
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

    private static List<LineSegment3D> ExtractSolidEdges(object solid)
    {
        var edges = new List<LineSegment3D>();
        try
        {
            var enumerator = solid.GetType().GetMethod("GetEdgeEnumerator", Type.EmptyTypes)?.Invoke(solid, null) as System.Collections.IEnumerator;
            if (enumerator == null)
            {
                return edges;
            }

            while (enumerator.MoveNext())
            {
                var edge = enumerator.Current;
                if (edge == null)
                {
                    continue;
                }

                var edgeType = edge.GetType();
                var start = edgeType.GetProperty("StartPoint")?.GetValue(edge) as Point;
                var end = edgeType.GetProperty("EndPoint")?.GetValue(edge) as Point;
                if (start == null || end == null)
                {
                    continue;
                }

                if (Distance(start, end) <= 1e-6)
                {
                    continue;
                }

                edges.Add(new LineSegment3D
                {
                    Start = ToPoint3D(start),
                    End = ToPoint3D(end)
                });
            }
        }
        catch
        {
        }

        return edges;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
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

        return parts.GroupBy(part => part.Identifier.ID).Select(group => group.First());
    }

    private static string ReadMemberId(Assembly assembly, Part mainPart)
    {
        var assemblyPosition = TryGetStringReportProperty(assembly, "ASSEMBLY_POS")
            ?? TryGetStringReportProperty(assembly, "ASSEMBLY_POSITION")
            ?? TryGetStringReportProperty(mainPart, "ASSEMBLY_POS")
            ?? TryGetStringReportProperty(mainPart, "ASSEMBLY_POSITION");
        if (!string.IsNullOrWhiteSpace(assemblyPosition))
        {
            return assemblyPosition.Trim();
        }

        var partPosition = TryGetStringReportProperty(mainPart, "PART_POS");
        if (!string.IsNullOrWhiteSpace(partPosition))
        {
            return partPosition.Trim();
        }

        var prefix = mainPart.PartNumber?.Prefix ?? string.Empty;
        var number = mainPart.PartNumber?.StartNumber ?? 0;
        if (!string.IsNullOrWhiteSpace(prefix) || number > 0)
        {
            return $"{prefix}{number}";
        }

        return assembly.Identifier.ID.ToString();
    }

    private static string ReadPosition(Part part)
    {
        return $"{part.Position?.Plane}/{part.Position?.Depth}/{part.Position?.Rotation}".Trim('/');
    }

    private static string ReadMaterial(Part part)
    {
        return part.Material?.MaterialString ?? string.Empty;
    }

    private static string ReadProfile(Part part)
    {
        return part.Profile?.ProfileString ?? string.Empty;
    }

    private static string ReadPhase(ModelObject modelObject)
    {
        return modelObject.GetPhase(out var phase) ? $"{phase.PhaseNumber}" : string.Empty;
    }

    private static double ReadThickness(Part part)
    {
        if (part is ContourPlate)
        {
            var contourThickness = TryGetDoubleReportProperty(part, "WIDTH")
                ?? TryGetDoubleReportProperty(part, "PROFILE.WIDTH")
                ?? 0.0;
            if (contourThickness > 0)
            {
                return contourThickness;
            }
        }

        var profile = ReadProfile(part);
        return TryParseThicknessFromProfile(profile, out var profileThickness)
            ? profileThickness
            : 0.0;
    }

    private static (double? Value, string Source, string Detail) ResolveThicknessInfo(Part part, string profileString)
    {
        if (TryGetDoubleReportProperty(part, "DIM_A") is double dimA && dimA > 0)
        {
            return (dimA, "REPORT_PROPERTY", "DIM_A");
        }

        if (TryGetDoubleReportProperty(part, "PROFILE.WIDTH") is double profileWidth && profileWidth > 0)
        {
            return (profileWidth, "REPORT_PROPERTY", "PROFILE.WIDTH");
        }

        if (TryGetDoubleReportProperty(part, "PLATE_THICKNESS") is double plateThickness && plateThickness > 0)
        {
            return (plateThickness, "REPORT_PROPERTY", "PLATE_THICKNESS");
        }

        if (part is ContourPlate && TryGetDoubleReportProperty(part, "WIDTH") is double width && width > 0)
        {
            return (width, "REPORT_PROPERTY", "WIDTH");
        }

        if (TryParseThicknessFromProfile(profileString, out var profileThickness) && profileThickness > 0)
        {
            return (profileThickness, "PROFILE_PARSE", profileString);
        }

        var solid = SafeGetSolid(part);
        if (solid != null)
        {
            var bbox = BuildBoundingBox(solid);
            var dimensions = new[]
            {
                Math.Abs(bbox.Max.X - bbox.Min.X),
                Math.Abs(bbox.Max.Y - bbox.Min.Y),
                Math.Abs(bbox.Max.Z - bbox.Min.Z)
            }
            .Where(value => value > 1e-6)
            .OrderBy(value => value)
            .ToArray();

            if (dimensions.Length > 0)
            {
                return (dimensions[0], "LOCAL_BBOX_MIN_DIMENSION", "BOUNDING_BOX_AXIS_MIN");
            }
        }

        return (null, "UNKNOWN", string.Empty);
    }

    private static bool IsPlateLike(Part part, string profileString)
    {
        if (part is ContourPlate || part is BentPlate)
        {
            return true;
        }

        return profileString.StartsWith("PL", StringComparison.OrdinalIgnoreCase);
    }

    private static List<Point3D> ExtractContourPoints(Part part)
    {
        var contourPlate = part as ContourPlate;
        if (contourPlate?.Contour == null)
        {
            return new List<Point3D>();
        }

        var points = new List<Point3D>();
        foreach (var item in contourPlate.Contour.ContourPoints.Cast<object>())
        {
            if (item is ContourPoint contourPoint)
            {
                points.Add(ToPoint3D(contourPoint));
            }
        }

        return points;
    }

    private static PartInputLayerDraftMetadata BuildInputLayerDraftMetadata(
        Part part,
        string profileString,
        IReadOnlyList<Point3D> contourPoints,
        (double? Value, string Source, string Detail) thicknessInfo)
    {
        var isContourPlateLike = part is ContourPlate || part is BentPlate;
        var isBeamLike = part is Beam || part is PolyBeam;
        var hasProfileSection = !string.IsNullOrWhiteSpace(profileString);
        var hasThickness = thicknessInfo.Value is > 0;
        var hasExplicitBoundary = isContourPlateLike && contourPoints.Count >= 3;
        var canUseExactPathSection = isBeamLike && hasProfileSection;
        var metadata = new PartInputLayerDraftMetadata
        {
            PartModelType = canUseExactPathSection ? "PATH_SECTION_SWEEP" : "PLATE_BOUNDARY_THICKNESS",
            RepresentationKind = canUseExactPathSection
                ? "PATH_SECTION_SWEEP"
                : hasExplicitBoundary && hasThickness
                    ? "PLATE_BOUNDARY_THICKNESS"
                    : "APPROX_BOUNDARY_THICKNESS",
            RepresentationLevel = canUseExactPathSection || (hasExplicitBoundary && hasThickness) ? "EXACT" : "APPROXIMATE",
            DistortionRiskCode = canUseExactPathSection || (hasExplicitBoundary && hasThickness) ? "LOW" : "HIGH",
            Thickness = thicknessInfo.Value,
            ThicknessSource = thicknessInfo.Source,
            ThicknessSourceDetail = thicknessInfo.Detail,
            BoundaryLoopKind = hasExplicitBoundary ? "OUTER_CONTOUR" : "UNRESOLVED",
            BoundarySource = hasExplicitBoundary ? "CONTOUR_PLATE_POINTS" : "NONE",
            BoundaryClosed = hasExplicitBoundary,
            LocalFrameSource = "PART_COORDINATE_SYSTEM",
            SectionSource = canUseExactPathSection
                ? profileString.StartsWith("PL", StringComparison.OrdinalIgnoreCase)
                    ? "PROFILE_STRING_PLATE_SECTION"
                    : "PROFILE_STRING"
                : string.Empty,
            SectionProfile = canUseExactPathSection ? profileString : string.Empty,
            HasExplicitBoundary = hasExplicitBoundary,
            HasExplicitThickness = hasThickness,
            HasExplicitSection = canUseExactPathSection
        };

        foreach (var point in contourPoints)
        {
            metadata.BoundaryPoints.Add(point);
        }

        if (!canUseExactPathSection && isContourPlateLike && contourPoints.Count < 3)
        {
            metadata.WarningCodes.Add("BOUNDARY_UNRESOLVED");
            metadata.DistortionRiskReasons.Add("BOUNDARY_UNRESOLVED");
            metadata.DegradationReasonCodes.Add("BOUNDARY_MISSING");
        }

        if (isBeamLike && !hasProfileSection)
        {
            metadata.PartModelType = "PATH_SECTION_SWEEP";
            metadata.RepresentationKind = "APPROX_PATH_SECTION_SWEEP";
            metadata.RepresentationLevel = "APPROXIMATE";
            metadata.DistortionRiskCode = "HIGH";
            metadata.WarningCodes.Add("SECTION_PROFILE_UNRESOLVED");
            metadata.DistortionRiskReasons.Add("SECTION_PROFILE_UNRESOLVED");
            metadata.DegradationReasonCodes.Add("SECTION_PROFILE_MISSING");
        }

        if (thicknessInfo.Source == "PROFILE_PARSE")
        {
            metadata.WarningCodes.Add("THICKNESS_INFERRED_FROM_PROFILE");
        }
        else if (thicknessInfo.Source == "LOCAL_BBOX_MIN_DIMENSION")
        {
            metadata.WarningCodes.Add("THICKNESS_INFERRED_FROM_BBOX");
        }
        else if (thicknessInfo.Source == "UNKNOWN")
        {
            metadata.WarningCodes.Add("THICKNESS_UNRESOLVED");
        }

        return metadata;
    }

    private static bool TryParseThicknessFromProfile(string profile, out double thickness)
    {
        thickness = 0.0;
        if (string.IsNullOrWhiteSpace(profile))
        {
            return false;
        }

        var normalized = profile.Replace(" ", string.Empty).ToUpperInvariant();
        var numbers = ExtractNumbers(normalized);
        if (numbers.Count == 0)
        {
            return false;
        }

        if (normalized.StartsWith("PL", StringComparison.OrdinalIgnoreCase))
        {
            thickness = numbers[0];
            return thickness > 0;
        }

        if (normalized.StartsWith("RHS", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("SHS", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("BOX", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("PIPE", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("CHS", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("ROD", StringComparison.OrdinalIgnoreCase))
        {
            thickness = numbers[numbers.Count - 1];
            return thickness > 0;
        }

        return false;
    }

    private static List<double> ExtractNumbers(string text)
    {
        var numbers = new List<double>();
        var buffer = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if ((ch >= '0' && ch <= '9') || ch == '.')
            {
                buffer.Append(ch);
                continue;
            }

            FlushNumber(buffer, numbers);
        }

        FlushNumber(buffer, numbers);
        return numbers;
    }

    private static void FlushNumber(System.Text.StringBuilder buffer, List<double> numbers)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        if (double.TryParse(buffer.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            numbers.Add(value);
        }

        buffer.Clear();
    }

    private static double? TryGetDoubleReportProperty(Part part, string propertyName)
    {
        double value = 0.0;
        return part.GetReportProperty(propertyName, ref value) ? value : null;
    }

    private static string? TryGetStringReportProperty(ModelObject modelObject, string propertyName)
    {
        var value = string.Empty;
        if (!modelObject.GetReportProperty(propertyName, ref value))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static AxisDefinition BuildAxisDefinition(Part part)
    {
        var solid = part.GetSolid();
        var min = ToPoint3D(solid.MinimumPoint);
        var max = ToPoint3D(solid.MaximumPoint);
        var corners = GetBoundingBoxCorners(min, max);

        try
        {
            var cs = part.GetCoordinateSystem();
            var candidates = new[]
            {
                NormalizeVector(ToVector3D(cs.AxisX)),
                NormalizeVector(ToVector3D(cs.AxisY)),
                NormalizeVector(Cross(ToVector3D(cs.AxisX), ToVector3D(cs.AxisY)))
            }
            .Where(vector => VectorLength(vector) > 1e-6)
            .ToList();

            var best = candidates
                .Select(vector => BuildAxisFromDirection(corners, vector))
                .OrderByDescending(axis => axis.Length)
                .FirstOrDefault();

            if (best != null && best.Length > 1e-6)
            {
                return best;
            }
        }
        catch
        {
        }

        var dx = max.X - min.X;
        var dy = max.Y - min.Y;
        var dz = max.Z - min.Z;

        if (dx >= dy && dx >= dz)
        {
            return new AxisDefinition
            {
                Origin = new Point3D(min.X, 0, 0),
                Direction = new Vector3D(1, 0, 0),
                Length = dx
            };
        }

        if (dy >= dx && dy >= dz)
        {
            return new AxisDefinition
            {
                Origin = new Point3D(0, min.Y, 0),
                Direction = new Vector3D(0, 1, 0),
                Length = dy
            };
        }

        return new AxisDefinition
        {
            Origin = new Point3D(0, 0, min.Z),
            Direction = new Vector3D(0, 0, 1),
            Length = dz
        };
    }


    private static AxisDefinition BuildAxisFromDirection(IReadOnlyList<Point3D> corners, Vector3D direction)
    {
        var normalized = NormalizeVector(direction);
        var projections = corners.Select(point => DotPoint(point, normalized)).ToArray();
        var minProjection = projections.Min();
        var maxProjection = projections.Max();
        return new AxisDefinition
        {
            Origin = new Point3D(normalized.X * minProjection, normalized.Y * minProjection, normalized.Z * minProjection),
            Direction = normalized,
            Length = Math.Max(0.0, maxProjection - minProjection)
        };
    }

    private static List<Point3D> GetBoundingBoxCorners(Point3D min, Point3D max)
    {
        return new List<Point3D>
        {
            new Point3D(min.X, min.Y, min.Z),
            new Point3D(min.X, min.Y, max.Z),
            new Point3D(min.X, max.Y, min.Z),
            new Point3D(min.X, max.Y, max.Z),
            new Point3D(max.X, min.Y, min.Z),
            new Point3D(max.X, min.Y, max.Z),
            new Point3D(max.X, max.Y, min.Z),
            new Point3D(max.X, max.Y, max.Z)
        };
    }

    private static double DotPoint(Point3D point, Vector3D vector)
    {
        return point.X * vector.X + point.Y * vector.Y + point.Z * vector.Z;
    }

    private static double VectorLength(Vector3D vector)
    {
        return Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
    }

    private static Vector3D NormalizeVector(Vector3D vector)
    {
        var length = VectorLength(vector);
        if (length <= 1e-9)
        {
            return new Vector3D(1, 0, 0);
        }

        return new Vector3D(vector.X / length, vector.Y / length, vector.Z / length);
    }
    private static BoundingBox3D BuildBoundingBox(Part part)
    {
        var solid = SafeGetSolid(part);
        return solid != null ? BuildBoundingBox(solid) : new BoundingBox3D();
    }

    private static BoundingBox3D BuildBoundingBox(object solid)
    {
        var solidType = solid.GetType();
        var min = solidType.GetProperty("MinimumPoint")?.GetValue(solid) as Point;
        var max = solidType.GetProperty("MaximumPoint")?.GetValue(solid) as Point;
        if (min == null || max == null)
        {
            return new BoundingBox3D();
        }

        return new BoundingBox3D
        {
            Min = ToPoint3D(min),
            Max = ToPoint3D(max)
        };
    }

    private static CoordinateSystem3D SafeGetCoordinateSystem(Part part)
    {
        try
        {
            var cs = part.GetCoordinateSystem();
            return new CoordinateSystem3D
            {
                Origin = ToPoint3D(cs.Origin),
                X = ToVector3D(cs.AxisX),
                Y = ToVector3D(cs.AxisY),
                Z = Cross(ToVector3D(cs.AxisX), ToVector3D(cs.AxisY))
            };
        }
        catch
        {
            return new CoordinateSystem3D();
        }
    }

    private static AxisProjection BuildAxisProjection(BoundingBox3D box, AxisDefinition axis)
    {
        var values = GetBoundingBoxCorners(box.Min, box.Max)
            .Select(point => ProjectRelative(point, axis))
            .ToArray();

        var start = values.Min();
        var end = values.Max();
        var length = Math.Max(0.0, end - start);
        var coverage = axis.Length <= 0.0 ? 0.0 : length / axis.Length;

        return new AxisProjection
        {
            Start = start,
            End = end,
            Length = length,
            CoverageRatio = coverage
        };
    }

    private static double InferPlanarity(Part part)
    {
        return part is ContourPlate || part is BentPlate ? 1.0 : 0.5;
    }

    private static bool InferFoldedLike(Part part)
    {
        var name = part.Name ?? string.Empty;
        return name.IndexOf("fold", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("折", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static double ProjectRelative(Point3D point, AxisDefinition axis)
    {
        var dx = point.X - axis.Origin.X;
        var dy = point.Y - axis.Origin.Y;
        var dz = point.Z - axis.Origin.Z;
        return dx * axis.Direction.X + dy * axis.Direction.Y + dz * axis.Direction.Z;
    }

    private static Point3D Midpoint(Point3D a, Point3D b)
    {
        return new Point3D((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, (a.Z + b.Z) / 2.0);
    }

    private static Point3D ToPoint3D(Point point)
    {
        return new Point3D(point.X, point.Y, point.Z);
    }

    private static Vector3D ToVector3D(Vector vector)
    {
        return new Vector3D(vector.X, vector.Y, vector.Z);
    }

    private static Vector3D Cross(Vector3D a, Vector3D b)
    {
        return new Vector3D(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);
    }

    private static (string Kind, List<Point3D> GuidePolyline, List<LongitudinalAxisSegment> AxisSegments, double Confidence, string Source)
        BuildLongitudinalAxis(IReadOnlyList<PartSnapshot> parts, AxisDefinition fallbackAxis)
    {
        var referenceAxisLength = Math.Max(
            Math.Max(fallbackAxis.Length, parts.Count > 0 ? parts.Max(part => part.AxisProjection.Length) : 0.0),
            1.0);
        var candidates = parts
            .Where(HasUsableGuideSegments)
            .Select(
                part => new
                {
                    Part = part,
                    Polyline = BuildGuidePolyline(part)
                })
            .Select(
                item => new
                {
                    item.Part,
                    item.Polyline,
                    Length = EstimatePolylineLength(item.Polyline),
                    ProgressRatio = EstimateProgressRatio(item.Polyline, fallbackAxis.Direction),
                    HasSufficientSpan = HasSufficientAxisSpan(item.Part, referenceAxisLength)
                })
            .Where(item => item.Polyline.Count >= 2 && item.Length > 1e-3)
            .OrderByDescending(item => item.HasSufficientSpan)
            .ThenByDescending(item => item.ProgressRatio)
            .ThenByDescending(item => item.Length)
            .ThenByDescending(item => item.Part.AxisProjection.Length)
            .ToArray();

        if (candidates.Length == 0)
        {
            var straightSegments = BuildSingleAxisSegments(parts, fallbackAxis);
            var straightPolyline = straightSegments
                .Select(segment => segment.Start)
                .Concat(straightSegments.Count > 0 ? new[] { straightSegments[straightSegments.Count - 1].End } : Array.Empty<Point3D>())
                .ToList();
            return ("STRAIGHT", straightPolyline, straightSegments, 1.0, "MAIN_AXIS");
        }

        var preferredCandidate = candidates.FirstOrDefault(item => item.HasSufficientSpan) ?? candidates[0];
        var polyline = preferredCandidate.Polyline;
        var segments = BuildLongitudinalSegments(polyline);
        if (segments.Count == 0)
        {
            var straightSegments = BuildSingleAxisSegments(parts, fallbackAxis);
            var straightPolyline = straightSegments
                .Select(segment => segment.Start)
                .Concat(straightSegments.Count > 0 ? new[] { straightSegments[straightSegments.Count - 1].End } : Array.Empty<Point3D>())
                .ToList();
            return ("STRAIGHT", straightPolyline, straightSegments, 0.75, "DERIVED_FALLBACK");
        }

        if (!IsAxisConsistent(segments, fallbackAxis.Length, referenceAxisLength))
        {
            var straightSegments = BuildSingleAxisSegments(parts, fallbackAxis);
            var straightPolyline = straightSegments
                .Select(segment => segment.Start)
                .Concat(straightSegments.Count > 0 ? new[] { straightSegments[straightSegments.Count - 1].End } : Array.Empty<Point3D>())
                .ToList();
            return ("STRAIGHT", straightPolyline, straightSegments, 0.70, "MAIN_AXIS_CONSISTENCY_FALLBACK");
        }

        var kind = segments.Count >= 2 ? "POLYLINE" : "STRAIGHT";
        var confidence = kind == "POLYLINE" ? 0.88 : 0.82;
        return (kind, polyline, segments, confidence, "SOLID_EDGE_GUIDE");
    }

    private static bool HasUsableGuideSegments(PartSnapshot part) =>
        part.SolidEdges.Count > 0 &&
        (string.Equals(part.PartType, nameof(PolyBeam), StringComparison.OrdinalIgnoreCase) ||
         string.Equals(part.PartType, nameof(Beam), StringComparison.OrdinalIgnoreCase));

    private static bool HasSufficientAxisSpan(PartSnapshot part, double referenceAxisLength)
    {
        if (referenceAxisLength <= 1e-6)
        {
            return true;
        }

        return part.AxisProjection.CoverageRatio >= 0.60 ||
               part.AxisProjection.Length >= referenceAxisLength * 0.60;
    }

    private static bool IsAxisConsistent(
        IReadOnlyList<LongitudinalAxisSegment> segments,
        double mainAxisLength,
        double longestPartAxisLength)
    {
        if (segments.Count == 0)
        {
            return false;
        }

        var axisLength = segments[segments.Count - 1].CumulativeEnd;
        var referenceMainAxis = Math.Max(mainAxisLength, 1.0);
        var referencePartAxis = Math.Max(longestPartAxisLength, 1.0);
        var respectsMainAxis = axisLength >= referenceMainAxis * 0.50;
        var respectsLongestPart = axisLength >= referencePartAxis * 0.50;
        return respectsMainAxis || respectsLongestPart;
    }

    private static List<Point3D> BuildGuidePolyline(PartSnapshot part)
    {
        var candidates = BuildGuideSegments(part);
        if (candidates.Length == 0)
        {
            return new List<Point3D>();
        }

        var overallAxis = NormalizeVector(part.LocalCoordinateSystem.X);
        if (VectorLength(overallAxis) <= 1e-6)
        {
            var endpoints = candidates
                .SelectMany(candidate => new[] { candidate.Start, candidate.End })
                .ToArray();
            var pair = FindFarthestPair(endpoints);
            overallAxis = NormalizeVector(new Vector3D(pair.End.X - pair.Start.X, pair.End.Y - pair.Start.Y, pair.End.Z - pair.Start.Z));
        }

        if (VectorLength(overallAxis) <= 1e-6)
        {
            overallAxis = new Vector3D(1, 0, 0);
        }

        return ReconstructGuidePolyline(candidates, overallAxis);
    }

    private static (Point3D Start, Point3D End, double Length, double Alignment)[] BuildGuideSegments(PartSnapshot part)
    {
        return part.SolidEdges
            .Select(segment => NormalizeGuideSegment(part, segment))
            .Where(item => item.Length > 50.0)
            .Where(item => item.Alignment >= 0.45)
            .GroupBy(item => BuildSegmentKey(item.Start, item.End), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Length).First())
            .ToArray();
    }

    private static List<Point3D> ReconstructGuidePolyline(
        IReadOnlyList<(Point3D Start, Point3D End, double Length, double Alignment)> candidates,
        Vector3D overallAxis)
    {
        const double nodeMergeTolerance = 5.0;
        var graph = new GuideGraph();

        foreach (var candidate in candidates)
        {
            var startIndex = graph.GetOrAddNode(candidate.Start, nodeMergeTolerance);
            var endIndex = graph.GetOrAddNode(candidate.End, nodeMergeTolerance);
            if (startIndex == endIndex)
            {
                continue;
            }

            graph.AddEdge(startIndex, endIndex, candidate.Length);
        }

        if (graph.Nodes.Count < 2 || graph.Edges.Count == 0)
        {
            return graph.Nodes.Select(node => node.Position).ToList();
        }

        var projections = Enumerable.Range(0, graph.Nodes.Count)
            .ToDictionary(index => index, index => DotPoint(graph.Nodes[index].Position, overallAxis));
        var componentMetrics = BuildConnectedComponents(graph)
            .Select(
                component => new
                {
                    NodeIndexes = component,
                    ProjectionSpan = component.Max(index => projections[index]) - component.Min(index => projections[index]),
                    EdgeLength = graph.Edges
                        .Where(edge => component.Contains(edge.StartNodeIndex) && component.Contains(edge.EndNodeIndex))
                        .Sum(edge => edge.Length)
                })
            .OrderByDescending(item => item.ProjectionSpan)
            .ThenByDescending(item => item.EdgeLength)
            .ToArray();

        if (componentMetrics.Length == 0)
        {
            return graph.Nodes.Select(node => node.Position).ToList();
        }

        var bestSpan = componentMetrics[0].ProjectionSpan;
        var selectedNodeIndexes = componentMetrics
            .Where(item => item.ProjectionSpan >= bestSpan * 0.75)
            .SelectMany(item => item.NodeIndexes)
            .Distinct()
            .ToArray();
        var polyline = BuildCenterlinePolyline(graph, selectedNodeIndexes, projections);
        return polyline.Count >= 2
            ? polyline
            : selectedNodeIndexes
                .OrderBy(index => projections[index])
                .Select(index => graph.Nodes[index].Position)
                .ToList();
    }

    private static List<HashSet<int>> BuildConnectedComponents(GuideGraph graph)
    {
        var components = new List<HashSet<int>>();
        var visited = new bool[graph.Nodes.Count];
        for (var startIndex = 0; startIndex < graph.Nodes.Count; startIndex++)
        {
            if (visited[startIndex])
            {
                continue;
            }

            var component = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(startIndex);
            visited[startIndex] = true;

            while (queue.Count > 0)
            {
                var nodeIndex = queue.Dequeue();
                component.Add(nodeIndex);
                foreach (var edgeIndex in graph.Nodes[nodeIndex].EdgeIndices)
                {
                    var edge = graph.Edges[edgeIndex];
                    var nextNodeIndex = edge.StartNodeIndex == nodeIndex ? edge.EndNodeIndex : edge.StartNodeIndex;
                    if (visited[nextNodeIndex])
                    {
                        continue;
                    }

                    visited[nextNodeIndex] = true;
                    queue.Enqueue(nextNodeIndex);
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static List<Point3D> BuildCenterlinePolyline(
        GuideGraph graph,
        IReadOnlyCollection<int> componentNodeIndexes,
        IReadOnlyDictionary<int, double> projections)
    {
        const double projectionClusterTolerance = 10.0;
        const double pointMergeTolerance = 5.0;
        var orderedNodes = componentNodeIndexes
            .Select(index => new { Index = index, Projection = projections[index], Point = graph.Nodes[index].Position })
            .OrderBy(item => item.Projection)
            .ToArray();
        if (orderedNodes.Length == 0)
        {
            return new List<Point3D>();
        }

        var clusters = new List<List<Point3D>>();
        var currentCluster = new List<Point3D> { orderedNodes[0].Point };
        var currentProjection = orderedNodes[0].Projection;

        for (var index = 1; index < orderedNodes.Length; index++)
        {
            var item = orderedNodes[index];
            if (Math.Abs(item.Projection - currentProjection) <= projectionClusterTolerance)
            {
                currentCluster.Add(item.Point);
                currentProjection = (currentProjection * (currentCluster.Count - 1) + item.Projection) / currentCluster.Count;
                continue;
            }

            clusters.Add(currentCluster);
            currentCluster = new List<Point3D> { item.Point };
            currentProjection = item.Projection;
        }

        clusters.Add(currentCluster);

        var polyline = new List<Point3D>();
        foreach (var cluster in clusters)
        {
            var averagedPoint = new Point3D(
                cluster.Average(point => point.X),
                cluster.Average(point => point.Y),
                cluster.Average(point => point.Z));
            if (polyline.Count > 0 &&
                Distance(polyline[polyline.Count - 1].X, polyline[polyline.Count - 1].Y, polyline[polyline.Count - 1].Z, averagedPoint.X, averagedPoint.Y, averagedPoint.Z) <= pointMergeTolerance)
            {
                continue;
            }

            polyline.Add(averagedPoint);
        }

        return polyline;
    }

    private static List<LongitudinalAxisSegment> BuildLongitudinalSegments(IReadOnlyList<Point3D> polyline)
    {
        var segments = new List<LongitudinalAxisSegment>();
        var axisPosition = 0.0;
        for (var index = 0; index < polyline.Count - 1; index++)
        {
            var start = polyline[index];
            var end = polyline[index + 1];
            var vector = new Vector3D(end.X - start.X, end.Y - start.Y, end.Z - start.Z);
            var length = VectorLength(vector);
            if (length <= 1e-3)
            {
                continue;
            }

            var direction = NormalizeVector(vector);
            segments.Add(new LongitudinalAxisSegment
            {
                Index = index,
                Start = start,
                End = end,
                Direction = direction,
                Length = length,
                CumulativeStart = axisPosition,
                CumulativeEnd = axisPosition + length
            });

            axisPosition += length;
        }

        return segments;
    }

    private static List<LongitudinalAxisSegment> BuildSingleAxisSegments(IReadOnlyList<PartSnapshot> parts, AxisDefinition axis)
    {
        var normalizedAxis = NormalizeVector(axis.Direction);
        if (VectorLength(normalizedAxis) <= 1e-9)
        {
            normalizedAxis = new Vector3D(1, 0, 0);
        }

        var points = parts
            .SelectMany(CollectRepresentativePoints)
            .ToArray();
        if (points.Length == 0)
        {
            points = new[] { new Point3D() };
        }

        var projections = points.Select(point => DotPoint(point, normalizedAxis)).ToArray();
        var min = projections.Min();
        var max = projections.Max();
        if ((max - min) <= 1e-6)
        {
            max = min + Math.Max(axis.Length, 1.0);
        }

        var start = new Point3D(normalizedAxis.X * min, normalizedAxis.Y * min, normalizedAxis.Z * min);
        var end = new Point3D(normalizedAxis.X * max, normalizedAxis.Y * max, normalizedAxis.Z * max);
        return new List<LongitudinalAxisSegment>
        {
            new LongitudinalAxisSegment
            {
                Index = 0,
                Start = start,
                End = end,
                Direction = normalizedAxis,
                Length = max - min,
                CumulativeStart = 0.0,
                CumulativeEnd = max - min
            }
        };
    }

    private static (Point3D Start, Point3D End, double Length, double Alignment) NormalizeGuideSegment(PartSnapshot part, LineSegment3D segment)
    {
        var normal = NormalizeVector(part.LocalCoordinateSystem.Z);
        var start = ProjectToMidPlane(segment.Start, part.Centroid, normal);
        var end = ProjectToMidPlane(segment.End, part.Centroid, normal);
        var vector = new Vector3D(end.X - start.X, end.Y - start.Y, end.Z - start.Z);
        var length = VectorLength(vector);
        if (length <= 1e-6)
        {
            return (start, end, 0.0, 0.0);
        }

        var direction = NormalizeVector(vector);
        var guide = NormalizeVector(part.LocalCoordinateSystem.X);
        var alignment = Math.Abs(direction.X * guide.X + direction.Y * guide.Y + direction.Z * guide.Z);
        if (DotPoint(start, guide) > DotPoint(end, guide))
        {
            (start, end) = (end, start);
        }

        return (start, end, length, alignment);
    }

    private static Point3D ProjectToMidPlane(Point3D point, Point3D origin, Vector3D normal)
    {
        var unitNormal = NormalizeVector(normal);
        if (VectorLength(unitNormal) <= 1e-9)
        {
            return point;
        }

        var dx = point.X - origin.X;
        var dy = point.Y - origin.Y;
        var dz = point.Z - origin.Z;
        var offset = dx * unitNormal.X + dy * unitNormal.Y + dz * unitNormal.Z;
        return new Point3D(
            point.X - (unitNormal.X * offset),
            point.Y - (unitNormal.Y * offset),
            point.Z - (unitNormal.Z * offset));
    }

    private static IEnumerable<Point3D> CollectRepresentativePoints(PartSnapshot part)
    {
        yield return part.Centroid;
        foreach (var corner in GetBoundingBoxCorners(part.BoundingBox.Min, part.BoundingBox.Max))
        {
            yield return corner;
        }

        foreach (var edge in part.SolidEdges)
        {
            yield return edge.Start;
            yield return edge.End;
        }
    }

    private static string BuildPointKey(Point3D point) =>
        $"{Math.Round(point.X, 3):0.###}|{Math.Round(point.Y, 3):0.###}|{Math.Round(point.Z, 3):0.###}";

    private static string BuildSegmentKey(Point3D start, Point3D end)
    {
        var startKey = BuildPointKey(start);
        var endKey = BuildPointKey(end);
        return string.CompareOrdinal(startKey, endKey) <= 0 ? $"{startKey}->{endKey}" : $"{endKey}->{startKey}";
    }

    private sealed class GuideGraph
    {
        public List<GuideNode> Nodes { get; } = new List<GuideNode>();
        public List<GuideEdge> Edges { get; } = new List<GuideEdge>();

        public int GetOrAddNode(Point3D point, double tolerance)
        {
            for (var index = 0; index < Nodes.Count; index++)
            {
                if (Distance(Nodes[index].Position.X, Nodes[index].Position.Y, Nodes[index].Position.Z, point.X, point.Y, point.Z) <= tolerance)
                {
                    return index;
                }
            }

            Nodes.Add(new GuideNode(point));
            return Nodes.Count - 1;
        }

        public void AddEdge(int startNodeIndex, int endNodeIndex, double length)
        {
            if (Edges.Any(edge =>
                    (edge.StartNodeIndex == startNodeIndex && edge.EndNodeIndex == endNodeIndex) ||
                    (edge.StartNodeIndex == endNodeIndex && edge.EndNodeIndex == startNodeIndex)))
            {
                return;
            }

            var edgeIndex = Edges.Count;
            Edges.Add(new GuideEdge(startNodeIndex, endNodeIndex, length));
            Nodes[startNodeIndex].EdgeIndices.Add(edgeIndex);
            Nodes[endNodeIndex].EdgeIndices.Add(edgeIndex);
        }
    }

    private sealed class GuideNode
    {
        public GuideNode(Point3D position)
        {
            Position = position;
        }

        public Point3D Position { get; }
        public List<int> EdgeIndices { get; } = new List<int>();
    }

    private sealed class GuideEdge
    {
        public GuideEdge(int startNodeIndex, int endNodeIndex, double length)
        {
            StartNodeIndex = startNodeIndex;
            EndNodeIndex = endNodeIndex;
            Length = length;
        }

        public int StartNodeIndex { get; }
        public int EndNodeIndex { get; }
        public double Length { get; }
    }

    private static double EstimatePolylineLength(IReadOnlyList<Point3D> polyline)
    {
        double total = 0.0;
        for (var index = 0; index < polyline.Count - 1; index++)
        {
            total += Distance(polyline[index].X, polyline[index].Y, polyline[index].Z, polyline[index + 1].X, polyline[index + 1].Y, polyline[index + 1].Z);
        }

        return total;
    }

    private static double EstimateProgressRatio(IReadOnlyList<Point3D> polyline, Vector3D fallbackAxis)
    {
        if (polyline.Count < 2)
        {
            return 0.0;
        }

        var axis = NormalizeVector(fallbackAxis);
        if (VectorLength(axis) <= 1e-6)
        {
            axis = NormalizeVector(new Vector3D(
                polyline[polyline.Count - 1].X - polyline[0].X,
                polyline[polyline.Count - 1].Y - polyline[0].Y,
                polyline[polyline.Count - 1].Z - polyline[0].Z));
        }

        if (VectorLength(axis) <= 1e-6)
        {
            axis = new Vector3D(1, 0, 0);
        }

        var vector = new Vector3D(
            polyline[polyline.Count - 1].X - polyline[0].X,
            polyline[polyline.Count - 1].Y - polyline[0].Y,
            polyline[polyline.Count - 1].Z - polyline[0].Z);
        var progress = Math.Abs(vector.X * axis.X + vector.Y * axis.Y + vector.Z * axis.Z);
        var length = EstimatePolylineLength(polyline);
        return length <= 1e-6 ? 0.0 : progress / length;
    }

    private static (Point3D Start, Point3D End) FindFarthestPair(IReadOnlyList<Point3D> points)
    {
        var bestStart = new Point3D();
        var bestEnd = new Point3D();
        var bestDistance = -1.0;

        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                var distance = Distance(points[i].X, points[i].Y, points[i].Z, points[j].X, points[j].Y, points[j].Z);
                if (distance <= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestStart = points[i];
                bestEnd = points[j];
            }
        }

        return (bestStart, bestEnd);
    }

    private static double Distance(double ax, double ay, double az, double bx, double by, double bz)
    {
        var dx = ax - bx;
        var dy = ay - by;
        var dz = az - bz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}




