using System;

namespace ZeusMapEditor.Core;

/// <summary>Converte uma textura do TXD para RGBA de 8 bits por canal (primeira linha = topo da imagem).</summary>
public static class TextureDecoder
{
    /// <summary>
    /// Ordem das cores da paleta. false = R,G,B,A (padrão do RenderWare). Se as texturas com paleta
    /// aparecerem com vermelho e azul trocados, use true.
    /// </summary>
    public static bool PaletteIsBgra = false;

    public static byte[] ToRgba(TxdTexture t)
    {
        int w = t.Width, h = t.Height;
        if (w <= 0 || h <= 0 || (long)w * h > int.MaxValue / 4) return Array.Empty<byte>();
        var o = new byte[w * h * 4];
        int dxt = t.DxtKind;
        if (dxt != 0) DecodeDxt(t, dxt, o);
        else if (t.Palette.Length > 0) DecodePalette(t, o);
        else DecodeRaw(t, o);
        return o;
    }

    // ------------------------------------------------------------------ DXT
    private static void DecodeDxt(TxdTexture t, int kind, byte[] o)
    {
        int w = t.Width, h = t.Height;
        var s = t.Data;
        int blockBytes = kind == 1 ? 8 : 16;
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        int off = 0;
        var alpha = new byte[16];
        var cr = new int[4]; var cg = new int[4]; var cb = new int[4]; var ca = new int[4];

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                if (off + blockBytes > s.Length) return;
                int co = off;
                if (kind == 3)
                {
                    ulong bits = BitConverter.ToUInt64(s, off);
                    for (int i = 0; i < 16; i++) alpha[i] = (byte)(((bits >> (i * 4)) & 15) * 17);
                    co += 8;
                }
                else if (kind == 5) { Dxt5Alpha(s, off, alpha); co += 8; }
                else Array.Fill(alpha, (byte)255);

                ushort c0 = BitConverter.ToUInt16(s, co), c1 = BitConverter.ToUInt16(s, co + 2);
                uint bitsC = BitConverter.ToUInt32(s, co + 4);
                Rgb565(c0, out int r0, out int g0, out int b0);
                Rgb565(c1, out int r1, out int g1, out int b1);
                cr[0] = r0; cg[0] = g0; cb[0] = b0; ca[0] = 255;
                cr[1] = r1; cg[1] = g1; cb[1] = b1; ca[1] = 255;
                if (kind == 1 && c0 <= c1)
                {
                    cr[2] = (r0 + r1) / 2; cg[2] = (g0 + g1) / 2; cb[2] = (b0 + b1) / 2; ca[2] = 255;
                    cr[3] = 0; cg[3] = 0; cb[3] = 0; ca[3] = 0;     // transparente
                }
                else
                {
                    cr[2] = (2 * r0 + r1) / 3; cg[2] = (2 * g0 + g1) / 3; cb[2] = (2 * b0 + b1) / 3; ca[2] = 255;
                    cr[3] = (r0 + 2 * r1) / 3; cg[3] = (g0 + 2 * g1) / 3; cb[3] = (b0 + 2 * b1) / 3; ca[3] = 255;
                }

                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int px = bx * 4 + x, py = by * 4 + y;
                        if (px >= w || py >= h) continue;
                        int p = y * 4 + x;
                        int ci = (int)((bitsC >> (p * 2)) & 3);
                        int d = (py * w + px) * 4;
                        o[d] = (byte)cr[ci]; o[d + 1] = (byte)cg[ci]; o[d + 2] = (byte)cb[ci];
                        o[d + 3] = kind == 1 ? (byte)ca[ci] : alpha[p];
                    }
                off += blockBytes;
            }
    }

    private static void Rgb565(ushort v, out int r, out int g, out int b)
    {
        r = ((v >> 11) & 31) * 255 / 31;
        g = ((v >> 5) & 63) * 255 / 63;
        b = (v & 31) * 255 / 31;
    }

    private static void Dxt5Alpha(byte[] s, int off, byte[] alpha)
    {
        int a0 = s[off], a1 = s[off + 1];
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)s[off + 2 + i] << (8 * i);
        var tab = new int[8];
        tab[0] = a0; tab[1] = a1;
        if (a0 > a1) for (int i = 1; i <= 6; i++) tab[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else { for (int i = 1; i <= 4; i++) tab[i + 1] = ((5 - i) * a0 + i * a1) / 5; tab[6] = 0; tab[7] = 255; }
        for (int i = 0; i < 16; i++) alpha[i] = (byte)tab[(int)((bits >> (3 * i)) & 7)];
    }

    // ------------------------------------------------------------------ paleta
    private static void DecodePalette(TxdTexture t, byte[] o)
    {
        int w = t.Width, h = t.Height, n = w * h;
        var s = t.Data; var pal = t.Palette;
        bool four = (t.RasterFormat & 0x4000) != 0 || (t.RasterFormat & 0x2000) == 0 && t.Depth == 4;
        int entries = pal.Length / 4;

        bool anyAlpha = false;
        for (int i = 0; i < entries; i++) if (pal[i * 4 + 3] != 0) { anyAlpha = true; break; }

        for (int i = 0; i < n; i++)
        {
            int idx;
            if (four) { if ((i >> 1) >= s.Length) break; byte b = s[i >> 1]; idx = (i & 1) == 0 ? b & 15 : b >> 4; }
            else { if (i >= s.Length) break; idx = s[i]; }
            if (idx >= entries) continue;
            int pe = idx * 4, d = i * 4;
            if (PaletteIsBgra) { o[d] = pal[pe + 2]; o[d + 1] = pal[pe + 1]; o[d + 2] = pal[pe]; }
            else { o[d] = pal[pe]; o[d + 1] = pal[pe + 1]; o[d + 2] = pal[pe + 2]; }
            o[d + 3] = anyAlpha ? pal[pe + 3] : (byte)255;   // paleta com alpha todo zero = opaca
        }
    }

    // ------------------------------------------------------------------ sem compressão
    private static void DecodeRaw(TxdTexture t, byte[] o)
    {
        int w = t.Width, h = t.Height, n = w * h;
        var s = t.Data;
        uint fmt = t.RasterFormat & 0x0F00;

        if (fmt == 0x0400 || (t.Depth == 8 && fmt != 0x0500 && fmt != 0x0600))   // luminância
        {
            for (int i = 0; i < n && i < s.Length; i++) { int d = i * 4; o[d] = o[d + 1] = o[d + 2] = s[i]; o[d + 3] = 255; }
            return;
        }

        if (t.Depth == 16 || fmt == 0x0100 || fmt == 0x0200 || fmt == 0x0300 || fmt == 0x0A00)
        {
            for (int i = 0; i < n && i * 2 + 1 < s.Length; i++)
            {
                ushort v = BitConverter.ToUInt16(s, i * 2);
                int d = i * 4;
                if (fmt == 0x0100)        // 1555
                { o[d] = (byte)(((v >> 10) & 31) * 255 / 31); o[d + 1] = (byte)(((v >> 5) & 31) * 255 / 31); o[d + 2] = (byte)((v & 31) * 255 / 31); o[d + 3] = (v & 0x8000) != 0 ? (byte)255 : (byte)0; }
                else if (fmt == 0x0300)   // 4444
                { o[d] = (byte)(((v >> 8) & 15) * 17); o[d + 1] = (byte)(((v >> 4) & 15) * 17); o[d + 2] = (byte)((v & 15) * 17); o[d + 3] = (byte)(((v >> 12) & 15) * 17); }
                else if (fmt == 0x0A00)   // 555
                { o[d] = (byte)(((v >> 10) & 31) * 255 / 31); o[d + 1] = (byte)(((v >> 5) & 31) * 255 / 31); o[d + 2] = (byte)((v & 31) * 255 / 31); o[d + 3] = 255; }
                else                      // 565
                { o[d] = (byte)(((v >> 11) & 31) * 255 / 31); o[d + 1] = (byte)(((v >> 5) & 63) * 255 / 63); o[d + 2] = (byte)((v & 31) * 255 / 31); o[d + 3] = 255; }
            }
            return;
        }

        // 32 bits (B,G,R,A) ou 24 bits (B,G,R)
        int bpp = s.Length >= (long)n * 4 ? 4 : 3;
        bool hasAlpha = fmt != 0x0600;
        if (hasAlpha)
        {   // alpha todo zero = formato X8R8G8B8 (sem alpha de verdade)
            hasAlpha = false;
            for (int i = 0; i < n && i * bpp + 3 < s.Length && bpp == 4; i++) if (s[i * 4 + 3] != 0) { hasAlpha = true; break; }
        }
        for (int i = 0; i < n && (long)(i + 1) * bpp <= s.Length; i++)
        {
            int sp = i * bpp, d = i * 4;
            o[d] = s[sp + 2]; o[d + 1] = s[sp + 1]; o[d + 2] = s[sp];
            o[d + 3] = hasAlpha ? s[sp + 3] : (byte)255;
        }
    }
}
