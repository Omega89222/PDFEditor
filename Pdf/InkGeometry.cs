using System;
using System.Collections.Generic;
using System.Windows;

namespace PDFEditor.Pdf;

public readonly record struct BezierSegment(Point Control1, Point Control2, Point End);

/// <summary>
/// Lissage et simplification des traits a main levee. Le meme algorithme sert
/// a l'affichage (WPF) et a l'export (PDFium) : le rendu est identique.
/// </summary>
public static class InkGeometry
{
    /// <summary>Spline de Catmull-Rom convertie en segments de Bezier cubiques.</summary>
    public static List<BezierSegment> Smooth(IReadOnlyList<Point> points)
    {
        var result = new List<BezierSegment>(Math.Max(0, points.Count - 1));
        if (points.Count < 2)
        {
            return result;
        }

        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[Math.Max(0, i - 1)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(points.Count - 1, i + 2)];

            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            result.Add(new BezierSegment(c1, c2, p2));
        }

        return result;
    }

    /// <summary>Simplification de Ramer-Douglas-Peucker (tolerance en points).</summary>
    public static List<Point> Simplify(IReadOnlyList<Point> points, double tolerance)
    {
        if (points.Count < 3 || tolerance <= 0)
        {
            return new List<Point>(points);
        }

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;

        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, points.Count - 1));

        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            var maxDistance = 0.0;
            var index = -1;

            for (var i = first + 1; i < last; i++)
            {
                var d = DistanceToSegment(points[i], points[first], points[last]);
                if (d > maxDistance)
                {
                    maxDistance = d;
                    index = i;
                }
            }

            if (index >= 0 && maxDistance > tolerance)
            {
                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }
        }

        var result = new List<Point>();
        for (var i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                result.Add(points[i]);
            }
        }

        return result;
    }

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9)
        {
            return (p - a).Length;
        }

        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        var projection = new Point(a.X + t * dx, a.Y + t * dy);
        return (p - projection).Length;
    }
}
