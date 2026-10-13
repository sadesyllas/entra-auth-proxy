using EntraAuthProxy;
using System.Text;
using System.Text.Json;

namespace ConfigurationBehaviorChecks;

internal sealed class TestWorkspace : IDisposable
{
    private readonly HashSet<IDisposable> fileProviders = [];
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

    /// <summary>
    /// Loads through the production boundary and owns file providers created for
    /// this workspace's absolute JSON paths. ConfigurationManager disposes change
    /// subscriptions but does not own these explicitly supplied file providers.
    /// Track them even when startup validation fails, so later directory deletion
    /// cannot leave watchers trying to resubscribe to removed test directories.
    /// </summary>
    public StartupConfiguration? LoadStartup(ConfigurationManager configuration, ConfigurationLocations locations)
    {
        try
        {
            return StartupConfiguration.Load(configuration, locations);
        }
        finally
        {
            foreach (var source in configuration.Sources.OfType<FileConfigurationSource>())
                if (source.FileProvider is IDisposable provider)
                    fileProviders.Add(provider);
        }
    }

    public static void WriteJson(string path, object settings, Encoding? encoding = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings), encoding ?? new UTF8Encoding(false));
    }

    public string[] Files() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Order().ToArray();

    public void Dispose()
    {
        foreach (var provider in fileProviders) provider.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
