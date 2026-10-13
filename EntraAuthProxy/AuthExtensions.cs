using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace EntraAuthProxy;

public static class AuthExtensions
{
    /// <summary>
    /// Validates callback configuration, builds the MSAL client, registers its
    /// persistent cache, and publishes the silently or interactively acquired token.
    /// </summary>
    public static Task<IPublicClientApplication> BuildAndAuthenticateAsync(string configDir, IConfiguration configuration, TokenProvider tokenProvider) =>
        BuildAndAuthenticateAsync(configDir, configuration, tokenProvider, CallbackSettings.Capture(configuration));

    /// <summary>Constructs the production MSAL client without cache access or sign-in.</summary>
    internal static IPublicClientApplication BuildClient(IConfiguration configuration, CallbackSettings callback)
    {
        var clientId = configuration["EntraAuth:ClientId"];
        var tenantId = configuration["EntraAuth:TenantId"];
        var targetScope = configuration["EntraAuth:TargetScope"];

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(targetScope))
        {
            throw new InvalidOperationException("EntraAuth:ClientId or EntraAuth:TargetScope is missing in settings.");
        }

        var builder = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId);

        return callback.Configure(builder).Build();
    }

    internal static async Task<IPublicClientApplication> BuildAndAuthenticateAsync(
        string configDir, IConfiguration configuration, TokenProvider tokenProvider, CallbackSettings callback)
    {
        var app = BuildClient(configuration, callback);
        var clientId = configuration["EntraAuth:ClientId"];
        var tenantId = configuration["EntraAuth:TenantId"];
        var targetScope = configuration["EntraAuth:TargetScope"]!;

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
