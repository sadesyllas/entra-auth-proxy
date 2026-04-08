using EntraInjectorProxy;
using Yarp.ReverseProxy.Transforms;

var appName = "EntraInjectorProxy";
var configDir = Environment.GetEnvironmentVariable("ENTRAINJECTORPROXY_CONFIG_DIR");
if (string.IsNullOrWhiteSpace(configDir))
{
    var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    configDir = Path.Combine(userProfile, ".config", appName);
}
var configPath = Path.Combine(configDir, "settings.json");

Directory.CreateDirectory(configDir);

if (!File.Exists(configPath))
{
    var defaultConfig = @"{
  ""EntraAuth"": {
    ""TenantId"": ""organizations"",
    ""ClientId"": """",
    ""TargetScope"": ""api://<app-id>/.default""
  },
  ""ReverseProxy"": {
    ""Routes"": {
      ""catch-all"": {
        ""ClusterId"": ""target-cluster"",
        ""Match"": {
          ""Path"": ""{**catch-all}""
        },
        ""Metadata"": {
            ""VirtualKey"": ""optional-virtual-key""
        }
      }
    },
    ""Clusters"": {
      ""target-cluster"": {
        ""Destinations"": {
          ""destination1"": {
            ""Address"": ""https://api.openai.com/""
          }
        }
      }
    }
  }
}";
    File.WriteAllText(configPath, defaultConfig);
    Console.WriteLine($"Created default configuration at {configPath}. Please update it with your ClientId and TargetScope, then restart.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var tokenProvider = new TokenProvider();
builder.Services.AddSingleton(tokenProvider);

var msalApp = await AuthExtensions.BuildAndAuthenticateAsync(configDir, builder.Configuration, tokenProvider);
builder.Services.AddSingleton(msalApp);

builder.Services.AddHostedService<TokenRefreshService>();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
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
                metadata.TryGetValue("VirtualKey", out var virtualKey))
            {
                transformContext.ProxyRequest.Headers.TryAddWithoutValidation("x-bf-vk", virtualKey);
            }

            return ValueTask.CompletedTask;
        });
    });

var app = builder.Build();
app.MapReverseProxy();
app.Run();