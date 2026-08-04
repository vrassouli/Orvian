using System.Text;
using System.Text.Json;

namespace Orvian.Diagnostics;

public sealed class RollingJsonDiagnosticLog : IApplicationDiagnosticLog,
    IApplicationDiagnosticReader,
    IAsyncDisposable
{
    private const string FileName = "orvian-diagnostics.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _directoryPath;
    private readonly string _filePath;
    private readonly long _maximumFileBytes;
    private readonly int _retainedFiles;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RollingJsonDiagnosticLog(
        string directoryPath,
        long maximumFileBytes = 1024 * 1024,
        int retainedFiles = 3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (maximumFileBytes is < 1024 or > 100 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        if (retainedFiles is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        }

        _directoryPath = Path.GetFullPath(directoryPath);
        _filePath = Path.Combine(_directoryPath, FileName);
        _maximumFileBytes = maximumFileBytes;
        _retainedFiles = retainedFiles;
    }

    public async Task<bool> TryWriteAsync(
        SafeDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default)
    {
        var safeEvent = DiagnosticSanitizer.Sanitize(diagnosticEvent);
        var line = JsonSerializer.Serialize(safeEvent, JsonOptions) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetByteCount(line);
        if (bytes > _maximumFileBytes)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directoryPath);
            TryRestrictPermissions(_directoryPath, isDirectory: true);
            if (File.Exists(_filePath) &&
                new FileInfo(_filePath).Length + bytes > _maximumFileBytes)
            {
                Rotate();
            }

            await File.AppendAllTextAsync(
                _filePath,
                line,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            TryRestrictPermissions(_filePath, isDirectory: false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SafeDiagnosticEvent>> QueryAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var events = new List<SafeDiagnosticEvent>();
            foreach (var path in GetPathsNewestFirst())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                foreach (var line in await File.ReadAllLinesAsync(
                             path,
                             cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        var item = JsonSerializer.Deserialize<SafeDiagnosticEvent>(
                            line,
                            JsonOptions);
                        if (item is not null)
                        {
                            events.Add(DiagnosticSanitizer.Sanitize(item));
                        }
                    }
                    catch (Exception exception) when (
                        exception is JsonException or ArgumentException)
                    {
                    }
                }
            }

            return events
                .OrderByDescending(item => item.OccurredAt)
                .Take(limit)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private IEnumerable<string> GetPathsNewestFirst()
    {
        yield return _filePath;
        for (var index = 1; index <= _retainedFiles; index++)
        {
            yield return $"{_filePath}.{index}";
        }
    }

    private void Rotate()
    {
        var oldest = $"{_filePath}.{_retainedFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = $"{_filePath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_filePath}.{index + 1}");
            }
        }

        File.Move(_filePath, $"{_filePath}.1");
    }

    private static void TryRestrictPermissions(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                isDirectory
                    ? UnixFileMode.UserRead |
                      UnixFileMode.UserWrite |
                      UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            PlatformNotSupportedException)
        {
        }
    }
}
