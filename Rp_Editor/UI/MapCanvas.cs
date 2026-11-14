using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;

using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL;
using OpenTK.GLControl;
using OpenTK.Windowing.Common;

using ZeusMapEditor.Core;

using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

using Vector3 = OpenTK.Mathematics.Vector3;
using NumericsVector3 = System.Numerics.Vector3;

namespace ZeusMapEditor.UI;

public sealed class MapCanvas3D : GLControl
{
    public GtaMapDocument Document { get; set; } = new();
    public AssetLibrary Assets { get; set; } = new();
    public MapObject? Selected { get; set; }

    public event Action<MapObject?>? ObjectPicked;
    public event Action<float, float>? CursorMoved;

    private float yaw = 45f;
    private float pitch = 35f;
    private float distance = 100f;

    private Vector3 target = Vector3.Zero;
    private Point lastMouse;

    private bool rotating;
    private bool panning;
    private bool glReady;

    private const float GridSize = 500f;
    private const float GridStep = 25f;

    private sealed class GpuSubmesh
    {
        public int Ebo;
        public int IndexCount;
        public int MaterialIndex;
    }

    private sealed class GpuMesh
    {
        public int Vbo;
        public List<GpuSubmesh> Submeshes = new();
    }

    private readonly Dictionary<ModelGeometry, GpuMesh> gpuCache = new();
    private readonly Dictionary<string, int> textureCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly int VertexStride = 12 * sizeof(float);

    public MapCanvas3D()
        : base(new GLControlSettings
        {
            API = ContextAPI.OpenGL,
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Compatability
        })
    {
        Dock = DockStyle.Fill;
        Load += (_, _) => InitializeGL();
    }

    private void InitializeGL()
    {
        if (glReady)
            return;

        MakeCurrent();

        GL.ClearColor(0.12f, 0.16f, 0.20f, 1f);

        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Lighting);
        GL.Enable(EnableCap.Light0);
        GL.Enable(EnableCap.ColorMaterial);
        GL.Enable(EnableCap.Normalize);
        GL.Enable(EnableCap.Texture2D);

        GL.ColorMaterial(
            MaterialFace.FrontAndBack,
            ColorMaterialParameter.AmbientAndDiffuse
        );

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // pixels transparentes não escrevem profundidade (senão viram "buracos" escuros)
        GL.Enable(EnableCap.AlphaTest);
        GL.AlphaFunc(AlphaFunction.Greater, 0.1f);

        // faces com normal invertida também recebem luz (antes ficavam pretas)
        GL.LightModel(LightModelParameter.LightModelTwoSide, 1);

        GL.ShadeModel(ShadingModel.Smooth);
        GL.Disable(EnableCap.CullFace);

        GL.EnableClientState(ArrayCap.VertexArray);
        GL.EnableClientState(ArrayCap.NormalArray);
        GL.EnableClientState(ArrayCap.TextureCoordArray);
        GL.DisableClientState(ArrayCap.ColorArray);

        GL.TexEnv(
            TextureEnvTarget.TextureEnv,
            TextureEnvParameter.TextureEnvMode,
            (int)TextureEnvMode.Modulate
        );

        GL.Viewport(0, 0, Width, Height);

        glReady = true;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (!IsHandleCreated || !glReady || Height <= 0)
            return;

        MakeCurrent();
        GL.Viewport(0, 0, Width, Height);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (!IsHandleCreated || !glReady || Width <= 0 || Height <= 0)
            return;

        MakeCurrent();

        GL.Clear(
            ClearBufferMask.ColorBufferBit |
            ClearBufferMask.DepthBufferBit
        );

        GL.MatrixMode(MatrixMode.Projection);

        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(60f),
            (float)Width / Height,
            0.1f,
            20000f
        );

        GL.LoadMatrix(ref projection);

        GL.MatrixMode(MatrixMode.Modelview);

        float ry = MathHelper.DegreesToRadians(yaw);
        float rp = MathHelper.DegreesToRadians(pitch);

        Vector3 cameraPosition = target + new Vector3(
            distance * MathF.Cos(rp) * MathF.Cos(ry),
            distance * MathF.Cos(rp) * MathF.Sin(ry),
            distance * MathF.Sin(rp)
        );

        Matrix4 view = Matrix4.LookAt(
            cameraPosition,
            target,
            Vector3.UnitZ
        );

        GL.LoadMatrix(ref view);

        SetupLighting();
        DrawGrid();
        DrawAxes();
        DrawObjects();

        SwapBuffers();
    }

    private void SetupLighting()
    {
        float[] position = { -0.4f, -0.5f, 1f, 0f };
        float[] ambient = { 0.45f, 0.45f, 0.45f, 1f };
        float[] diffuse = { 0.75f, 0.75f, 0.75f, 1f };

        GL.Light(LightName.Light0, LightParameter.Position, position);
        GL.Light(LightName.Light0, LightParameter.Ambient, ambient);
        GL.Light(LightName.Light0, LightParameter.Diffuse, diffuse);
    }

    private void DrawGrid()
    {
        GL.Disable(EnableCap.Lighting);
        GL.Disable(EnableCap.Texture2D);

        GL.LineWidth(1f);

        float z = target.Z - 0.05f;

        GL.Begin(PrimitiveType.Lines);
        GL.Color3(0.28f, 0.33f, 0.38f);

        for (float i = -GridSize; i <= GridSize; i += GridStep)
        {
            GL.Vertex3(target.X + i, target.Y - GridSize, z);
            GL.Vertex3(target.X + i, target.Y + GridSize, z);

            GL.Vertex3(target.X - GridSize, target.Y + i, z);
            GL.Vertex3(target.X + GridSize, target.Y + i, z);
        }

        GL.End();

        GL.Enable(EnableCap.Texture2D);
        GL.Enable(EnableCap.Lighting);
    }

    private void DrawAxes()
    {
        GL.Disable(EnableCap.Lighting);
        GL.Disable(EnableCap.Texture2D);

        GL.LineWidth(3f);

        GL.Begin(PrimitiveType.Lines);

        GL.Color3(1f, 0f, 0f);
        GL.Vertex3(target.X, target.Y, target.Z);
        GL.Vertex3(target.X + 100f, target.Y, target.Z);

        GL.Color3(0f, 1f, 0f);
        GL.Vertex3(target.X, target.Y, target.Z);
        GL.Vertex3(target.X, target.Y + 100f, target.Z);

        GL.Color3(0f, 0.4f, 1f);
        GL.Vertex3(target.X, target.Y, target.Z);
        GL.Vertex3(target.X, target.Y, target.Z + 100f);

        GL.End();

        GL.LineWidth(1f);

        GL.Enable(EnableCap.Texture2D);
        GL.Enable(EnableCap.Lighting);
    }

    // =====================================================
    // CRIAR MALHA NA GPU
    // =====================================================

    private GpuMesh CreateGpuMesh(ModelGeometry geometry)
    {
        int vertexCount = geometry.Vertices.Length;

        var normals = new NVector3[vertexCount];

        for (int i = 0; i + 2 < geometry.Indices.Length; i += 3)
        {
            int a = geometry.Indices[i];
            int b = geometry.Indices[i + 1];
            int c = geometry.Indices[i + 2];

            if ((uint)a >= vertexCount ||
                (uint)b >= vertexCount ||
                (uint)c >= vertexCount)
                continue;

            NVector3 v0 = geometry.Vertices[a];
            NVector3 v1 = geometry.Vertices[b];
            NVector3 v2 = geometry.Vertices[c];

            NVector3 normal = NVector3.Cross(v1 - v0, v2 - v0);

            if (normal.LengthSquared() < 0.000001f)
                continue;

            normals[a] += normal;
            normals[b] += normal;
            normals[c] += normal;
        }

        float[] data = new float[vertexCount * 12];

        for (int i = 0; i < vertexCount; i++)
        {
            NVector3 p = geometry.Vertices[i];
            NVector3 n = normals[i];

            if (n.LengthSquared() > 0.000001f)
                n = NVector3.Normalize(n);
            else
                n = NVector3.UnitZ;

            NVector2 uv = geometry.UVs.Length > i
                ? geometry.UVs[i]
                : NVector2.Zero;

            NVector4 color = geometry.VertexColors.Length > i
                ? geometry.VertexColors[i]
                : NVector4.One;

            int o = i * 12;

            data[o] = p.X;
            data[o + 1] = p.Y;
            data[o + 2] = p.Z;

            data[o + 3] = n.X;
            data[o + 4] = n.Y;
            data[o + 5] = n.Z;

            data[o + 6] = uv.X;
            data[o + 7] = uv.Y;

            data[o + 8] = color.X;
            data[o + 9] = color.Y;
            data[o + 10] = color.Z;
            data[o + 11] = color.W;
        }

        var mesh = new GpuMesh
        {
            Vbo = GL.GenBuffer()
        };

        GL.BindBuffer(BufferTarget.ArrayBuffer, mesh.Vbo);

        GL.BufferData(
            BufferTarget.ArrayBuffer,
            (IntPtr)(data.Length * sizeof(float)),
            data,
            BufferUsageHint.StaticDraw
        );

        if (geometry.Submeshes.Count > 0)
        {
            foreach (var sub in geometry.Submeshes)
            {
                if (sub.Indices.Length == 0)
                    continue;

                int ebo = GL.GenBuffer();

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);

                GL.BufferData(
                    BufferTarget.ElementArrayBuffer,
                    (IntPtr)(sub.Indices.Length * sizeof(int)),
                    sub.Indices,
                    BufferUsageHint.StaticDraw
                );

                mesh.Submeshes.Add(new GpuSubmesh
                {
                    Ebo = ebo,
                    IndexCount = sub.Indices.Length,
                    MaterialIndex = sub.MaterialIndex
                });
            }
        }
        else if (geometry.Indices.Length > 0)
        {
            int ebo = GL.GenBuffer();

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);

            GL.BufferData(
                BufferTarget.ElementArrayBuffer,
                (IntPtr)(geometry.Indices.Length * sizeof(int)),
                geometry.Indices,
                BufferUsageHint.StaticDraw
            );

            mesh.Submeshes.Add(new GpuSubmesh
            {
                Ebo = ebo,
                IndexCount = geometry.Indices.Length,
                MaterialIndex = -1
            });
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);

        return mesh;
    }

    // =====================================================
    // CONVERSÃO DA TEXTURA -> RGBA (veja Core/TextureDecoder.cs)
    // =====================================================

    private static byte[] DecodeDxt(TxdTexture texture) => TextureDecoder.ToRgba(texture);

    // =====================================================
    // ENVIAR TEXTURA PARA GPU
    // =====================================================

    private int GetGpuTexture(string model, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0;

        // a chave inclui o TXD do modelo: o mesmo nome de textura existe em TXD diferentes
        string key = Assets.GetModelTxd(model) + "/" +
                     System.IO.Path.GetFileNameWithoutExtension(name.Trim());

        if (textureCache.TryGetValue(key, out int cached))
            return cached;

        TxdTexture? texture = Assets.GetTexture(model, name);

        if (texture == null || texture.Width <= 0 || texture.Height <= 0)
            return 0;

        try
        {
            byte[] pixels = DecodeDxt(texture);

            long expected = (long)texture.Width * texture.Height * 4;
            if (expected <= 0 || expected > int.MaxValue || pixels.Length != expected)
                return 0;

            int id = GL.GenTexture();

            GL.BindTexture(TextureTarget.Texture2D, id);

            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba,
                texture.Width,
                texture.Height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                pixels
            );

            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.LinearMipmapLinear
            );

            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear
            );

            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.Repeat
            );

            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.Repeat
            );

            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

            GL.BindTexture(TextureTarget.Texture2D, 0);

            textureCache[key] = id;

            return id;
        }
        catch
        {
            return 0;
        }
    }

    // =====================================================
    // DESENHAR OBJETOS
    // =====================================================

    private void DrawObjects()
    {
        GL.Enable(EnableCap.Texture2D);

        GL.EnableClientState(ArrayCap.VertexArray);
        GL.EnableClientState(ArrayCap.NormalArray);
        GL.EnableClientState(ArrayCap.TextureCoordArray);
        GL.DisableClientState(ArrayCap.ColorArray);

        foreach (var obj in Document.Objects)
        {
            var geometry = Assets.GetGeometry(obj.ModelName);

            if (geometry == null ||
                geometry.Vertices.Length == 0 ||
                geometry.Indices.Length < 3)
                continue;

            if (!gpuCache.TryGetValue(geometry, out var mesh))
            {
                mesh = CreateGpuMesh(geometry);
                gpuCache.Add(geometry, mesh);
            }

            GL.PushMatrix();

            GL.Translate(obj.X, obj.Y, obj.Z);

            var q = obj.Orientation;

            float qw = Math.Clamp(q.W, -1f, 1f);
            float angle = 2f * MathF.Acos(qw);
            float sin = MathF.Sqrt(Math.Max(0f, 1f - qw * qw));

            if (sin > 0.0001f && angle > 0.0001f)
            {
                GL.Rotate(
                    MathHelper.RadiansToDegrees(angle),
                    q.X / sin,
                    q.Y / sin,
                    q.Z / sin
                );
            }

            GL.BindBuffer(BufferTarget.ArrayBuffer, mesh.Vbo);

            GL.VertexPointer(
                3,
                VertexPointerType.Float,
                VertexStride,
                IntPtr.Zero
            );

            GL.NormalPointer(
                NormalPointerType.Float,
                VertexStride,
                (IntPtr)(3 * sizeof(float))
            );

            GL.TexCoordPointer(
                2,
                TexCoordPointerType.Float,
                VertexStride,
                (IntPtr)(6 * sizeof(float))
            );

            GL.ColorPointer(
                4,
                ColorPointerType.Float,
                VertexStride,
                (IntPtr)(8 * sizeof(float))
            );

            bool selected = ReferenceEquals(obj, Selected);

            // Cores "prelit" do DFF são as cores NOTURNAS (muitas vezes pretas): ignoradas.
            GL.DisableClientState(ArrayCap.ColorArray);

            foreach (var sub in mesh.Submeshes)
            {
                ModelMaterial? material = null;

                if (sub.MaterialIndex >= 0 &&
                    sub.MaterialIndex < geometry.Materials.Count)
                {
                    material = geometry.Materials[sub.MaterialIndex];
                }

                int textureId = 0;

                if (!selected && material != null &&
                    !string.IsNullOrWhiteSpace(material.TextureName))
                {
                    textureId = GetGpuTexture(obj.ModelName, material.TextureName);
                }

                if (textureId != 0)
                {
                    GL.Enable(EnableCap.Texture2D);
                    GL.BindTexture(TextureTarget.Texture2D, textureId);
                }
                else
                {
                    GL.BindTexture(TextureTarget.Texture2D, 0);
                    GL.Disable(EnableCap.Texture2D);
                }

                if (selected)
                {
                    GL.Color4(1f, 0.55f, 0.05f, 1f);
                }
                else if (material != null)
                {
                    GL.Color4(
                        material.Color.X,
                        material.Color.Y,
                        material.Color.Z,
                        material.Color.W
                    );
                }
                else
                {
                    GL.Color4(0.78f, 0.78f, 0.78f, 1f);
                }

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, sub.Ebo);

                GL.DrawElements(
                    PrimitiveType.Triangles,
                    sub.IndexCount,
                    DrawElementsType.UnsignedInt,
                    IntPtr.Zero
                );
            }

            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.DisableClientState(ArrayCap.ColorArray);

            GL.PopMatrix();
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);

        GL.Enable(EnableCap.Texture2D);
    }

    // =====================================================
    // CÂMERA
    // =====================================================

    public void FitToObjects()
    {
        if (Document.Objects.Count == 0)
        {
            target = Vector3.Zero;
            distance = 100f;
            Invalidate();
            return;
        }

        float minX = Document.Objects.Min(o => o.X);
        float maxX = Document.Objects.Max(o => o.X);

        float minY = Document.Objects.Min(o => o.Y);
        float maxY = Document.Objects.Max(o => o.Y);

        float minZ = Document.Objects.Min(o => o.Z);
        float maxZ = Document.Objects.Max(o => o.Z);

        target = new Vector3(
            (minX + maxX) / 2f,
            (minY + maxY) / 2f,
            (minZ + maxZ) / 2f
        );

        float size = Math.Max(
            Math.Max(maxX - minX, maxY - minY),
            maxZ - minZ
        );

        distance = Math.Clamp(size * 1.5f, 20f, 10000f);

        yaw = 45f;
        pitch = 35f;

        Invalidate();
    }

    public void FocusOn(MapObject obj)
    {
        target = new Vector3(obj.X, obj.Y, obj.Z);
        distance = 50f;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        lastMouse = e.Location;

        if (e.Button == MouseButtons.Right ||
            e.Button == MouseButtons.Middle)
            rotating = true;

        if (e.Button == MouseButtons.Left)
            panning = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        float dx = e.X - lastMouse.X;
        float dy = e.Y - lastMouse.Y;

        if (rotating)
        {
            yaw -= dx * 0.5f;
            pitch = Math.Clamp(pitch + dy * 0.5f, 2f, 89f);
            Invalidate();
        }

        if (panning)
        {
            float ry = MathHelper.DegreesToRadians(yaw);

            Vector3 right = new(
                -MathF.Sin(ry),
                MathF.Cos(ry),
                0f
            );

            float speed = distance / 500f;

            target -= right * dx * speed;
            target += Vector3.UnitZ * dy * speed;

            Invalidate();
        }

        CursorMoved?.Invoke(e.X, e.Y);

        lastMouse = e.Location;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        rotating = false;
        panning = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        distance *= e.Delta > 0 ? 0.85f : 1.15f;
        distance = Math.Clamp(distance, 2f, 20000f);

        Invalidate();
    }

    // =====================================================
    // LIBERAR RECURSOS GPU
    // =====================================================

    public void ReloadTextures()
    {
        if (!glReady || !IsHandleCreated)
            return;

        MakeCurrent();

        foreach (var texture in textureCache.Values)
            GL.DeleteTexture(texture);

        textureCache.Clear();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && glReady && IsHandleCreated)
        {
            try
            {
                MakeCurrent();

                foreach (var mesh in gpuCache.Values)
                {
                    GL.DeleteBuffer(mesh.Vbo);

                    foreach (var sub in mesh.Submeshes)
                        GL.DeleteBuffer(sub.Ebo);
                }

                foreach (var texture in textureCache.Values)
                    GL.DeleteTexture(texture);

                gpuCache.Clear();
                textureCache.Clear();
            }
            catch
            {
                // Contexto OpenGL encerrado.
            }
        }

        base.Dispose(disposing);
    }
}