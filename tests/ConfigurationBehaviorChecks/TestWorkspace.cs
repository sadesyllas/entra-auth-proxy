using EntraAuthProxy;
using System.Text.Json;

namespace ConfigurationBehaviorChecks;

internal sealed class TestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "entra-config-" + Guid.NewGuid().ToString("N"));
    public string UserProfile => Path.Combine(Root, "user");
    public string WorkingDirectory => Path.Combine(Root, "work");
    public string EnvironmentDirectory => Path.Combine(Root, "environment");
    public string DefaultBase => Path.Combine(UserProfile, ".config", "entraauthproxy");

    public TestWorkspace()
    {
        Directory.CreateDirectory(WorkingDirectory);
    }

    public ConfigurationLocations Resolve(string? profile = null, string? environment = null, string? explicitFile = null) =>
        ConfigurationLocations.Resolve(explicitFile, profile, environment, UserProfile, WorkingDirectory);

    public static void WriteJson(string path, object settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings));
    }

    public string[] Files() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Order().ToArray();

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
