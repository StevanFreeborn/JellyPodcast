using System.Net;
using System.Net.Http.Headers;

using Jellyfin.Plugin.Podcast.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class PodcastMediaDownloaderTests : IDisposable
{
  private readonly string _tempDir;

  public PodcastMediaDownloaderTests()
  {
    _tempDir = Path.Combine(Path.GetTempPath(), $"JellyPodcast_DownloadTests_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempDir);
  }

  public void Dispose()
  {
    if (Directory.Exists(_tempDir))
    {
      Directory.Delete(_tempDir, recursive: true);
    }

    GC.SuppressFinalize(this);
  }

  [Fact]
  public async Task DownloadEpisodeAsync_StreamsToTempFileAndMovesAtomically_OnSuccess()
  {
    var expectedBytes = new byte[] { 0x49, 0x44, 0x33, 0x03, 0x00, 0x01, 0x02, 0x03 };
    string? observedUserAgent = null;

    var handler = new StubHttpMessageHandler(
      (request, _) =>
      {
        observedUserAgent = request.Headers.UserAgent.ToString();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new ByteArrayContent(expectedBytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        return Task.FromResult(response);
      }
    );

    var downloader = CreateDownloader(handler);
    var destinationPath = Path.Combine(_tempDir, "2026-11-23 - Episode.mp3");

    var contentType = await downloader.DownloadEpisodeAsync(
      "https://cdn.example.com/episode.mp3",
      destinationPath,
      CancellationToken.None
    );

    Assert.Equal("audio/mpeg", contentType);
    Assert.False(string.IsNullOrWhiteSpace(observedUserAgent));
    Assert.True(File.Exists(destinationPath));
    Assert.False(File.Exists($"{destinationPath}.tmp"));
    Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(destinationPath, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task DownloadEpisodeAsync_DoesNotLeaveCorruptOrTempFiles_OnHttp500()
  {
    var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

    var downloader = CreateDownloader(handler);
    var destinationPath = Path.Combine(_tempDir, "2026-11-23 - Failed.mp3");

    await Assert.ThrowsAsync<HttpRequestException>(
      () =>
        downloader.DownloadEpisodeAsync(
          "https://cdn.example.com/fail.mp3",
          destinationPath,
          CancellationToken.None
        )
    );

    Assert.False(File.Exists(destinationPath));
    Assert.False(File.Exists($"{destinationPath}.tmp"));
  }

  [Fact]
  public async Task DownloadEpisodeAsync_IgnoresLockedTempFileCleanupFailure_AndRethrowsHttpError()
  {
    var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

    var downloader = CreateDownloader(handler);
    var destinationPath = Path.Combine(_tempDir, "2026-11-23 - LockedTemp.mp3");
    var tempPath = $"{destinationPath}.tmp";

    using (var locked = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    {
      await Assert.ThrowsAsync<HttpRequestException>(
        () =>
          downloader.DownloadEpisodeAsync(
            "https://cdn.example.com/fail.mp3",
            destinationPath,
            TestContext.Current.CancellationToken
          )
      );
    }

    Assert.False(File.Exists(destinationPath));
  }

  [Fact]
  public async Task DownloadEpisodeAsync_CleansUpTempFile_OnCancellation()
  {
    using var cts = new CancellationTokenSource();

    var handler = new StubHttpMessageHandler(
      (_, _) =>
      {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StreamContent(new CancellingStream(cts))
        };
        return Task.FromResult(response);
      }
    );

    var downloader = CreateDownloader(handler);
    var destinationPath = Path.Combine(_tempDir, "2026-11-23 - Cancelled.mp3");

    await Assert.ThrowsAnyAsync<OperationCanceledException>(
      () =>
        downloader.DownloadEpisodeAsync(
          "https://cdn.example.com/slow.mp3",
          destinationPath,
          cts.Token
        )
    );

    Assert.False(File.Exists(destinationPath));
    Assert.False(File.Exists($"{destinationPath}.tmp"));
  }

  [Fact]
  public async Task DownloadCoverArtAsync_SavesCoverJpgOnce_AndSkipsIfAlreadyExists()
  {
    var coverBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
    var callCount = 0;

    var handler = new StubHttpMessageHandler(
      (_, _) =>
      {
        callCount++;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new ByteArrayContent(coverBytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return Task.FromResult(response);
      }
    );

    var downloader = CreateDownloader(handler);

    var firstResult = await downloader.DownloadCoverArtAsync(
      "https://cdn.example.com/cover.jpg",
      _tempDir,
      CancellationToken.None
    );

    var secondResult = await downloader.DownloadCoverArtAsync(
      "https://cdn.example.com/cover.jpg",
      _tempDir,
      CancellationToken.None
    );

    var coverPath = Path.Combine(_tempDir, PodcastMediaDownloader.CoverFileName);
    Assert.True(File.Exists(coverPath));
    Assert.Equal(coverBytes, firstResult);
    Assert.Equal(coverBytes, secondResult);
    Assert.Equal(1, callCount);
  }

  [Fact]
  public async Task DownloadCoverArtAsync_ReturnsNull_WhenUrlMissingOrEmptyBodyOrHttpError()
  {
    var emptyHandler = new StubHttpMessageHandler(
      (_, _) =>
        Task.FromResult(
          new HttpResponseMessage(HttpStatusCode.OK)
          {
            Content = new ByteArrayContent([]),
          }
        )
    );

    var emptyDownloader = CreateDownloader(emptyHandler);

    Assert.Null(await emptyDownloader.DownloadCoverArtAsync("   ", _tempDir, TestContext.Current.CancellationToken));
    Assert.Null(await emptyDownloader.DownloadCoverArtAsync("https://cdn.example.com/empty.jpg", _tempDir, TestContext.Current.CancellationToken));

    var errorHandler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    var errorDownloader = CreateDownloader(errorHandler);

    Assert.Null(await errorDownloader.DownloadCoverArtAsync("https://cdn.example.com/fail.jpg", _tempDir, TestContext.Current.CancellationToken));
    Assert.False(File.Exists(Path.Combine(_tempDir, PodcastMediaDownloader.CoverFileName)));
    Assert.False(File.Exists(Path.Combine(_tempDir, $"{PodcastMediaDownloader.CoverFileName}.tmp")));
  }

  [Fact]
  public async Task DownloadCoverArtAsync_RethrowsCancellation_AndCleansTempFile()
  {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var handler = new StubHttpMessageHandler(
      (_, ct) =>
      {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
      }
    );

    var downloader = CreateDownloader(handler);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadCoverArtAsync("https://cdn.example.com/cover.jpg", _tempDir, cts.Token));

    Assert.False(File.Exists(Path.Combine(_tempDir, $"{PodcastMediaDownloader.CoverFileName}.tmp")));
  }

  [Fact]
  public async Task DownloadImageBytesAsync_ReturnsBytesOnSuccess_AndNullOnInvalidEmptyOrError()
  {
    byte[] expectedBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    var okHandler = new StubHttpMessageHandler(
      (_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new ByteArrayContent(expectedBytes),
        }
      )
    );
    var okDownloader = CreateDownloader(okHandler);

    var downloaded = await okDownloader.DownloadImageBytesAsync("https://cdn.example.com/ep.png", TestContext.Current.CancellationToken);
    Assert.Equal(expectedBytes, downloaded);
    Assert.Null(await okDownloader.DownloadImageBytesAsync("   ", TestContext.Current.CancellationToken));

    var emptyHandler = new StubHttpMessageHandler(
      (_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new ByteArrayContent([]),
        }
      )
    );
    var emptyDownloader = CreateDownloader(emptyHandler);
    Assert.Null(await emptyDownloader.DownloadImageBytesAsync("https://cdn.example.com/empty.png", TestContext.Current.CancellationToken));

    var errorHandler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
    var errorDownloader = CreateDownloader(errorHandler);
    Assert.Null(await errorDownloader.DownloadImageBytesAsync("https://cdn.example.com/missing.png", TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task DownloadImageBytesAsync_RethrowsCancellation()
  {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var handler = new StubHttpMessageHandler(
      (_, ct) =>
      {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
      }
    );
    var downloader = CreateDownloader(handler);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadImageBytesAsync("https://cdn.example.com/ep.png", cts.Token));
  }

  private static PodcastMediaDownloader CreateDownloader(HttpMessageHandler handler)
  {
    var factoryMock = new Mock<IHttpClientFactory>();

    factoryMock
      .Setup(f => f.CreateClient(It.IsAny<string>()))
      .Returns(() => new HttpClient(handler, disposeHandler: false));

    return new PodcastMediaDownloader(factoryMock.Object, NullLogger<PodcastMediaDownloader>.Instance);
  }

  private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
  {
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _responder(request, cancellationToken);
  }

  private sealed class CancellingStream(CancellationTokenSource cts) : Stream
  {
    private readonly CancellationTokenSource _cts = cts;
    private int _reads;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
      if (_reads is 0)
      {
        _reads++;
        buffer.Span[0] = 0xAA;
        _cts.Cancel();
        return ValueTask.FromResult(1);
      }

      cancellationToken.ThrowIfCancellationRequested();
      return ValueTask.FromResult(0);
    }
  }
}