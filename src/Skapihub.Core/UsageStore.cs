using System.Text.Json;
using System.Threading.Channels;

namespace Skapihub.Core;

public sealed class UsageStore : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _file;
    private readonly Channel<bool> _dirty = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _writer;
    private UsageSnapshot _state;
    public string? PersistenceError { get; private set; }

    public UsageStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "usage.json");
        _state = File.Exists(_file)
            ? JsonSerializer.Deserialize<UsageSnapshot>(File.ReadAllBytes(_file), JsonDefaults.Options) ?? new()
            : new();
        _writer = WriteLoopAsync();
    }

    public void Record(RequestUsage usage)
    {
        lock (_gate)
        {
            _state.Total.Add(usage);
            var day = usage.At.ToLocalTime().ToString("yyyy-MM-dd");
            if (!_state.Days.TryGetValue(day, out var daily)) _state.Days[day] = daily = new();
            daily.Add(usage);
            if (!string.IsNullOrEmpty(usage.GroupId))
            {
                if (!_state.Groups.TryGetValue(usage.GroupId, out var group)) _state.Groups[usage.GroupId] = group = new();
                group.Add(usage);
            }
            _state.Recent.Insert(0, usage);
            if (_state.Recent.Count > 300) _state.Recent.RemoveRange(300, _state.Recent.Count - 300);
            foreach (var old in _state.Days.Keys.Order().Take(Math.Max(0, _state.Days.Count - 90)).ToArray()) _state.Days.Remove(old);
        }
        _dirty.Writer.TryWrite(true);
    }

    public UsageSnapshot Snapshot()
    {
        lock (_gate) return JsonSerializer.Deserialize<UsageSnapshot>(JsonSerializer.SerializeToUtf8Bytes(_state, JsonDefaults.Options), JsonDefaults.Options)!;
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var _ in _dirty.Reader.ReadAllAsync())
        {
            try
            {
                await AtomicFile.WriteAsync(_file, JsonSerializer.SerializeToUtf8Bytes(Snapshot(), JsonDefaults.Options));
                PersistenceError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PersistenceError = "用量写入失败：" + ex.Message; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _dirty.Writer.TryComplete();
        await _writer;
    }
}
