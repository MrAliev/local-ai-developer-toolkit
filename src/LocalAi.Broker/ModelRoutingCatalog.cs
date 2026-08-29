using System.Reflection;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class ModelRoutingCatalog
{
    private readonly IReadOnlyDictionary<string, ModelCatalogEntry> _modelsByTag;
    private readonly IReadOnlyDictionary<LocalTaskProfile, TaskRouteEntry> _routesByProfile;
    private readonly HashSet<string> _maintenanceAllowlist;

    private ModelRoutingCatalog(ModelRoutingCatalogDocument document)
    {
        SchemaVersion = document.SchemaVersion;
        CatalogVersion = document.CatalogVersion;
        Models = Array.AsReadOnly(document.Models.ToArray());
        Routes = Array.AsReadOnly(document.Routes.ToArray());
        MaintenanceAllowlist = Array.AsReadOnly(
            document.MaintenanceAllowlist.ToArray());
        _modelsByTag = Models.ToDictionary(model => model.Tag, StringComparer.Ordinal);
        _routesByProfile = Routes.ToDictionary(route => route.Profile);
        _maintenanceAllowlist = new HashSet<string>(
            MaintenanceAllowlist,
            StringComparer.Ordinal);
    }

    public int SchemaVersion { get; }

    public string CatalogVersion { get; }

    public IReadOnlyList<ModelCatalogEntry> Models { get; }

    public IReadOnlyList<TaskRouteEntry> Routes { get; }

    public IReadOnlyList<string> MaintenanceAllowlist { get; }

    public ModelCatalogEntry Model(string tag) =>
        _modelsByTag.TryGetValue(tag, out var model)
            ? model
            : throw new KeyNotFoundException($"Model '{tag}' is not in the routing catalog.");

    public TaskRouteEntry Route(LocalTaskProfile profile) =>
        _routesByProfile.TryGetValue(profile, out var route)
            ? route
            : throw new KeyNotFoundException(
                $"Task profile '{profile}' is not in the routing catalog.");

    public bool IsMaintenanceAllowed(string tag) =>
        _maintenanceAllowlist.Contains(tag);

    /// <summary>
    /// Whether routed chat must ask Ollama to switch the model's reasoning off. False for a
    /// model outside the catalog: the transport also carries native passthrough traffic, and
    /// an unknown tag there is the caller's own request to leave untouched.
    /// </summary>
    public bool DisablesThinking(string tag) =>
        _modelsByTag.TryGetValue(tag, out var model) && model.DisableThinking;

    public static ModelRoutingCatalog LoadEmbedded()
    {
        // The document itself is owned by LocalAi.Contracts so the installer can read the
        // same copy; validation and routing stay here.
        var document = ModelRoutingCatalogResource.LoadDocument();
        Validate(document);
        return new ModelRoutingCatalog(document);
    }

    private static void Validate(ModelRoutingCatalogDocument document)
    {
        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported model routing schema '{document.SchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(document.CatalogVersion))
        {
            throw new InvalidDataException("Catalog version cannot be blank.");
        }

        var models = document.Models
            ?? throw new InvalidDataException("Models are required.");
        if (models.Count == 0)
        {
            throw new InvalidDataException("At least one model is required.");
        }

        var modelsByTag = new Dictionary<string, ModelCatalogEntry>(
            StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Tag) ||
                string.IsNullOrWhiteSpace(model.Source))
            {
                throw new InvalidDataException("Model tag and source are required.");
            }

            if (!modelsByTag.TryAdd(model.Tag, model))
            {
                throw new InvalidDataException($"Duplicate model tag '{model.Tag}'.");
            }

            if (!Enum.IsDefined(model.Lifecycle) ||
                !Enum.IsDefined(model.InstallPolicy) ||
                model.Capabilities is null ||
                model.Capabilities.Count == 0 ||
                model.Capabilities.Any(capability => !Enum.IsDefined(capability)))
            {
                throw new InvalidDataException(
                    $"Model '{model.Tag}' has invalid lifecycle or capabilities.");
            }

            if (model.ContextTokens is null ||
                model.ContextTokens.Count == 0 ||
                model.ContextTokens.Distinct().Count() != model.ContextTokens.Count ||
                model.ContextTokens.Any(context => !LocalContextTiers.IsSupported(context)))
            {
                throw new InvalidDataException(
                    $"Model '{model.Tag}' has unsupported context tiers.");
            }

            if (model.MaxImagePixels is < 1 || !model.SupportsImages && model.MaxImagePixels is not null)
            {
                throw new InvalidDataException(
                    $"Model '{model.Tag}' has invalid image constraints.");
            }
        }

        var routes = document.Routes
            ?? throw new InvalidDataException("Routes are required.");
        if (routes.Count != Enum.GetValues<LocalTaskProfile>().Length ||
            routes.Select(route => route.Profile).Distinct().Count() != routes.Count)
        {
            throw new InvalidDataException(
                "Every task profile must have exactly one route.");
        }

        foreach (var route in routes)
        {
            if (!Enum.IsDefined(route.Profile) ||
                !Enum.IsDefined(route.Mode) ||
                !Enum.IsDefined(route.Validator) ||
                !Enum.IsDefined(route.DefaultDuration) ||
                route.Candidates is null ||
                route.Fallbacks is null)
            {
                throw new InvalidDataException(
                    $"Route '{route.Profile}' is invalid.");
            }

            if (route.Mode == LocalRouteMode.Model &&
                (route.Candidates.Count == 0 || route.Fallbacks.Count == 0))
            {
                throw new InvalidDataException(
                    $"Model route '{route.Profile}' requires candidates and fallbacks.");
            }

            if (route.Mode == LocalRouteMode.Deterministic &&
                (route.Candidates.Count != 0 || route.Fallbacks.Count != 0))
            {
                throw new InvalidDataException(
                    $"Deterministic route '{route.Profile}' cannot name models.");
            }

            foreach (var tag in route.Candidates.Concat(route.Fallbacks))
            {
                if (!modelsByTag.ContainsKey(tag))
                {
                    throw new InvalidDataException(
                        $"Route '{route.Profile}' references unknown model '{tag}'.");
                }
            }
        }

        var allowlist = document.MaintenanceAllowlist
            ?? throw new InvalidDataException("Maintenance allowlist is required.");
        if (allowlist.Count != allowlist.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("Maintenance allowlist contains duplicates.");
        }

        foreach (var tag in allowlist)
        {
            if (!modelsByTag.TryGetValue(tag, out var model) ||
                model.Lifecycle != LocalModelLifecycle.Experimental &&
                model.InstallPolicy != LocalModelInstallPolicy.Recommended)
            {
                throw new InvalidDataException(
                    $"Maintenance model '{tag}' is not recommended or experimental.");
            }
        }
    }
}
