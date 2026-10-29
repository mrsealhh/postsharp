using Google.Protobuf.Reflection;

namespace PostSharp.Services;

public enum GrpcFieldKind
{
    Double,
    Float,
    Int32,
    Int64,
    UInt32,
    UInt64,
    SInt32,
    SInt64,
    Fixed32,
    Fixed64,
    SFixed32,
    SFixed64,
    Bool,
    String,
    Bytes,
    Enum,
    Message,
}

public sealed class GrpcField
{
    public required string Name { get; init; }

    public required string JsonName { get; init; }

    public required int Number { get; init; }

    public required GrpcFieldKind Kind { get; init; }

    public string TypeName { get; init; } = string.Empty;

    public bool Repeated { get; init; }

    public bool Map { get; init; }

    public GrpcFieldKind MapKeyKind { get; init; } = GrpcFieldKind.String;

    public GrpcFieldKind MapValueKind { get; init; } = GrpcFieldKind.String;

    public string MapValueTypeName { get; init; } = string.Empty;

    public bool Optional { get; init; }
}

public sealed class GrpcMessage
{
    public required string Name { get; init; }

    public List<GrpcField> Fields { get; } = new();

    public bool IsWellKnown { get; init; }
}

public sealed class GrpcEnum
{
    public required string Name { get; init; }

    public Dictionary<int, string> NamesByNumber { get; } = new();

    public Dictionary<string, int> NumbersByName { get; } = new(StringComparer.Ordinal);
}

public sealed class GrpcMethod
{
    public required string Name { get; init; }

    public required string InputType { get; init; }

    public required string OutputType { get; init; }

    public bool ClientStreaming { get; init; }

    public bool ServerStreaming { get; init; }
}

public sealed class GrpcService
{
    public required string Name { get; init; }

    public List<GrpcMethod> Methods { get; } = new();
}

/// <summary>A shallow runtime protobuf schema built from reflection FileDescriptorProto files.</summary>
public sealed class GrpcSchema
{
    private readonly Dictionary<string, GrpcMessage> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrpcEnum> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrpcService> _services = new(StringComparer.Ordinal);

    public IReadOnlyCollection<GrpcMessage> Messages => _messages.Values;

    public IReadOnlyCollection<GrpcService> Services => _services.Values;

    public static GrpcSchema FromFiles(IEnumerable<FileDescriptorProto> files)
    {
        var schema = new GrpcSchema();
        var fileList = files.ToList();
        var messageProtos = new Dictionary<string, DescriptorProto>(StringComparer.Ordinal);
        var enumProtos = new Dictionary<string, EnumDescriptorProto>(StringComparer.Ordinal);

        foreach (var file in fileList)
        {
            var packagePrefix = string.IsNullOrWhiteSpace(file.Package)
                ? string.Empty
                : "." + file.Package;
            foreach (var message in file.MessageType)
            {
                CollectMessage(message, packagePrefix, messageProtos, enumProtos);
            }

            foreach (var enumType in file.EnumType)
            {
                var full = packagePrefix + "." + enumType.Name;
                enumProtos[full] = enumType;
            }
        }

        foreach (var (fullName, proto) in enumProtos)
        {
            schema._enums[fullName] = BuildEnum(fullName, proto);
        }

        foreach (var (fullName, proto) in messageProtos)
        {
            var message = new GrpcMessage { Name = fullName.TrimStart('.') };
            var mapEntries = proto
                .NestedType.Where(nested => nested.Options?.MapEntry == true)
                .ToDictionary(nested => fullName + "." + nested.Name, StringComparer.Ordinal);
            foreach (var field in proto.Field)
            {
                message.Fields.Add(BuildField(field, mapEntries));
            }

            schema._messages[fullName] = message;
        }

        foreach (var file in fileList)
        {
            var packagePrefix = string.IsNullOrWhiteSpace(file.Package)
                ? string.Empty
                : "." + file.Package;
            foreach (var service in file.Service)
            {
                var full = packagePrefix + "." + service.Name;
                var model = new GrpcService { Name = full.TrimStart('.') };
                foreach (var method in service.Method)
                {
                    model.Methods.Add(
                        new GrpcMethod
                        {
                            Name = method.Name,
                            InputType = Normalize(method.InputType),
                            OutputType = Normalize(method.OutputType),
                            ClientStreaming = method.ClientStreaming,
                            ServerStreaming = method.ServerStreaming,
                        }
                    );
                }

                schema._services[full] = model;
            }
        }

        schema.AddWellKnownTypes();
        return schema;
    }

    public GrpcMessage? FindMessage(string? name)
    {
        var key = Normalize(name);
        if (key.Length == 0)
        {
            return null;
        }

        if (_messages.TryGetValue("." + key, out var message))
        {
            return message;
        }

        return WellKnown(key);
    }

    public GrpcEnum? FindEnum(string? name)
    {
        var key = Normalize(name);
        return _enums.TryGetValue("." + key, out var value) ? value : null;
    }

    public GrpcService? FindService(string? name)
    {
        var key = Normalize(name);
        return _services.TryGetValue("." + key, out var value) ? value : null;
    }

    public GrpcMethod? FindMethod(string? service, string? method)
    {
        var model = FindService(service);
        return model?.Methods.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, method?.Trim(), StringComparison.Ordinal)
        );
    }

    private static void CollectMessage(
        DescriptorProto message,
        string parent,
        Dictionary<string, DescriptorProto> messages,
        Dictionary<string, EnumDescriptorProto> enums
    )
    {
        var full = parent + "." + message.Name;
        messages[full] = message;
        foreach (var nested in message.NestedType)
        {
            CollectMessage(nested, full, messages, enums);
        }

        foreach (var enumType in message.EnumType)
        {
            enums[full + "." + enumType.Name] = enumType;
        }
    }

    private static GrpcEnum BuildEnum(string fullName, EnumDescriptorProto proto)
    {
        var model = new GrpcEnum { Name = fullName.TrimStart('.') };
        foreach (var value in proto.Value)
        {
            model.NamesByNumber[value.Number] = value.Name;
            model.NumbersByName[value.Name] = value.Number;
            model.NumbersByName[value.Name.ToUpperInvariant()] = value.Number;
        }

        return model;
    }

    private static GrpcField BuildField(
        FieldDescriptorProto field,
        IReadOnlyDictionary<string, DescriptorProto> mapEntries
    )
    {
        var kind = ToKind(field.Type);
        var typeName = Normalize(field.TypeName);
        var repeated = field.Label == FieldDescriptorProto.Types.Label.Repeated;
        var isMap =
            repeated
            && kind == GrpcFieldKind.Message
            && field.TypeName.Length > 0
            && mapEntries.TryGetValue(field.TypeName, out var entry)
            && entry.Field.Count >= 2;

        if (isMap && mapEntries.TryGetValue(field.TypeName, out var mapEntry))
        {
            var key = mapEntry.Field.First(candidate => candidate.Number == 1);
            var value = mapEntry.Field.First(candidate => candidate.Number == 2);
            return new GrpcField
            {
                Name = field.Name,
                JsonName = string.IsNullOrWhiteSpace(field.JsonName)
                    ? ToJsonName(field.Name)
                    : field.JsonName,
                Number = field.Number,
                Kind = GrpcFieldKind.Message,
                TypeName = typeName,
                Repeated = true,
                Map = true,
                MapKeyKind = ToKind(key.Type),
                MapValueKind = ToKind(value.Type),
                MapValueTypeName = Normalize(value.TypeName),
                Optional = field.Proto3Optional,
            };
        }

        return new GrpcField
        {
            Name = field.Name,
            JsonName = string.IsNullOrWhiteSpace(field.JsonName)
                ? ToJsonName(field.Name)
                : field.JsonName,
            Number = field.Number,
            Kind = kind,
            TypeName = typeName,
            Repeated = repeated,
            Optional = field.Proto3Optional,
        };
    }

    private static GrpcFieldKind ToKind(FieldDescriptorProto.Types.Type type) =>
        type switch
        {
            FieldDescriptorProto.Types.Type.Double => GrpcFieldKind.Double,
            FieldDescriptorProto.Types.Type.Float => GrpcFieldKind.Float,
            FieldDescriptorProto.Types.Type.Int64 => GrpcFieldKind.Int64,
            FieldDescriptorProto.Types.Type.Uint64 => GrpcFieldKind.UInt64,
            FieldDescriptorProto.Types.Type.Int32 => GrpcFieldKind.Int32,
            FieldDescriptorProto.Types.Type.Fixed64 => GrpcFieldKind.Fixed64,
            FieldDescriptorProto.Types.Type.Fixed32 => GrpcFieldKind.Fixed32,
            FieldDescriptorProto.Types.Type.Bool => GrpcFieldKind.Bool,
            FieldDescriptorProto.Types.Type.String => GrpcFieldKind.String,
            FieldDescriptorProto.Types.Type.Bytes => GrpcFieldKind.Bytes,
            FieldDescriptorProto.Types.Type.Uint32 => GrpcFieldKind.UInt32,
            FieldDescriptorProto.Types.Type.Sfixed32 => GrpcFieldKind.SFixed32,
            FieldDescriptorProto.Types.Type.Sfixed64 => GrpcFieldKind.SFixed64,
            FieldDescriptorProto.Types.Type.Sint32 => GrpcFieldKind.SInt32,
            FieldDescriptorProto.Types.Type.Sint64 => GrpcFieldKind.SInt64,
            FieldDescriptorProto.Types.Type.Enum => GrpcFieldKind.Enum,
            FieldDescriptorProto.Types.Type.Message => GrpcFieldKind.Message,
            _ => GrpcFieldKind.String,
        };

    private static string Normalize(string? name) => (name ?? string.Empty).Trim().TrimStart('.');

    private static string ToJsonName(string name)
    {
        var builder = new System.Text.StringBuilder();
        var upper = false;
        foreach (var character in name)
        {
            if (character == '_')
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(character) : character);
            upper = false;
        }

        return builder.ToString();
    }

    private void AddWellKnownTypes()
    {
        AddWellKnown("google.protobuf.Empty");
        AddWellKnown(
            "google.protobuf.Timestamp",
            ("seconds", "seconds", 1, GrpcFieldKind.Int64),
            ("nanos", "nanos", 2, GrpcFieldKind.Int32)
        );
        AddWellKnown(
            "google.protobuf.Duration",
            ("seconds", "seconds", 1, GrpcFieldKind.Int64),
            ("nanos", "nanos", 2, GrpcFieldKind.Int32)
        );
        AddWellKnown("google.protobuf.DoubleValue", ("value", "value", 1, GrpcFieldKind.Double));
        AddWellKnown("google.protobuf.FloatValue", ("value", "value", 1, GrpcFieldKind.Float));
        AddWellKnown("google.protobuf.Int64Value", ("value", "value", 1, GrpcFieldKind.Int64));
        AddWellKnown("google.protobuf.UInt64Value", ("value", "value", 1, GrpcFieldKind.UInt64));
        AddWellKnown("google.protobuf.Int32Value", ("value", "value", 1, GrpcFieldKind.Int32));
        AddWellKnown("google.protobuf.UInt32Value", ("value", "value", 1, GrpcFieldKind.UInt32));
        AddWellKnown("google.protobuf.BoolValue", ("value", "value", 1, GrpcFieldKind.Bool));
        AddWellKnown("google.protobuf.StringValue", ("value", "value", 1, GrpcFieldKind.String));
        AddWellKnown("google.protobuf.BytesValue", ("value", "value", 1, GrpcFieldKind.Bytes));
        AddWellKnown("google.protobuf.FieldMask", ("paths", "paths", 1, GrpcFieldKind.String));
        AddWellKnown(
            "google.protobuf.Any",
            ("typeUrl", "type_url", 1, GrpcFieldKind.String),
            ("value", "value", 2, GrpcFieldKind.Bytes)
        );
        AddWellKnown("google.protobuf.Value");
        AddWellKnown("google.protobuf.Struct");
        AddWellKnown("google.protobuf.ListValue");
        if (!_enums.ContainsKey(".google.protobuf.NullValue"))
        {
            var nullValue = new GrpcEnum { Name = "google.protobuf.NullValue" };
            nullValue.NamesByNumber[0] = "NULL_VALUE";
            nullValue.NumbersByName["NULL_VALUE"] = 0;
            _enums[".google.protobuf.NullValue"] = nullValue;
        }
    }

    private void AddWellKnown(
        string name,
        params (string Name, string JsonName, int Number, GrpcFieldKind Kind)[] fields
    )
    {
        var key = "." + name;
        if (_messages.ContainsKey(key))
        {
            return;
        }

        var message = new GrpcMessage { Name = name, IsWellKnown = true };
        foreach (var field in fields)
        {
            message.Fields.Add(
                new GrpcField
                {
                    Name = field.Name,
                    JsonName = field.JsonName,
                    Number = field.Number,
                    Kind = field.Kind,
                }
            );
        }

        _messages[key] = message;
    }

    private GrpcMessage? WellKnown(string name) =>
        _messages.TryGetValue("." + name, out var message) && message.IsWellKnown ? message : null;
}
