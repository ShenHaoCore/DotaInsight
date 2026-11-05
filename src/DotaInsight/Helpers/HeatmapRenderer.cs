using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DotaInsight.Helpers;

/// <summary>
/// 活动热区渲染：lane_pos 聚合计数 → 128×128 Pbgra32 位图（透明底），
/// 天辉绿 / 夜魇红同格叠加，alpha 随出现次数开根号收敛。
/// 展示时 Image 用 Fill 拉伸到小地图尺寸，BitmapScalingMode=NearestNeighbor 保持网格感。
/// </summary>
public static class HeatmapRenderer
{
    private static readonly byte[] RadiantColor = [0x2F, 0xD5, 0x7F]; // 与胜负语义绿一致
    private static readonly byte[] DireColor = [0xF0, 0x56, 0x4A];    // 与胜负语义红一致

    /// <summary>alpha 收敛基准：单格出现这么多次即接近饱和（lane_pos 单格多为个位到几十次）。</summary>
    private const double SaturationCount = 10.0;

    private const double MaxAlpha = 0.9;

    public static ImageSource? Render(
        IReadOnlyDictionary<string, int>? radiant,
        IReadOnlyDictionary<string, int>? dire,
        int grid = 128)
    {
        if (radiant is not { Count: > 0 } && dire is not { Count: > 0 })
        {
            return null;
        }

        var buffer = new byte[grid * grid * 4];
        BlendSide(buffer, grid, radiant, RadiantColor);
        BlendSide(buffer, grid, dire, DireColor);

        var bitmap = new WriteableBitmap(grid, grid, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, grid, grid), buffer, grid * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>把一侧的计数以 alpha-over 方式混入缓冲。缓冲为 Pbgra32（预乘 alpha），
    /// 全程在预乘域合成：out_p = src_p·a + dst_p·(1−a)。</summary>
    private static void BlendSide(byte[] buffer, int grid, IReadOnlyDictionary<string, int>? cells, byte[] color)
    {
        if (cells is null)
        {
            return;
        }
        foreach (var (key, count) in cells)
        {
            var comma = key.IndexOf(',');
            if (comma <= 0
                || !int.TryParse(key.AsSpan(0, comma), out var x)
                || !int.TryParse(key.AsSpan(comma + 1), out var y)
                || x < 0 || x >= grid || y < 0 || y >= grid
                || count <= 0)
            {
                continue;
            }

            // sqrt 曲线：低频也可见，高频不糊成一片
            var alpha = Math.Min(MaxAlpha, Math.Sqrt(count) / Math.Sqrt(SaturationCount) * MaxAlpha);

            var offset = (y * grid + x) * 4;
            var inv = 1 - alpha;

            // color 是直色（sRGB 字节），预乘后写入
            buffer[offset] = (byte)Math.Round(color[2] * alpha + buffer[offset] * inv);
            buffer[offset + 1] = (byte)Math.Round(color[1] * alpha + buffer[offset + 1] * inv);
            buffer[offset + 2] = (byte)Math.Round(color[0] * alpha + buffer[offset + 2] * inv);
            buffer[offset + 3] = (byte)Math.Round(alpha * 255 + buffer[offset + 3] * inv);
        }
    }
}
