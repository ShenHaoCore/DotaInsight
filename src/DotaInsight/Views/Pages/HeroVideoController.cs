using System.Windows;
using System.Windows.Controls;
using DotaInsight.Helpers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DotaInsight.Views.Pages;

/// <summary>
/// 英雄头图视频控制器：封装 WebView2 的初始化、视频切换与
/// 下滑冻结 / 回顶恢复状态机，让页面 code-behind 只保留事件转发。
/// </summary>
internal sealed class HeroVideoController
{
    private readonly WebView2CompositionControl _videoView;
    private readonly Image _posterImage;
    private readonly Func<(string? VideoUrl, string? PosterUrl)> _resolveMedia;

    private readonly SemaphoreSlim _ensureWebViewLock = new(1, 1);
    private bool _webViewReady;
    private bool _webMessageHooked;
    private bool _videoFrozen;
    private string? _pendingVideoUrl;
    private string? _playingVideoUrl;
    private int _mediaGeneration;

    public HeroVideoController(
        WebView2CompositionControl videoView,
        Image posterImage,
        Func<(string? VideoUrl, string? PosterUrl)> resolveMedia)
    {
        _videoView = videoView;
        _posterImage = posterImage;
        _resolveMedia = resolveMedia;
    }

    /// <summary>
    /// 按当前数据源刷新封面与视频（切英雄 / 页面载入时调用）。
    /// </summary>
    public async Task RefreshAsync()
    {
        // 源数据变化（切英雄）时解除下滑冻结
        _videoFrozen = false;
        var generation = ++_mediaGeneration;

        var (videoUrlSource, posterUrl) = _resolveMedia();
        ApplyPosterImage(posterUrl);
        ShowPosterCover();

        if (string.IsNullOrWhiteSpace(videoUrlSource))
        {
            _pendingVideoUrl = null;
            _playingVideoUrl = null;
            return;
        }

        var videoUrl = videoUrlSource;
        if (string.Equals(_playingVideoUrl, videoUrl, StringComparison.OrdinalIgnoreCase)
            && _posterImage.Visibility != Visibility.Visible
            && !_videoFrozen)
        {
            return;
        }

        try
        {
            await EnsureWebViewAsync().ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        if (generation != _mediaGeneration)
        {
            return;
        }

        if (!_webViewReady || _videoView.CoreWebView2 is null)
        {
            return;
        }

        _pendingVideoUrl = videoUrl;
        _playingVideoUrl = null;
        ShowPosterCover();
        _videoView.Visibility = Visibility.Visible;

        try
        {
            _videoView.NavigateToString(BuildVideoHtml(videoUrl));
        }
        catch
        {
            // NavigateToString 失败时保留封面
        }
    }

    /// <summary>
    /// 切换英雄：立即作废旧视频与封面，避免新数据加载期间残留上个英雄的画面。
    /// 注意不能 Visibility.Collapse WebView2（会挂起渲染），改为导航到空白页。
    /// </summary>
    public void PrepareForSwitch()
    {
        _mediaGeneration++;
        _pendingVideoUrl = null;
        _playingVideoUrl = null;
        _videoFrozen = false;
        _posterImage.Source = null;
        ShowPosterCover();
        try
        {
            if (_videoView.CoreWebView2 is not null)
            {
                _videoView.NavigateToString(EmptyHtml());
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>页面卸载：作废在途加载，释放视频为空白页。</summary>
    public void OnUnloaded()
    {
        _mediaGeneration++;
        ShowPosterCover();
        try
        {
            if (_videoView.CoreWebView2 is not null)
            {
                _videoView.NavigateToString(EmptyHtml());
            }
        }
        catch
        {
            // ignore
        }

        _pendingVideoUrl = null;
        _playingVideoUrl = null;
        _videoFrozen = false;
        // CoreWebView2 在单例页上可复用；若控件已丢弃则下次 Ensure 重建
        if (_videoView.CoreWebView2 is null)
        {
            _webViewReady = false;
        }
    }

    /// <summary>
    /// 滚动联动：下滑冻结为封面，回到顶部恢复播放，降低 GPU/合成压力。
    /// </summary>
    public async Task OnScrollAsync(double verticalOffset, double verticalChange)
    {
        if (Math.Abs(verticalChange) < 0.1)
        {
            return;
        }

        if (verticalOffset > 8)
        {
            FreezeVideo();
            return;
        }

        if (_videoFrozen
            && (!string.IsNullOrWhiteSpace(_playingVideoUrl)
                || !string.IsNullOrWhiteSpace(_pendingVideoUrl)))
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
            if (_videoView.CoreWebView2 is not null)
            {
                _ = _videoView.CoreWebView2.ExecuteScriptAsync(
                    "document.getElementById('v')?.pause()");
            }
        }
        catch
        {
            // ignore
        }

        _videoView.Visibility = Visibility.Collapsed;
    }

    private async Task ResumeVideoAsync()
    {
        if (!_videoFrozen)
        {
            return;
        }

        var playUrl = _playingVideoUrl ?? _pendingVideoUrl;
        if (string.IsNullOrWhiteSpace(playUrl))
        {
            return;
        }

        _videoFrozen = false;
        _videoView.Visibility = Visibility.Visible;

        // 尚未收到 video-ready：保持封面，等消息再揭开
        if (string.IsNullOrWhiteSpace(_playingVideoUrl))
        {
            return;
        }

        try
        {
            if (_videoView.CoreWebView2 is not null)
            {
                await _videoView.CoreWebView2
                    .ExecuteScriptAsync("document.getElementById('v')?.play()")
                    .ConfigureAwait(true);
            }
        }
        catch
        {
            // ignore
        }

        await _videoView.Dispatcher.InvokeAsync(() =>
        {
            if (!_videoFrozen && !string.IsNullOrWhiteSpace(_playingVideoUrl))
            {
                RevealVideo();
            }
        });
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady && _videoView.CoreWebView2 is not null)
        {
            return;
        }

        if (_videoView.CoreWebView2 is not null)
        {
            HookWebMessage();
            _webViewReady = true;
            return;
        }

        await _ensureWebViewLock.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_webViewReady && _videoView.CoreWebView2 is not null)
            {
                return;
            }

            if (_videoView.CoreWebView2 is not null)
            {
                HookWebMessage();
                _webViewReady = true;
                return;
            }

            var userData = System.IO.Path.Combine(AppPaths.Root, "webview2");
            System.IO.Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment
                .CreateAsync(userDataFolder: userData)
                .ConfigureAwait(true);
            await _videoView.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            var core = _videoView.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 初始化后 CoreWebView2 仍为空。");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            _videoView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);
            HookWebMessage();
            _webViewReady = true;
        }
        catch
        {
            _webViewReady = false;
            throw;
        }
        finally
        {
            _ensureWebViewLock.Release();
        }
    }

    private void HookWebMessage()
    {
        if (_webMessageHooked || _videoView.CoreWebView2 is null)
        {
            return;
        }

        _videoView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _webMessageHooked = true;
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

        _videoView.Dispatcher.InvokeAsync(() =>
        {
            if (!string.Equals(_pendingVideoUrl, expected, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 即使下滑冻结，也记录可播放地址，回到顶部才能恢复
            _playingVideoUrl = expected;
            if (_videoFrozen)
            {
                return;
            }

            RevealVideo();
        });
    }

    private void ApplyPosterImage(string? posterUrl)
    {
        if (!string.IsNullOrWhiteSpace(posterUrl))
        {
            // 头图区实际只有 180×180，源图 1440×1440：内存解码 400px、
            // 磁盘只留 640px 版本（1.5 MB → 约 0.37 MB），避免无谓的磁盘与内存占用。
            _posterImage.Source = HeroImageCache.Get(posterUrl, decodeWidth: 400, storeWidth: 640);
            _posterImage.Visibility = Visibility.Visible;
        }
        else
        {
            _posterImage.Source = null;
        }
    }

    private void ShowPosterCover()
    {
        _posterImage.BeginAnimation(UIElement.OpacityProperty, null);
        _posterImage.Opacity = 1;
        _posterImage.Visibility = Visibility.Visible;
    }

    /// <summary>官网式渐进加载：视频就绪后封面淡出，交叉过渡到视频。</summary>
    private void RevealVideo()
    {
        _videoView.Visibility = Visibility.Visible;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
        fade.Completed += (_, _) =>
        {
            if (_posterImage.Opacity <= 0.01)
            {
                _posterImage.Visibility = Visibility.Collapsed;
            }
        };
        _posterImage.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// 视频页 HTML。刻意不设 &lt;video poster&gt;：
    /// 封面由上层 WPF 的 HeroPosterImage 显示（已走本地缓存），
    /// 若这里再写一次远程 URL，WebView2 会把同一张 1.5 MB 的图重新下载一遍。
    /// </summary>
    private static string BuildVideoHtml(string videoUrl)
    {
        var safeVideo = System.Net.WebUtility.HtmlEncode(videoUrl);

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8" />
              <style>
                html, body {
                  margin:0; padding:0; width:100%; height:100%;
                  overflow:hidden; background:transparent; position:relative;
                }
                #wrap {
                  position:absolute; inset:0; overflow:hidden;
                }
                /* webm 为 1:1 环绕展示动图、容器也是 1:1；
                   cover 保证任何比例下都铺满且居中，不出现黑边或偏移。 */
                video {
                  display:block;
                  width:100%;
                  height:100%;
                  object-fit:cover;
                  object-position:center;
                }
              </style>
            </head>
            <body>
              <div id="wrap">
                <video id="v" muted loop playsinline autoplay preload="metadata" src="{{safeVideo}}"></video>
              </div>
              <script>
                (function () {
                  const v = document.getElementById('v');
                  let notified = false;
                  function notifyReady() {
                    if (notified) return;
                    if (v.paused) return;
                    notified = true;
                    try { chrome.webview.postMessage('video-ready'); } catch (e) {}
                  }
                  function tryPlay() {
                    const p = v.play();
                    if (p && p.then) {
                      p.then(function () { notifyReady(); }).catch(function () {});
                    } else {
                      notifyReady();
                    }
                  }
                  v.addEventListener('canplay', tryPlay);
                  v.addEventListener('playing', notifyReady);
                  v.addEventListener('loadeddata', tryPlay);
                  tryPlay();
                  setTimeout(tryPlay, 1500);
                })();
              </script>
            </body>
            </html>
            """;
    }

    private static string EmptyHtml()
        => "<html><body style='margin:0;background:transparent'></body></html>";
}
