using System;
using System.Collections;
using System.Collections.Generic;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace TeklaSectionClassifier;

public static class TeklaPartAxisSampler
{
    public static AlignedSpanPartInput TryRead(Part part)
    {
        if (part == null)
        {
            return null;
        }

        try
        {
            var solid = part.GetSolid();
            if (solid == null)
            {
                return null;
            }

            var min = solid.MinimumPoint;
            var max = solid.MaximumPoint;
            var coordinateSystem = part.GetCoordinateSystem();
            return new AlignedSpanPartInput
            {
                PartType = part.GetType().Name,
                Centroid = new Point3D(
                    (min.X + max.X) / 2.0,
                    (min.Y + max.Y) / 2.0,
                    (min.Z + max.Z) / 2.0),
                AxisX = new Vector3D(coordinateSystem.AxisX.X, coordinateSystem.AxisX.Y, coordinateSystem.AxisX.Z),
                AxisY = new Vector3D(coordinateSystem.AxisY.X, coordinateSystem.AxisY.Y, coordinateSystem.AxisY.Z),
                BoundingBox = new BoundingBox3D
                {
                    Min = new Point3D(min.X, min.Y, min.Z),
                    Max = new Point3D(max.X, max.Y, max.Z)
                },
                SolidEdges = ReadEdges(solid)
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<LineSegment3D> ReadEdges(Solid solid)
    {
        var edges = new List<LineSegment3D>();
        var enumerator = solid.GetType().GetMethod("GetEdgeEnumerator", Type.EmptyTypes)?.Invoke(solid, null) as IEnumerator;
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
            var start = edgeType.GetProperty("StartPoint")?.GetValue(edge, null) as Point;
            var end = edgeType.GetProperty("EndPoint")?.GetValue(edge, null) as Point;
            if (start == null || end == null)
            {
                continue;
            }

            var dx = start.X - end.X;
            var dy = start.Y - end.Y;
            var dz = start.Z - end.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) <= 1e-12)
            {
                continue;
            }

            edges.Add(new LineSegment3D
            {
                Start = new Point3D(start.X, start.Y, start.Z),
                End = new Point3D(end.X, end.Y, end.Z)
            });
        }

        return edges;
    }
}
