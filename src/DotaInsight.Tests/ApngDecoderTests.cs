using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;
using DotaInsight.Helpers;

namespace DotaInsight.Tests;

/// <summary>
/// APNG 解码器：手工构造最小动图（2 帧、区域混合、dispose）验证帧数、
/// 像素、延时；静态 png / 不支持色型返回 null 走静态回退。
/// </summary>
public class ApngDecoderTests
{
    [Fact]
    public void Decodes_TwoFrameApng_WithRegionBlendAndDelays()
    {
        // 2×2 RGBA：帧0 全红（100ms）；帧1 在 (1,0) 混入 1×1 蓝（SOURCE, 50ms）
        var png = BuildApng(
            width: 2, height: 2,
            frames:
            [
                new TestFrame(
                    Fctl: new byte[] { 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100, 3, 232, 0, 0 },
                    Scanlines: [Scanline([(255, 0, 0, 255), (255, 0, 0, 255)]),
                                Scanline([(255, 0, 0, 255), (255, 0, 0, 255)])],
                    IsDefault: true),
                new TestFrame(
                    Fctl: new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 50, 3, 232, 1, 0 },
                    Scanlines: [Scanline([(0, 0, 255, 255)])],
                    IsDefault: false)
            ]);

        var anim = ApngDecoder.Decode(png);

        Assert.NotNull(anim);
        Assert.Equal(2, anim!.Frames.Count);

        var f0 = anim.Frames[0];
        Assert.Equal(100, f0.DelayMs);
        AssertPixel(f0.Bitmap, 0, 0, 255, 0, 0, 255);
        AssertPixel(f0.Bitmap, 1, 1, 255, 0, 0, 255);

        var f1 = anim.Frames[1];
        Assert.Equal(50, f1.DelayMs);
        AssertPixel(f1.Bitmap, 0, 0, 255, 0, 0, 255);
        AssertPixel(f1.Bitmap, 1, 0, 0, 0, 255, 255); // SOURCE 混合替换为蓝
    }

    [Fact]
    public void Returns_Null_ForStaticPng()
    {
        var png = BuildApng(
            width: 2, height: 2,
            frames:
            [
                new TestFrame(
                    Fctl: null, // 无 acTL / fcTL 的普通 png
                    Scanlines: [Scanline([(1, 2, 3, 255), (4, 5, 6, 255)]),
                                Scanline([(7, 8, 9, 255), (10, 11, 12, 255)])],
                    IsDefault: true)
            ],
            withAnimation: false);

        Assert.Null(ApngDecoder.Decode(png));
    }

    [Fact]
    public void Returns_Null_ForUnsupportedColorType()
    {
        var png = BuildApng(
            width: 2, height: 2,
            colorType: 0, // 灰度：不支持
            frames:
            [
                new TestFrame(
                    Fctl: new byte[] { 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100, 3, 232, 0, 0 },
                    Scanlines: [ScanlineGrayscale([(10, 10), (20, 20)]),
                                ScanlineGrayscale([(30, 30), (40, 40)])],
                    IsDefault: true)
            ]);

        Assert.Null(ApngDecoder.Decode(png));
    }

    [Fact]
    public void Returns_Null_ForTruncatedData()
    {
        Assert.Null(ApngDecoder.Decode([0x89, 0x50, 0x4E, 0x47]));
        Assert.Null(ApngDecoder.Decode([]));
        Assert.Null(ApngDecoder.Decode(null!));
    }

    // ---------- 构造工具 ----------

    private sealed record TestFrame(byte[]? Fctl, List<byte[]> Scanlines, bool IsDefault);

    private static byte[] Scanline((byte R, byte G, byte B, byte A)[] pixels)
    {
        var line = new byte[1 + pixels.Length * 4];
        line[0] = 0; // filter none
        for (var i = 0; i < pixels.Length; i++)
        {
            line[1 + i * 4] = pixels[i].R;
            line[2 + i * 4] = pixels[i].G;
            line[3 + i * 4] = pixels[i].B;
            line[4 + i * 4] = pixels[i].A;
        }

        return line;
    }

    private static byte[] ScanlineGrayscale((byte V, byte A)[] pixels)
    {
        var line = new byte[1 + pixels.Length];
        line[0] = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            line[1 + i] = pixels[i].V;
        }

        return line;
    }

    private static byte[] BuildApng(
        int width, int height,
        IReadOnlyList<TestFrame> frames,
        byte colorType = 6,
        bool withAnimation = true)
    {
        var outMs = new MemoryStream();
        outMs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteInt32(ihdr, 0, width);
        WriteInt32(ihdr, 4, height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = colorType;
        Chunk(outMs, "IHDR", ihdr);

        if (withAnimation)
        {
            var actl = new byte[8];
            WriteInt32(actl, 0, frames.Count);
            WriteInt32(actl, 4, 0); // 无限循环
            Chunk(outMs, "acTL", actl);
        }

        var sequence = 0u;
        foreach (var frame in frames)
        {
            if (frame.Fctl is not null)
            {
                var fctl = new byte[26];
                Array.Copy(frame.Fctl, fctl, 26);
                WriteInt32(fctl, 0, (int)sequence++);
                Chunk(outMs, "fcTL", fctl);
            }

            var raw = new MemoryStream();
            foreach (var line in frame.Scanlines)
            {
                raw.Write(line, 0, line.Length);
            }

            var compressed = ZlibCompress(raw.ToArray());
            Chunk(outMs, frame.IsDefault ? "IDAT" : "fdAT",
                  frame.IsDefault ? compressed : [(byte)(sequence++ >> 24 & 0xFF), (byte)(sequence >> 16 & 0xFF), (byte)(sequence >> 8 & 0xFF), (byte)(sequence & 0xFF), .. compressed]);
        }

        Chunk(outMs, "IEND", []);
        return outMs.ToArray();
    }

    private static void Chunk(MemoryStream ms, string type, byte[] data)
    {
        var len = new byte[4];
        WriteInt32(len, 0, data.Length);
        ms.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        ms.Write(typeBytes);
        ms.Write(data);

        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        var crc = new byte[4];
        WriteInt32(crc, 0, (int)Crc32(crcInput));
        ms.Write(crc);
    }

    private static void WriteInt32(byte[] b, int offset, int value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return ms.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static void AssertPixel(BitmapSource bitmap, int x, int y, byte r, byte g, byte b, byte a)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var offset = y * stride + x * 4;
        if (b != pixels[offset] || g != pixels[offset + 1] || r != pixels[offset + 2] || a != pixels[offset + 3])
        {
            throw new Exception(
                $"pixel ({x},{y}) expected r={r} g={g} b={b} a={a}, got B={pixels[offset]} G={pixels[offset + 1]} R={pixels[offset + 2]} A={pixels[offset + 3]}");
        }
    }
}
