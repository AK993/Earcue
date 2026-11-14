using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace Earcue.Speech.Asr;

public sealed record ModelInstallProgress(string Stage, long DownloadedBytes, long TotalBytes)
{
    public double Percent => TotalBytes > 0 ? Math.Clamp(DownloadedBytes * 100.0 / TotalBytes, 0, 100) : 0;
}

/// <summary>Downloads into an isolated staging directory; publishes only verified, complete models.</summary>
public sealed class AsrModelInstaller
{
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _http;

    public AsrModelInstaller(HttpClient? http = null) => _http = http ?? SharedClient;

    public async Task InstallAsync(AsrModelDescriptor model, string root,
        IProgress<ModelInstallProgress>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.IsNullOrWhiteSpace(model.Id) || model.Id != Path.GetFileName(model.Id) || model.Id.Contains("..") || model.Id.Contains(':'))
            throw new InvalidDataException("模型 ID 不合法。");
        var destination = ChildPath(root, model.Id);
        Directory.CreateDirectory(root);
        FileStream installLock;
        try { installLock = new FileStream(ChildPath(root, ".install-" + model.Id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("该模型正在安装，请等待另一个下载完成。", error); }
        await using var heldLock = installLock;
        if (AsrModelLocator.TryLocate(model.Id, root, true, out _, out _))
        {
            progress?.Report(new("模型已就绪", model.SizeBytes, model.SizeBytes));
            return;
        }
        var staging = ChildPath(root, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var stagedModel = ChildPath(staging, model.Id);
        long downloaded = 0;
        var total = model.Files.Count > 0 ? model.Files.Sum(f => f.SizeBytes) : model.SizeBytes;
        try
        {
            if (model.Files.Count > 0)
            {
                foreach (var file in model.Files)
                {
                    var target = ChildPath(stagedModel, file.Path);
                    await DownloadAsync(file.Url, file.Sha256, file.SizeBytes, target,
                        bytes => progress?.Report(new("下载 " + file.Path, downloaded + bytes, total)), ct).ConfigureAwait(false);
                    downloaded += file.SizeBytes;
                }
            }
            else
            {
                var archive = ChildPath(staging, "model.tar.bz2");
                await DownloadAsync(model.Url, model.Sha256, model.SizeBytes, archive,
                    bytes => progress?.Report(new("下载模型压缩包", bytes, total)), ct).ConfigureAwait(false);
                progress?.Report(new("校验通过，正在解压…", total, total));
                await ExtractAsync(archive, staging, model.Id, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (!AsrModelLocator.TryLocate(model.Id, staging, true, out _, out var error))
                throw new InvalidDataException("模型安装文件不完整：" + error);
            // Move an incomplete old directory aside; rollback if publishing fails.
            var backup = ChildPath(staging, "previous-model");
            var hadPrevious = Directory.Exists(destination);
            if (hadPrevious) Directory.Move(destination, backup);
            try { Directory.Move(stagedModel, destination); }
            catch
            {
                if (hadPrevious) Directory.Move(backup, destination);
                throw;
            }
            progress?.Report(new("安装完成", total, total));
        }
        finally
        {
            // Staging is an internally generated direct child; never recursively delete model/root paths.
            try
            {
                if (Path.GetDirectoryName(staging) == root && Directory.Exists(staging)) Directory.Delete(staging, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadAsync(string url, string expectedHash, long expectedSize, string target,
        Action<long> report, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            expectedHash.Length != 64 || expectedHash.Any(c => !Uri.IsHexDigit(c)) || expectedSize <= 0)
            throw new InvalidDataException("模型下载清单缺少可信地址、大小或 SHA256。");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Earcue/0.1.9");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"模型下载失败（HTTP {(int)response.StatusCode}），请检查网络后重试。", null, response.StatusCode);
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072];
            long received = 0;
            var lastReport = Stopwatch.StartNew();
            while (true)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                received += count;
                if (received > expectedSize) throw new InvalidDataException("下载大小超出模型清单，已停止安装。");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                if (lastReport.ElapsedMilliseconds >= 100 || received == expectedSize)
                {
                    report(received);
                    lastReport.Restart();
                }
            }
            if (received != expectedSize || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("模型文件 SHA256 或大小校验失败，请重新下载。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("模型下载超过 60 秒没有进展，请检查网络后重试。");
        }
    }

    private static async Task ExtractAsync(string archive, string staging, string id, CancellationToken ct)
    {
        // tar.exe is included with supported Windows versions. Validate paths before extraction.
        var listing = await TarAsync(new[] { "-tjf", archive }, ct).ConfigureAwait(false);
        foreach (var name in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = name.TrimEnd('\r').Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
            if (normalized.Length == 0) continue;
            if (normalized != id && !normalized.StartsWith(id + "/", StringComparison.Ordinal))
                throw new InvalidDataException("模型压缩包包含目录外文件。");
            _ = ChildPath(staging, normalized);
        }
        await TarAsync(new[] { "-xjf", archive, "-C", staging }, ct).ConfigureAwait(false);
    }

    private static async Task<string> TarAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动模型解压程序。");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw;
        }
        var result = await output.ConfigureAwait(false);
        var diagnostic = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidDataException("模型解压失败，请检查磁盘空间或重新下载。" + diagnostic[..Math.Min(200, diagnostic.Length)]);
        return result;
    }

    internal static string ChildPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Replace('\\', '/').Split('/').Any(part => part == ".."))
            throw new InvalidDataException("模型清单包含无效路径。");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模型文件超出安装目录。");
        return path;
    }
}
