using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DotaInsight.Helpers;

/// <summary>解码出的单帧：整画布合成后的位图（冻结，可跨线程）+ 帧延时（毫秒）。</summary>
public sealed record ApngFrame(BitmapSource Bitmap, int DelayMs);

/// <summary>一个 APNG 动画：帧序列 + 循环次数（0 = 无限循环）。</summary>
public sealed record ApngAnimation(IReadOnlyList<ApngFrame> Frames, int LoopCount);

/// <summary>
/// 极简 APNG（animated PNG）解码器。Steam 头像动效框的主源 png 实为 APNG
/// （资料页 &lt;picture&gt; 里 reduced-motion 源是静态回退、主源是动图），
/// WPF 的 BitmapDecoder 不识别 APNG 只能拿到第一帧，这里手动解出全部帧。
/// 支持范围：8-bit、RGB / RGBA、非隔行；其余格式返回 null，调用方回退静态图。
/// </summary>
public static class ApngDecoder
{
    /// <summary>帧数上限保护：超出视为异常数据，回退静态。</summary>
    private const int MaxFrames = 200;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>解析 PNG 字节；不是 APNG 动图（无 acTL 或仅 1 帧）或格式不支持时返回 null。</summary>
    public static ApngAnimation? Decode(byte[]? data)
    {
        if (data is null || data.Length < 8 + 12 + 13 || !data.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            return null;
        }

        var pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        var numFrames = 0;
        var haveActl = false;
        var ordered = new List<(string Type, byte[] Payload)>();

        while (pos + 8 <= data.Length)
        {
            var length = ReadInt32Be(data, pos);
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var bodyStart = pos + 8;
            if (bodyStart + length + 4 > data.Length)
            {
                return null; // 结构损坏
            }

            var payload = data[bodyStart..(bodyStart + length)];
            switch (type)
            {
                case "IHDR" when length >= 13:
                    width = ReadInt32Be(payload, 0);
                    height = ReadInt32Be(payload, 4);
                    bitDepth = payload[8];
                    colorType = payload[9];
                    interlace = payload[12];
                    break;
                case "acTL" when length >= 8:
                    numFrames = ReadInt32Be(payload, 0);
                    haveActl = true;
                    break;
                case "IDAT":
                case "fcTL":
                case "fdAT":
                    ordered.Add((type, payload));
                    break;
            }

            pos = bodyStart + length + 4; // 跳过 CRC
        }

        if (!haveActl || numFrames <= 1
            || bitDepth != 8 || interlace != 0
            || width <= 0 || height <= 0
            || colorType is not (6 or 2))
        {
            return null;
        }

        // fcTL / fdAT 按 APNG 规范组装帧：fcTL 开启一帧，其后紧邻的 IDAT（首帧，默认图）
        // 或 fdAT（后续帧，跳过 4 字节序号）是该帧的 zlib 数据
        var fctls = new List<Fctl>();
        var frameDatas = new List<List<byte[]>>();
        List<byte[]>? current = null;
        var sawIdat = false;

        foreach (var (chunkType, payload) in ordered)
        {
            if (chunkType == "fcTL")
            {
                if (payload.Length < 26)
                {
                    return null;
                }

                if (current is not null)
                {
                    frameDatas.Add(current);
                }

                current = new List<byte[]>();
                fctls.Add(new Fctl(
                    Width: ReadInt32Be(payload, 4),
                    Height: ReadInt32Be(payload, 8),
                    X: ReadInt32Be(payload, 12),
                    Y: ReadInt32Be(payload, 16),
                    DelayNum: ReadUInt16Be(payload, 20),
                    DelayDen: ReadUInt16Be(payload, 22),
                    DisposeOp: payload[24],
                    BlendOp: payload[25]));
                continue;
            }

            if (current is null)
            {
                continue; // 首个 fcTL 之前的数据（异常结构）忽略
            }

            if (chunkType == "IDAT")
            {
                sawIdat = true;
                current.Add(payload);
            }
            else if (chunkType == "fdAT" && payload.Length >= 4)
            {
                current.Add(payload[4..]);
            }
        }

        if (current is not null)
        {
            frameDatas.Add(current);
        }

        _ = sawIdat;
        if (fctls.Count == 0 || fctls.Count != frameDatas.Count || fctls.Count > MaxFrames)
        {
            return null;
        }

        var bytesPerPixel = colorType == 6 ? 4 : 3;
        // 画布统一按 BGRA 存（PNG 原始为 RGBA，合成时交换 R/B），直接喂 Bgra32 位图
        var canvas = new byte[width * height * 4];
        var savedForDispose = new byte[canvas.Length];
        var frames = new List<ApngFrame>(fctls.Count);

        for (var i = 0; i < fctls.Count; i++)
        {
            var fctl = fctls[i];
            if (fctl.X < 0 || fctl.Y < 0 || fctl.Width <= 0 || fctl.Height <= 0
                || fctl.X + fctl.Width > width || fctl.Y + fctl.Height > height)
            {
                return null;
            }

            var raw = Inflate(frameDatas[i]);
            var expected = fctl.Height * (1 + fctl.Width * bytesPerPixel);
            if (raw is null || raw.Length != expected)
            {
                return null;
            }

            Array.Copy(canvas, savedForDispose, canvas.Length);
            Unfilter(raw, fctl.Width, fctl.Height, bytesPerPixel);
            Composite(canvas, raw, fctl, width, bytesPerPixel);

            var snapshot = (byte[])canvas.Clone();
            var bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null,
                snapshot, width * 4);
            bitmap.Freeze();

            var delayMs = fctl.DelayDen == 0
                ? (fctl.DelayNum <= 0 ? 100 : fctl.DelayNum * 10) // den=0 规范视为 100（即 ×10 得 ms）
                : (int)Math.Round(fctl.DelayNum * 1000.0 / fctl.DelayDen);
            frames.Add(new ApngFrame(bitmap, Math.Clamp(delayMs, 20, 1000)));

            switch (fctl.DisposeOp)
            {
                case 1: // background：区域清为透明
                    ClearRegion(canvas, fctl, width);
                    break;
                case 2: // previous：还原渲染前画布
                    Array.Copy(savedForDispose, canvas, canvas.Length);
                    break;
            }
        }

        return new ApngAnimation(frames, LoopCount: 0);
    }

    private static bool IsPng(byte[] data)
        => data.Length >= 8 && PngSignature.AsSpan().SequenceEqual(data.AsSpan(0, 8));

    private static int ReadInt32Be(byte[] b, int offset)
        => (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

    private static int ReadUInt16Be(byte[] b, int offset)
        => (b[offset] << 8) | b[offset + 1];

    private static byte[]? Inflate(List<byte[]> parts)
    {
        try
        {
            using var raw = new MemoryStream();
            foreach (var part in parts)
            {
                raw.Write(part, 0, part.Length);
            }

            raw.Position = 0;
            using var zlib = new ZLibStream(raw, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PNG 逐行反滤波（None/Sub/Up/Average/Paeth），就地重建。</summary>
    private static void Unfilter(byte[] raw, int width, int height, int bpp)
    {
        var stride = 1 + width * bpp;
        var prevStart = -1;
        for (var y = 0; y < height; y++)
        {
            var lineStart = y * stride;
            var filter = raw[lineStart];
            var line = lineStart + 1;
            for (var x = 0; x < width * bpp; x++)
            {
                var a = x >= bpp ? raw[line + x - bpp] : (byte)0;
                var b = prevStart >= 0 ? raw[prevStart + 1 + x] : (byte)0;
                var c = x >= bpp && prevStart >= 0 ? raw[prevStart + 1 + x - bpp] : (byte)0;

                var value = raw[line + x];
                switch (filter)
                {
                    case 1:
                        value += a;
                        break;
                    case 2:
                        value += b;
                        break;
                    case 3:
                        value += (byte)((a + b) / 2);
                        break;
                    case 4:
                        value += Paeth(a, b, c);
                        break;
                }

                raw[line + x] = value;
            }

            prevStart = lineStart;
        }
    }

    private static byte Paeth(byte pa, byte pb, byte pc)
    {
        var a = pa;
        var b = pb;
        var c = pc;
        var p = a + b - c;
        var pa2 = Math.Abs(p - a);
        var pb2 = Math.Abs(p - b);
        var pc2 = Math.Abs(p - c);
        return pa2 <= pb2 && pa2 <= pc2 ? a : pb2 <= pc2 ? b : c;
    }

    /// <summary>帧像素按 blend_op 合成到画布区域；RGB 帧补不透明 alpha。</summary>
    private static void Composite(byte[] canvas, byte[] raw, Fctl fctl, int canvasWidth, int bpp)
    {
        var frameStride = fctl.Width * bpp;
        for (var y = 0; y < fctl.Height; y++)
        {
            var srcLine = y * (1 + frameStride); // 行首 1 字节 filter，像素数据从 +1 开始
            var dstLine = ((fctl.Y + y) * canvasWidth + fctl.X) * 4;
            for (var x = 0; x < fctl.Width; x++)
            {
                byte sr, sg, sb, sa;
                if (bpp == 4)
                {
                    var s = srcLine + 1 + x * 4;
                    sr = raw[s];
                    sg = raw[s + 1];
                    sb = raw[s + 2];
                    sa = raw[s + 3];
                }
                else
                {
                    var s = srcLine + 1 + x * 3;
                    sr = raw[s];
                    sg = raw[s + 1];
                    sb = raw[s + 2];
                    sa = 255;
                }

                var d = dstLine + x * 4;
                if (fctl.BlendOp == 0 || sa == 255)
                {
                    canvas[d] = sb;     // BGRA：交换 R/B
                    canvas[d + 1] = sg;
                    canvas[d + 2] = sr;
                    canvas[d + 3] = sa;
                }
                else if (sa > 0)
                {
                    // source-over：out = src + dst*(1-srcA)
                    var saF = sa / 255f;
                    var daF = canvas[d + 3] / 255f;
                    var oaF = saF + daF * (1 - saF);
                    if (oaF <= 0)
                    {
                        canvas[d] = canvas[d + 1] = canvas[d + 2] = canvas[d + 3] = 0;
                        continue;
                    }

                    canvas[d] = (byte)Math.Round((sb * saF + canvas[d] * daF * (1 - saF)) / oaF);
                    canvas[d + 1] = (byte)Math.Round((sg * saF + canvas[d + 1] * daF * (1 - saF)) / oaF);
                    canvas[d + 2] = (byte)Math.Round((sr * saF + canvas[d + 2] * daF * (1 - saF)) / oaF);
                    canvas[d + 3] = (byte)Math.Round(oaF * 255);
                }
            }
        }
    }

    private static void ClearRegion(byte[] canvas, Fctl fctl, int canvasWidth)
    {
        for (var y = 0; y < fctl.Height; y++)
        {
            var start = ((fctl.Y + y) * canvasWidth + fctl.X) * 4;
            Array.Clear(canvas, start, fctl.Width * 4);
        }
    }

    /// <summary>fcTL 块的帧描述。</summary>
    private sealed record Fctl(
        int Width, int Height, int X, int Y,
        int DelayNum, int DelayDen, byte DisposeOp, byte BlendOp);
}
