using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Skapihub.Core;

public static partial class UpstreamAddress
{
    [GeneratedRegex(@"/v\d+(?:\.\d+)?(?:beta)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix();
    public static Uri Endpoint(string baseUrl, string endpoint, string query = "")
    {
        var clean = ConfigurationStore.NormalizeUrl(baseUrl);
        return new Uri(clean + (VersionSuffix().IsMatch(new Uri(clean).AbsolutePath) ? "/" : "/v1/") + endpoint.TrimStart('/') + query);
    }
}

public sealed class ModelDiscovery(ConfigurationStore configuration, HttpClient client)
{
    private readonly SemaphoreSlim _parallel = new(4, 4);
    public async Task SyncAsync(string relayId, string? groupId, CancellationToken cancellationToken)
    {
        var relay = configuration.Current.Relays.FirstOrDefault(x => x.Id == relayId) ?? throw new KeyNotFoundException("中转不存在。");
        var groups = relay.Groups.Where(x => groupId is null || x.Id == groupId).ToArray();
        if (groupId is not null && groups.Length == 0) throw new KeyNotFoundException("分组不存在。");
        await Task.WhenAll(groups.Select(group => SyncOneAsync(relay, group, cancellationToken)));
    }

    private async Task SyncOneAsync(Relay relay, KeyGroup group, CancellationToken cancellationToken)
    {
        await _parallel.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var request = new HttpRequestMessage(HttpMethod.Get, UpstreamAddress.Endpoint(relay.Url, "models"));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + group.Key);
            request.Headers.TryAddWithoutValidation("x-api-key", group.Key);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("获取模型失败 · HTTP " + (int)response.StatusCode);
            if (response.Content.Headers.ContentLength is > 8 * 1024 * 1024) throw new InvalidDataException("模型列表超过 8 MB。");
            await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, timeout.Token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
            var rootElement = json.RootElement;
            JsonElement rows;
            if (rootElement.ValueKind == JsonValueKind.Array) rows = rootElement;
            else if (!rootElement.TryGetProperty("data", out rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("上游没有返回有效的模型列表，可手动添加模型。");
            var ids = rows.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()
                : x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) throw new InvalidDataException("上游返回了空模型列表，可手动添加模型。");
            await ApplyAsync(relay, group, current => current with
            {
                Models = ids.Select(id => new ModelEntry(id, current.Models.FirstOrDefault(x => x.Id == id)?.Enabled ?? true)).ToList(),
                SyncedAt = DateTimeOffset.UtcNow,
                SyncError = null
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ApplyAsync(relay, group, x => x with { SyncError = "获取模型超时，已保留原列表。" });
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
        {
            await ApplyAsync(relay, group, x => x with { SyncError = ex is HttpRequestException && !ex.Message.StartsWith("获取模型失败", StringComparison.Ordinal) ? "无法连接上游，请检查地址和网络。" : ex.Message });
        }
        finally { _parallel.Release(); }
    }

    private Task ApplyAsync(Relay originalRelay, KeyGroup originalGroup, Func<KeyGroup, KeyGroup> update) => configuration.UpdateAsync(current => current with
    {
        Relays = current.Relays.Select(relay => relay.Id != originalRelay.Id || relay.Url != originalRelay.Url ? relay : relay with
        {
            Groups = relay.Groups.Select(group => group.Id == originalGroup.Id && group.Key == originalGroup.Key ? update(group) : group).ToList()
        }).ToList()
    });
}
