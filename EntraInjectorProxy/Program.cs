using EntraInjectorProxy;
using Yarp.ReverseProxy.Transforms;

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
  ""DangerousAcceptAnyServerCertificate"": false
}";
    File.WriteAllText(globalConfigPath, defaultConfig);
    Console.WriteLine($"Created default configuration at {globalConfigPath}. Please update it with your ClientId and TargetScope, then restart.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.Sources.Clear();

if (envOrGlobalExists)
{
    builder.Configuration.AddJsonFile(globalConfigPath, optional: false, reloadOnChange: true);
}

if (!isEnvSet && localExists)
{
    builder.Configuration.AddJsonFile(localConfigPath, optional: false, reloadOnChange: true);
}

var portStr = builder.Configuration["Port"];
if (!string.IsNullOrEmpty(portStr) && int.TryParse(portStr, out var port))
{
    builder.WebHost.UseUrls($"http://*:{port}");
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var tokenProvider = new TokenProvider();
builder.Services.AddSingleton(tokenProvider);

var msalApp = await AuthExtensions.BuildAndAuthenticateAsync(globalConfigDir, builder.Configuration, tokenProvider);
builder.Services.AddSingleton(msalApp);

builder.Services.AddHostedService<TokenRefreshService>();

var yarpConfig = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> {
        {"Routes:catch-all:ClusterId", "target-cluster"},
        {"Routes:catch-all:Match:Path", "{**catch-all}"},
        {"Routes:catch-all:Metadata:VirtualKey", builder.Configuration["VirtualKey"] ?? string.Empty},
        {"Clusters:target-cluster:Destinations:destination1:Address", builder.Configuration["TargetAddress"] ?? string.Empty},
        {"Clusters:target-cluster:HttpClient:DangerousAcceptAnyServerCertificate", builder.Configuration["DangerousAcceptAnyServerCertificate"] ?? "false"}
    })
    .AddConfiguration(builder.Configuration.GetSection("ReverseProxy"))
    .Build();

builder.Services.AddReverseProxy()
    .LoadFromConfig(yarpConfig)
    .AddTransforms(builderContext =>
    {
        builderContext.AddRequestTransform(transformContext =>
        {
            var provider = transformContext.HttpContext.RequestServices.GetRequiredService<TokenProvider>();
            var token = provider.GetToken();
            if (!string.IsNullOrEmpty(token))
            {
                transformContext.ProxyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var proxyFeature = transformContext.HttpContext.GetReverseProxyFeature();
            var metadata = proxyFeature?.Route?.Config?.Metadata;

            if (metadata != null && 
                metadata.TryGetValue("VirtualKey", out var virtualKey) &&
                !string.IsNullOrWhiteSpace(virtualKey))
            {
                transformContext.ProxyRequest.Headers.TryAddWithoutValidation("x-bf-vk", virtualKey);
            }

            return ValueTask.CompletedTask;
        });
    });

var app = builder.Build();
app.MapReverseProxy();
app.Run();