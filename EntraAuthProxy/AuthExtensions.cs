using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace EntraAuthProxy;

public static class AuthExtensions 
{
    public static async Task<IPublicClientApplication> BuildAndAuthenticateAsync(string configDir, IConfiguration configuration, TokenProvider tokenProvider)
    {
        var clientId = configuration["EntraAuth:ClientId"];
        var tenantId = configuration["EntraAuth:TenantId"];
        var targetScope = configuration["EntraAuth:TargetScope"];
        var redirectPortStr = configuration["RedirectPort"] ?? configuration["Port"];

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(targetScope))
        {
            throw new InvalidOperationException("EntraAuth:ClientId or EntraAuth:TargetScope is missing in settings.");
        }

        var builder = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId);

        if (!string.IsNullOrEmpty(redirectPortStr) && int.TryParse(redirectPortStr, out var redirectPort))
        {
            builder = builder.WithRedirectUri($"http://localhost:{redirectPort}");
        }
        else
        {
            builder = builder.WithDefaultRedirectUri();
        }

        var app = builder.Build();

        var cacheKeyString = $"{tenantId}_{clientId}_{targetScope}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKeyString));
        var hashString = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var cacheFileName = $"EntraAuthProxy_{hashString}";

        var cacheHelper = await CreateCacheHelperAsync(configDir, cacheFileName);
        cacheHelper.RegisterCache(app.UserTokenCache);

        var scopes = new[] { targetScope };
        AuthenticationResult result;

        var envVar = Environment.GetEnvironmentVariable("ENTRAAUTHPROXY_FORCE_INTERACTIVE");
        bool forceInteractive = envVar != null
            ? envVar.ToLowerInvariant() == "true"
            : configuration.GetValue<bool>("ForceInteractiveAuthentication");

        if (forceInteractive)
        {
            result = await app.AcquireTokenInteractive(scopes).ExecuteAsync();
        }
        else
        {
            try
            {
                var accounts = await app.GetAccountsAsync();
                var firstAccount = accounts.FirstOrDefault();
                result = await app.AcquireTokenSilent(scopes, firstAccount).ExecuteAsync();
            }
            catch (MsalUiRequiredException)
            {
                result = await app.AcquireTokenInteractive(scopes).ExecuteAsync();
            }
        }

        tokenProvider.SetToken(result.AccessToken);
        return app;
    }

    private static async Task<MsalCacheHelper> CreateCacheHelperAsync(string configDir, string cacheFileName)
    {
        var storageProperties = new StorageCreationPropertiesBuilder(cacheFileName, configDir)
            .WithMacKeyChain("EntraAuthProxy", cacheFileName)
            .WithLinuxKeyring(cacheFileName, "default", cacheFileName, 
                new KeyValuePair<string, string>("MsalClientID", "EntraAuthProxy"),
                new KeyValuePair<string, string>("MsalClientVersion", "1.0.0.0"))
            .Build();

        var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties);
        cacheHelper.VerifyPersistence();
        return cacheHelper;
    }
}