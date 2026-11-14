using System;
using System.Collections.Generic;
using System.Text;

namespace ZeusMapEditor.Core;

public sealed class TxdTexture
{
    public string Name = "";
    public string MaskName = "";

    public int Width;
    public int Height;
    public int Depth;

    public int Platform;          // 8 = D3D8, 9 = D3D9
    public uint RasterFormat;     // flags: 0x0100 1555, 0x0200 565, 0x0300 4444, 0x0400 LUM8, 0x0500 8888, 0x0600 888, 0x0A00 555, 0x2000 PAL8, 0x4000 PAL4
    public uint D3DFormat;        // D3D9: FOURCC (DXT1...) ou formato D3D
    public uint Alpha;            // D3D8: tem alpha
    public int MipLevels;
    public byte Compression;      // D3D8: tipo DXT (1..5)
    public byte RasterType;

    public byte[] Palette = Array.Empty<byte>();   // entradas de 4 bytes
    public byte[] Data = Array.Empty<byte>();      // só o mipmap 0

    public const uint FourCC_DXT1 = 0x31545844;
    public const uint FourCC_DXT2 = 0x32545844;
    public const uint FourCC_DXT3 = 0x33545844;
    public const uint FourCC_DXT4 = 0x34545844;
    public const uint FourCC_DXT5 = 0x35545844;

    /// <summary>0 = sem compressão, 1 = DXT1, 3 = DXT2/3, 5 = DXT4/5.</summary>
    public int DxtKind
    {
        get
        {
            switch (D3DFormat)
            {
                case FourCC_DXT1: return 1;
                case FourCC_DXT2: case FourCC_DXT3: return 3;
                case FourCC_DXT4: case FourCC_DXT5: return 5;
            }
            if (Platform == 8)
            {
                switch (Compression)
                {
                    case 1: return 1;
                    case 2: case 3: return 3;
                    case 4: case 5: return 5;
                }
            }
            return 0;
        }
    }

    public bool IsDxt => DxtKind != 0;
    public bool IsPaletted => Palette.Length > 0;
}

/// <summary>
/// Leitor de TXD (RenderWare). Layout do bloco nativo D3D8/D3D9 (88 bytes de cabeçalho):
/// platform, filtro, nome[32], máscara[32], rasterFormat, d3dFormat/alpha, largura, altura,
/// profundidade, mips, tipo, compressão — depois a PALETA (se houver) e, para cada mip,
/// um uint32 com o tamanho seguido dos pixels.
/// </summary>
public static class TxdReader
{
    private const uint STRUCT = 0x01;
    private const uint TEXTURE_DICTIONARY = 0x16;
    private const uint NATIVE_TEXTURE = 0x15;

    private readonly struct Chunk
    {
        public readonly uint Type;
        public readonly int Start;
        public readonly int End;
        public Chunk(uint type, int start, int end) { Type = type; Start = start; End = end; }
    }

    private static bool TryChunk(byte[] data, int position, int limit, out Chunk chunk)
    {
        chunk = default;
        if (position < 0 || limit > data.Length || position > limit - 12) return false;
        uint type = BitConverter.ToUInt32(data, position);
        uint size = BitConverter.ToUInt32(data, position + 4);
        long end = (long)position + 12L + size;
        if (end > limit || end < position + 12) return false;
        chunk = new Chunk(type, position + 12, (int)end);
        return true;
    }

    public static List<TxdTexture> Read(byte[] data)
    {
        var textures = new List<TxdTexture>();
        if (data == null || data.Length < 12) return textures;
        try { ReadChunks(data, 0, data.Length, textures); }
        catch { /* arquivo truncado: devolve o que leu */ }
        return textures;
    }

    private static void ReadChunks(byte[] data, int start, int end, List<TxdTexture> textures)
    {
        int position = start;
        while (position <= end - 12)
        {
            if (!TryChunk(data, position, end, out var chunk)) break;
            if (chunk.Type == TEXTURE_DICTIONARY) ReadChunks(data, chunk.Start, chunk.End, textures);
            else if (chunk.Type == NATIVE_TEXTURE)
            {
                TxdTexture? t = null;
                try { t = ReadNativeTexture(data, chunk.Start, chunk.End); } catch { }
                if (t != null) textures.Add(t);
            }
            if (chunk.End <= position) break;
            position = chunk.End;
        }
    }

    private static long ExpectedSize(int w, int h, int dxt, int paletteBits, int depth)
    {
        if (dxt != 0) return Math.Max(1L, (w + 3) / 4) * Math.Max(1L, (h + 3) / 4) * (dxt == 1 ? 8 : 16);
        if (paletteBits == 8) return (long)w * h;
        if (paletteBits == 4) return ((long)w * h + 1) / 2;
        return (long)w * h * Math.Max(1, depth / 8);
    }

    private static TxdTexture? ReadNativeTexture(byte[] data, int start, int end)
    {
        if (!TryChunk(data, start, end, out var structure) || structure.Type != STRUCT) return null;
        int p = structure.Start;
        int limit = structure.End;
        if (limit - p < 88) return null;

        int platform = BitConverter.ToInt32(data, p);
        if (platform != 8 && platform != 9) return null;

        string name = ReadString(data, p + 8, 32);
        string mask = ReadString(data, p + 40, 32);
        if (string.IsNullOrWhiteSpace(name)) return null;

        uint raster = BitConverter.ToUInt32(data, p + 72);
        uint field76 = BitConverter.ToUInt32(data, p + 76);
        int width = BitConverter.ToUInt16(data, p + 80);
        int height = BitConverter.ToUInt16(data, p + 82);
        int depth = data[p + 84];
        int mips = data[p + 85];
        byte rasterType = data[p + 86];
        byte compression = data[p + 87];

        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return null;
        if (mips < 1) mips = 1;

        var tex = new TxdTexture
        {
            Name = name, MaskName = mask, Platform = platform,
            Width = width, Height = height, Depth = depth,
            RasterFormat = raster,
            D3DFormat = platform == 9 ? field76 : 0,
            Alpha = platform == 8 ? field76 : 0,
            MipLevels = mips, Compression = compression, RasterType = rasterType
        };

        int dxt = tex.DxtKind;

        // Paleta: pelas flags do raster (0x2000 = 8 bits, 0x4000 = 4 bits); sem DXT.
        int paletteBits = 0;
        if (dxt == 0)
        {
            if ((raster & 0x2000) != 0) paletteBits = 8;
            else if ((raster & 0x4000) != 0) paletteBits = 4;
            else if (depth == 8 && (raster & 0x0F00) != 0x0400) paletteBits = 8;   // 8 bits que não é luminância
            else if (depth == 4) paletteBits = 4;
        }
        int paletteBytes = paletteBits == 8 ? 1024 : paletteBits == 4 ? 128 : 0;
        long expected = ExpectedSize(width, height, dxt, paletteBits, depth);

        int q = p + 88;
        byte[] palette = Array.Empty<byte>();
        int pixelStart;
        int pixelSize;

        if (paletteBytes > 0)
        {
            if (q + paletteBytes + 4 > limit) return null;
            int sizeA = BitConverter.ToInt32(data, q + paletteBytes);   // layout A: paleta, depois [tamanho, pixels]
            int sizeB = BitConverter.ToInt32(data, q);                  // layout B: [tamanho, paleta+pixels]
            if (sizeA == expected)
            {
                palette = Slice(data, q, paletteBytes);
                pixelStart = q + paletteBytes + 4; pixelSize = sizeA;
            }
            else if (sizeB == paletteBytes + expected)
            {
                palette = Slice(data, q + 4, paletteBytes);
                pixelStart = q + 4 + paletteBytes; pixelSize = (int)(sizeB - paletteBytes);
            }
            else
            {
                palette = Slice(data, q, paletteBytes);
                pixelStart = q + paletteBytes + 4; pixelSize = sizeA;
            }
        }
        else
        {
            if (q + 4 > limit) return null;
            pixelSize = BitConverter.ToInt32(data, q);
            pixelStart = q + 4;
        }

        if (pixelSize <= 0 || pixelStart < 0 || pixelStart > limit || pixelSize > limit - pixelStart) return null;

        tex.Palette = palette;
        tex.Data = Slice(data, pixelStart, pixelSize);
        return tex;
    }

    private static byte[] Slice(byte[] src, int start, int len)
    {
        var b = new byte[len];
        Buffer.BlockCopy(src, start, b, 0, len);
        return b;
    }

    private static string ReadString(byte[] data, int position, int length)
    {
        if (position < 0 || length < 0 || position > data.Length - length) return "";
        int count = 0;
        while (count < length && data[position + count] != 0) count++;
        return Encoding.ASCII.GetString(data, position, count).Trim();
    }
}
