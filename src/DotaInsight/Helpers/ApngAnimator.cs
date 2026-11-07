using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DotaInsight.Helpers;

/// <summary>
/// Image 附加属性：AnimateSource 绑定图片 URL。URL 指向 APNG 动图时循环播放
/// （Steam 动效头像框即此类资源），普通静态图回退 HeroImageCache 位图。
/// </summary>
public static class ApngAnimator
{
    public static readonly DependencyProperty AnimateSourceProperty =
        DependencyProperty.RegisterAttached(
            "AnimateSource", typeof(string), typeof(ApngAnimator),
            new PropertyMetadata(null, OnAnimateSourceChanged));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State", typeof(Playback), typeof(ApngAnimator), new PropertyMetadata(null));

    public static void SetAnimateSource(Image element, string? value)
        => element.SetValue(AnimateSourceProperty, value);

    public static string? GetAnimateSource(Image element)
        => (string?)element.GetValue(AnimateSourceProperty);

    private static Playback? GetState(Image image)
        => (Playback?)image.GetValue(StateProperty);

    private static void SetState(Image image, Playback? value)
        => image.SetValue(StateProperty, value);

    private static void OnAnimateSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
        {
            return;
        }

        GetState(image)?.Stop();
        SetState(image, null);

        image.Loaded -= OnImageLoaded;
        image.Unloaded -= OnImageUnloaded;
        image.Loaded += OnImageLoaded;
        image.Unloaded += OnImageUnloaded;

        if (e.NewValue is not string url || string.IsNullOrWhiteSpace(url))
        {
            image.Source = null;
            return;
        }

        var playback = new Playback(image, url);
        SetState(image, playback);
        _ = playback.StartAsync();
    }

    private static void OnImageLoaded(object sender, RoutedEventArgs e)
        => GetState((Image)sender)?.Resume();

    private static void OnImageUnloaded(object sender, RoutedEventArgs e)
        => GetState((Image)sender)?.Stop();

    /// <summary>一次 URL 的播放会话：加载 → 静态回退或定时器逐帧推进。</summary>
    private sealed class Playback
    {
        private readonly Image _image;
        private readonly string _url;
        private DispatcherTimer? _timer;
        private IReadOnlyList<ApngFrame>? _frames;
        private int _index;
        private bool _stopped;

        public Playback(Image image, string url)
        {
            _image = image;
            _url = url;
        }

        public async Task StartAsync()
        {
            var anim = await Task.Run(() => Load(_url)).ConfigureAwait(false);

            // 回 UI 线程应用结果；期间 URL 已被换掉则丢弃本次加载
            await _image.Dispatcher.InvokeAsync(() =>
            {
                if (_stopped || !ReferenceEquals(GetState(_image), this))
                {
                    return;
                }

                if (anim is null || anim.Frames.Count <= 1)
                {
                    _image.Source = HeroImageCache.Get(_url, 160);
                    return;
                }

                _frames = anim.Frames;
                _index = 0;
                _image.Source = _frames[0].Bitmap;

                _timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(Math.Max(20, _frames[0].DelayMs))
                };
                _timer.Tick += OnTick;
                _timer.Start();
            });
        }

        public void Stop()
        {
            _stopped = true;
            _timer?.Stop();
            _timer = null;
        }

        /// <summary>页面卸载后定时器已停，重新可见时续播。</summary>
        public void Resume()
        {
            if (_timer is not null && _frames is not null && !_stopped)
            {
                _timer.Start();
            }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_frames is null || _timer is null)
            {
                return;
            }

            _index = (_index + 1) % _frames.Count;
            var frame = _frames[_index];
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, frame.DelayMs));
            _image.Source = frame.Bitmap;
        }

        private static ApngAnimation? Load(string url)
        {
            var path = HeroImageCache.EnsureDownloaded(url);
            if (path is null)
            {
                return null;
            }

            try
            {
                return ApngDecoder.Decode(File.ReadAllBytes(path));
            }
            catch
            {
                return null;
            }
        }
    }
}
