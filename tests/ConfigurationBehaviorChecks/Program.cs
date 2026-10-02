using ConfigurationBehaviorChecks;
using static ConfigurationBehaviorChecks.TestAssertions;

using var workspace = new TestWorkspace();
var defaultBase = Path.Combine(workspace.UserProfile, ".config", "entraauthproxy");
var localPath = Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json");
var explicitPath = Path.Combine(workspace.WorkingDirectory, "explicit.json");
var cases = 0;

foreach (var environment in new string?[] { null, "", " \t\r\n" })
{
    var defaults = workspace.Resolve(environment: environment);
    Require(defaults.BaseDirectory == defaultBase, "default base for ineffective environment values");
    Require(defaults.GlobalDirectory == defaultBase, "unprofiled global directory");
    Require(defaults.GlobalConfigurationPath == Path.Combine(defaultBase, "entraauthproxy.json"), "default global JSON");
    Require(defaults.LocalConfigurationPath == localPath, "eligible local JSON");
    Require(defaults.ExplicitConfigurationPath == null, "no explicit selection");

    var profile = workspace.Resolve("work-prod_2", environment);
    Require(profile.BaseDirectory == defaultBase, "default profile cache base");
    Require(profile.GlobalDirectory == Path.Combine(defaultBase, "work-prod_2"), "default profile directory");
    Require(profile.GlobalConfigurationPath == Path.Combine(defaultBase, "work-prod_2", "entraauthproxy.json"), "default profile JSON");
    Require(profile.LocalConfigurationPath == localPath, "profile local eligibility");

    var explicitFile = workspace.Resolve(environment: environment, explicitFile: "explicit.json");
    Require(explicitFile.ExplicitConfigurationPath == explicitPath, "relative explicit file resolution");
    Require(explicitFile.BaseDirectory == defaultBase, "explicit file default cache base");
    cases += 3;
}

var environmentOnly = workspace.Resolve(environment: workspace.EnvironmentDirectory);
Require(environmentOnly.BaseDirectory == workspace.EnvironmentDirectory, "complete environment base without extra segment");
Require(environmentOnly.GlobalConfigurationPath == Path.Combine(workspace.EnvironmentDirectory, "entraauthproxy.json"), "environment JSON");
Require(environmentOnly.LocalConfigurationPath == null, "environment suppresses local lookup");

var environmentProfile = workspace.Resolve("παραγωγή_٢", workspace.EnvironmentDirectory);
Require(environmentProfile.BaseDirectory == workspace.EnvironmentDirectory, "environment profile cache base");
Require(environmentProfile.GlobalConfigurationPath == Path.Combine(workspace.EnvironmentDirectory, "παραγωγή_٢", "entraauthproxy.json"), "environment profile JSON");
Require(environmentProfile.LocalConfigurationPath == null, "environment profile suppresses local lookup");

var environmentExplicit = workspace.Resolve(environment: workspace.EnvironmentDirectory, explicitFile: explicitPath);
Require(environmentExplicit.ExplicitConfigurationPath == explicitPath, "absolute explicit file selection");
Require(environmentExplicit.BaseDirectory == workspace.EnvironmentDirectory, "explicit file environment cache base");
Require(Directory.GetFileSystemEntries(workspace.Root).SequenceEqual(new[] { workspace.WorkingDirectory }), "resolution has no filesystem side effects");
cases += 3;

Console.WriteLine($"PASS: {cases} production configuration-location cases without authentication.");

await ConfigurationLoadingChecks.RunAsync();
