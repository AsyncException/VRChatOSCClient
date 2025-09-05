using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRChatOSCClient.OpenVR;

public record AppManifest(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("applications")] IReadOnlyList<Application> Applications
) {
    public void WriteToFile(string directoryPath) {
        if (!Directory.Exists(directoryPath)) {
            Directory.CreateDirectory(directoryPath);
        }

        string filePath = Path.Combine(directoryPath, "app.vrmanifest");
        using FileStream stream = File.OpenWrite(filePath);
        JsonSerializer.Serialize(stream, this, AppManifestTypeInfo.Default.AppManifest);
    }
}

public record Application(
    [property: JsonPropertyName("app_key")] string AppKey,
    [property: JsonPropertyName("launch_type")] string LaunchType,
    [property: JsonPropertyName("binary_path_windows")] string BinaryPathWindows,
    [property: JsonPropertyName("is_dashboard_overlay")] bool IsDashboardOverlay,
    [property: JsonPropertyName("strings")] Strings Strings
);

public record EnUs(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description
);

public record Strings(
    [property: JsonPropertyName("en_us")] EnUs EnUs
);

[JsonSerializable(typeof(AppManifest))]
[JsonSerializable(typeof(Application))]
[JsonSerializable(typeof(EnUs))]
[JsonSerializable(typeof(Strings))]
internal partial class AppManifestTypeInfo : JsonSerializerContext;

public class AppManifestBuilder {
    public required string Source { get; set; }
    public required string AppKey { get; set; }
    public required string LaunchType { get; set; }
    public required string BinaryPathWindows { get; set; }
    public required bool IsDashboardOverlay { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }

    public AppManifest Build() => new(Source, [new(AppKey, LaunchType, BinaryPathWindows, IsDashboardOverlay, new(new(Name, Description)))]);
}

#if DEBUG

public static class AppManifestExample {
    public static void Example() {
        AppManifest manifest = new AppManifestBuilder() {
            Source = "builtin",
            AppKey = "application.async",
            LaunchType = "binary",
            BinaryPathWindows = "exe file name with extension",
            IsDashboardOverlay = true,
            Name = "application name",
            Description = "A short discription of the app"
        }.Build();

        manifest.WriteToFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "application name"));
    }
}

#endif