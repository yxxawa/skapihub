using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Skapihub.Core;

public sealed class UiAssets
{
    private static readonly string[] Names = ["index.html", "app.css", "app.js", "logo.svg"];
    private readonly Dictionary<string, byte[]> _files;
    public string BuildId { get; }
    public string Html { get; }
    public string EntryPath => "/?ui=" + BuildId;

    public UiAssets(string? webRoot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var useFiles = webRoot is not null && Names.All(name => File.Exists(Path.Combine(webRoot, name)));
        _files = Names.ToDictionary(name => "/" + name, name => useFiles ? File.ReadAllBytes(Path.Combine(webRoot!, name)) : Resource("Skapihub.Ui." + name));
        foreach (var name in Names) { hash.AppendData(Encoding.UTF8.GetBytes(name)); hash.AppendData(_files["/" + name]); }
        BuildId = Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
        var html = Encoding.UTF8.GetString(_files["/index.html"]);
        foreach (var name in Names.Where(name => name != "index.html"))
            html = html.Replace("\"/" + name + "\"", "\"/" + name + "?ui=" + BuildId + "\"", StringComparison.Ordinal);
        Html = html.Replace("</head>", "<meta name=\"skapihub-build\" content=\"" + BuildId + "\"></head>", StringComparison.Ordinal);
    }

    public bool TryGet(string path, out byte[] bytes, out string contentType)
    {
        contentType = Path.GetExtension(path) switch { ".css" => "text/css; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
        return _files.TryGetValue(path, out bytes!);
    }

    public static string Notices => string.Join("\n\n", typeof(UiAssets).Assembly.GetManifestResourceNames().Where(name => name.StartsWith("Skapihub.Legal.", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
        .Select(name => Encoding.UTF8.GetString(Resource(name))));

    private static byte[] Resource(string name)
    {
        using var stream = typeof(UiAssets).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("缺少程序内嵌资源：" + name);
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }
}
