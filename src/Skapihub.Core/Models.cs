using System.Text.Json;

namespace Skapihub.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record HubConfiguration
{
    public int Port { get; init; } = 27123;
    public string ApiKey { get; init; } = "sk-local";
    public bool Enabled { get; init; } = true;
    public List<Relay> Relays { get; init; } = [];
}

public sealed record Relay
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public List<KeyGroup> Groups { get; init; } = [];
}

public sealed record KeyGroup
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Key { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public List<ModelEntry> Models { get; init; } = [];
    public DateTimeOffset? SyncedAt { get; init; }
    public string? SyncError { get; init; }
}

public sealed record ModelEntry(string Id, bool Enabled = true);
public sealed record RelayInput(string Name, string Url, List<GroupInput> Groups);
public sealed record GroupInput(string Name, string Key, string? Id = null);
public sealed record SettingsInput(int Port, string ApiKey);
public sealed record ToggleInput(bool Enabled);
public sealed record ModelToggleInput(List<string> Models, bool Enabled);
public sealed record ManualModelsInput(List<string> Models);

public sealed record RequestUsage
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string Model { get; init; } = "";
    public string? RouteId { get; init; }
    public string RelayId { get; init; } = "";
    public string RelayName { get; init; } = "";
    public string GroupId { get; init; } = "";
    public string GroupName { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public int Status { get; init; }
    public double? FirstTokenMs { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? TotalTokens { get; init; }
    public string? Error { get; init; }
}

public sealed record UsageTotals
{
    public long Requests { get; set; }
    public long Successes { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long UnknownUsage { get; set; }
    public double FirstTokenSumMs { get; set; }
    public long FirstTokenSamples { get; set; }
    public double? AverageFirstTokenMs => FirstTokenSamples == 0 ? null : Math.Round(FirstTokenSumMs / FirstTokenSamples, 1);
    public void Add(RequestUsage value)
    {
        Requests++;
        if (value.Status is >= 200 and < 300) Successes++;
        InputTokens += value.InputTokens ?? 0;
        OutputTokens += value.OutputTokens ?? 0;
        TotalTokens += value.TotalTokens ?? ((value.InputTokens ?? 0) + (value.OutputTokens ?? 0));
        if (value.TotalTokens is null && value.InputTokens is null && value.OutputTokens is null) UnknownUsage++;
        if (value.FirstTokenMs is { } ms) { FirstTokenSumMs += ms; FirstTokenSamples++; }
    }
}

public sealed record UsageSnapshot
{
    public UsageTotals Total { get; init; } = new();
    public Dictionary<string, UsageTotals> Days { get; init; } = [];
    public Dictionary<string, UsageTotals> Groups { get; init; } = [];
    public List<RequestUsage> Recent { get; init; } = [];
}
