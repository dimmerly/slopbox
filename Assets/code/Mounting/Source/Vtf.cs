using System;
using System.IO;
using System.Threading.Tasks;

namespace Slopbox.Mounting.Source;

/// <summary>A decoded Valve texture: the top mip of its first frame and face, as RGBA bytes, top row first.</summary>
internal sealed class VtfImage
{
    public int Width, Height;
    public byte[] Rgba;
    public uint Flags;

    /// <summary>True if any texel is not fully opaque.</summary>
    public bool HasAlpha;

    public const uint FlagEnvMap = 0x4000;
    public const uint FlagOneBitAlpha = 0x1000;
    public const uint FlagEightBitAlpha = 0x2000;
    public const uint FlagNoMip = 0x100;
}

/// <summary>Reads Valve texture files (.vtf), versions 7.0 to 7.5, decoding DXT and the plain formats on the CPU.</summary>
internal static class Vtf
{
    private const uint Signature = 0x00465456; // "VTF\0"
    private const uint ImageResource = 0x30;

    private enum Format
    {
        RGBA8888 = 0, ABGR8888, RGB888, BGR888, RGB565, I8, IA88, P8, A8, RGB888Bluescreen, BGR888Bluescreen,
        ARGB8888, BGRA8888, DXT1, DXT3, DXT5, BGRX8888, BGR565, BGRX5551, BGRA4444, DXT1OneBitAlpha, BGRA5551,
        UV88, UVWQ8888, RGBA16161616F, RGBA16161616, UVLX8888,
    }

    public static VtfImage Decode(byte[] data)
    {
        if (data == null || data.Length < 64) return null;

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        if (reader.ReadUInt32() != Signature) return null;
        int major = reader.ReadInt32(), minor = reader.ReadInt32();
        int headerSize = reader.ReadInt32();
        if (major != 7) return null;

        int width = reader.ReadUInt16(), height = reader.ReadUInt16();
        uint flags = reader.ReadUInt32();
        int frames = Math.Max(1, (int)reader.ReadUInt16());
        reader.ReadUInt16(); // first frame
        ms.Seek(4 + 12 + 4 + 4, SeekOrigin.Current); // padding, reflectivity, padding, bump scale
        var format = (Format)reader.ReadInt32();
        int mips = reader.ReadByte();
        int lowFormat = reader.ReadInt32();
        int lowWidth = reader.ReadByte(), lowHeight = reader.ReadByte();
        int depth = minor >= 2 ? Math.Max(1, (int)reader.ReadUInt16()) : 1;

        if (width <= 0 || height <= 0 || mips <= 0) return null;

        long imageOffset;
        if (minor >= 3)
        {
            ms.Seek(3, SeekOrigin.Current);
            int resources = reader.ReadInt32();
            ms.Seek(0x50, SeekOrigin.Begin);
            imageOffset = -1;
            for (int i = 0; i < resources; i++)
            {
                uint type = reader.ReadUInt32();
                uint offset = reader.ReadUInt32();
                if ((type & 0x00FFFFFF) == ImageResource) imageOffset = offset;
            }
            if (imageOffset < 0) return null;
        }
        else
        {
            int lowSize = lowWidth > 0 && lowHeight > 0 && lowFormat >= 0 ? Size(lowWidth, lowHeight, (Format)lowFormat) : 0;
            imageOffset = headerSize + lowSize;
        }

        // Environment maps have six faces, seven in 7.1 to 7.4 files that kept a sphere map.
        int faces = 1;
        if ((flags & VtfImage.FlagEnvMap) != 0)
        {
            faces = 6;
            if (minor is >= 1 and < 5)
            {
                long with7 = 0;
                for (int m = 0; m < mips; m++) with7 += (long)Size(Math.Max(1, width >> m), Math.Max(1, height >> m), format) * frames * 7 * depth;
                if (imageOffset + with7 <= data.Length) faces = 7;
            }
        }

        // Mips run smallest first; the largest is last.
        long skip = 0;
        for (int m = mips - 1; m >= 1; m--)
            skip += (long)Size(Math.Max(1, width >> m), Math.Max(1, height >> m), format) * frames * faces * depth;

        long start = imageOffset + skip;
        int size = Size(width, height, format);
        if (size <= 0 || start + size > data.Length) return null;

        byte[] rgba = DecodePixels(data, (int)start, width, height, format);
        if (rgba == null) return null;

        bool hasAlpha = false;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 255)
            {
                hasAlpha = true;
                break;
            }
        }

        return new VtfImage { Width = width, Height = height, Rgba = rgba, Flags = flags, HasAlpha = hasAlpha };
    }

    private static int Size(int w, int h, Format format)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        return format switch
        {
            Format.DXT1 or Format.DXT1OneBitAlpha => bw * bh * 8,
            Format.DXT3 or Format.DXT5 => bw * bh * 16,
            Format.RGBA8888 or Format.ABGR8888 or Format.ARGB8888 or Format.BGRA8888 or Format.BGRX8888
                or Format.UVWQ8888 or Format.UVLX8888 => w * h * 4,
            Format.RGB888 or Format.BGR888 or Format.RGB888Bluescreen or Format.BGR888Bluescreen => w * h * 3,
            Format.RGB565 or Format.BGR565 or Format.BGRX5551 or Format.BGRA5551 or Format.BGRA4444 or Format.IA88
                or Format.UV88 => w * h * 2,
            Format.I8 or Format.A8 or Format.P8 => w * h,
            Format.RGBA16161616F or Format.RGBA16161616 => w * h * 8,
            _ => 0,
        };
    }

    private static byte[] DecodePixels(byte[] src, int at, int w, int h, Format format)
    {
        var dst = new byte[w * h * 4];
        switch (format)
        {
            case Format.DXT1:
            case Format.DXT1OneBitAlpha:
            case Format.DXT3:
            case Format.DXT5:
                DecodeDxt(src, at, w, h, format, dst);
                return dst;
        }

        int n = w * h;
        for (int i = 0, o = 0; i < n; i++, o += 4)
        {
            byte r, g, b, a = 255;
            switch (format)
            {
                case Format.RGBA8888: { int p = at + i * 4; r = src[p]; g = src[p + 1]; b = src[p + 2]; a = src[p + 3]; break; }
                case Format.ABGR8888: { int p = at + i * 4; a = src[p]; b = src[p + 1]; g = src[p + 2]; r = src[p + 3]; break; }
                case Format.ARGB8888: { int p = at + i * 4; a = src[p]; r = src[p + 1]; g = src[p + 2]; b = src[p + 3]; break; }
                case Format.BGRA8888: { int p = at + i * 4; b = src[p]; g = src[p + 1]; r = src[p + 2]; a = src[p + 3]; break; }
                case Format.BGRX8888: { int p = at + i * 4; b = src[p]; g = src[p + 1]; r = src[p + 2]; break; }
                case Format.UVWQ8888:
                case Format.UVLX8888: { int p = at + i * 4; r = src[p]; g = src[p + 1]; b = src[p + 2]; a = src[p + 3]; break; }
                case Format.RGB888: { int p = at + i * 3; r = src[p]; g = src[p + 1]; b = src[p + 2]; break; }
                case Format.BGR888: { int p = at + i * 3; b = src[p]; g = src[p + 1]; r = src[p + 2]; break; }
                case Format.RGB888Bluescreen:
                case Format.BGR888Bluescreen:
                {
                    int p = at + i * 3;
                    bool rgb = format == Format.RGB888Bluescreen;
                    r = src[rgb ? p : p + 2]; g = src[p + 1]; b = src[rgb ? p + 2 : p];
                    if (r == 0 && g == 0 && b == 255) { b = 0; a = 0; }
                    break;
                }
                case Format.RGB565:
                case Format.BGR565:
                {
                    int v = src[at + i * 2] | (src[at + i * 2 + 1] << 8);
                    byte hi = (byte)(((v >> 11) & 31) * 255 / 31), mid = (byte)(((v >> 5) & 63) * 255 / 63), lo = (byte)((v & 31) * 255 / 31);
                    if (format == Format.RGB565) { r = lo; g = mid; b = hi; } else { r = hi; g = mid; b = lo; }
                    break;
                }
                case Format.BGRX5551:
                case Format.BGRA5551:
                {
                    int v = src[at + i * 2] | (src[at + i * 2 + 1] << 8);
                    b = (byte)((v & 31) * 255 / 31); g = (byte)(((v >> 5) & 31) * 255 / 31); r = (byte)(((v >> 10) & 31) * 255 / 31);
                    if (format == Format.BGRA5551) a = (v & 0x8000) != 0 ? (byte)255 : (byte)0;
                    break;
                }
                case Format.BGRA4444:
                {
                    int v = src[at + i * 2] | (src[at + i * 2 + 1] << 8);
                    b = (byte)((v & 15) * 17); g = (byte)(((v >> 4) & 15) * 17); r = (byte)(((v >> 8) & 15) * 17); a = (byte)(((v >> 12) & 15) * 17);
                    break;
                }
                case Format.I8: r = g = b = src[at + i]; break;
                case Format.P8: r = g = b = src[at + i]; break;
                case Format.A8: r = g = b = 255; a = src[at + i]; break;
                case Format.IA88: r = g = b = src[at + i * 2]; a = src[at + i * 2 + 1]; break;
                case Format.UV88: r = src[at + i * 2]; g = src[at + i * 2 + 1]; b = 255; break;
                case Format.RGBA16161616F:
                {
                    int p = at + i * 8;
                    r = HalfToByte(src, p); g = HalfToByte(src, p + 2); b = HalfToByte(src, p + 4); a = HalfToByte(src, p + 6);
                    break;
                }
                case Format.RGBA16161616:
                {
                    int p = at + i * 8;
                    r = src[p + 1]; g = src[p + 3]; b = src[p + 5]; a = src[p + 7];
                    break;
                }
                default:
                    return null;
            }
            dst[o] = r; dst[o + 1] = g; dst[o + 2] = b; dst[o + 3] = a;
        }
        return dst;
    }

    private static byte HalfToByte(byte[] src, int p)
    {
        float v = (float)BitConverter.ToHalf(src, p);
        // HDR textures are lit linearly; a simple curve keeps them in range.
        v = v / (1f + v);
        return (byte)Math.Clamp((int)(MathF.Pow(v, 1f / 2.2f) * 255f + 0.5f), 0, 255);
    }

    // ----------------------------------------------------------------
    //  DXT
    // ----------------------------------------------------------------

    private static void DecodeDxt(byte[] src, int at, int w, int h, Format format, byte[] dst)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        int blockSize = format is Format.DXT1 or Format.DXT1OneBitAlpha ? 8 : 16;

        // Rows of blocks are independent; big textures decode on every core.
        void Row(int by)
        {
            Span<uint> colors = stackalloc uint[4];
            Span<byte> alphas = stackalloc byte[8];
            for (int bx = 0; bx < bw; bx++)
            {
                int block = at + (by * bw + bx) * blockSize;
                int colorBlock = blockSize == 16 ? block + 8 : block;

                ushort c0 = (ushort)(src[colorBlock] | (src[colorBlock + 1] << 8));
                ushort c1 = (ushort)(src[colorBlock + 2] | (src[colorBlock + 3] << 8));
                colors[0] = Rgb565(c0);
                colors[1] = Rgb565(c1);
                bool fourColor = c0 > c1 || blockSize == 16;
                if (fourColor)
                {
                    colors[2] = Mix(colors[0], colors[1], 2, 1, 3);
                    colors[3] = Mix(colors[0], colors[1], 1, 2, 3);
                }
                else
                {
                    colors[2] = Mix(colors[0], colors[1], 1, 1, 2);
                    colors[3] = 0; // transparent black
                }
                uint bits = (uint)(src[colorBlock + 4] | (src[colorBlock + 5] << 8) | (src[colorBlock + 6] << 16) | (src[colorBlock + 7] << 24));

                ulong alphaBits = 0;
                if (format == Format.DXT5)
                {
                    alphas[0] = src[block];
                    alphas[1] = src[block + 1];
                    if (alphas[0] > alphas[1])
                    {
                        for (int i = 1; i < 7; i++) alphas[i + 1] = (byte)(((7 - i) * alphas[0] + i * alphas[1]) / 7);
                    }
                    else
                    {
                        for (int i = 1; i < 5; i++) alphas[i + 1] = (byte)(((5 - i) * alphas[0] + i * alphas[1]) / 5);
                        alphas[6] = 0;
                        alphas[7] = 255;
                    }
                    for (int i = 0; i < 6; i++) alphaBits |= (ulong)src[block + 2 + i] << (8 * i);
                }

                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= h) break;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= w) continue;
                        int texel = py * 4 + px;
                        uint c = colors[(int)((bits >> (texel * 2)) & 3)];
                        int o = (y * w + x) * 4;
                        dst[o] = (byte)c;
                        dst[o + 1] = (byte)(c >> 8);
                        dst[o + 2] = (byte)(c >> 16);
                        dst[o + 3] = format switch
                        {
                            Format.DXT3 => (byte)(((src[block + texel / 2] >> ((texel & 1) * 4)) & 15) * 17),
                            Format.DXT5 => alphas[(int)((alphaBits >> (texel * 3)) & 7)],
                            _ => (byte)(c >> 24),
                        };
                    }
                }
            }
        }

        if (bw * bh >= 4096) Parallel.For(0, bh, Row);
        else for (int by = 0; by < bh; by++) Row(by);
    }

    private static uint Rgb565(ushort c)
    {
        uint r = (uint)((c >> 11) & 31), g = (uint)((c >> 5) & 63), b = (uint)(c & 31);
        r = (r << 3) | (r >> 2);
        g = (g << 2) | (g >> 4);
        b = (b << 3) | (b >> 2);
        return r | (g << 8) | (b << 16) | 0xFF000000u;
    }

    private static uint Mix(uint a, uint b, uint wa, uint wb, uint d)
    {
        uint r = ((a & 0xFF) * wa + (b & 0xFF) * wb) / d;
        uint g = (((a >> 8) & 0xFF) * wa + ((b >> 8) & 0xFF) * wb) / d;
        uint bl = (((a >> 16) & 0xFF) * wa + ((b >> 16) & 0xFF) * wb) / d;
        return r | (g << 8) | (bl << 16) | 0xFF000000u;
    }
}
