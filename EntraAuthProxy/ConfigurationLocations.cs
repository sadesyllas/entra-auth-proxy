namespace EntraAuthProxy;

/// <summary>
/// Configuration lookup paths and the independent base directory used by MSAL.
/// A null local path means an environment-selected base suppresses local lookup.
/// </summary>
internal sealed record ConfigurationLocations(
    string BaseDirectory,
    string GlobalConfigurationPath,
    string? LocalConfigurationPath,
    string? ExplicitConfigurationPath)
{
    public const string FileName = "entraauthproxy.json";

    public string GlobalDirectory => Path.GetDirectoryName(GlobalConfigurationPath)!;

    /// <summary>
    /// Resolves paths without reading or creating files. The caller must validate
    /// the profile name and reject an explicit file combined with a profile first.
    /// Empty or whitespace environment values retain default and local lookup.
    /// </summary>
    public static ConfigurationLocations Resolve(
        string? explicitConfigFile,
        string? profileName,
        string? environmentDirectory,
        string userProfile,
        string workingDirectory)
    {
        bool isEnvironmentSet = !string.IsNullOrWhiteSpace(environmentDirectory);
        var baseDirectory = isEnvironmentSet
            ? environmentDirectory!
            : Path.Combine(userProfile, ".config", "entraauthproxy");
        var globalDirectory = profileName == null
            ? baseDirectory
            : Path.Combine(baseDirectory, profileName);

        return new ConfigurationLocations(
            baseDirectory,
            Path.Combine(globalDirectory, FileName),
            isEnvironmentSet ? null : Path.Combine(workingDirectory, FileName),
            explicitConfigFile == null ? null : Path.GetFullPath(explicitConfigFile, workingDirectory));
    }
}
