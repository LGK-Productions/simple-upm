#:sdk Microsoft.NET.Sdk.Web
#:package NuGet.Versioning@7.9.0

using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using NuGet.Versioning;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolver = JsonCtx.Default;
    options.SerializerOptions.Converters.Add(new SemanticVersionJsonConverter());
});

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();

var baseUrl = app.Configuration.GetValue<Uri>("BaseUrl");
if (baseUrl is not { IsAbsoluteUri: true })
{
    logger.LogError("No BaseUrl set");
    return;
}
else
{
    logger.LogInformation("Using base-url: {BaseUrl}", baseUrl);
}

Dictionary<string, PackageInfo> packages = [];
foreach (var path in Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.tgz"))
{
    var manifest = ReadManifest(path);
    if (manifest is null)
        continue;

    (CollectionsMarshal.GetValueRefOrAddDefault(packages, manifest.Name, out _) ??= [])[manifest.Version] = new(path, manifest);

    logger.LogDebug("Registered package '{PackageName}@{Version}'", manifest.Name, manifest.Version);
}

if (packages.Count == 0)
{
    logger.LogWarning("No packages found");
}

app.MapGet("/-/all", () =>
{
    return packages.Select(x => PackageIndex.Create(baseUrl, x.Key, x.Value.Values))
        .ToDictionary(x => x.Name);
});

var group = app.MapGroup("/{packageName}");
group.MapGet("/", ([FromRoute] string packageName) =>
{
    if (!packages.TryGetValue(packageName, out var versionMap))
        return Results.NotFound();

    return Results.Ok(
        PackageIndex.Create(baseUrl, packageName, versionMap.Values)
    );
});
group.MapGet("/-/{version}.tgz", ([FromRoute] string packageName, [FromRoute] SemanticVersion version) =>
{
    if (!packages.TryGetValue(packageName, out var versionMap))
        return Results.NotFound();

    if (!versionMap.TryGetValue(version, out var versionInfo))
        return Results.NotFound();

    return Results.File(versionInfo.Path);
});

app.Run();

static PackageManifest? ReadManifest(string path)
{
    using var packageStream = File.OpenRead(path);
    using GZipStream gzipStream = new(packageStream, CompressionMode.Decompress);
    using TarReader reader = new(gzipStream);

    while (reader.GetNextEntry() is { } entry)
    {
        if (entry is not { Name: "package/package.json", DataStream: { } manifestStream })
            continue;

        return JsonSerializer.Deserialize(manifestStream, JsonCtx.Instance.PackageManifest);
    }

    return null;
}

sealed class PackageInfo : Dictionary<SemanticVersion, PackageVersionInfo>;
sealed record PackageVersionInfo(string Path, PackageManifest Manifest)
{
    public SemanticVersion Version => Manifest.Version;
}

sealed record PackageManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] SemanticVersion Version
)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

sealed record PackageIndex(
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dist-tags")] Dictionary<string, SemanticVersion> Tags,
    [property: JsonPropertyName("versions")] Dictionary<SemanticVersion, PackageVersion> Versions
)
{
    public static PackageIndex Create(Uri baseUrl, string packageName, IEnumerable<PackageVersionInfo> versions)
    {
        Dictionary<string, SemanticVersion> tags = [];
        if (versions.OrderByDescending(x => x.Version).FirstOrDefault() is { } latest)
            tags["latest"] = latest.Version;
        return new(
            Id: packageName,
            Name: packageName,
            tags,
            Versions: versions.Select(version => PackageVersion.Create(baseUrl, version.Manifest))
                .ToDictionary(x => x.Version)
        );
    }
}

sealed record PackageVersion(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] SemanticVersion Version,
    [property: JsonPropertyName("dist")] Dictionary<string, Uri> Distribution
)
{

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];

    public static PackageVersion Create(Uri baseUrl, PackageManifest manifest)
    {
        return new(
            Name: manifest.Name,
            Version: manifest.Version,
            Distribution: new()
            {
                { "tarball", new Uri(baseUrl, $"/{manifest.Name}/-/{manifest.Version}.tgz") }
            }
        )
        {
            ExtensionData = manifest.ExtensionData
        };
    }
}

[JsonSerializable(typeof(PackageManifest))]
[JsonSerializable(typeof(PackageIndex))]
[JsonSerializable(typeof(Dictionary<string, PackageIndex>))]
sealed partial class JsonCtx : JsonSerializerContext
{
    public static JsonCtx Instance => field ??= new(new JsonSerializerOptions()
    {
        Converters = { new SemanticVersionJsonConverter() }
    });
}

sealed class SemanticVersionJsonConverter : JsonConverter<SemanticVersion>
{
    public override SemanticVersion? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var stringValue = reader.GetString();
        return stringValue == null ? null : SemanticVersion.Parse(stringValue);
    }

    public override void Write(Utf8JsonWriter writer, SemanticVersion value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }

    public override SemanticVersion ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var propertyName = reader.GetString();
        return SemanticVersion.Parse(propertyName!);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, SemanticVersion value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.ToString());
    }
}
