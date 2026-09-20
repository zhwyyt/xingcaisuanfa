using System;
using System.Collections.Generic;

namespace TeklaSectionClassifier;

internal static class LongitudinalAxisResolver
{
    public static double GetAxisLength(MemberSnapshot member)
    {
        if (member.AxisSegments != null && member.AxisSegments.Count > 0)
        {
            var axisLength = member.AxisSegments[member.AxisSegments.Count - 1].CumulativeEnd;
            if (axisLength > 1e-6)
            {
                return axisLength;
            }
        }

        return member.Member.MainAxis.Length;
    }

    public static (Point3D Origin, Vector3D Direction) ResolveStationFrame(MemberSnapshot member, double station)
    {
        if (member.AxisSegments != null && member.AxisSegments.Count > 0)
        {
            var segment = FindStationSegment(member.AxisSegments, station);
            var local = Clamp(station, segment.CumulativeStart, segment.CumulativeEnd) - segment.CumulativeStart;
            return (
                new Point3D(
                    segment.Start.X + segment.Direction.X * local,
                    segment.Start.Y + segment.Direction.Y * local,
                    segment.Start.Z + segment.Direction.Z * local),
                segment.Direction);
        }

        var direction = Normalize(member.Member.MainAxis.Direction);
        return (
            new Point3D(
                member.Member.MainAxis.Origin.X + direction.X * station,
                member.Member.MainAxis.Origin.Y + direction.Y * station,
                member.Member.MainAxis.Origin.Z + direction.Z * station),
            direction);
    }

    public static AxisProjection EstimatePartAxisProjection(MemberSnapshot member, PartSnapshot part)
    {
        var stations = new List<double>();
        foreach (var point in CollectRepresentativePoints(part))
        {
            stations.Add(ProjectPointToStation(member, point));
        }

        if (stations.Count == 0)
        {
            return new AxisProjection();
        }

        var start = double.MaxValue;
        var end = double.MinValue;
        foreach (var station in stations)
        {
            if (station < start)
            {
                start = station;
            }

            if (station > end)
            {
                end = station;
            }
        }

        var length = Math.Max(0.0, end - start);
        var axisLength = Math.Max(GetAxisLength(member), 1.0);
        return new AxisProjection
        {
            Start = start,
            End = end,
            Length = length,
            CoverageRatio = length / axisLength
        };
    }

    private static LongitudinalAxisSegment FindStationSegment(IReadOnlyList<LongitudinalAxisSegment> segments, double station)
    {
        foreach (var segment in segments)
        {
            if (station >= segment.CumulativeStart - 1e-6 && station <= segment.CumulativeEnd + 1e-6)
            {
                return segment;
            }
        }

        return station <= segments[0].CumulativeStart
            ? segments[0]
            : segments[segments.Count - 1];
    }

    private static double ProjectPointToStation(MemberSnapshot member, Point3D point)
    {
        if (member.AxisSegments != null && member.AxisSegments.Count > 0)
        {
            double bestDistanceSquared = double.MaxValue;
            double bestStation = 0.0;

            foreach (var segment in member.AxisSegments)
            {
                var projected = ProjectPointToSegment(point, segment);
                if (projected.DistanceSquared >= bestDistanceSquared)
                {
                    continue;
                }

                bestDistanceSquared = projected.DistanceSquared;
                bestStation = segment.CumulativeStart + projected.LocalPosition;
            }

            return bestStation;
        }

        var axis = Normalize(member.Member.MainAxis.Direction);
        var dx = point.X - member.Member.MainAxis.Origin.X;
        var dy = point.Y - member.Member.MainAxis.Origin.Y;
        var dz = point.Z - member.Member.MainAxis.Origin.Z;
        var station = dx * axis.X + dy * axis.Y + dz * axis.Z;
        return Clamp(station, 0.0, Math.Max(member.Member.MainAxis.Length, 0.0));
    }

    private static (double LocalPosition, double DistanceSquared) ProjectPointToSegment(Point3D point, LongitudinalAxisSegment segment)
    {
        var px = point.X - segment.Start.X;
        var py = point.Y - segment.Start.Y;
        var pz = point.Z - segment.Start.Z;
        var local = Clamp(px * segment.Direction.X + py * segment.Direction.Y + pz * segment.Direction.Z, 0.0, segment.Length);
        var closestX = segment.Start.X + segment.Direction.X * local;
        var closestY = segment.Start.Y + segment.Direction.Y * local;
        var closestZ = segment.Start.Z + segment.Direction.Z * local;
        var dx = point.X - closestX;
        var dy = point.Y - closestY;
        var dz = point.Z - closestZ;
        return (local, dx * dx + dy * dy + dz * dz);
    }

    private static IEnumerable<Point3D> CollectRepresentativePoints(PartSnapshot part)
    {
        yield return part.Centroid;

        var min = part.BoundingBox.Min;
        var max = part.BoundingBox.Max;
        for (var xIndex = 0; xIndex < 2; xIndex++)
        {
            var x = xIndex == 0 ? min.X : max.X;
            for (var yIndex = 0; yIndex < 2; yIndex++)
            {
                var y = yIndex == 0 ? min.Y : max.Y;
                for (var zIndex = 0; zIndex < 2; zIndex++)
                {
                    var z = zIndex == 0 ? min.Z : max.Z;
                    yield return new Point3D(x, y, z);
                }
            }
        }

        foreach (var edge in part.SolidEdges)
        {
            yield return edge.Start;
            yield return edge.End;
        }
    }

    private static Vector3D Normalize(Vector3D vector)
    {
        var length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
        return length <= 1e-9
            ? new Vector3D(1, 0, 0)
            : new Vector3D(vector.X / length, vector.Y / length, vector.Z / length);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }
}
