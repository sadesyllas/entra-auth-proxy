using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace EntraInjectorProxy;

public static class AuthExtensions 
{
    public static async Task<IPublicClientApplication> BuildAndAuthenticateAsync(string configDir, IConfiguration configuration, TokenProvider tokenProvider)
    {
        var clientId = configuration["EntraAuth:ClientId"];
        var tenantId = configuration["EntraAuth:TenantId"] ?? "organizations";
        var targetScope = configuration["EntraAuth:TargetScope"];

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(targetScope))
        {
            throw new InvalidOperationException("EntraAuth:ClientId or EntraAuth:TargetScope is missing in settings.json");
        }

        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithRedirectUri("http://localhost:34527")
            .Build();

        var cacheHelper = await CreateCacheHelperAsync(configDir);
        cacheHelper.RegisterCache(app.UserTokenCache);

        var scopes = new[] { targetScope };
        AuthenticationResult result;

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

        tokenProvider.SetToken(result.AccessToken);
        return app;
    }

    private static async Task<MsalCacheHelper> CreateCacheHelperAsync(string configDir)
    {
        var storageProperties = new StorageCreationPropertiesBuilder("msal.cache", configDir)
            .WithMacKeyChain("EntraInjectorProxy", "msal.cache")
            .WithLinuxKeyring("msal.cache", "default", "EntraInjectorProxy", 
                new KeyValuePair<string, string>("MsalClientID", "EntraInjectorProxy"),
                new KeyValuePair<string, string>("MsalClientVersion", "1.0.0.0"))
            .Build();

        var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties);
        cacheHelper.VerifyPersistence();
        return cacheHelper;
    }
}