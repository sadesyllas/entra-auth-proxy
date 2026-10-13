using System.Text.Json;
using Microsoft.Extensions.Configuration.Json;

namespace EntraAuthProxy;

/// <summary>Normal watched JSON configuration with top-level value-shape metadata.</summary>
internal sealed class StartupJsonConfigurationSource : JsonConfigurationSource
{
    /// <summary>
    /// Resolves shape from the highest provider defining the setting. Merged
    /// children can retain overridden lower values; JSON metadata also distinguishes
    /// empty objects/arrays from null, which standard configuration flattens alike.
    /// </summary>
    internal static bool HasNonScalarValue(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root)
            return configuration.GetSection(key).GetChildren().Any();

        foreach (var provider in root.Providers.Reverse())
        {
            if (provider.GetChildKeys([], key).Any() ||
                provider is Provider json && json.GetValueKind(key) is JsonValueKind.Object or JsonValueKind.Array)
                return true;

            if (provider.TryGet(key, out _))
                return false;
        }

        return false;
    }

    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new Provider(this);
    }

    internal sealed class Provider(StartupJsonConfigurationSource source) : JsonConfigurationProvider(source)
    {
        private IReadOnlyDictionary<string, JsonValueKind> valueKinds = new Dictionary<string, JsonValueKind>();

        public JsonValueKind? GetValueKind(string key) => valueKinds.TryGetValue(key, out var kind) ? kind : null;

        public override void Load(Stream stream)
        {
            using var contents = new MemoryStream();
            stream.CopyTo(contents);
            var bytes = contents.ToArray();
            // The standard JSON parser disposes its input stream. Parse metadata
            // from the same bytes after it has applied normal parsing and errors,
            // using the same BOM-aware StreamReader decoding as that parser.
            base.Load(new MemoryStream(bytes));
            using var reader = new StreamReader(new MemoryStream(bytes));
            using var document = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
            });
            var kinds = new Dictionary<string, JsonValueKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                kinds[property.Name] = property.Value.ValueKind;
            valueKinds = kinds;
        }
    }
}
