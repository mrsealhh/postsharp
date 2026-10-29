using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;

namespace PostSharp.Services;

public static class GrpcReflectionClient
{
    private const string V1AlphaService = "grpc.reflection.v1alpha.ServerReflection";
    private const string V1Service = "grpc.reflection.v1.ServerReflection";
    private static readonly TimeSpan ReflectionTimeout = TimeSpan.FromSeconds(15);

    public static async Task<IReadOnlyList<string>> ListServicesAsync(
        GrpcChannel channel,
        Metadata? metadata,
        CancellationToken cancellationToken
    )
    {
        var response = await CallAsync(
                channel,
                GrpcReflectionCodec.ListServicesRequest(),
                metadata,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            throw new InvalidOperationException(response.Error);
        }

        return response.Services;
    }

    public static async Task<GrpcSchema> ResolveSchemaAsync(
        GrpcChannel channel,
        string service,
        Metadata? metadata,
        CancellationToken cancellationToken
    )
    {
        var response = await CallAsync(
                channel,
                GrpcReflectionCodec.FileContainingSymbolRequest(service),
                metadata,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            throw new InvalidOperationException(response.Error);
        }

        if (response.FileDescriptors.Count == 0)
        {
            throw new InvalidOperationException($"服务端反射没有返回 {service} 的描述符。");
        }

        var files = new Dictionary<string, FileDescriptorProto>(StringComparer.Ordinal);
        var queue = new Queue<byte[]>(response.FileDescriptors);
        while (queue.Count > 0 && files.Count < 100)
        {
            var bytes = queue.Dequeue();
            FileDescriptorProto descriptor;
            try
            {
                descriptor = FileDescriptorProto.Parser.ParseFrom(bytes);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"gRPC 描述符解析失败：{exception.Message}",
                    exception
                );
            }

            if (descriptor.Name.Length > 0 && !files.TryAdd(descriptor.Name, descriptor))
            {
                continue;
            }

            foreach (var dependency in descriptor.Dependency)
            {
                if (files.ContainsKey(dependency))
                {
                    continue;
                }

                try
                {
                    var dependencyResponse = await CallAsync(
                            channel,
                            GrpcReflectionCodec.FileByFilenameRequest(dependency),
                            metadata,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    if (dependencyResponse.FileDescriptors.Count > 0)
                    {
                        foreach (var dependencyBytes in dependencyResponse.FileDescriptors)
                        {
                            queue.Enqueue(dependencyBytes);
                        }
                    }
                }
                catch (RpcException)
                {
                    // Some servers inline dependencies; missing optional imports are skipped.
                }
            }
        }

        var schema = GrpcSchema.FromFiles(files.Values);
        if (schema.FindService(service) is null)
        {
            throw new InvalidOperationException($"描述符里没有服务 {service}，请检查服务全名。");
        }

        return schema;
    }

    public static GrpcSchema SchemaFromDescriptorSet(byte[] bytes)
    {
        var set = FileDescriptorSet.Parser.ParseFrom(bytes);
        return GrpcSchema.FromFiles(set.File);
    }

    private static async Task<GrpcReflectionResponse> CallAsync(
        GrpcChannel channel,
        byte[] request,
        Metadata? metadata,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await CallServiceAsync(
                    channel,
                    V1AlphaService,
                    request,
                    metadata,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Unimplemented)
        {
            return await CallServiceAsync(channel, V1Service, request, metadata, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<GrpcReflectionResponse> CallServiceAsync(
        GrpcChannel channel,
        string serviceName,
        byte[] request,
        Metadata? metadata,
        CancellationToken cancellationToken
    )
    {
        var method = new Method<byte[], byte[]>(
            MethodType.DuplexStreaming,
            serviceName,
            "ServerReflectionInfo",
            Marshallers.Create(bytes => bytes, bytes => bytes),
            Marshallers.Create(bytes => bytes, bytes => bytes)
        );
        var headers = metadata is null ? new Metadata() : CloneMetadata(metadata);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReflectionTimeout);
        using var call = channel
            .CreateCallInvoker()
            .AsyncDuplexStreamingCall(
                method,
                null,
                new CallOptions(
                    headers,
                    deadline: DateTime.UtcNow.Add(ReflectionTimeout),
                    cancellationToken: timeout.Token
                )
            );
        await call.RequestStream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        if (!await call.ResponseStream.MoveNext(timeout.Token).ConfigureAwait(false))
        {
            throw new InvalidOperationException("服务端反射没有返回响应。");
        }

        return GrpcReflectionCodec.Parse(call.ResponseStream.Current);
    }

    private static Metadata CloneMetadata(Metadata source)
    {
        var clone = new Metadata();
        foreach (var entry in source)
        {
            if (entry.IsBinary)
            {
                clone.Add(entry.Key, entry.ValueBytes);
            }
            else
            {
                clone.Add(entry.Key, entry.Value);
            }
        }

        return clone;
    }
}
