using System.Diagnostics;
using EntraAuthProxy;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Identity.Client;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using static ConfigurationBehaviorChecks.TestAssertions;

namespace ConfigurationBehaviorChecks;

internal static class ConfigurationLoadingChecks
{
    public static async Task RunAsync()
    {
        var cases = 0;
        // Constructing a client does not sign in, register a persistent cache, or
        // access credential storage. It only provides a return marker for the seam.
        var authenticationResult = PublicClientApplicationBuilder.Create("11111111-1111-1111-1111-111111111111").Build();

        foreach (var environment in new string?[] { null, "", " \t\r\n" })
        foreach (var profile in new string?[] { null, "work" })
        {
            using var workspace = new TestWorkspace();
            SeedDistinctFiles(workspace);
            var globalMarker = profile == null ? "default-base" : "default-profile";
            var globalPath = profile == null
                ? Path.Combine(workspace.DefaultBase, "entraauthproxy.json")
                : Path.Combine(workspace.DefaultBase, "work", "entraauthproxy.json");
            await CheckLoadedAsync(workspace, workspace.Resolve(profile, environment), workspace.DefaultBase,
                [globalPath, Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json")],
                new Dictionary<string, string?>
                {
                    ["Marker"] = "local",
                    ["EntraAuth:TenantId"] = globalMarker + "-tenant",
                    ["EntraAuth:ClientId"] = "local-client",
                    ["EntraAuth:TargetScope"] = globalMarker + "-scope",
                    ["Port"] = "8100",
                    ["BaseOnly"] = profile == null ? "default-base" : null,
                    ["ProfileOnly"] = profile == null ? null : "default-profile",
                    ["LocalOnly"] = "local",
                    ["ExplicitOnly"] = null
                }, authenticationResult);
            cases++;
        }

        foreach (var profile in new string?[] { null, "work" })
        {
            using var workspace = new TestWorkspace();
            SeedDistinctFiles(workspace);
            var globalMarker = profile == null ? "environment-base" : "environment-profile";
            var globalPath = profile == null
                ? Path.Combine(workspace.EnvironmentDirectory, "entraauthproxy.json")
                : Path.Combine(workspace.EnvironmentDirectory, "work", "entraauthproxy.json");
            await CheckLoadedAsync(workspace, workspace.Resolve(profile, workspace.EnvironmentDirectory), workspace.EnvironmentDirectory,
                [globalPath],
                new Dictionary<string, string?>
                {
                    ["Marker"] = globalMarker,
                    ["EntraAuth:TenantId"] = globalMarker + "-tenant",
                    ["EntraAuth:ClientId"] = globalMarker + "-client",
                    ["Port"] = "8000",
                    ["BaseOnly"] = profile == null ? "environment-base" : null,
                    ["ProfileOnly"] = profile == null ? null : "environment-profile",
                    ["LocalOnly"] = null,
                    ["ExplicitOnly"] = null
                }, authenticationResult);
            cases++;
        }

        foreach (var useEnvironment in new[] { false, true })
        {
            using var workspace = new TestWorkspace();
            SeedDistinctFiles(workspace);
            var explicitPath = Path.Combine(workspace.WorkingDirectory, "explicit.json");
            await CheckLoadedAsync(workspace,
                workspace.Resolve(environment: useEnvironment ? workspace.EnvironmentDirectory : null, explicitFile: explicitPath),
                useEnvironment ? workspace.EnvironmentDirectory : workspace.DefaultBase,
                [explicitPath],
                new Dictionary<string, string?>
                {
                    ["Marker"] = "explicit",
                    ["EntraAuth:TenantId"] = "explicit-tenant",
                    ["EntraAuth:ClientId"] = "explicit-client",
                    ["BaseOnly"] = null,
                    ["ProfileOnly"] = null,
                    ["LocalOnly"] = null,
                    ["ExplicitOnly"] = "explicit"
                }, authenticationResult);
            cases++;
        }

        foreach (var environment in new string?[] { null, "", " \t\r\n" })
        foreach (var profile in new string?[] { null, "work" })
        {
            using var workspace = new TestWorkspace();
            var localPath = Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json");
            WriteGlobal(localPath, "local-only", "LocalOnly");
            if (profile != null)
                WriteGlobal(Path.Combine(workspace.DefaultBase, "entraauthproxy.json"), "ignored-base", "BaseOnly");
            await CheckLoadedAsync(workspace, workspace.Resolve(profile, environment), workspace.DefaultBase,
                [localPath], new Dictionary<string, string?>
                {
                    ["Marker"] = "local-only",
                    ["EntraAuth:TenantId"] = "local-only-tenant",
                    ["LocalOnly"] = "local-only",
                    ["BaseOnly"] = null
                }, authenticationResult);
            Require(!Directory.Exists(Path.Combine(workspace.DefaultBase, "work")), "local-only lookup does not create a profile folder or starter");
            cases++;
        }

        foreach (var useEnvironment in new[] { false, true })
        foreach (var profile in new string?[] { null, "work" })
        {
            using var workspace = new TestWorkspace();
            var expectedBase = useEnvironment ? workspace.EnvironmentDirectory : workspace.DefaultBase;
            var expectedStarter = profile == null
                ? Path.Combine(expectedBase, "entraauthproxy.json")
                : Path.Combine(expectedBase, "work", "entraauthproxy.json");
            // An unrelated base JSON must not supply a missing profile. An eligible
            // local JSON is covered above; under an environment base it is ignored.
            if (profile != null) WriteGlobal(Path.Combine(expectedBase, "entraauthproxy.json"), "ignored-base", "BaseOnly");
            if (useEnvironment) WriteGlobal(Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json"), "ignored-local", "LocalOnly");
            var before = workspace.Files();
            using var configuration = new ConfigurationManager();
            var startup = StartupConfiguration.Load(configuration, workspace.Resolve(profile, useEnvironment ? expectedBase : null));
            Require(startup == null, "starter generation tells startup to exit before authentication");
            Require(File.Exists(expectedStarter), "starter is written to the selected global location");
            Require(workspace.Files().SequenceEqual(before.Append(expectedStarter).Order()), "starter generation writes only the selected JSON");
            Require(configuration.Sources.Count == 0, "starter exit does not load unrelated JSON");
            cases++;
        }

        using (var workspace = new TestWorkspace())
        {
            var path = Path.Combine(workspace.EnvironmentDirectory, "work", "entraauthproxy.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not JSON");
            using var configuration = new ConfigurationManager();
            try
            {
                StartupConfiguration.Load(configuration, workspace.Resolve("work", workspace.EnvironmentDirectory));
                throw new InvalidOperationException("Malformed selected JSON unexpectedly loaded.");
            }
            catch (InvalidDataException)
            {
                Require(File.ReadAllText(path) == "not JSON", "invalid JSON is not replaced with a starter");
            }
            cases++;
        }

        Console.WriteLine($"PASS: {cases} production loading, overlay, starter, authentication-boundary, logging-mode, and watched-reload cases without sign-in or persistent cache access.");
    }

    private static async Task CheckLoadedAsync(
        TestWorkspace workspace,
        ConfigurationLocations locations,
        string expectedCacheDirectory,
        string[] expectedSources,
        Dictionary<string, string?> expectedSettings,
        IPublicClientApplication authenticationResult)
    {
        using var environment = new LoggingEnvironmentScope("Error", "Debug");
        var before = workspace.Files();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = workspace.WorkingDirectory, EnvironmentName = Environments.Production
        });
        using var configuration = builder.Configuration;
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["HostDefault"] = "must-clear" });
        var startup = StartupConfiguration.Load(configuration, locations);
        Require(startup != null, "existing eligible JSON prevents starter exit");
        Require(configuration["HostDefault"] == null, "standard host configuration sources are cleared");
        foreach (var setting in expectedSettings)
            Require(configuration[setting.Key] == setting.Value, $"effective setting {setting.Key}, expected {setting.Value ?? "absent"}");

        var sources = configuration.Sources.Cast<JsonConfigurationSource>().ToArray();
        Require(sources.Length == expectedSources.Length, "only selected configuration sources are loaded");
        for (var index = 0; index < sources.Length; index++)
        {
            Require(!sources[index].Optional && sources[index].ReloadOnChange, "required JSON keeps reload watching enabled");
            Require(sources[index].FileProvider!.GetFileInfo(sources[index].Path!).PhysicalPath == expectedSources[index], "JSON source path and ordering");
        }

        Require(startup!.Logging.DefaultLevel == LogLevel.Error && startup.Logging.AspNetCoreLevel == LogLevel.Debug,
            "logging overrides are captured under every production configuration-selection mode");
        startup.Logging.Configure(builder.Logging);
        await using var app = builder.Build();
        using var provider = new CapturingLoggerProvider();
        var factory = app.Services.GetRequiredService<ILoggerFactory>();
        factory.AddProvider(provider);
        LoggingChecks.AssertFiltering(factory, provider, LogLevel.Error, LogLevel.Debug);
        CheckNativeLogging(factory, provider, LogLevel.Trace, LogLevel.Error, LogLevel.Debug);

        var tokens = new TokenProvider();
        var called = false;
        var result = await startup.AuthenticateAsync(tokens, (cacheDirectory, loadedConfiguration, tokenProvider) =>
        {
            called = true;
            Require(cacheDirectory == expectedCacheDirectory, "authentication receives the base directory, independent of profile or explicit JSON");
            Require(ReferenceEquals(loadedConfiguration, configuration), "authentication receives the loaded production configuration");
            Require(ReferenceEquals(tokenProvider, tokens), "authentication receives the startup token provider");
            return Task.FromResult(authenticationResult);
        });
        Require(called && ReferenceEquals(result, authenticationResult), "startup invokes and returns the authentication boundary");
        Require(workspace.Files().SequenceEqual(before), "loading and the test authentication boundary do not write configuration or token caches");

        // Watch the real JSON source rather than calling Reload, including host
        // logging options. Environment changes must not trigger fresh resolution.
        using var changedEnvironment = new LoggingEnvironmentScope("invalid-after-startup", "invalid-after-startup");
        TestWorkspace.WriteJson(expectedSources[^1], new
        {
            Marker = "logging-reloaded",
            Logging = LoggingSettings("None", "None", "Critical", "Trace", "Trace")
        });
        var timeout = Stopwatch.StartNew();
        while (configuration["Marker"] != "logging-reloaded" ||
               !factory.CreateLogger("Microsoft.AspNetCore.Server").IsEnabled(LogLevel.Trace) ||
               !factory.CreateLogger("EntraAuthProxy.ProviderCategory").IsEnabled(LogLevel.Trace))
        {
            Require(timeout.Elapsed < TimeSpan.FromSeconds(10), "watched JSON reload updates configuration and native logging rules");
            await Task.Delay(50);
        }
        Require(configuration["Logging:LogLevel:Default"] == "None" && configuration["Logging:LogLevel:Microsoft.AspNetCore"] == "None",
            "watched JSON reload changes the configured default and ASP.NET Core levels");
        LoggingChecks.AssertFiltering(factory, provider, LogLevel.Error, LogLevel.Debug);
        CheckNativeLogging(factory, provider, LogLevel.Critical, LogLevel.Trace, LogLevel.Trace);
        Require(startup.Logging.DefaultLevel == LogLevel.Error && startup.Logging.AspNetCoreLevel == LogLevel.Debug,
            "captured override levels survive JSON reload and environment changes");
        Require(workspace.Files().SequenceEqual(before), "reload adds no configuration or token-cache files");
    }

    private static void CheckNativeLogging(ILoggerFactory factory, CapturingLoggerProvider provider,
        LogLevel otherCategory, LogLevel aspNetCoreSubcategory, LogLevel providerCategory)
    {
        LoggingChecks.AssertCategoryFiltering(factory, provider, "EntraAuthProxy.NativeCategory", otherCategory);
        LoggingChecks.AssertCategoryFiltering(factory, provider, "Microsoft.AspNetCore.Server", aspNetCoreSubcategory);
        LoggingChecks.AssertCategoryFiltering(factory, provider, "EntraAuthProxy.ProviderCategory", providerCategory);
    }

    private static object LoggingSettings(string defaultLevel, string aspNetCoreLevel, string otherCategory,
        string aspNetCoreSubcategory, string providerCategory) => new
    {
        LogLevel = new Dictionary<string, string>
        {
            ["Default"] = defaultLevel,
            ["Microsoft.AspNetCore"] = aspNetCoreLevel,
            ["EntraAuthProxy.NativeCategory"] = otherCategory,
            ["Microsoft.AspNetCore.Server"] = aspNetCoreSubcategory
        },
        Capture = new { LogLevel = new Dictionary<string, string> { ["EntraAuthProxy.ProviderCategory"] = providerCategory } }
    };

    private static void SeedDistinctFiles(TestWorkspace workspace)
    {
        WriteGlobal(Path.Combine(workspace.DefaultBase, "entraauthproxy.json"), "default-base", "BaseOnly");
        WriteGlobal(Path.Combine(workspace.DefaultBase, "work", "entraauthproxy.json"), "default-profile", "ProfileOnly");
        WriteGlobal(Path.Combine(workspace.EnvironmentDirectory, "entraauthproxy.json"), "environment-base", "BaseOnly");
        WriteGlobal(Path.Combine(workspace.EnvironmentDirectory, "work", "entraauthproxy.json"), "environment-profile", "ProfileOnly");
        WriteGlobal(Path.Combine(workspace.WorkingDirectory, "explicit.json"), "explicit", "ExplicitOnly");
        TestWorkspace.WriteJson(Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json"), new
        {
            Marker = "local",
            EntraAuth = new { ClientId = "local-client" },
            Port = 8100,
            LocalOnly = "local"
        });
    }

    private static void WriteGlobal(string path, string marker, string uniqueKey) =>
        TestWorkspace.WriteJson(path, new Dictionary<string, object>
        {
            ["Marker"] = marker,
            ["EntraAuth"] = new { TenantId = marker + "-tenant", ClientId = marker + "-client", TargetScope = marker + "-scope" },
            ["Port"] = 8000,
            ["Logging"] = LoggingSettings("Trace", "Critical", "Trace", "Error", "Debug"),
            [uniqueKey] = marker
        });
}
