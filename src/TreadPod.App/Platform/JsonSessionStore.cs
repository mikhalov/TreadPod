using System.Text.Json;
using TreadPod.Core.Bridge;

namespace TreadPod.App.Platform;

public sealed class JsonSessionStore : ISessionStore
{
    private const int MaxStoredSessions = 30;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSessionStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TreadPod",
            "sessions.json");
    }

    public async Task<IReadOnlyList<RunSessionRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(RunSessionRecord session, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            list.Insert(0, session);
            if (list.Count > MaxStoredSessions)
            {
                list.RemoveRange(MaxStoredSessions, list.Count - MaxStoredSessions);
            }

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await using var stream = File.Create(_filePath);
            await JsonSerializer.SerializeAsync(stream, list, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<RunSessionRecord>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<RunSessionRecord>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? [];
        }
        catch
        {
            return [];
        }
    }
}
