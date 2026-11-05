using System.IO;
using DotaInsight.Helpers;

namespace DotaInsight.Tests;

/// <summary>
/// 磁盘图片缓存的自愈能力：落盘文件损坏时必须能识别并回退，
/// 而不是把异常抛给页面（否则详情页海报会永久失效，直到用户手动清缓存）。
/// </summary>
public class HeroImageCacheTests : IDisposable
{
    private readonly string _tempDir;

    public HeroImageCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dotainsight-imagecache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    [Fact]
    public void TryLoadFromDisk_CorruptBytes_ReturnsFalseInsteadOfThrowing()
    {
        // PNG 头 + 截断的垃圾字节：模拟断电残留 / 杀软截断的坏文件
        var path = Path.Combine(_tempDir, "corrupt.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFF, 0x00]);

        var ok = RunOnSta(() => HeroImageCache.TryLoadFromDisk(path, decodeWidth: 160, out var image));

        Assert.False(ok);
        Assert.Null(RunOnSta(() =>
        {
            HeroImageCache.TryLoadFromDisk(path, 160, out var img);
            return img;
        }));
    }

    [Fact]
    public void TryLoadFromDisk_PlainTextGarbage_ReturnsFalse()
    {
        var path = Path.Combine(_tempDir, "garbage.png");
        File.WriteAllText(path, "this is not an image");

        var ok = RunOnSta(() => HeroImageCache.TryLoadFromDisk(path, 160, out _));

        Assert.False(ok);
    }

    [Fact]
    public void TryLoadFromDisk_ValidPng_ReturnsTrueWithImage()
    {
        // 1×1 透明 PNG（公开的标准字节），模拟正常落盘的缓存文件
        const string base64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var path = Path.Combine(_tempDir, "valid.png");
        File.WriteAllBytes(path, Convert.FromBase64String(base64));

        var (ok, hasImage) = RunOnSta(() =>
        {
            var success = HeroImageCache.TryLoadFromDisk(path, 160, out var image);
            return (success, image is not null);
        });

        Assert.True(ok);
        Assert.True(hasImage);
    }

    /// <summary>BitmapImage 属 DispatcherObject，用 STA 线程执行，与生产代码 DownscaleToPng 的做法一致。</summary>
    private static T RunOnSta<T>(Func<T> func)
    {
        var result = default(T);
        var thread = new Thread(() => result = func())
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result!;
    }
}
