using Microsoft.Identity.Client;

namespace EntraAuthProxy;

/// <summary>Loads JSON, captures startup settings, and retains the authentication base directory.</summary>
internal sealed class StartupConfiguration
{
    private readonly ConfigurationLocations locations;
    private readonly IConfiguration configuration;

    private StartupConfiguration(ConfigurationLocations locations, IConfiguration configuration)
    {
        this.locations = locations;
        this.configuration = configuration;
        Logging = StartupLogging.CaptureEnvironment();
        Headers = CustomRequestHeaders.Capture(configuration);
    }

    public CustomRequestHeaders Headers { get; }

    public StartupLogging Logging { get; }

    /// <summary>
    /// Replaces host configuration sources with the explicit JSON or selected global
    /// JSON plus any eligible local overlay. Returns null after writing and reporting
    /// a missing configuration's starter, so the caller exits before authentication.
    /// Existing JSON remains required and watched for changes; loading errors propagate.
    /// Captures and validates logging overrides and Headers before authentication
    /// or persistent cache access. A starter exit does not validate overrides.
    /// </summary>
    public static StartupConfiguration? Load(ConfigurationManager configuration, ConfigurationLocations locations)
    {
        Directory.CreateDirectory(locations.BaseDirectory);
        configuration.Sources.Clear();

        if (locations.ExplicitConfigurationPath != null)
        {
            AddJson(configuration, locations.ExplicitConfigurationPath);
        }
        else
        {
            bool globalExists = File.Exists(locations.GlobalConfigurationPath);
            bool localExists = locations.LocalConfigurationPath != null && File.Exists(locations.LocalConfigurationPath);

            if (!globalExists && !localExists)
            {
                Directory.CreateDirectory(locations.GlobalDirectory);
                File.WriteAllText(locations.GlobalConfigurationPath, DefaultConfiguration);
                Console.WriteLine($"Created default configuration at {locations.GlobalConfigurationPath}. Please update it with your TenantId, ClientId, and TargetScope, then restart.");
                return null;
            }

            if (globalExists) AddJson(configuration, locations.GlobalConfigurationPath);
            if (localExists) AddJson(configuration, locations.LocalConfigurationPath!);
        }

        return new StartupConfiguration(locations, configuration);
    }

    /// <summary>
    /// Calls the authentication boundary with the base cache directory and loaded
    /// configuration. The optional delegate permits checks without sign-in or OS
    /// cache access; normal startup uses the existing MSAL implementation.
    /// </summary>
    public Task<IPublicClientApplication> AuthenticateAsync(
        TokenProvider tokenProvider,
        Func<string, IConfiguration, TokenProvider, Task<IPublicClientApplication>>? authenticate = null) =>
        (authenticate ?? AuthExtensions.BuildAndAuthenticateAsync)(locations.BaseDirectory, configuration, tokenProvider);

    private static void AddJson(ConfigurationManager configuration, string path) =>
        configuration.AddJsonFile(path, optional: false, reloadOnChange: true);

    private const string DefaultConfiguration = @"{
  ""EntraAuth"": {
    ""TenantId"": ""Entra tenant ID"",
    ""ClientId"": """",
    ""TargetScope"": ""api://<app-id>/.default""
  },
  ""TargetAddress"": ""https://api.<provider>.com/"",
  ""Headers"": {},
  ""Port"": 5000,
  ""RedirectPort"": 5000,
  ""DangerousAcceptAnyServerCertificate"": false,
  ""ForceInteractiveAuthentication"": false
}";
}
