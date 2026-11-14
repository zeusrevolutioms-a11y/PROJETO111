using System.Drawing;

namespace ZeusMapEditor.Core;

/// <summary>Funções 2D usadas pelo canvas (silhueta e seleção).</summary>
public static class Geometry2D
{
    private sealed class Cmp : IComparer<PointF>
    {
        public int Compare(PointF a, PointF b) { int c = a.X.CompareTo(b.X); return c != 0 ? c : a.Y.CompareTo(b.Y); }
    }
    private static readonly Cmp comparer = new();

    private static float Cross(PointF o, PointF a, PointF b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>Envoltória convexa (monotone chain). 'hull' precisa ter pelo menos 2*n+1 posições.</summary>
    public static int ConvexHull(PointF[] pts, int n, PointF[] hull)
    {
        if (n < 3) { for (int i = 0; i < n; i++) hull[i] = pts[i]; return n; }
        Array.Sort(pts, 0, n, comparer);
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], pts[i]) <= 0) k--;
            hull[k++] = pts[i];
        }
        for (int i = n - 2, t = k + 1; i >= 0; i--)
        {
            while (k >= t && Cross(hull[k - 2], hull[k - 1], pts[i]) <= 0) k--;
            hull[k++] = pts[i];
        }
        return k - 1;
    }

    public static bool PointInPolygon(PointF[] poly, PointF p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    public static float Area(PointF[] poly)
    {
        float a = 0;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++) a += (poly[j].X + poly[i].X) * (poly[j].Y - poly[i].Y);
        return MathF.Abs(a / 2f);
    }
}
