namespace Skapihub.Core;

public sealed record PublishedModel(string Id, string UpstreamId, Relay Relay, KeyGroup Group);

public static class ModelCatalog
{
    public static string RouteId(HubConfiguration configuration, Relay relay, KeyGroup group, string model)
    {
        var relayName = Label(relay.Name);
        var groupName = Label(group.Name);
        if (configuration.Relays.Count(item => Label(item.Name) == relayName) > 1) relayName += "@" + Label(relay.Id);
        if (relay.Groups.Count(item => Label(item.Name) == groupName) > 1) groupName += "@" + Label(group.Id);
        return relayName + "/" + groupName + "/" + model;
    }

    public static IEnumerable<PublishedModel> Enabled(HubConfiguration configuration) => configuration.Relays.Where(relay => relay.Enabled)
        .SelectMany(relay => relay.Groups.Where(group => group.Enabled).SelectMany(group => group.Models.Where(model => model.Enabled)
            .DistinctBy(model => model.Id, StringComparer.Ordinal).Select(model => new PublishedModel(RouteId(configuration, relay, group, model.Id), model.Id, relay, group))));

    private static string Label(string value) => value.Replace("%", "%25", StringComparison.Ordinal)
        .Replace("/", "%2F", StringComparison.Ordinal).Replace("@", "%40", StringComparison.Ordinal)
        .Replace("\\", "%5C", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);
}
