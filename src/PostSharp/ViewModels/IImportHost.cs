using PostSharp.Models;

namespace PostSharp.ViewModels;

/// <summary>Workspace mutations required by <see cref="ImportViewModel"/>.</summary>
public interface IImportHost
{
    CollectionImportOutcome ApplyCollectionImport(ImportResult result);

    CollectionImportOutcome ApplyOpenApiImport(OpenApiImportDocument document);

    CollectionImportOutcome ApplyEnvironmentImport(EnvironmentImportResult result);

    string? ApplyImportedEnvironment(ImportedEnvironment? imported);

    RequestItem ApplyCurlImport(CurlImportResult result);

    void CompleteImport();

    void OpenSuiteBuilderForImportedCollection(Guid collectionId);

    void ShowToast(string title);
}
