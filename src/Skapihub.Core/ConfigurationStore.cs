using System.Text.Json;

namespace Skapihub.Core;

public interface ISettingsCodec
{
    byte[] Encode(byte[] value);
    byte[] Decode(byte[] value);
}

public sealed class PlainSettingsCodec : ISettingsCodec
{
    public byte[] Encode(byte[] value) => value;
    public byte[] Decode(byte[] value) => value;
}

public sealed class ConfigurationStore
{
    private readonly string _file;
    private readonly ISettingsCodec _codec;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConfiguration _current;
    public HubConfiguration Current => Volatile.Read(ref _current);
    public string DirectoryPath { get; }

    public ConfigurationStore(string directory, ISettingsCodec? codec = null)
    {
        DirectoryPath = directory;
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "settings.dat");
        _codec = codec ?? new PlainSettingsCodec();
        _current = File.Exists(_file)
            ? JsonSerializer.Deserialize<HubConfiguration>(_codec.Decode(File.ReadAllBytes(_file)), JsonDefaults.Options)
                ?? throw new InvalidDataException("配置文件内容无效，请从备份恢复。")
            : new HubConfiguration();
        Validate(_current);
    }

    public async Task<HubConfiguration> UpdateAsync(Func<HubConfiguration, HubConfiguration> update)
    {
        await _gate.WaitAsync();
        try
        {
            var next = update(Current);
            Validate(next);
            var bytes = _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(next, JsonDefaults.Options));
            await AtomicFile.WriteAsync(_file, bytes);
            Volatile.Write(ref _current, next);
            return next;
        }
        finally { _gate.Release(); }
    }

    private static void Validate(HubConfiguration configuration)
    {
        if (configuration.Port is < 1024 or > 65535) throw new ArgumentException("端口需为 1024–65535。 ");
        if (configuration.ApiKey is null) throw new ArgumentException("接入 Key 需为文本，可留空。 ");
        if (configuration.ApiKey.Contains('\r') || configuration.ApiKey.Contains('\n')) throw new ArgumentException("Key 不能包含换行。");
        foreach (var relay in configuration.Relays)
        {
            NormalizeUrl(relay.Url);
            if (string.IsNullOrWhiteSpace(relay.Name)) throw new ArgumentException("请填写中转名称。");
            if (relay.Groups.Select(x => x.Id).Distinct().Count() != relay.Groups.Count) throw new ArgumentException("分组重复。");
        }
    }

    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("请填写中转地址。");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("请填写有效的 HTTP / HTTPS 地址，不含查询参数。");
        return uri.AbsoluteUri.TrimEnd('/');
    }
}

internal static class AtomicFile
{
    public static async Task WriteAsync(string file, byte[] bytes)
    {
        var temporary = file + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
        File.Move(temporary, file, true);
    }
}
