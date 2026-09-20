using System;
using System.Collections.Generic;
using System.Linq;

namespace TeklaSectionClassifier;

public sealed class ApproximateSectionIntersectionService : ISectionIntersectionService
{
    public SectionSample PopulateSample(MemberSnapshot member, SectionSample sample)
    {
        var stationFrame = LongitudinalAxisResolver.ResolveStationFrame(member, sample.Station);
        var axis = Normalize(stationFrame.Direction);
        var sectionX = BuildSectionXAxis(axis);
        var sectionY = Cross(axis, sectionX);

        var activeParts = member.Parts
            .Where(part =>
            {
                var projection = LongitudinalAxisResolver.EstimatePartAxisProjection(member, part);
                return projection.Start <= sample.Station && projection.End >= sample.Station;
            })
            .Select(part => BuildHit(member, part, sample, stationFrame.Origin, axis, sectionX, sectionY))
            .Where(hit => hit != null)
            .Cast<SectionPartHit>()
            .OrderBy(hit => hit.Center2D.Y)
            .ThenBy(hit => hit.Center2D.X)
            .ToList();

        sample.SectionParts = activeParts;
        sample.SectionFeatures = BuildFeatures(activeParts);
        return sample;
    }

    private static SectionPartHit? BuildHit(MemberSnapshot member, PartSnapshot part, SectionSample sample, Point3D planeOrigin, Vector3D axis, Vector3D sectionX, Vector3D sectionY)
    {
        var solidHit = TryBuildSolidSectionHit(member, part, planeOrigin, axis, sectionX, sectionY);
        return solidHit ?? BuildBoundingBoxSectionHit(member, part, planeOrigin, sectionX, sectionY);
    }

    private static SectionPartHit? TryBuildSolidSectionHit(MemberSnapshot member, PartSnapshot part, Point3D planeOrigin, Vector3D axis, Vector3D sectionX, Vector3D sectionY)
    {
        if (part.SolidEdges == null || part.SolidEdges.Count == 0)
        {
            return null;
        }

        var points = new List<Point3D>();
        foreach (var edge in part.SolidEdges)
        {
            if (TryIntersectSegmentWithPlane(edge, planeOrigin, axis, out var intersection))
            {
                AddDistinctPoint(points, intersection);
            }
        }

        if (points.Count < 2)
        {
            return null;
        }

        var projected = points
            .Select(point => ProjectToSection(planeOrigin, point, sectionX, sectionY))
            .ToList();
        var minX = projected.Min(point => point.X);
        var maxX = projected.Max(point => point.X);
        var minY = projected.Min(point => point.Y);
        var maxY = projected.Max(point => point.Y);
        var spanX = Math.Max(0.0, maxX - minX);
        var spanY = Math.Max(0.0, maxY - minY);
        var roleHint = InferRoleHint(part, spanX, spanY);
        var thickness = ResolveSectionThickness(part, spanX, spanY);

        if (spanX <= 1e-6 && spanY <= 1e-6)
        {
            return null;
        }

        return new SectionPartHit
        {
            PartId = part.PartId,
            IntersectionType = "solid_section",
            SegmentCount = Math.Max(1, points.Count / 2),
            TotalCutLength = Math.Max(spanX, spanY),
            CutSpanX = spanX,
            CutSpanY = spanY,
            Thickness = thickness,
            Center2D = new Point2D((minX + maxX) / 2.0, (minY + maxY) / 2.0),
            Direction2D = spanX >= spanY ? new Point2D(1, 0) : new Point2D(0, 1),
            RoleHint = NormalizeRoleHint(part, roleHint)
        };
    }

    private static SectionPartHit BuildBoundingBoxSectionHit(MemberSnapshot member, PartSnapshot part, Point3D planeOrigin, Vector3D sectionX, Vector3D sectionY)
    {
        var relative = new Vector3D(
            part.Centroid.X - planeOrigin.X,
            part.Centroid.Y - planeOrigin.Y,
            part.Centroid.Z - planeOrigin.Z);

        var centerX = Dot(relative, sectionX);
        var centerY = Dot(relative, sectionY);
        var spanX = EstimateSectionWidth(part, sectionX);
        var spanY = EstimateSectionWidth(part, sectionY);
        var roleHint = InferRoleHint(part, spanX, spanY);

        return new SectionPartHit
        {
            PartId = part.PartId,
            IntersectionType = "bbox_slice",
            SegmentCount = 1,
            TotalCutLength = Math.Max(spanX, spanY),
            CutSpanX = spanX,
            CutSpanY = spanY,
            Thickness = ResolveSectionThickness(part, spanX, spanY),
            Center2D = new Point2D(centerX, centerY),
            Direction2D = spanX >= spanY ? new Point2D(1, 0) : new Point2D(0, 1),
            RoleHint = NormalizeRoleHint(part, roleHint)
        };
    }

    private static SectionFeatures BuildFeatures(IReadOnlyList<SectionPartHit> hits)
    {
        if (hits.Count == 0)
        {
            return new SectionFeatures();
        }

        var minX = hits.Min(hit => hit.Center2D.X - (GetSpanX(hit) / 2.0));
        var maxX = hits.Max(hit => hit.Center2D.X + (GetSpanX(hit) / 2.0));
        var minY = hits.Min(hit => hit.Center2D.Y - (GetSpanY(hit) / 2.0));
        var maxY = hits.Max(hit => hit.Center2D.Y + (GetSpanY(hit) / 2.0));
        var flangeCandidates = hits.Count(hit => hit.RoleHint == "flange_candidate");
        var webCandidates = hits.Count(hit => hit.RoleHint == "web_candidate");
        var enclosure = ComputeEnclosureEvidence(hits, minX, maxX, minY, maxY);

        return new SectionFeatures
        {
            ClosedLoops = enclosure.HasClosedLoop ? 1 : 0,
            MajorPlateCount = hits.Count(hit => hit.RoleHint != "stiffener_candidate"),
            OuterWidth = Math.Max(0.0, maxX - minX),
            OuterHeight = Math.Max(0.0, maxY - minY),
            CavityCount = enclosure.HasClosedLoop ? 1 : 0,
            HorizontalSymmetryScore = ComputeSymmetryScore(hits, horizontal: true),
            VerticalSymmetryScore = ComputeSymmetryScore(hits, horizontal: false),
            CentralVerticalPlateCount = webCandidates,
            CentralHorizontalPlateCount = flangeCandidates,
            EnclosureScore = enclosure.Score
        };
    }

    private static double ComputeSymmetryScore(IReadOnlyList<SectionPartHit> hits, bool horizontal)
    {
        if (hits.Count == 0)
        {
            return 0.0;
        }

        var tolerance = hits.Max(hit => Math.Max(hit.TotalCutLength, Math.Max(GetSpanX(hit), GetSpanY(hit)))) * 0.25;
        var matched = 0;
        foreach (var hit in hits)
        {
            var target = horizontal ? -hit.Center2D.Y : -hit.Center2D.X;
            var exists = hits.Any(other =>
                other.PartId != hit.PartId &&
                Math.Abs((horizontal ? other.Center2D.Y : other.Center2D.X) - target) <= tolerance);
            if (exists)
            {
                matched++;
            }
        }

        return (double)matched / hits.Count;
    }

    private static EnclosureEvidence ComputeEnclosureEvidence(
        IReadOnlyList<SectionPartHit> hits,
        double minX,
        double maxX,
        double minY,
        double maxY)
    {
        var majorHits = hits
            .Where(hit => hit.RoleHint != "stiffener_candidate" && hit.RoleHint != "direct_profile_body")
            .Select(ToRect)
            .ToArray();
        if (majorHits.Length == 0)
        {
            return EnclosureEvidence.Empty;
        }

        var outerWidth = Math.Max(0.0, maxX - minX);
        var outerHeight = Math.Max(0.0, maxY - minY);
        var tolerance = Math.Max(
            10.0,
            majorHits.Max(hit => Math.Max(ResolveThickness(hit), Math.Min(hit.Width, hit.Height))) * 1.1);
        var minHorizontalSpan = outerWidth * 0.45;
        var minVerticalSpan = outerHeight * 0.45;
        var top = majorHits.Where(hit => hit.CanActAsHorizontalBoundary && Math.Abs(hit.MaxY - maxY) <= tolerance && hit.Width >= minHorizontalSpan).ToArray();
        var bottom = majorHits.Where(hit => hit.CanActAsHorizontalBoundary && Math.Abs(hit.MinY - minY) <= tolerance && hit.Width >= minHorizontalSpan).ToArray();
        var left = majorHits.Where(hit => hit.CanActAsVerticalBoundary && Math.Abs(hit.MinX - minX) <= tolerance && hit.Height >= minVerticalSpan).ToArray();
        var right = majorHits.Where(hit => hit.CanActAsVerticalBoundary && Math.Abs(hit.MaxX - maxX) <= tolerance && hit.Height >= minVerticalSpan).ToArray();

        var boundarySupportCount =
            (top.Length > 0 ? 1 : 0) +
            (bottom.Length > 0 ? 1 : 0) +
            (left.Length > 0 ? 1 : 0) +
            (right.Length > 0 ? 1 : 0);
        var boundaryCoverageScore = boundarySupportCount / 4.0;

        var hasClosedLoop =
            top.Length > 0 &&
            bottom.Length > 0 &&
            left.Length > 0 &&
            right.Length > 0 &&
            HasCornerTouch(top, left, tolerance) &&
            HasCornerTouch(top, right, tolerance) &&
            HasCornerTouch(bottom, left, tolerance) &&
            HasCornerTouch(bottom, right, tolerance);

        if (hasClosedLoop)
        {
            return new EnclosureEvidence(true, 1.0, BuildOuterLoopFromBoundaries(top, bottom, left, right), "sectionBoundaryHits.v1");
        }

        return new EnclosureEvidence(false, boundaryCoverageScore * 0.3, new List<Point2D>(), string.Empty);
    }


    private static List<Point2D> BuildOuterLoopFromBoundaries(
        IReadOnlyList<SectionRect> top,
        IReadOnlyList<SectionRect> bottom,
        IReadOnlyList<SectionRect> left,
        IReadOnlyList<SectionRect> right)
    {
        var topBoundary = MergeBoundary(top);
        var bottomBoundary = MergeBoundary(bottom);
        var leftBoundary = MergeBoundary(left);
        var rightBoundary = MergeBoundary(right);
        var minX = Math.Min(leftBoundary.MinX, Math.Min(topBoundary.MinX, bottomBoundary.MinX));
        var maxX = Math.Max(rightBoundary.MaxX, Math.Max(topBoundary.MaxX, bottomBoundary.MaxX));
        var minY = Math.Min(bottomBoundary.MinY, Math.Min(leftBoundary.MinY, rightBoundary.MinY));
        var maxY = Math.Max(topBoundary.MaxY, Math.Max(leftBoundary.MaxY, rightBoundary.MaxY));

        return new List<Point2D>
        {
            new Point2D(Math.Round(minX, 6), Math.Round(minY, 6)),
            new Point2D(Math.Round(maxX, 6), Math.Round(minY, 6)),
            new Point2D(Math.Round(maxX, 6), Math.Round(maxY, 6)),
            new Point2D(Math.Round(minX, 6), Math.Round(maxY, 6))
        };
    }

    private static SectionRect MergeBoundary(IReadOnlyList<SectionRect> rects)
    {
        return new SectionRect(
            rects.Min(rect => rect.MinX),
            rects.Max(rect => rect.MaxX),
            rects.Min(rect => rect.MinY),
            rects.Max(rect => rect.MaxY),
            rects.Sum(rect => Math.Max(0.0, rect.Width)),
            rects.Sum(rect => Math.Max(0.0, rect.Height)),
            rects.Max(rect => rect.Thickness),
            rects.Any(rect => rect.CanActAsHorizontalBoundary),
            rects.Any(rect => rect.CanActAsVerticalBoundary));
    }
    private static SectionRect ToRect(SectionPartHit hit)
    {
        var spanX = GetSpanX(hit);
        var spanY = GetSpanY(hit);
        var canActAsHorizontalBoundary =
            string.Equals(hit.RoleHint, "flange_candidate", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(hit.RoleHint, "wall_candidate", StringComparison.OrdinalIgnoreCase) ||
            spanX >= spanY * 1.1;
        var canActAsVerticalBoundary =
            string.Equals(hit.RoleHint, "web_candidate", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(hit.RoleHint, "wall_candidate", StringComparison.OrdinalIgnoreCase) ||
            spanY >= spanX * 1.1;
        return new SectionRect(
            hit.Center2D.X - spanX / 2.0,
            hit.Center2D.X + spanX / 2.0,
            hit.Center2D.Y - spanY / 2.0,
            hit.Center2D.Y + spanY / 2.0,
            spanX,
            spanY,
            hit.Thickness,
            canActAsHorizontalBoundary,
            canActAsVerticalBoundary);
    }

    private static bool HasCornerTouch(IReadOnlyList<SectionRect> horizontals, IReadOnlyList<SectionRect> verticals, double tolerance)
    {
        foreach (var horizontal in horizontals)
        {
            foreach (var vertical in verticals)
            {
                if (RectsTouch(horizontal, vertical, tolerance))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool RectsTouch(SectionRect a, SectionRect b, double tolerance)
    {
        var overlapX = Math.Min(a.MaxX + tolerance, b.MaxX + tolerance) - Math.Max(a.MinX - tolerance, b.MinX - tolerance);
        var overlapY = Math.Min(a.MaxY + tolerance, b.MaxY + tolerance) - Math.Max(a.MinY - tolerance, b.MinY - tolerance);
        return overlapX >= 0.0 && overlapY >= 0.0;
    }

    private static double ResolveThickness(SectionRect rect)
    {
        if (rect.Thickness > 1e-6)
        {
            return rect.Thickness;
        }

        return Math.Min(rect.Width, rect.Height);
    }

    private static string InferRoleHint(PartSnapshot part, double spanX, double spanY)
    {
        if (part.AxisProjection.CoverageRatio < 0.20)
        {
            return "stiffener_candidate";
        }

        if (IsDirectProfileBody(part))
        {
            return "direct_profile_body";
        }

        if (part.GeometryHints.IsFoldedLike)
        {
            return "folded_flange_candidate";
        }

        if (spanX >= spanY * 1.4)
        {
            return "flange_candidate";
        }

        if (spanY >= spanX * 1.4)
        {
            return "web_candidate";
        }

        return "wall_candidate";
    }

    private static bool IsDirectProfileBody(PartSnapshot part)
    {
        if (!string.Equals(part.PartType, "Beam", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(part.PartType, "PolyBeam", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(part.ProfileString))
        {
            return false;
        }

        var profile = part.ProfileString.Replace(" ", string.Empty).ToUpperInvariant();
        if (profile.StartsWith("PL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return profile.StartsWith("BH", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("H", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("HN", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("HM", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("HW", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("BOX", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("RHS", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("SHS", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("CHS", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("PIPE", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("T", StringComparison.OrdinalIgnoreCase) ||
               profile.StartsWith("L", StringComparison.OrdinalIgnoreCase);
    }


    private static string NormalizeRoleHint(PartSnapshot part, string roleHint)
    {
        return IsDirectProfileBody(part) ? "direct_profile_body" : roleHint;
    }
    private static bool TryIntersectSegmentWithPlane(LineSegment3D segment, Point3D planeOrigin, Vector3D planeNormal, out Point3D intersection)
    {
        const double tolerance = 1e-6;

        var startDistance = SignedDistanceToPlane(segment.Start, planeOrigin, planeNormal);
        var endDistance = SignedDistanceToPlane(segment.End, planeOrigin, planeNormal);

        if (Math.Abs(startDistance) <= tolerance && Math.Abs(endDistance) <= tolerance)
        {
            intersection = segment.Start;
            return true;
        }

        if (Math.Abs(startDistance) <= tolerance)
        {
            intersection = segment.Start;
            return true;
        }

        if (Math.Abs(endDistance) <= tolerance)
        {
            intersection = segment.End;
            return true;
        }

        if ((startDistance < 0 && endDistance < 0) || (startDistance > 0 && endDistance > 0))
        {
            intersection = new Point3D();
            return false;
        }

        var denominator = startDistance - endDistance;
        if (Math.Abs(denominator) <= tolerance)
        {
            intersection = new Point3D();
            return false;
        }

        var t = startDistance / denominator;
        if (t < -tolerance || t > 1.0 + tolerance)
        {
            intersection = new Point3D();
            return false;
        }

        t = Math.Max(0.0, Math.Min(1.0, t));
        intersection = new Point3D(
            segment.Start.X + ((segment.End.X - segment.Start.X) * t),
            segment.Start.Y + ((segment.End.Y - segment.Start.Y) * t),
            segment.Start.Z + ((segment.End.Z - segment.Start.Z) * t));
        return true;
    }

    private static double SignedDistanceToPlane(Point3D point, Point3D planeOrigin, Vector3D planeNormal)
    {
        var vector = new Vector3D(
            point.X - planeOrigin.X,
            point.Y - planeOrigin.Y,
            point.Z - planeOrigin.Z);
        return Dot(vector, planeNormal);
    }

    private static void AddDistinctPoint(List<Point3D> points, Point3D candidate)
    {
        if (points.Any(point => Distance(point, candidate) <= 1.0))
        {
            return;
        }

        points.Add(candidate);
    }

    private static Point2D ProjectToSection(Point3D planeOrigin, Point3D point, Vector3D sectionX, Vector3D sectionY)
    {
        var relative = new Vector3D(
            point.X - planeOrigin.X,
            point.Y - planeOrigin.Y,
            point.Z - planeOrigin.Z);
        return new Point2D(Dot(relative, sectionX), Dot(relative, sectionY));
    }

    private static double ResolveSectionThickness(PartSnapshot part, double spanX, double spanY)
    {
        if (part.Thickness > 0)
        {
            return part.Thickness;
        }

        if (TryParseThicknessFromProfile(part.ProfileString, out var profileThickness))
        {
            return profileThickness;
        }

        return InferThicknessFromSection(spanX, spanY);
    }

    private static double InferThicknessFromSection(double spanX, double spanY)
    {
        var spans = new[] { spanX, spanY }.Where(value => value > 1e-6).OrderBy(value => value).ToArray();
        return spans.Length == 0 ? 0.0 : spans[0];
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

        if (normalized.StartsWith("BH", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("HN", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("HM", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("HW", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("H", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("T", StringComparison.OrdinalIgnoreCase))
        {
            if (numbers.Count >= 4)
            {
                thickness = Math.Min(numbers[numbers.Count - 1], numbers[numbers.Count - 2]);
                return thickness > 0;
            }
        }

        if (normalized.StartsWith("L", StringComparison.OrdinalIgnoreCase))
        {
            thickness = numbers[numbers.Count - 1];
            return thickness > 0;
        }

        return false;
    }


    private readonly struct EnclosureEvidence
    {
        public EnclosureEvidence(bool hasClosedLoop, double score, List<Point2D> outerLoop, string evidenceSource)
        {
            HasClosedLoop = hasClosedLoop;
            Score = score;
            OuterLoop = outerLoop;
            EvidenceSource = evidenceSource;
        }

        public bool HasClosedLoop { get; }
        public double Score { get; }
        public List<Point2D> OuterLoop { get; }
        public string EvidenceSource { get; }

        public static EnclosureEvidence Empty => new EnclosureEvidence(false, 0.0, new List<Point2D>(), string.Empty);
    }
    private readonly struct SectionRect
    {
        public SectionRect(double minX, double maxX, double minY, double maxY, double width, double height, double thickness, bool canActAsHorizontalBoundary, bool canActAsVerticalBoundary)
        {
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
            Width = width;
            Height = height;
            Thickness = thickness;
            CanActAsHorizontalBoundary = canActAsHorizontalBoundary;
            CanActAsVerticalBoundary = canActAsVerticalBoundary;
        }

        public double MinX { get; }
        public double MaxX { get; }
        public double MinY { get; }
        public double MaxY { get; }
        public double Width { get; }
        public double Height { get; }
        public double Thickness { get; }
        public bool CanActAsHorizontalBoundary { get; }
        public bool CanActAsVerticalBoundary { get; }
    }

    private static List<double> ExtractNumbers(string text)
    {
        var numbers = new List<double>();
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\d+(?:\.\d+)?");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (double.TryParse(match.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                numbers.Add(value);
            }
        }

        return numbers;
    }

    private static double GetSpanX(SectionPartHit hit)
    {
        return hit.CutSpanX > 0 ? hit.CutSpanX : (Math.Abs(hit.Direction2D.X) >= Math.Abs(hit.Direction2D.Y) ? hit.TotalCutLength : hit.Thickness);
    }

    private static double GetSpanY(SectionPartHit hit)
    {
        return hit.CutSpanY > 0 ? hit.CutSpanY : (Math.Abs(hit.Direction2D.Y) > Math.Abs(hit.Direction2D.X) ? hit.TotalCutLength : hit.Thickness);
    }

    private static double EstimateSectionWidth(PartSnapshot part, Vector3D axis)
    {
        var x = Math.Abs(axis.X) * Math.Abs(part.BoundingBox.Max.X - part.BoundingBox.Min.X);
        var y = Math.Abs(axis.Y) * Math.Abs(part.BoundingBox.Max.Y - part.BoundingBox.Min.Y);
        var z = Math.Abs(axis.Z) * Math.Abs(part.BoundingBox.Max.Z - part.BoundingBox.Min.Z);
        return x + y + z;
    }

    private static Vector3D BuildSectionXAxis(Vector3D axis)
    {
        var reference = Math.Abs(axis.Z) < 0.9 ? new Vector3D(0, 0, 1) : new Vector3D(1, 0, 0);
        var sectionX = Cross(reference, axis);
        return Normalize(sectionX);
    }

    private static double Distance(Point3D a, Point3D b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double Dot(Vector3D a, Vector3D b)
    {
        return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }

    private static Vector3D Cross(Vector3D a, Vector3D b)
    {
        return new Vector3D(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);
    }

    private static Vector3D Normalize(Vector3D vector)
    {
        var length = Math.Sqrt(Dot(vector, vector));
        if (length <= 1e-9)
        {
            return new Vector3D(1, 0, 0);
        }

        return new Vector3D(vector.X / length, vector.Y / length, vector.Z / length);
    }
}






