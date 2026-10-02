using System.CommandLine;
using System.Text;
using EntraAuthProxy;

var configOption = new Option<FileInfo?>(
    aliases: new[] { "-c", "--config" },
    description: "The path to an explicit configuration file.");

var profileOption = new Option<string?>(
    name: "--profile",
    description: "Select a configuration folder under the application base directory.")
{
    Arity = ArgumentArity.ExactlyOne,
    ArgumentHelpName = "name"
};
profileOption.AddValidator(result =>
{
    var name = result.GetValueOrDefault<string?>();
    if (string.IsNullOrEmpty(name) ||
        name.EnumerateRunes().Any(rune => !Rune.IsLetter(rune) && !Rune.IsDigit(rune) && rune.Value != '-' && rune.Value != '_'))
    {
        result.ErrorMessage = "--profile requires a non-empty name containing only Unicode letters, decimal digits, hyphens, or underscores.";
    }
});

var rootCommand = new RootCommand("Entra ID Auth Proxy")
{
    configOption,
    profileOption
};
rootCommand.AddValidator(result =>
{
    if (result.FindResultFor(configOption) != null && result.FindResultFor(profileOption) != null)
    {
        result.ErrorMessage = "--profile cannot be combined with -c or --config.";
    }
});

rootCommand.SetHandler(async (FileInfo? configFileInfo, string? profileName) =>
{
    string? explicitConfigFile = configFileInfo?.FullName;

    if (explicitConfigFile != null && !File.Exists(explicitConfigFile))
    {
        Console.Error.WriteLine($"Error: Configuration file not found at {explicitConfigFile}");
        Environment.Exit(1);
        return;
    }

    var locations = ConfigurationLocations.Resolve(
        explicitConfigFile,
        profileName,
        Environment.GetEnvironmentVariable("ENTRAAUTHPROXY_CONFIG_DIR"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Directory.GetCurrentDirectory());

    var builder = WebApplication.CreateBuilder(args);
    var startup = StartupConfiguration.Load(builder.Configuration, locations);
    if (startup == null) return;

    var portStr = builder.Configuration["Port"];
    if (!string.IsNullOrEmpty(portStr) && int.TryParse(portStr, out var port))
    {
        builder.WebHost.UseUrls($"http://*:{port}");
    }

    startup.Logging.Configure(builder.Logging);

    var tokenProvider = new TokenProvider();
    builder.Services.AddSingleton(tokenProvider);

    var msalApp = await startup.AuthenticateAsync(tokenProvider);
    
    Console.WriteLine("Successfully authenticated with Entra ID.");
    
    builder.Services.AddSingleton(msalApp);

    builder.Services.AddHostedService<TokenRefreshService>();

    builder.Services.AddTokenInjectingProxy(builder.Configuration, startup.Headers);

    var app = builder.Build();
    app.MapReverseProxy();
    await app.RunAsync();

}, configOption, profileOption);

return await rootCommand.InvokeAsync(args);
