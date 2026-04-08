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

        return snapshot;
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
        var solid = SafeGetSolid(part);
        var bbox = solid != null ? BuildBoundingBox(solid) : new BoundingBox3D();
        var centroid = Midpoint(bbox.Min, bbox.Max);
        var axisProjection = BuildAxisProjection(bbox, axis);
        var coordinateSystem = SafeGetCoordinateSystem(part);
        var solidEdges = solid != null ? ExtractSolidEdges(solid) : new List<LineSegment3D>();

        return new PartSnapshot
        {
            PartId = part.Identifier.ID.ToString(),
            Guid = part.Identifier.GUID.ToString(),
            Name = part.Name ?? string.Empty,
            PartType = part.GetType().Name,
            ProfileString = ReadProfile(part),
            Material = ReadMaterial(part),
            Thickness = ReadThickness(part),
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
            TopologyHints = new TopologyHints()
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
            return TryGetDoubleReportProperty(part, "WIDTH")
                ?? TryGetDoubleReportProperty(part, "PROFILE.WIDTH")
                ?? 0.0;
        }

        return 0.0;
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
}




