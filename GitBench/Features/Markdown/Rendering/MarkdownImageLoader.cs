using GitBench.Features.Diff;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Fetches and decodes the pictures a markdown document references. One app-wide instance: the
/// decoded frames are cached across surfaces, so scrolling a README back into view or reopening
/// the same commit does not decode the same PNG twice.
/// </summary>
internal interface IMarkdownImageLoader
{
    /// <summary>
    /// Starts loading <paramref name="src"/>: an absolute http(s) URL is fetched, anything else is
    /// resolved against <paramref name="source"/> (null when the surface has none, in which case
    /// only remote images load). <paramref name="onLoaded"/> runs later on the UI thread — never
    /// inside this call, even for a cache hit, so a view may start a load while mounting — with
    /// the decoded frame, or null when the image could not be read or decoded. Disposing the
    /// handle cancels: the callback never fires afterwards.
    /// </summary>
    IDisposable Load(string src, IMarkdownImageSource? source, Action<ImageFrame?> onLoaded);
}

internal sealed class MarkdownImageLoader : IMarkdownImageLoader, IDisposable
{
    /// <summary>Largest blob fetched or read for an inline image — the image preview's cap.</summary>
    public const int MaxSourceBytes = ImagePreviewDecoder.MaxSourceBytes;

    // Decoded RGBA held across surfaces. Past this the oldest frames go; a frame in use stays
    // uploaded on its view's canvas regardless.
    private const long CacheBudgetBytes = 96L * 1024 * 1024;

    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(20);

    private readonly IUiDispatcher _dispatcher;
    private readonly HttpClient _http;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, ImageFrame> _cache = new();
    private readonly Queue<string> _cacheOrder = new();
    private long _cacheBytes;

    public MarkdownImageLoader(IUiDispatcher dispatcher, HttpMessageHandler? handler = null)
    {
        _dispatcher = dispatcher;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = RemoteTimeout,
        };
    }

    public IDisposable Load(string src, IMarkdownImageSource? source, Action<ImageFrame?> onLoaded)
    {
        var request = new Request(onLoaded);
        var remote = MarkdownImagePath.IsRemote(src, out var uri);
        var relative = remote || source is null ? null : MarkdownImagePath.Resolve(source.BaseDir, src);

        if (remote && TryCached(uri.ToString(), out var cached))
        {
            _dispatcher.Post(() => request.Complete(cached));
            return request;
        }
        if (!remote && relative is null)
        {
            _dispatcher.Post(() => request.Complete(null));
            return request;
        }

        _ = Task.Run(async () =>
        {
            ImageFrame? frame = null;
            try
            {
                var bytes = remote
                    ? await FetchAsync(uri, request.Token).ConfigureAwait(false)
                    : source!.Read(relative!, MaxSourceBytes);
                if (bytes != null)
                {
                    // Remote content is keyed by URL (never re-fetched); local content by its own
                    // bytes, so an edited file on disk decodes afresh while an unchanged one hits.
                    var key = remote ? uri.ToString() : "blob:" + ImagePreviewDecoder.ContentHash(bytes);
                    if (!TryCached(key, out frame))
                    {
                        frame = ImagePreviewDecoder.TryDecode(bytes)?.Primary;
                        if (frame != null) Store(key, frame);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                frame = null;
            }
            if (request.Token.IsCancellationRequested) return;
            _dispatcher.Post(() => request.Complete(frame));
        });
        return request;
    }

    private async Task<byte[]?> FetchAsync(Uri uri, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength is > MaxSourceBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxSourceBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private bool TryCached(string key, out ImageFrame? frame)
    {
        lock (_cacheLock)
        {
            return _cache.TryGetValue(key, out frame);
        }
    }

    private void Store(string key, ImageFrame frame)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryAdd(key, frame)) return;
            _cacheOrder.Enqueue(key);
            _cacheBytes += frame.Rgba.Length;
            while (_cacheBytes > CacheBudgetBytes && _cacheOrder.TryDequeue(out var oldest))
            {
                if (_cache.Remove(oldest, out var evicted)) _cacheBytes -= evicted.Rgba.Length;
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class Request : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Action<ImageFrame?>? _onLoaded;

        public Request(Action<ImageFrame?> onLoaded) => _onLoaded = onLoaded;

        public CancellationToken Token => _cts.Token;

        public void Complete(ImageFrame? frame)
        {
            var callback = _onLoaded;
            _onLoaded = null;
            callback?.Invoke(frame);
        }

        public void Dispose()
        {
            _onLoaded = null;
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
