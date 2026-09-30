using System.CommandLine;
using EntraInjectorProxy;

var configOption = new Option<FileInfo?>(
    aliases: new[] { "-c", "--config" },
    description: "The path to an explicit configuration file.");

var rootCommand = new RootCommand("Entra ID Injector Proxy")
{
    configOption
};

rootCommand.SetHandler(async (FileInfo? configFileInfo) =>
{
    string? explicitConfigFile = configFileInfo?.FullName;

    if (explicitConfigFile != null && !File.Exists(explicitConfigFile))
    {
        Console.Error.WriteLine($"Error: Configuration file not found at {explicitConfigFile}");
        Environment.Exit(1);
        return;
    }

    var configFileName = "entrainjectorproxy.json";
    var envConfigDir = Environment.GetEnvironmentVariable("ENTRAINJECTORPROXY_CONFIG_DIR");
    var localConfigPath = Path.Combine(Directory.GetCurrentDirectory(), configFileName);

    string globalConfigDir;

    bool isEnvSet = !string.IsNullOrWhiteSpace(envConfigDir);

    if (isEnvSet)
    {
        globalConfigDir = envConfigDir!;
    }
    else
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        globalConfigDir = Path.Combine(userProfile, ".config", "entrainjectorproxy");
    }

    string globalConfigPath = Path.Combine(globalConfigDir, configFileName);

    Directory.CreateDirectory(globalConfigDir);

    var builder = WebApplication.CreateBuilder(args);
    builder.Configuration.Sources.Clear();

    if (explicitConfigFile != null)
    {
        builder.Configuration.AddJsonFile(Path.GetFullPath(explicitConfigFile), optional: false, reloadOnChange: true);
    }
    else
    {
        bool envOrGlobalExists = File.Exists(globalConfigPath);
        bool localExists = !isEnvSet && File.Exists(localConfigPath);

        if (!envOrGlobalExists && !localExists)
        {
            var defaultConfig = @"{
  ""EntraAuth"": {
    ""TenantId"": ""organizations"",
    ""ClientId"": """",
    ""TargetScope"": ""api://<app-id>/.default""
  },
  ""TargetAddress"": ""https://api.<provider>.com/"",
  ""Port"": 5000,
  ""RedirectPort"": 5000,
  ""DangerousAcceptAnyServerCertificate"": false,
  ""ForceInteractiveAuthentication"": false
}";
            File.WriteAllText(globalConfigPath, defaultConfig);
            Console.WriteLine($"Created default configuration at {globalConfigPath}. Please update it with your ClientId and TargetScope, then restart.");
            return;
        }

        if (envOrGlobalExists)
        {
            builder.Configuration.AddJsonFile(globalConfigPath, optional: false, reloadOnChange: true);
        }

        if (!isEnvSet && localExists)
        {
            builder.Configuration.AddJsonFile(localConfigPath, optional: false, reloadOnChange: true);
        }
    }

    var portStr = builder.Configuration["Port"];
    if (!string.IsNullOrEmpty(portStr) && int.TryParse(portStr, out var port))
    {
        builder.WebHost.UseUrls($"http://*:{port}");
    }

    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options =>
    {
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        options.UseUtcTimestamp = true;
        options.SingleLine = true;
    });
    builder.Logging.SetMinimumLevel(LogLevel.Information);
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

    var tokenProvider = new TokenProvider();
    builder.Services.AddSingleton(tokenProvider);

    var msalApp = await AuthExtensions.BuildAndAuthenticateAsync(globalConfigDir, builder.Configuration, tokenProvider);
    
    Console.WriteLine("Successfully authenticated with Entra ID.");
    
    builder.Services.AddSingleton(msalApp);

    builder.Services.AddHostedService<TokenRefreshService>();

    builder.Services.AddTokenInjectingProxy(builder.Configuration);

    var app = builder.Build();
    app.MapReverseProxy();
    await app.RunAsync();

}, configOption);

return await rootCommand.InvokeAsync(args);