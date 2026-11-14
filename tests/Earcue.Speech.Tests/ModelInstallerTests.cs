using System.Net;
using System.Security.Cryptography;
using Earcue.Speech.Asr;

namespace Earcue.Speech.Tests;

public class ModelInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "earcue-install-test-" + Guid.NewGuid().ToString("N"));
    private const string Id = "sherpa-onnx-streaming-paraformer-bilingual-zh-en";
    private static readonly string[] Names = { "encoder.int8.onnx", "decoder.int8.onnx", "tokens.txt" };
    private static readonly byte[] Bytes = { 1, 2, 3, 4 };
    private static AsrModelDescriptor Descriptor(string? badHash = null, string? path = null) => new()
    {
        Id=Id, ModelType=AsrModelType.Paraformer,
        Files=Names.Select((name, i) => new AsrModelDownloadFile {
            Path=i == 0 && path is not null ? path : name, Url="https://models.example/"+name,
            SizeBytes=Bytes.Length, Sha256=badHash ?? Convert.ToHexString(SHA256.HashData(Bytes)) }).ToList()
    };

    [Fact]
    public async Task VerifiedDownload_InstallsModel_AndSkipsInstalledModel()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Response()); }));
        var service = new AsrModelInstaller(http);
        await service.InstallAsync(Descriptor(), _root);
        Assert.True(AsrModelLocator.TryLocate(Id, _root, true, out var files, out var error), error);
        Assert.Equal(Bytes, File.ReadAllBytes(files!.Encoder));
        await service.InstallAsync(Descriptor(), _root);
        Assert.Equal(3, calls);
        Assert.Empty(Directory.GetDirectories(_root, ".download-*"));
    }

    [Fact]
    public async Task BadChecksum_PreservesExistingIncompleteModel_AndRemovesStaging()
    {
        var previous = Path.Combine(_root, Id);
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "keep.txt"), "previous");
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response())));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AsrModelInstaller(http).InstallAsync(Descriptor(new string('0', 64)), _root));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(previous, "keep.txt")));
        Assert.Empty(Directory.GetDirectories(_root, ".download-*"));
        Assert.False(File.Exists(Path.Combine(previous, "encoder.int8.onnx")));
    }

    [Theory]
    [InlineData("../escape.onnx")]
    [InlineData("C:/escape.onnx")]
    public async Task UnsafeManifestPath_IsRejected(string path)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response())));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AsrModelInstaller(http).InstallAsync(Descriptor(path:path), _root));
        Assert.False(Directory.Exists(Path.Combine(_root, Id)));
    }

    [Fact]
    public async Task Cancellation_RemovesUnfinishedFiles_AndAllowsRetry()
    {
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return Response(); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AsrModelInstaller(http).InstallAsync(Descriptor(), _root, ct:cts.Token));
        Assert.False(Directory.Exists(Path.Combine(_root, Id)));
        Assert.Empty(Directory.GetDirectories(_root, ".download-*"));
        using var retryHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Response())));
        await new AsrModelInstaller(retryHttp).InstallAsync(Descriptor(), _root);
        Assert.True(AsrModelLocator.TryLocate(Id, _root, true, out _, out _));
    }

    private static HttpResponseMessage Response() => new(HttpStatusCode.OK) { Content=new ByteArrayContent(Bytes) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
