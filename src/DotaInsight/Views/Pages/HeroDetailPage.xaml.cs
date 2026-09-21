using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DotaInsight.Helpers;
using DotaInsight.ViewModels.Pages;
using Microsoft.Web.WebView2.Core;

namespace DotaInsight.Views.Pages;

/// <summary>
/// 英雄详情页。头图 WebView2 固定在滚动区外，避免 HWND 随滚动重定位卡顿。
/// </summary>
public partial class HeroDetailPage : Page
{
    private readonly HeroDetailViewModel _viewModel;
    private bool _webViewReady;
    private bool _webMessageHooked;
    private bool _videoFrozen;
    private string? _pendingVideoUrl;
    private string? _playingVideoUrl;
    private int _mediaGeneration;

    public HeroDetailPage(HeroDetailViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        HeroVideoView.Visibility = Visibility.Visible;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowPosterCover();
        ApplyPosterImage();
        await RefreshHeroMediaAsync().ConfigureAwait(true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _mediaGeneration++;
        ShowPosterCover();
        try
        {
            if (HeroVideoView.CoreWebView2 is not null)
            {
                HeroVideoView.NavigateToString(EmptyHtml());
            }
        }
        catch
        {
            // ignore
        }

        _pendingVideoUrl = null;
        _playingVideoUrl = null;
        _videoFrozen = false;
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HeroDetailViewModel.Profile)
            or nameof(HeroDetailViewModel.Hero))
        {
            _videoFrozen = false;
            ShowPosterCover();
            ApplyPosterImage();
            await RefreshHeroMediaAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 下滑时冻结视频为 PNG，回到顶部再恢复，降低 GPU/合成压力。
    /// </summary>
    private async void OnDetailScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) < 0.1)
        {
            return;
        }

        if (e.VerticalOffset > 8)
        {
            FreezeVideo();
            return;
        }

        if (_videoFrozen && !string.IsNullOrWhiteSpace(_playingVideoUrl))
        {
            await ResumeVideoAsync().ConfigureAwait(true);
        }
    }

    private void FreezeVideo()
    {
        if (_videoFrozen)
        {
            return;
        }

        _videoFrozen = true;
        ShowPosterCover();
        try
        {
            if (HeroVideoView.CoreWebView2 is not null)
            {
                _ = HeroVideoView.CoreWebView2.ExecuteScriptAsync(
                    "document.getElementById('v')?.pause()");
            }
        }
        catch
        {
            // ignore
        }

        HeroVideoView.Visibility = Visibility.Collapsed;
    }

    private async Task ResumeVideoAsync()
    {
        if (!_videoFrozen || string.IsNullOrWhiteSpace(_playingVideoUrl))
        {
            return;
        }

        _videoFrozen = false;
        HeroVideoView.Visibility = Visibility.Visible;
        try
        {
            if (HeroVideoView.CoreWebView2 is not null)
            {
                await HeroVideoView.CoreWebView2
                    .ExecuteScriptAsync("document.getElementById('v')?.play()")
                    .ConfigureAwait(true);
            }
        }
        catch
        {
            // ignore
        }

        // 稍等一帧再揭开，避免黑闪
        await Dispatcher.InvokeAsync(() =>
        {
            if (!_videoFrozen && !string.IsNullOrWhiteSpace(_playingVideoUrl))
            {
                HeroPosterImage.Visibility = Visibility.Collapsed;
            }
        });
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady)
        {
            return;
        }

        try
        {
            var userData = System.IO.Path.Combine(AppPaths.Root, "webview2");
            System.IO.Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment
                .CreateAsync(userDataFolder: userData)
                .ConfigureAwait(true);
            await HeroVideoView.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            HeroVideoView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            HeroVideoView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            HeroVideoView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            HeroVideoView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);

            if (!_webMessageHooked)
            {
                HeroVideoView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webMessageHooked = true;
            }

            _webViewReady = true;
        }
        catch
        {
            _webViewReady = false;
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch
        {
            return;
        }

        if (!string.Equals(message, "video-ready", StringComparison.Ordinal))
        {
            return;
        }

        var expected = _pendingVideoUrl;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (!string.Equals(_pendingVideoUrl, expected, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_videoFrozen)
            {
                return;
            }

            _playingVideoUrl = expected;
            HeroPosterImage.Visibility = Visibility.Collapsed;
            HeroVideoView.Visibility = Visibility.Visible;
        });
    }

    private async Task RefreshHeroMediaAsync()
    {
        var generation = ++_mediaGeneration;
        ApplyPosterImage();
        ShowPosterCover();

        var videoUrl = _viewModel.Profile?.VideoUrl;
        var posterUrl = FirstNonEmpty(
            _viewModel.Profile?.PosterUrl,
            _viewModel.Hero?.IconUrl);

        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            _pendingVideoUrl = null;
            _playingVideoUrl = null;
            return;
        }

        if (string.Equals(_playingVideoUrl, videoUrl, StringComparison.OrdinalIgnoreCase)
            && HeroPosterImage.Visibility != Visibility.Visible
            && !_videoFrozen)
        {
            return;
        }

        await EnsureWebViewAsync().ConfigureAwait(true);
        if (generation != _mediaGeneration)
        {
            return;
        }

        if (!_webViewReady || HeroVideoView.CoreWebView2 is null)
        {
            return;
        }

        _pendingVideoUrl = videoUrl;
        _playingVideoUrl = null;
        ShowPosterCover();
        HeroVideoView.Visibility = Visibility.Visible;

        var html = BuildVideoHtml(videoUrl, posterUrl);
        HeroVideoView.NavigateToString(html);
    }

    private void ApplyPosterImage()
    {
        var posterUrl = FirstNonEmpty(
            _viewModel.Profile?.PosterUrl,
            _viewModel.Hero?.IconUrl);

        if (!string.IsNullOrWhiteSpace(posterUrl))
        {
            // 头图固定区不需要超大解码宽度，减轻内存与滚动压力
            HeroPosterImage.Source = HeroImageCache.Get(posterUrl, decodeWidth: 960);
            HeroPosterImage.Visibility = Visibility.Visible;
        }
        else
        {
            HeroPosterImage.Source = null;
        }
    }

    private void ShowPosterCover()
    {
        HeroPosterImage.Visibility = Visibility.Visible;
    }

    private static string BuildVideoHtml(string videoUrl, string? posterUrl)
    {
        var safeVideo = System.Net.WebUtility.HtmlEncode(videoUrl);
        var posterAttr = string.IsNullOrWhiteSpace(posterUrl)
            ? string.Empty
            : $" poster=\"{System.Net.WebUtility.HtmlEncode(posterUrl)}\"";

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8" />
              <style>
                html, body {
                  margin:0; padding:0; width:100%; height:100%;
                  overflow:hidden; background:#0a0c10; position:relative;
                }
                /* 等比覆盖：不拉伸，多出部分裁切，主体偏右 */
                #wrap {
                  position:absolute; inset:0; overflow:hidden;
                }
                video {
                  position:absolute;
                  top:50%;
                  right:0;
                  transform: translateY(-50%);
                  height:100%;
                  width:auto;
                  max-width:none;
                  object-fit:unset;
                }
                /* 视频偏竖/偏方时，改为按宽铺满并垂直居中裁切 */
                video.fit-width {
                  top:50%;
                  left:50%;
                  right:auto;
                  transform: translate(-40%, -50%);
                  width:100%;
                  height:auto;
                }
              </style>
            </head>
            <body>
              <div id="wrap">
                <video id="v" muted loop playsinline autoplay preload="metadata"{{posterAttr}} src="{{safeVideo}}"></video>
              </div>
              <script>
                (function () {
                  const v = document.getElementById('v');
                  let notified = false;
                  function pickFit() {
                    if (!v.videoWidth || !v.videoHeight) return;
                    const box = document.getElementById('wrap');
                    const boxRatio = box.clientWidth / Math.max(box.clientHeight, 1);
                    const videoRatio = v.videoWidth / v.videoHeight;
                    // 视频比容器更“瘦”时按宽铺满，否则按高铺满贴右
                    if (videoRatio < boxRatio) {
                      v.classList.add('fit-width');
                    } else {
                      v.classList.remove('fit-width');
                    }
                  }
                  function notifyReady() {
                    if (notified) return;
                    if (v.paused) return;
                    notified = true;
                    try { chrome.webview.postMessage('video-ready'); } catch (e) {}
                  }
                  function tryPlay() {
                    pickFit();
                    const p = v.play();
                    if (p && p.then) {
                      p.then(function () { pickFit(); notifyReady(); }).catch(function () {});
                    } else {
                      notifyReady();
                    }
                  }
                  v.addEventListener('loadedmetadata', pickFit);
                  v.addEventListener('canplay', tryPlay);
                  v.addEventListener('playing', function () { pickFit(); notifyReady(); });
                  v.addEventListener('loadeddata', tryPlay);
                  window.addEventListener('resize', pickFit);
                  tryPlay();
                  setTimeout(tryPlay, 1500);
                })();
              </script>
            </body>
            </html>
            """;
    }

    private static string EmptyHtml()
        => "<html><body style='margin:0;background:#10151c'></body></html>";

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
