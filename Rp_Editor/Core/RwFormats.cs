using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace ZeusMapEditor.Core;

public sealed class ModelMaterial
{
    public string TextureName = "";
    public string MaskName = "";
    public Vector4 Color = Vector4.One;
    public bool HasTexture;
}

public sealed class ModelSubmesh
{
    public int MaterialIndex;
    public int[] Indices = Array.Empty<int>();
}

public sealed class ModelGeometry
{
    public Vector3[] Vertices = Array.Empty<Vector3>();
    public Vector2[] UVs = Array.Empty<Vector2>();
    public Vector4[] VertexColors = Array.Empty<Vector4>();
    public int[] Indices = Array.Empty<int>();
    public List<ModelMaterial> Materials = new();
    public List<ModelSubmesh> Submeshes = new();
    public Vector3 Min, Max;
    public float Radius;

    public void Finish()
    {
        if (Vertices.Length == 0) return;
        Min = new Vector3(float.MaxValue);
        Max = new Vector3(float.MinValue);
        foreach (var v in Vertices)
        {
            Min = Vector3.Min(Min, v);
            Max = Vector3.Max(Max, v);
        }
        Radius = Corners(Min, Max).Max(c => c.Length());
    }

    public static IEnumerable<Vector3> Corners(Vector3 min, Vector3 max)
    {
        for (int i = 0; i < 8; i++)
        {
            yield return new Vector3(
                (i & 1) == 0 ? min.X : max.X,
                (i & 2) == 0 ? min.Y : max.Y,
                (i & 4) == 0 ? min.Z : max.Z
            );
        }
    }
}

public sealed class CollisionModel
{
    public string Name = "";
    public List<(Vector3 Min, Vector3 Max)> Boxes = new();
    public List<(Vector3 Center, float Radius)> Spheres = new();
    public Vector3[] Vertices = Array.Empty<Vector3>();
    public int[] Indices = Array.Empty<int>();
    public Vector3 Min, Max;
    public bool HasData;
    public float Radius;

    private static bool Bad(Vector3 v) =>
        !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) ||
        MathF.Abs(v.X) > 5000 || MathF.Abs(v.Y) > 5000 || MathF.Abs(v.Z) > 5000;

    public void Finish()
    {
        Boxes.RemoveAll(b => Bad(b.Min) || Bad(b.Max));
        Spheres.RemoveAll(sp => Bad(sp.Center) || !float.IsFinite(sp.Radius) || sp.Radius > 5000 || sp.Radius < 0);

        if (Vertices.Any(Bad))
        {
            Vertices = Array.Empty<Vector3>();
            Indices = Array.Empty<int>();
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;

        foreach (var b in Boxes) { min = Vector3.Min(min, b.Min); max = Vector3.Max(max, b.Max); any = true; }
        foreach (var s in Spheres) { min = Vector3.Min(min, s.Center - new Vector3(s.Radius)); max = Vector3.Max(max, s.Center + new Vector3(s.Radius)); any = true; }
        foreach (var v in Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); any = true; }

        HasData = any;
        if (!any) return;

        Min = min;
        Max = max;
        Radius = ModelGeometry.Corners(min, max).Max(c => c.Length());
    }
}

internal static class Bin
{
    public static uint U32(byte[] d, int p) => BitConverter.ToUInt32(d, p);
    public static int I32(byte[] d, int p) => BitConverter.ToInt32(d, p);
    public static ushort U16(byte[] d, int p) => BitConverter.ToUInt16(d, p);
    public static short I16(byte[] d, int p) => BitConverter.ToInt16(d, p);
    public static float F(byte[] d, int p) => BitConverter.ToSingle(d, p);
    public static Vector3 V3(byte[] d, int p) => new(F(d, p), F(d, p + 4), F(d, p + 8));

    public static string Str(byte[] d, int p, int len)
    {
        int n = 0;
        while (n < len && d[p + n] != 0) n++;
        return Encoding.ASCII.GetString(d, p, n);
    }
}

public static class DffReader
{
    private const uint STRUCT = 0x01;
    private const uint STRING = 0x02;
    private const uint MATERIAL = 0x07;
    private const uint MATERIAL_LIST = 0x08;
    private const uint TEXTURE = 0x06;
    private const uint GEOMETRY = 0x0F;

    private sealed class Chunk
    {
        public uint Type;
        public int Start;
        public int End;
    }

    private static bool TryChunk(byte[] d, int pos, int end, out Chunk chunk)
    {
        chunk = new Chunk();
        if (pos < 0 || pos + 12 > end || end > d.Length) return false;
        uint type = Bin.U32(d, pos);
        uint size = Bin.U32(d, pos + 4);
        long finish = (long)pos + 12 + size;
        if (finish > end || finish < pos) return false;
        chunk.Type = type;
        chunk.Start = pos + 12;
        chunk.End = (int)finish;
        return true;
    }

    private static IEnumerable<Chunk> Children(byte[] d, int start, int end)
    {
        int pos = start;
        while (pos + 12 <= end)
        {
            if (!TryChunk(d, pos, end, out var chunk)) yield break;
            yield return chunk;
            if (chunk.End <= pos) yield break;
            pos = chunk.End;
        }
    }

    public static ModelGeometry? Read(byte[] d)
    {
        if (d == null || d.Length < 12) return null;
        var result = new ModelGeometry();
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var colors = new List<Vector4>();
        var indices = new List<int>();

        try { Walk(d, 0, d.Length, result, vertices, uvs, colors, indices); }
        catch { return null; }

        if (vertices.Count == 0) return null;

        result.Vertices = vertices.ToArray();
        result.UVs = uvs.ToArray();
        result.VertexColors = colors.ToArray();
        result.Indices = indices.ToArray();
        result.Finish();
        return result;
    }

    private static void Walk(byte[] d, int start, int end, ModelGeometry result, List<Vector3> vertices, List<Vector2> uvs, List<Vector4> colors, List<int> indices)
    {
        foreach (var chunk in Children(d, start, end))
        {
            if (chunk.Type == GEOMETRY)
            {
                uint lib = Bin.U32(d, chunk.Start - 4);
                ReadGeometry(d, chunk, lib, result, vertices, uvs, colors, indices);
            }
            else if (chunk.Type == 0x10 || chunk.Type == 0x1A)
            {
                Walk(d, chunk.Start, chunk.End, result, vertices, uvs, colors, indices);
            }
        }
    }

    private static int Version(uint lib)
    {
        return (lib & 0xFFFF0000) != 0
            ? (int)(((lib >> 14) & 0x3FF00) | ((lib >> 16) & 0x3F) | 0x30000)
            : (int)(lib << 8);
    }

    private static void ReadGeometry(byte[] d, Chunk geometry, uint lib, ModelGeometry result, List<Vector3> vertices, List<Vector2> uvs, List<Vector4> colors, List<int> indices)
    {
        Chunk? structure = null;
        Chunk? materialList = null;

        foreach (var child in Children(d, geometry.Start, geometry.End))
        {
            if (child.Type == STRUCT) structure = child;
            if (child.Type == MATERIAL_LIST) materialList = child;
        }

        if (structure == null) return;
        int p = structure.Start;
        if (p + 16 > structure.End) return;

        uint flags = Bin.U32(d, p);
        int triangleCount = (int)Bin.U32(d, p + 4);
        int vertexCount = (int)Bin.U32(d, p + 8);
        int morphCount = (int)Bin.U32(d, p + 12);
        p += 16;

        if (vertexCount <= 0 || vertexCount > 1_000_000 || triangleCount < 0 || triangleCount > 2_000_000 || morphCount <= 0 || morphCount > 100) return;
        if (Version(lib) < 0x34000) p += 12;
        if ((flags & 0x01000000) != 0) return;

        int colorStart = -1;
        if ((flags & 0x08) != 0) { colorStart = p; p += 4 * vertexCount; }

        int uvSets = (int)((flags >> 16) & 0xFF);
        if (uvSets == 0) uvSets = (flags & 0x80) != 0 ? 2 : (flags & 0x04) != 0 ? 1 : 0;

        int uvStart = p;
        long uvEnd = (long)p + 8L * vertexCount * uvSets;
        if (uvEnd > structure.End) return;
        p = (int)uvEnd;

        int triangleStart = p;
        long triangleEnd = (long)p + 8L * triangleCount;
        if (triangleEnd > structure.End) return;
        p = (int)triangleEnd;

        if (p + 24 > structure.End) return;
        bool hasPositions = Bin.U32(d, p + 16) != 0;
        p += 24;

        if (!hasPositions || (long)p + 12L * vertexCount > structure.End) return;

        int vertexBase = vertices.Count;

        for (int i = 0; i < vertexCount; i++)
            vertices.Add(Bin.V3(d, p + 12 * i));

        // Preserva as cores RGBA originais gravadas no DFF.
        if (colorStart >= 0)
        {
            for (int i = 0; i < vertexCount; i++)
            {
                int cp = colorStart + i * 4;
                float r = d[cp] / 255f;
                float g = d[cp + 1] / 255f;
                float b = d[cp + 2] / 255f;
                float a = d[cp + 3] / 255f;

                colors.Add(new Vector4(r, g, b, a));
            }
        }
        else
        {
            for (int i = 0; i < vertexCount; i++) colors.Add(Vector4.One);
        }

        if (uvSets > 0)
        {
            for (int i = 0; i < vertexCount; i++)
            {
                int up = uvStart + i * 8;
                float u = Bin.F(d, up);
                float v = Bin.F(d, up + 4);
                if (!float.IsFinite(u) || !float.IsFinite(v)) { u = 0; v = 0; }
                uvs.Add(new Vector2(u, v));
            }
        }
        else
        {
            for (int i = 0; i < vertexCount; i++) uvs.Add(Vector2.Zero);
        }

        int materialBase = result.Materials.Count;
        var localMaterials = materialList != null ? ReadMaterialList(d, materialList) : new List<ModelMaterial>();
        if (localMaterials.Count == 0) localMaterials.Add(new ModelMaterial());
        result.Materials.AddRange(localMaterials);

        var groups = new Dictionary<int, List<int>>();
        for (int t = 0; t < triangleCount; t++)
        {
            int tp = triangleStart + t * 8;
            int a = Bin.U16(d, tp + 2);
            int b = Bin.U16(d, tp);
            int material = Bin.U16(d, tp + 4);
            int c = Bin.U16(d, tp + 6);

            if (a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
            if (material >= localMaterials.Count) material = 0;

            int ia = vertexBase + a;
            int ib = vertexBase + b;
            int ic = vertexBase + c;

            indices.Add(ia);
            indices.Add(ib);
            indices.Add(ic);

            if (!groups.TryGetValue(material, out var group))
            {
                group = new List<int>();
                groups[material] = group;
            }
            group.Add(ia); group.Add(ib); group.Add(ic);
        }

        foreach (var group in groups)
        {
            result.Submeshes.Add(new ModelSubmesh
            {
                MaterialIndex = materialBase + group.Key,
                Indices = group.Value.ToArray()
            });
        }
    }

    private static List<ModelMaterial> ReadMaterialList(byte[] d, Chunk list)
    {
        var materials = new List<ModelMaterial>();
        Chunk? structure = null;
        var materialChunks = new List<Chunk>();

        foreach (var child in Children(d, list.Start, list.End))
        {
            if (child.Type == STRUCT) structure = child;
            if (child.Type == MATERIAL) materialChunks.Add(child);
        }

        if (structure == null || structure.Start + 4 > structure.End) return materials;

        int count = (int)Bin.U32(d, structure.Start);
        if (count < 0 || count > 100000 || structure.Start + 4L + count * 4L > structure.End) return materials;

        var references = new int[count];
        for (int i = 0; i < count; i++) references[i] = Bin.I32(d, structure.Start + 4 + i * 4);

        var parsed = new List<ModelMaterial>();
        foreach (var chunk in materialChunks) parsed.Add(ReadMaterial(d, chunk));

        int nextMaterial = 0;
        for (int i = 0; i < count; i++)
        {
            int reference = references[i];
            if (reference == -1)
            {
                if (nextMaterial < parsed.Count) materials.Add(parsed[nextMaterial++]);
                else materials.Add(new ModelMaterial());
            }
            else if (reference >= 0 && reference < materials.Count) materials.Add(materials[reference]);
            else materials.Add(new ModelMaterial());
        }

        return materials;
    }

    private static ModelMaterial ReadMaterial(byte[] d, Chunk materialChunk)
    {
        var material = new ModelMaterial();
        foreach (var child in Children(d, materialChunk.Start, materialChunk.End))
        {
            if (child.Type == STRUCT)
            {
                int p = child.Start;
                if (p + 16 > child.End) continue;

                material.Color = new Vector4(
                    d[p + 4] / 255f,
                    d[p + 5] / 255f,
                    d[p + 6] / 255f,
                    d[p + 7] / 255f
                );

                bool textured = Bin.U32(d, p + 12) != 0;
                material.HasTexture = textured;
            }
            else if (child.Type == TEXTURE)
            {
                ReadTextureNames(d, child, material);
            }
        }
        return material;
    }

    private static void ReadTextureNames(byte[] d, Chunk texture, ModelMaterial material)
    {
        var strings = new List<string>();
        foreach (var child in Children(d, texture.Start, texture.End))
        {
            if (child.Type != STRING) continue;
            int length = child.End - child.Start;
            if (length <= 0) continue;
            strings.Add(Bin.Str(d, child.Start, length));
        }

        if (strings.Count > 0) material.TextureName = strings[0].Trim();
        if (strings.Count > 1) material.MaskName = strings[1].Trim();
        material.HasTexture = !string.IsNullOrWhiteSpace(material.TextureName);
    }
}

public static class ColReader
{
    public static List<CollisionModel> ReadAll(byte[] d)
    {
        var list = new List<CollisionModel>();
        int pos = 0;

        while (pos + 32 <= d.Length)
        {
            string cc = Encoding.ASCII.GetString(d, pos, 4);
            if (cc != "COLL" && cc != "COL2" && cc != "COL3" && cc != "COL4") break;

            long size = Bin.U32(d, pos + 4);
            if (size < 0 || size > d.Length - pos - 8) break;

            var model = new CollisionModel { Name = Bin.Str(d, pos + 8, 22) };
            try
            {
                if (cc == "COLL") ReadCol1(d, pos, model);
                else ReadCol23(d, pos, model);
            }
            catch { }

            model.Finish();
            list.Add(model);

            long next = pos + 8 + size;
            if (next <= pos || next > d.Length) break;
            pos = (int)next;
        }
        return list;
    }

    private static void ReadCol1(byte[] d, int pos, CollisionModel m)
    {
        int p = pos + 32 + 40;
        if (p + 4 > d.Length) return;

        int ns = Bin.I32(d, p); p += 4;
        if (ns < 0 || ns > 100000 || (long)p + ns * 20L > d.Length) return;

        for (int i = 0; i < ns; i++, p += 20) m.Spheres.Add((Bin.V3(d, p + 4), Bin.F(d, p)));

        if (p + 4 > d.Length) return;
        int nl = Bin.I32(d, p); p += 4;
        if (nl < 0 || nl > 100000 || (long)p + nl * 24L > d.Length) return;
        p += nl * 24;

        if (p + 4 > d.Length) return;
        int nb = Bin.I32(d, p); p += 4;
        if (nb < 0 || nb > 100000 || (long)p + nb * 28L > d.Length) return;

        for (int i = 0; i < nb; i++, p += 28) m.Boxes.Add((Bin.V3(d, p), Bin.V3(d, p + 12)));

        if (p + 4 > d.Length) return;
        int nv = Bin.I32(d, p); p += 4;
        if (nv < 0 || nv > 1000000 || (long)p + nv * 12L > d.Length) return;

        var verts = new Vector3[nv];
        for (int i = 0; i < nv; i++, p += 12) verts[i] = Bin.V3(d, p);

        if (p + 4 > d.Length) return;
        int nf = Bin.I32(d, p); p += 4;
        if (nf < 0 || nf > 2000000 || (long)p + nf * 16L > d.Length) return;

        var idx = new List<int>(nf * 3);
        for (int i = 0; i < nf; i++, p += 16)
        {
            int a = Bin.I32(d, p);
            int b = Bin.I32(d, p + 4);
            int c = Bin.I32(d, p + 8);
            if (a >= 0 && b >= 0 && c >= 0 && a < nv && b < nv && c < nv)
            {
                idx.Add(a); idx.Add(b); idx.Add(c);
            }
        }

        m.Vertices = verts;
        m.Indices = idx.ToArray();
    }

    private static void ReadCol23(byte[] d, int pos, CollisionModel m)
    {
        int bse = pos + 4;
        int h = pos + 72;
        if (h + 32 > d.Length) return;

        int ns = Bin.U16(d, h);
        int nb = Bin.U16(d, h + 2);
        int nf = Bin.U16(d, h + 4);
        uint offS = Bin.U32(d, h + 12);
        uint offB = Bin.U32(d, h + 16);
        uint offV = Bin.U32(d, h + 24);
        uint offF = Bin.U32(d, h + 28);

        if (ns > 0 && offS != 0)
        {
            for (int i = 0; i < ns; i++)
            {
                long pp = (long)bse + offS + 20L * i;
                if (pp < 0 || pp + 20 > d.Length) break;
                int p = (int)pp;
                m.Spheres.Add((Bin.V3(d, p), Bin.F(d, p + 12)));
            }
        }

        if (nb > 0 && offB != 0)
        {
            for (int i = 0; i < nb; i++)
            {
                long pp = (long)bse + offB + 28L * i;
                if (pp < 0 || pp + 28 > d.Length) break;
                int p = (int)pp;
                m.Boxes.Add((Bin.V3(d, p), Bin.V3(d, p + 12)));
            }
        }

        if (nf > 0 && offF != 0 && offV != 0)
        {
            var faces = new int[nf * 3];
            int maxIdx = -1;
            int validFaces = 0;

            for (int i = 0; i < nf; i++)
            {
                long pp = (long)bse + offF + 8L * i;
                if (pp < 0 || pp + 8 > d.Length) break;
                int p = (int)pp;

                int a = Bin.U16(d, p);
                int b = Bin.U16(d, p + 2);
                int c = Bin.U16(d, p + 4);

                faces[validFaces * 3] = a;
                faces[validFaces * 3 + 1] = b;
                faces[validFaces * 3 + 2] = c;

                maxIdx = Math.Max(maxIdx, Math.Max(a, Math.Max(b, c)));
                validFaces++;
            }

            if (maxIdx < 0 || maxIdx > 1000000) return;

            var verts = new Vector3[maxIdx + 1];
            for (int i = 0; i <= maxIdx; i++)
            {
                long pp = (long)bse + offV + 6L * i;
                if (pp < 0 || pp + 6 > d.Length) return;
                int p = (int)pp;

                verts[i] = new Vector3(Bin.I16(d, p), Bin.I16(d, p + 2), Bin.I16(d, p + 4)) / 128f;
            }

            m.Vertices = verts;
            m.Indices = faces.Take(validFaces * 3).ToArray();
        }
    }
}