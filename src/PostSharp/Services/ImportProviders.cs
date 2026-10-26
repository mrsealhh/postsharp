using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Parses one import format without applying it to the workspace.</summary>
public interface IImportProvider
{
    ImportFileKind Kind { get; }

    string FormatName { get; }

    ImportPayload Parse(string fileName, string content, ImportProviderContext context);
}

/// <summary>Resolves import providers by detected file kind.</summary>
public sealed class ImportProviderRegistry
{
    private readonly IReadOnlyDictionary<ImportFileKind, IImportProvider> _providers;

    public ImportProviderRegistry(IEnumerable<IImportProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Kind);
    }

    public static ImportProviderRegistry CreateDefault()
    {
        return new ImportProviderRegistry(
            new IImportProvider[]
            {
                new OpenApiImportProvider(),
                new PostmanCollectionImportProvider(),
                new PostmanEnvironmentImportProvider(),
                new HarImportProvider(),
                new InsomniaImportProvider(),
                new HoppscotchImportProvider(),
                new CurlImportProvider(),
                new PowerShellImportProvider(),
            }
        );
    }

    public IImportProvider? Find(ImportFileKind kind)
    {
        return _providers.TryGetValue(kind, out var provider) ? provider : null;
    }
}

public sealed class OpenApiImportProvider : IImportProvider
{
    public ImportFileKind Kind => ImportFileKind.OpenApi;

    public string FormatName => "OpenAPI";

    public ImportPayload Parse(string fileName, string content, ImportProviderContext context)
    {
        return new OpenApiImportPayload(OpenApiImportService.Parse(content, fileName));
    }
}

public sealed class PostmanCollectionImportProvider : IImportProvider
{
    public ImportFileKind Kind => ImportFileKind.PostmanCollection;

    public string FormatName => "Postman Collection";

    public ImportPayload Parse(string fileName, string content, ImportProviderContext context)
    {
        return new CollectionImportPayload(PostmanImportService.Parse(content, fileName));
    }
}

public sealed class PostmanEnvironmentImportProvider : IImportProvider
{
    public ImportFileKind Kind => ImportFileKind.PostmanEnvironment;

    public string FormatName => "Postman Environment";

    public ImportPayload Parse(string fileName, string content, ImportProviderContext context)
    {
        return new EnvironmentImportPayload(
            PostmanEnvironmentImportService.Parse(content, fileName)
        );
    }
}

public sealed class HarImportProvider : IImportProvider
{
    public ImportFileKind Kind => ImportFileKind.Har;

    public string FormatName => "HAR";

    public ImportPayload Parse(string fileName, string content, ImportProviderContext context)
    {
        return new CollectionImportPayload(
            HarImportService.Parse(content, fileName, context.IncludeStaticResources)
        );
    }
}

public sealed class InsomniaImportProvider : IImportProvider
{
    public ImportFileKind Kind => ImportFileKind.Insomnia;

    public string FormatName => "Insomnia";

    public ImportPayload Parse(string fileName, string content, ImportProviderContext context)
    {
        return new CollectionImportPayload(InsomniaImportService.Parse(content, fileName));
    }
}

public sealed class HoppscotchImportProvider : IImportProvider
{
    public ImportFileKind Kind => ImportFileKind.Hoppscotch;

    public string FormatName => "Hoppscotch";

    public ImportPayload Parse(string fileName, string content, ImportProviderContext context)
    {
        return new CollectionImportPayload(HoppscotchImportService.Parse(content, fileName));
    }
}

public sealed class CurlImportProvider : IImportProvider
{
    public ImportFileKind Kind => ImportFileKind.Curl;

    public string FormatName => "cURL";

    public ImportPayload Parse(string fileName, string content, ImportProviderContext context)
    {
        return new CurlImportPayload(CurlImportService.Parse(content));
    }
}

public sealed class PowerShellImportProvider : IImportProvider
{
    public ImportFileKind Kind => ImportFileKind.PowerShell;

    public string FormatName => "PowerShell";

    public ImportPayload Parse(string fileName, string content, ImportProviderContext context)
    {
        return new CurlImportPayload(PowerShellImportService.Parse(content));
    }
}
