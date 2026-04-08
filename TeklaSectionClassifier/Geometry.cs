using System;

namespace TeklaSectionClassifier;

public sealed class Vector3D
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public Vector3D()
    {
    }

    public Vector3D(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

public sealed class Point2D
{
    public double X { get; set; }
    public double Y { get; set; }

    public Point2D()
    {
    }

    public Point2D(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public sealed class Point3D
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public Point3D()
    {
    }

    public Point3D(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

public sealed class LineSegment3D
{
    public Point3D Start { get; set; } = new Point3D();
    public Point3D End { get; set; } = new Point3D();
}

public sealed class BoundingBox3D
{
    public Point3D Min { get; set; } = new Point3D();
    public Point3D Max { get; set; } = new Point3D();
}

public sealed class CoordinateSystem3D
{
    public Point3D Origin { get; set; } = new Point3D();
    public Vector3D X { get; set; } = new Vector3D(1, 0, 0);
    public Vector3D Y { get; set; } = new Vector3D(0, 1, 0);
    public Vector3D Z { get; set; } = new Vector3D(0, 0, 1);
}
