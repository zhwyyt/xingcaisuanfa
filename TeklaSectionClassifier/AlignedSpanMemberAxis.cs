using System;
using System.Collections.Generic;

namespace TeklaSectionClassifier;

public sealed class AlignedSpanPartInput
{
    public string PartType { get; set; } = string.Empty;

    public Point3D Centroid { get; set; } = new Point3D();

    public Vector3D AxisX { get; set; } = new Vector3D(1, 0, 0);

    public Vector3D AxisY { get; set; } = new Vector3D(0, 1, 0);

    public BoundingBox3D BoundingBox { get; set; } = new BoundingBox3D();

    public List<LineSegment3D> SolidEdges { get; set; } = new List<LineSegment3D>();
}

public sealed class AlignedSpanAxisResult
{
    public bool HasAxis { get; set; }

    public string Kind { get; set; } = "STRAIGHT";

    public string Source { get; set; } = AlignedSpanMemberAxis.SourceName;

    public AxisDefinition Axis { get; set; } = new AxisDefinition();

    public List<LongitudinalAxisSegment> Segments { get; set; } = new List<LongitudinalAxisSegment>();

    public List<Point3D> GuidePolyline { get; set; } = new List<Point3D>();

    public double Confidence { get; set; }
}

public static class AlignedSpanMemberAxis
{
    public const string SourceName = "assemblyParts.alignedSpanCentroid";

    private const double AngleLimitDegrees = 5.0;
    private const double CloseSpanRatio = 0.85;
    private const double MeetMillimetres = 300.0;
    private const double RadiusPadMillimetres = 50.0;

    private static readonly double CosLimit = Math.Cos(AngleLimitDegrees * Math.PI / 180.0);

    public static List<AlignedSpanPartInput> FromSnapshots(IReadOnlyList<PartSnapshot> parts)
    {
        var inputs = new List<AlignedSpanPartInput>(parts.Count);
        foreach (var part in parts)
        {
            var cs = part.LocalCoordinateSystem ?? new CoordinateSystem3D();
            inputs.Add(new AlignedSpanPartInput
            {
                PartType = part.PartType ?? string.Empty,
                Centroid = part.Centroid ?? new Point3D(),
                AxisX = cs.X ?? new Vector3D(1, 0, 0),
                AxisY = cs.Y ?? new Vector3D(0, 1, 0),
                BoundingBox = part.BoundingBox ?? new BoundingBox3D(),
                SolidEdges = part.SolidEdges ?? new List<LineSegment3D>()
            });
        }

        return inputs;
    }

    public static AlignedSpanAxisResult Resolve(IReadOnlyList<AlignedSpanPartInput> parts)
    {
        var items = new List<SpanItem>();
        if (parts != null)
        {
            foreach (var part in parts)
            {
                items.AddRange(MeasurePart(part));
            }
        }

        if (items.Count == 0)
        {
            return new AlignedSpanAxisResult();
        }

        var clusters = Cluster(items);
        var primary = clusters[0];
        var inliers = SelectInliers(primary.Items, primary.Direction, out var center, out var radius);
        var coverage = Cover(items, inliers, center, primary.Direction, radius);
        var kind = "STRAIGHT";
        SpanCluster second = null;
        if (clusters.Count >= 2 && clusters[1].Span >= primary.Span * CloseSpanRatio)
        {
            second = clusters[1];
            if (AngleDegrees(primary.Direction, second.Direction) > AngleLimitDegrees)
            {
                var other = ClusterEnds(second);
                var gap = Math.Min(
                    Math.Min(Distance(coverage.Start, other.Start), Distance(coverage.Start, other.End)),
                    Math.Min(Distance(coverage.End, other.Start), Distance(coverage.End, other.End)));
                if (gap <= Math.Max(MeetMillimetres, radius))
                {
                    kind = "POLYLINE";
                }
            }
        }

        var segments = kind == "POLYLINE"
            ? BuildPolyline(coverage, second)
            : BuildStraight(coverage);
        return new AlignedSpanAxisResult
        {
            HasAxis = coverage.Length > 1.0,
            Kind = kind,
            Source = SourceName,
            Axis = new AxisDefinition
            {
                Origin = coverage.Start,
                Direction = primary.Direction,
                Length = coverage.Length
            },
            Segments = segments,
            GuidePolyline = GuideOf(segments),
            Confidence = kind == "POLYLINE" ? 0.84 : 0.9
        };
    }

    private static List<SpanItem> MeasurePart(AlignedSpanPartInput part)
    {
        if (part == null)
        {
            return new List<SpanItem>();
        }

        if (string.Equals(part.PartType, "PolyBeam", StringComparison.OrdinalIgnoreCase))
        {
            var split = PolybeamSegments(part);
            if (split.Count >= 2)
            {
                return split;
            }
        }

        var single = MeasureExtents(part, SolidPoints(part));
        return single == null ? new List<SpanItem>() : new List<SpanItem> { single };
    }

    private static SpanItem MeasureExtents(AlignedSpanPartInput part, List<Point3D> points)
    {
        var axisX = Normalize(part.AxisX);
        var axisY = Normalize(part.AxisY);
        if (axisX == null || axisY == null)
        {
            return null;
        }

        var axisZ = Normalize(Cross(axisX, axisY));
        if (axisZ == null)
        {
            return null;
        }

        if (points == null || points.Count < 2)
        {
            points = BoxCorners(part.BoundingBox);
        }

        var spanX = Span(points, axisX);
        var spanY = Span(points, axisY);
        var spanZ = Span(points, axisZ);
        Vector3D direction;
        double partLength;
        double perpA;
        double perpB;
        if (spanX >= spanY)
        {
            direction = axisX;
            partLength = spanX;
            perpA = spanY;
            perpB = spanZ;
        }
        else
        {
            direction = axisY;
            partLength = spanY;
            perpA = spanX;
            perpB = spanZ;
        }

        if (partLength <= 1.0)
        {
            return null;
        }

        var sectionWidth = Math.Max(perpA, perpB);
        var minDim = Math.Min(perpA, perpB);
        return new SpanItem
        {
            Direction = direction,
            Length = partLength,
            Centroid = part.Centroid ?? new Point3D(),
            SectionWidth = sectionWidth,
            MinDim = minDim,
            SectionArea = Math.Max(minDim, 0.1) * Math.Max(sectionWidth, 0.1)
        };
    }

    private static List<SpanItem> PolybeamSegments(AlignedSpanPartInput part)
    {
        var grouped = new List<EdgeCluster>();
        foreach (var edge in part.SolidEdges ?? new List<LineSegment3D>())
        {
            if (edge?.Start == null || edge.End == null)
            {
                continue;
            }

            var delta = Sub(edge.End, edge.Start);
            var direction = Normalize(delta);
            var edgeLength = Length(delta);
            if (direction == null || edgeLength < 50.0)
            {
                continue;
            }

            EdgeCluster placed = null;
            foreach (var cluster in grouped)
            {
                if (Math.Abs(Dot(direction, cluster.Direction)) >= CosLimit)
                {
                    placed = cluster;
                    break;
                }
            }

            if (placed == null)
            {
                grouped.Add(new EdgeCluster
                {
                    Direction = direction,
                    Points = new List<Point3D> { edge.Start, edge.End }
                });
                continue;
            }

            var start = edge.Start;
            var end = edge.End;
            if (Dot(direction, placed.Direction) < 0)
            {
                start = edge.End;
                end = edge.Start;
            }

            placed.Points.Add(start);
            placed.Points.Add(end);
        }

        var boxItem = MeasureExtents(part, SolidPoints(part));
        var sectionWidth = boxItem?.SectionWidth ?? 1.0;
        var minDim = boxItem?.MinDim ?? 1.0;
        var sectionArea = boxItem?.SectionArea ?? 1.0;
        var segments = new List<SpanItem>();
        foreach (var cluster in grouped)
        {
            var low = double.MaxValue;
            var high = double.MinValue;
            foreach (var point in cluster.Points)
            {
                var station = Dot(point, cluster.Direction);
                low = Math.Min(low, station);
                high = Math.Max(high, station);
            }

            var span = high - low;
            if (span < 50.0)
            {
                continue;
            }

            var centerStation = (low + high) / 2.0;
            Point3D extreme = null;
            var far = -1.0;
            foreach (var point in cluster.Points)
            {
                var station = Dot(point, cluster.Direction);
                if (extreme == null || station < low + 1.0 || station > high - 1.0)
                {
                    var distance = Math.Abs(station - centerStation);
                    if (distance >= far)
                    {
                        far = distance;
                        extreme = point;
                    }
                }
            }

            var centroid = extreme == null
                ? new Point3D()
                : Add(extreme, Mul(cluster.Direction, centerStation - Dot(extreme, cluster.Direction)));
            segments.Add(new SpanItem
            {
                Direction = cluster.Direction,
                Length = span,
                Centroid = centroid,
                SectionWidth = sectionWidth,
                MinDim = minDim,
                SectionArea = sectionArea
            });
        }

        if (segments.Count < 2)
        {
            return new List<SpanItem>();
        }

        var longest = 0.0;
        foreach (var segment in segments)
        {
            longest = Math.Max(longest, segment.Length);
        }

        var kept = new List<SpanItem>();
        var minimum = Math.Max(200.0, longest * 0.40);
        foreach (var segment in segments)
        {
            if (segment.Length >= minimum)
            {
                kept.Add(segment);
            }
        }

        return kept.Count >= 2 ? kept : new List<SpanItem>();
    }

    private static List<SpanCluster> Cluster(List<SpanItem> items)
    {
        items.Sort((left, right) => right.Length.CompareTo(left.Length));
        var clusters = new List<SpanCluster>();
        foreach (var item in items)
        {
            SpanCluster match = null;
            foreach (var cluster in clusters)
            {
                if (Math.Abs(Dot(item.Direction, cluster.Direction)) >= CosLimit)
                {
                    match = cluster;
                    break;
                }
            }

            var copy = item.Copy();
            if (match == null)
            {
                clusters.Add(new SpanCluster
                {
                    Direction = copy.Direction,
                    Items = new List<SpanItem> { copy }
                });
                continue;
            }

            if (Dot(copy.Direction, match.Direction) < 0)
            {
                copy.Direction = Mul(copy.Direction, -1.0);
            }

            match.Items.Add(copy);
        }

        foreach (var cluster in clusters)
        {
            Stations(cluster.Items, cluster.Direction, out _, out _, out var span);
            cluster.Span = span;
        }

        clusters.Sort((left, right) => right.Span.CompareTo(left.Span));
        return clusters;
    }

    private static List<SpanItem> SelectInliers(List<SpanItem> items, Vector3D direction, out Point3D center, out double radius)
    {
        SpanItem seed = items[0];
        var seedScore = seed.SectionArea * Math.Max(seed.Length, 1.0);
        foreach (var item in items)
        {
            var score = item.SectionArea * Math.Max(item.Length, 1.0);
            if (score > seedScore)
            {
                seed = item;
                seedScore = score;
            }
        }

        center = seed.Centroid;
        radius = Math.Max(seed.SectionWidth, 1.0);
        var chosen = new List<SpanItem> { seed };
        for (var pass = 0; pass < 4; pass++)
        {
            chosen = new List<SpanItem>();
            foreach (var item in items)
            {
                if (Perpendicular(item.Centroid, center, direction) <= radius + RadiusPadMillimetres)
                {
                    chosen.Add(item);
                }
            }

            if (chosen.Count == 0)
            {
                chosen.Add(seed);
            }

            var area = 0.0;
            foreach (var item in chosen)
            {
                area += item.SectionArea;
            }

            area = area <= 0.0 ? 1.0 : area;
            center = new Point3D();
            foreach (var item in chosen)
            {
                var weight = item.SectionArea / area;
                center = Add(center, Mul(item.Centroid, weight));
            }

            radius = 1.0;
            foreach (var item in chosen)
            {
                radius = Math.Max(radius, item.SectionWidth);
            }
        }

        return chosen;
    }

    private static Coverage Cover(List<SpanItem> allItems, List<SpanItem> inliers, Point3D center, Vector3D direction, double radius)
    {
        var low = double.MaxValue;
        var high = double.MinValue;
        var inlierIds = new HashSet<SpanItem>(inliers);
        foreach (var item in inliers)
        {
            Take(item, center, direction, ref low, ref high);
        }

        foreach (var item in allItems)
        {
            if (inlierIds.Contains(item))
            {
                continue;
            }

            if (Perpendicular(item.Centroid, center, direction) > radius + RadiusPadMillimetres)
            {
                continue;
            }

            Take(item, center, direction, ref low, ref high);
        }

        if (low == double.MaxValue)
        {
            return new Coverage { Start = center, End = center, Length = 0.0 };
        }

        return new Coverage
        {
            Start = Add(center, Mul(direction, low)),
            End = Add(center, Mul(direction, high)),
            Length = high - low
        };
    }

    private static void Take(SpanItem item, Point3D center, Vector3D direction, ref double low, ref double high)
    {
        var station = Dot(Sub(item.Centroid, center), direction);
        var half = HalfAlong(item, direction);
        low = Math.Min(low, station - half);
        high = Math.Max(high, station + half);
    }

    private static double HalfAlong(SpanItem item, Vector3D direction)
    {
        var align = Math.Abs(Dot(item.Direction, direction));
        if (align >= CosLimit)
        {
            return align * item.Length / 2.0;
        }

        return item.MinDim / 2.0 + align * item.Length / 2.0;
    }

    private static Coverage ClusterEnds(SpanCluster cluster)
    {
        var inliers = SelectInliers(cluster.Items, cluster.Direction, out var center, out _);
        var low = double.MaxValue;
        var high = double.MinValue;
        foreach (var item in inliers)
        {
            Take(item, center, cluster.Direction, ref low, ref high);
        }

        if (low == double.MaxValue)
        {
            return new Coverage { Start = center, End = center, Length = 0.0 };
        }

        return new Coverage
        {
            Start = Add(center, Mul(cluster.Direction, low)),
            End = Add(center, Mul(cluster.Direction, high)),
            Length = high - low
        };
    }

    private static List<LongitudinalAxisSegment> BuildStraight(Coverage coverage)
    {
        return new List<LongitudinalAxisSegment>
        {
            Segment(0, coverage.Start, coverage.End, 0.0)
        };
    }

    private static List<LongitudinalAxisSegment> BuildPolyline(Coverage primary, SpanCluster second)
    {
        var other = ClusterEnds(second);
        var gapEndStart = Distance(primary.End, other.Start);
        var gapEndEnd = Distance(primary.End, other.End);
        var gapStartStart = Distance(primary.Start, other.Start);
        var gapStartEnd = Distance(primary.Start, other.End);
        var best = Math.Min(Math.Min(gapEndStart, gapEndEnd), Math.Min(gapStartStart, gapStartEnd));
        Point3D firstStart;
        Point3D firstEnd;
        Point3D secondStart;
        Point3D secondEnd;
        if (best == gapEndStart)
        {
            firstStart = primary.Start;
            firstEnd = primary.End;
            secondStart = other.Start;
            secondEnd = other.End;
        }
        else if (best == gapEndEnd)
        {
            firstStart = primary.Start;
            firstEnd = primary.End;
            secondStart = other.End;
            secondEnd = other.Start;
        }
        else if (best == gapStartEnd)
        {
            firstStart = other.Start;
            firstEnd = other.End;
            secondStart = primary.Start;
            secondEnd = primary.End;
        }
        else
        {
            firstStart = other.End;
            firstEnd = other.Start;
            secondStart = primary.Start;
            secondEnd = primary.End;
        }

        var first = Segment(0, firstStart, firstEnd, 0.0);
        var secondSegment = Segment(1, secondStart, secondEnd, first.CumulativeEnd);
        return new List<LongitudinalAxisSegment> { first, secondSegment };
    }

    private static LongitudinalAxisSegment Segment(int index, Point3D start, Point3D end, double cumulativeStart)
    {
        var delta = Sub(end, start);
        var segmentLength = Length(delta);
        var direction = Normalize(delta) ?? new Vector3D(1, 0, 0);
        return new LongitudinalAxisSegment
        {
            Index = index,
            Start = start,
            End = end,
            Direction = direction,
            Length = segmentLength,
            CumulativeStart = cumulativeStart,
            CumulativeEnd = cumulativeStart + segmentLength
        };
    }

    private static List<Point3D> GuideOf(List<LongitudinalAxisSegment> segments)
    {
        var guide = new List<Point3D>();
        foreach (var segment in segments)
        {
            if (guide.Count == 0)
            {
                guide.Add(segment.Start);
            }

            guide.Add(segment.End);
        }

        return guide;
    }

    private static void Stations(List<SpanItem> items, Vector3D direction, out double low, out double high, out double span)
    {
        low = double.MaxValue;
        high = double.MinValue;
        foreach (var item in items)
        {
            var half = item.Length / 2.0;
            for (var sign = -1.0; sign <= 1.0; sign += 2.0)
            {
                var point = Add(item.Centroid, Mul(item.Direction, half * sign));
                var station = Dot(point, direction);
                low = Math.Min(low, station);
                high = Math.Max(high, station);
            }
        }

        span = low == double.MaxValue ? 0.0 : high - low;
    }

    private static List<Point3D> SolidPoints(AlignedSpanPartInput part)
    {
        var points = new List<Point3D>();
        foreach (var edge in part.SolidEdges ?? new List<LineSegment3D>())
        {
            if (edge?.Start == null || edge.End == null)
            {
                continue;
            }

            points.Add(edge.Start);
            points.Add(edge.End);
        }

        return points;
    }

    private static List<Point3D> BoxCorners(BoundingBox3D box)
    {
        var low = box?.Min ?? new Point3D();
        var high = box?.Max ?? new Point3D();
        return new List<Point3D>
        {
            new Point3D(low.X, low.Y, low.Z),
            new Point3D(low.X, low.Y, high.Z),
            new Point3D(low.X, high.Y, low.Z),
            new Point3D(low.X, high.Y, high.Z),
            new Point3D(high.X, low.Y, low.Z),
            new Point3D(high.X, low.Y, high.Z),
            new Point3D(high.X, high.Y, low.Z),
            new Point3D(high.X, high.Y, high.Z)
        };
    }

    private static double Span(List<Point3D> points, Vector3D direction)
    {
        var low = double.MaxValue;
        var high = double.MinValue;
        foreach (var point in points)
        {
            var station = Dot(point, direction);
            low = Math.Min(low, station);
            high = Math.Max(high, station);
        }

        return low == double.MaxValue ? 0.0 : high - low;
    }

    private static double Perpendicular(Point3D point, Point3D origin, Vector3D direction)
    {
        var offset = Sub(point, origin);
        return Length(Sub(offset, Mul(direction, Dot(offset, direction))));
    }

    private static double AngleDegrees(Vector3D left, Vector3D right)
    {
        var value = Math.Abs(Dot(left, right));
        value = Math.Min(1.0, Math.Max(0.0, value));
        return Math.Acos(value) * 180.0 / Math.PI;
    }

    private static Vector3D Normalize(Vector3D vector)
    {
        if (vector == null)
        {
            return null;
        }

        var magnitude = Length(vector);
        if (magnitude <= 1e-9)
        {
            return null;
        }

        return new Vector3D(vector.X / magnitude, vector.Y / magnitude, vector.Z / magnitude);
    }

    private static Vector3D Cross(Vector3D left, Vector3D right)
    {
        return new Vector3D(
            left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);
    }

    private static double Dot(Vector3D vector, Vector3D other)
    {
        return vector.X * other.X + vector.Y * other.Y + vector.Z * other.Z;
    }

    private static double Dot(Point3D point, Vector3D vector)
    {
        return point.X * vector.X + point.Y * vector.Y + point.Z * vector.Z;
    }

    private static Point3D Add(Point3D point, Vector3D vector)
    {
        return new Point3D(point.X + vector.X, point.Y + vector.Y, point.Z + vector.Z);
    }

    private static Point3D Add(Point3D point, Point3D other)
    {
        return new Point3D(point.X + other.X, point.Y + other.Y, point.Z + other.Z);
    }

    private static Vector3D Sub(Point3D end, Point3D start)
    {
        return new Vector3D(end.X - start.X, end.Y - start.Y, end.Z - start.Z);
    }

    private static Vector3D Sub(Vector3D end, Vector3D start)
    {
        return new Vector3D(end.X - start.X, end.Y - start.Y, end.Z - start.Z);
    }

    private static Vector3D Mul(Vector3D vector, double scale)
    {
        return new Vector3D(vector.X * scale, vector.Y * scale, vector.Z * scale);
    }

    private static Point3D Mul(Point3D point, double scale)
    {
        return new Point3D(point.X * scale, point.Y * scale, point.Z * scale);
    }

    private static double Length(Vector3D vector)
    {
        return Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
    }

    private static double Distance(Point3D left, Point3D right)
    {
        return Length(Sub(right, left));
    }

    private sealed class SpanItem
    {
        public Vector3D Direction { get; set; }

        public double Length { get; set; }

        public Point3D Centroid { get; set; }

        public double SectionWidth { get; set; }

        public double MinDim { get; set; }

        public double SectionArea { get; set; }

        public SpanItem Copy()
        {
            return new SpanItem
            {
                Direction = Direction,
                Length = Length,
                Centroid = Centroid,
                SectionWidth = SectionWidth,
                MinDim = MinDim,
                SectionArea = SectionArea
            };
        }
    }

    private sealed class SpanCluster
    {
        public Vector3D Direction { get; set; }

        public List<SpanItem> Items { get; set; }

        public double Span { get; set; }
    }

    private sealed class EdgeCluster
    {
        public Vector3D Direction { get; set; }

        public List<Point3D> Points { get; set; }
    }

    private struct Coverage
    {
        public Point3D Start { get; set; }

        public Point3D End { get; set; }

        public double Length { get; set; }
    }
}
