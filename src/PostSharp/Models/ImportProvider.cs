namespace PostSharp.Models;

/// <summary>Options shared by batch import providers.</summary>
public sealed record ImportProviderContext(bool IncludeStaticResources = false);

/// <summary>A parsed import result that has not yet touched the workspace.</summary>
public abstract record ImportPayload;

public sealed record OpenApiImportPayload(OpenApiImportDocument Document) : ImportPayload;

public sealed record CollectionImportPayload(ImportResult Result) : ImportPayload;

public sealed record EnvironmentImportPayload(EnvironmentImportResult Result) : ImportPayload;

public sealed record CurlImportPayload(CurlImportResult Result) : ImportPayload;
