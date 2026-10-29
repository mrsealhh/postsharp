using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Unary gRPC calls through server reflection and a dynamic protobuf JSON codec.</summary>
public static class GrpcProtocolClient
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> ReservedMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "content-type",
        "te",
        "host",
        "user-agent",
        "grpc-timeout",
    };

    public static async Task<HttpResult> SendAsync(
        ComposedRequest composed,
        GrpcSendOptions options,
        CancellationToken cancellationToken,
        int timeoutSeconds = 0
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        GrpcChannel? channel = null;
        try
        {
            if (string.IsNullOrWhiteSpace(options.Service))
            {
                throw new InvalidOperationException(
                    "gRPC 服务名不能为空，例如 helloworld.Greeter。"
                );
            }

            if (string.IsNullOrWhiteSpace(options.Method))
            {
                throw new InvalidOperationException("gRPC 方法名不能为空，例如 SayHello。");
            }

            channel = CreateChannel(composed.Url, options.UseTls);
            var metadata = BuildMetadata(composed.Headers);
            var schema = await GrpcReflectionClient
                .ResolveSchemaAsync(channel, options.Service, metadata, cancellationToken)
                .ConfigureAwait(false);
            var service = schema.FindService(options.Service);
            var method = service?.Methods.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, options.Method, StringComparison.Ordinal)
            );
            if (method is null)
            {
                throw new InvalidOperationException(
                    $"服务 {options.Service} 没有方法 {options.Method}。"
                );
            }

            if (method.ClientStreaming || method.ServerStreaming)
            {
                throw new InvalidOperationException(
                    $"方法 {options.Method} 是流式 RPC，当前只支持一元调用。"
                );
            }

            var requestType = schema.FindMessage(method.InputType);
            var responseType = schema.FindMessage(method.OutputType);
            if (requestType is null || responseType is null)
            {
                throw new InvalidOperationException("反射描述符缺少请求或响应消息类型。");
            }

            JsonNode? requestJson;
            var requestText = options.RequestJson.Trim();
            if (requestText.Length == 0)
            {
                requestJson = new JsonObject();
            }
            else
            {
                try
                {
                    requestJson = JsonNode.Parse(requestText);
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException(
                        $"gRPC 请求 JSON 无法解析：{exception.Message}",
                        exception
                    );
                }
            }

            var requestBytes = GrpcMessageCodec.Encode(schema, requestType, requestJson, warnings);
            var deadlineSeconds =
                options.DeadlineSeconds > 0 ? options.DeadlineSeconds
                : timeoutSeconds > 0 ? Math.Min(timeoutSeconds, 300)
                : (int)DefaultDeadline.TotalSeconds;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(deadlineSeconds, 1, 300)));

            var grpcMethod = new Method<byte[], byte[]>(
                MethodType.Unary,
                options.Service,
                options.Method,
                Marshallers.Create(bytes => bytes, bytes => bytes),
                Marshallers.Create(bytes => bytes, bytes => bytes)
            );
            using var call = channel
                .CreateCallInvoker()
                .AsyncUnaryCall(
                    grpcMethod,
                    null,
                    new CallOptions(
                        metadata,
                        deadline: DateTime.UtcNow.AddSeconds(Math.Clamp(deadlineSeconds, 1, 300)),
                        cancellationToken: deadline.Token
                    ),
                    requestBytes
                );
            var responseHeaders = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var responseBytes = await call.ResponseAsync.ConfigureAwait(false);
            stopwatch.Stop();

            var responseJson = GrpcMessageCodec.Decode(schema, responseType, responseBytes);
            var body = JsonSerializer.Serialize(responseJson, PrettyJson);
            var headers = BuildResultHeaders(
                responseHeaders,
                call.GetTrailers(),
                new Status(StatusCode.OK, string.Empty)
            );
            if (warnings.Count > 0)
            {
                headers.Add(
                    new ResponseHeaderView("X-PostSharp-Warning", string.Join("; ", warnings))
                );
            }

            return new HttpResult(
                HasResponse: true,
                StatusCode: 200,
                ReasonPhrase: "gRPC OK",
                Body: body,
                IsJson: true,
                MediaType: "application/grpc+json",
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                SizeBytes: responseBytes.Length,
                Headers: headers,
                Error: null
            );
        }
        catch (RpcException exception)
        {
            stopwatch.Stop();
            var body = BuildErrorBody(exception);
            return new HttpResult(
                HasResponse: true,
                StatusCode: 500,
                ReasonPhrase: $"gRPC {exception.StatusCode}",
                Body: body,
                IsJson: true,
                MediaType: "application/grpc+json",
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                SizeBytes: 0,
                Headers: BuildResultHeaders(null, exception.Trailers, exception.Status),
                Error: exception.Status.Detail
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Failure("请求已取消。", stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return Failure("gRPC 调用超时。", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            Log.Warning(
                exception,
                "gRPC 调用失败 {Service} {Method}",
                options.Service,
                options.Method
            );
            return Failure($"gRPC 调用失败：{exception.Message}", stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            channel?.Dispose();
        }
    }

    public static async Task<JsonObject> DescribeAsync(
        string url,
        bool useTls,
        string? service,
        string? method,
        CancellationToken cancellationToken
    )
    {
        using var channel = CreateChannel(url, useTls);
        if (string.IsNullOrWhiteSpace(service))
        {
            var services = await GrpcReflectionClient
                .ListServicesAsync(channel, null, cancellationToken)
                .ConfigureAwait(false);
            return new JsonObject
            {
                ["services"] = new JsonArray(
                    services.Select(name => (JsonNode)JsonValue.Create(name)!).ToArray()
                ),
            };
        }

        var schema = await GrpcReflectionClient
            .ResolveSchemaAsync(channel, service.Trim().TrimStart('.'), null, cancellationToken)
            .ConfigureAwait(false);
        var serviceModel =
            schema.FindService(service)
            ?? throw new InvalidOperationException($"描述符里没有服务 {service}。");
        var result = new JsonObject
        {
            ["service"] = serviceModel.Name,
            ["methods"] = new JsonArray(
                serviceModel
                    .Methods.Select(candidate =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["name"] = candidate.Name,
                                ["inputType"] = candidate.InputType,
                                ["outputType"] = candidate.OutputType,
                                ["clientStreaming"] = candidate.ClientStreaming,
                                ["serverStreaming"] = candidate.ServerStreaming,
                            }
                    )
                    .ToArray()
            ),
        };

        if (!string.IsNullOrWhiteSpace(method))
        {
            var selected =
                serviceModel.Methods.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, method.Trim(), StringComparison.Ordinal)
                ) ?? throw new InvalidOperationException($"服务没有方法 {method}。");
            var request = schema.FindMessage(selected.InputType);
            var response = schema.FindMessage(selected.OutputType);
            result["method"] = new JsonObject
            {
                ["name"] = selected.Name,
                ["inputType"] = selected.InputType,
                ["outputType"] = selected.OutputType,
                ["requestSchema"] = request is null
                    ? null
                    : DescribeMessage(
                        schema,
                        request,
                        0,
                        new HashSet<string>(StringComparer.Ordinal)
                    ),
                ["responseSchema"] = response is null
                    ? null
                    : DescribeMessage(
                        schema,
                        response,
                        0,
                        new HashSet<string>(StringComparer.Ordinal)
                    ),
                ["requestSample"] = request is null
                    ? null
                    : GrpcMessageCodec.DefaultJson(schema, request),
            };
        }

        return result;
    }

    public static GrpcChannel CreateChannel(string url, bool useTls)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("gRPC 地址不能为空。");
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = (useTls ? "https://" : "http://") + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("gRPC 地址无效。");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("gRPC 地址必须是 http:// 或 https://。");
        }

        if (uri.Scheme == "http")
        {
            AppContext.SetSwitch(
                "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport",
                true
            );
        }

        var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true };
        return GrpcChannel.ForAddress(
            uri,
            new GrpcChannelOptions
            {
                HttpHandler = handler,
                MaxReceiveMessageSize = 32 * 1024 * 1024,
                MaxSendMessageSize = 32 * 1024 * 1024,
            }
        );
    }

    private static Metadata BuildMetadata(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var metadata = new Metadata();
        foreach (var (key, value) in headers)
        {
            if (
                string.IsNullOrWhiteSpace(key)
                || ReservedMetadata.Contains(key)
                || key.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            metadata.Add(key.ToLowerInvariant(), value);
        }

        return metadata;
    }

    private static List<ResponseHeaderView> BuildResultHeaders(
        Metadata? responseHeaders,
        Metadata trailers,
        Status status
    )
    {
        var result = new List<ResponseHeaderView>();
        if (responseHeaders is not null)
        {
            foreach (var entry in responseHeaders)
            {
                result.Add(new ResponseHeaderView(entry.Key, MetadataValue(entry)));
            }
        }

        foreach (var entry in trailers)
        {
            result.Add(new ResponseHeaderView(entry.Key, MetadataValue(entry)));
        }

        result.Add(new ResponseHeaderView("grpc-status", ((int)status.StatusCode).ToString()));
        if (status.Detail.Length > 0)
        {
            result.Add(new ResponseHeaderView("grpc-message", status.Detail));
        }

        return result;
    }

    private static string MetadataValue(Metadata.Entry entry) =>
        entry.IsBinary ? Convert.ToBase64String(entry.ValueBytes) : entry.Value;

    private static string BuildErrorBody(RpcException exception)
    {
        var body = new JsonObject
        {
            ["status"] = exception.StatusCode.ToString(),
            ["code"] = (int)exception.StatusCode,
            ["message"] = exception.Status.Detail,
        };
        if (exception.Trailers.Count > 0)
        {
            var trailers = new JsonObject();
            foreach (var entry in exception.Trailers)
            {
                trailers[entry.Key] = entry.Value;
            }

            body["trailers"] = trailers;
        }

        return body.ToJsonString(PrettyJson);
    }

    private static JsonObject DescribeMessage(
        GrpcSchema schema,
        GrpcMessage message,
        int depth,
        HashSet<string> visited
    )
    {
        var result = new JsonObject
        {
            ["name"] = message.Name,
            ["fields"] = new JsonArray(
                message
                    .Fields.Select(field =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["name"] = field.Name,
                                ["jsonName"] = field.JsonName,
                                ["number"] = field.Number,
                                ["type"] = field.Kind.ToString().ToLowerInvariant(),
                                ["typeName"] = field.TypeName,
                                ["repeated"] = field.Repeated,
                                ["map"] = field.Map,
                                ["mapKey"] = field.Map
                                    ? field.MapKeyKind.ToString().ToLowerInvariant()
                                    : null,
                                ["mapValue"] = field.Map
                                    ? field.MapValueKind.ToString().ToLowerInvariant()
                                    : null,
                            }
                    )
                    .ToArray()
            ),
        };
        if (depth < 3 && visited.Add(message.Name))
        {
            var nested = new JsonObject();
            foreach (
                var field in message.Fields.Where(field => field.Kind == GrpcFieldKind.Message)
            )
            {
                var type = schema.FindMessage(field.TypeName);
                if (type is not null && !type.IsWellKnown && nested[field.TypeName] is null)
                {
                    nested[field.TypeName] = DescribeMessage(schema, type, depth + 1, visited);
                }
            }

            if (nested.Count > 0)
            {
                result["nested"] = nested;
            }
        }

        return result;
    }

    private static HttpResult Failure(string message, long elapsedMs) =>
        new(
            HasResponse: false,
            StatusCode: 0,
            ReasonPhrase: string.Empty,
            Body: string.Empty,
            IsJson: false,
            MediaType: "application/grpc+json",
            ElapsedMs: elapsedMs,
            SizeBytes: 0,
            Headers: Array.Empty<ResponseHeaderView>(),
            Error: message
        );
}
