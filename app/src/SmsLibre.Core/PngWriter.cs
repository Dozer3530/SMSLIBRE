// SMSLIBRE — minimal PNG encoder for BgraImage.
//
// Self-contained (no image library): writes a standard 8-bit RGBA PNG using the
// built-in ZLibStream for IDAT compression and a small CRC-32 table. Backs the
// "export map as image" feature and headless rendering/tests.

using System;
using System.IO;
using System.IO.Compression;

namespace SmsLibre.Core;

public static class PngWriter
{
    public static void Save(BgraImage img, string path)
    {
        using var fs = File.Create(path);
        Write(img, fs);
    }

    public static byte[] Encode(BgraImage img)
    {
        using var ms = new MemoryStream();
        Write(img, ms);
        return ms.ToArray();
    }

    private static void Write(BgraImage img, Stream outStream)
    {
        Span<byte> sig = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        outStream.Write(sig);

        // IHDR
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)img.Width);
        WriteBE(ihdr, 4, (uint)img.Height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // color type: RGBA
        ihdr[10] = 0;   // compression
        ihdr[11] = 0;   // filter
        ihdr[12] = 0;   // interlace
        WriteChunk(outStream, "IHDR", ihdr);

        // IDAT: filtered scanlines (filter 0) of RGBA, zlib-compressed.
        byte[] raw = BuildRawScanlines(img);
        byte[] compressed;
        using (var comp = new MemoryStream())
        {
            using (var z = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            compressed = comp.ToArray();
        }
        WriteChunk(outStream, "IDAT", compressed);

        WriteChunk(outStream, "IEND", Array.Empty<byte>());
    }

    private static byte[] BuildRawScanlines(BgraImage img)
    {
        int stride = img.Width * 4;
        var raw = new byte[(stride + 1) * img.Height];
        int src = 0, dst = 0;
        for (int y = 0; y < img.Height; y++)
        {
            raw[dst++] = 0;   // filter type: none
            for (int x = 0; x < img.Width; x++)
            {
                byte b = img.Pixels[src], g = img.Pixels[src + 1],
                     r = img.Pixels[src + 2], a = img.Pixels[src + 3];
                raw[dst++] = r; raw[dst++] = g; raw[dst++] = b; raw[dst++] = a;
                src += 4;
            }
        }
        return raw;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len, 0, 4);

        var typeBytes = new byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);

        uint crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc);
        var crcBytes = new byte[4];
        WriteBE(crcBytes, 0, crc ^ 0xFFFFFFFF);
        s.Write(crcBytes, 0, 4);
    }

    private static void WriteBE(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v >> 24); buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8); buf[off + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
