using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace PostSharp.Services;

/// <summary>
/// Minimal dynamic protobuf JSON codec: enough for unary gRPC calls with scalar, enum,
/// repeated, map and nested message fields. Well-known Timestamp / Duration / wrappers /
/// Struct / Value / ListValue / FieldMask are handled explicitly.
/// </summary>
public static class GrpcMessageCodec
{
    private const int MaxDepth = 64;

    public static byte[] Encode(
        GrpcSchema schema,
        GrpcMessage message,
        JsonNode? json,
        ICollection<string> warnings
    )
    {
        var output = new MemoryStream();
        var coded = new CodedOutputStream(output);
        try
        {
            EncodeMessage(coded, schema, message, json, warnings, 0);
        }
        finally
        {
            coded.Flush();
        }

        return output.ToArray();
    }

    public static JsonNode Decode(GrpcSchema schema, GrpcMessage message, byte[] bytes)
    {
        var input = new CodedInputStream(bytes);
        return DecodeMessage(input, schema, message, 0);
    }

    public static JsonNode DefaultJson(GrpcSchema schema, GrpcMessage message, int depth = 0)
    {
        if (depth > 5)
        {
            return new JsonObject();
        }

        if (message.IsWellKnown)
        {
            if (message.Fields is [{ Name: "value" } wrapper])
            {
                return DefaultValue(schema, wrapper, depth) ?? new JsonObject();
            }

            return message.Name switch
            {
                "google.protobuf.Timestamp" => JsonValue.Create("2000-01-01T00:00:00Z")!,
                "google.protobuf.Duration" => JsonValue.Create("0s")!,
                "google.protobuf.FieldMask" => JsonValue.Create(string.Empty)!,
                "google.protobuf.Value" => JsonValue.Create(string.Empty)!,
                "google.protobuf.Struct" => new JsonObject(),
                "google.protobuf.ListValue" => new JsonArray(),
                _ => new JsonObject(),
            };
        }

        var result = new JsonObject();
        foreach (var field in message.Fields)
        {
            if (field.Map)
            {
                result[field.JsonName] = new JsonObject();
                continue;
            }

            var value = DefaultValue(schema, field, depth);
            if (value is null)
            {
                continue;
            }

            result[field.JsonName] = field.Repeated ? new JsonArray(value) : value;
        }

        return result;
    }

    private static JsonNode? DefaultValue(GrpcSchema schema, GrpcField field, int depth)
    {
        if (field.Kind == GrpcFieldKind.Message)
        {
            var type = schema.FindMessage(field.TypeName);
            return type is null ? new JsonObject() : DefaultJson(schema, type, depth + 1);
        }

        if (field.Kind == GrpcFieldKind.Enum)
        {
            return JsonValue.Create(0);
        }

        return field.Kind switch
        {
            GrpcFieldKind.Bool => JsonValue.Create(false),
            GrpcFieldKind.String => JsonValue.Create(string.Empty),
            GrpcFieldKind.Bytes => JsonValue.Create(string.Empty),
            GrpcFieldKind.Double or GrpcFieldKind.Float => JsonValue.Create(0),
            GrpcFieldKind.Int64
            or GrpcFieldKind.UInt64
            or GrpcFieldKind.SInt64
            or GrpcFieldKind.Fixed64
            or GrpcFieldKind.SFixed64 => JsonValue.Create("0"),
            _ => JsonValue.Create(0),
        };
    }

    private static void EncodeMessage(
        CodedOutputStream output,
        GrpcSchema schema,
        GrpcMessage message,
        JsonNode? json,
        ICollection<string> warnings,
        int depth
    )
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException("gRPC 消息嵌套层级过深。");
        }

        if (json is null)
        {
            return;
        }

        switch (message.Name)
        {
            case "google.protobuf.Timestamp":
                EncodeTimestamp(output, json);
                return;
            case "google.protobuf.Duration":
                EncodeDuration(output, json);
                return;
            case "google.protobuf.FieldMask":
                EncodeFieldMask(output, json);
                return;
            case "google.protobuf.Value":
                EncodeValue(output, schema, json, warnings, depth);
                return;
            case "google.protobuf.Struct":
                if (json is JsonObject structValue)
                {
                    EncodeStruct(output, schema, structValue, warnings, depth);
                }

                return;
            case "google.protobuf.ListValue":
                if (json is JsonArray list)
                {
                    foreach (var item in list)
                    {
                        var valueBytes = EncodeNested(
                            schema,
                            schema.FindMessage("google.protobuf.Value")!,
                            item,
                            warnings,
                            depth + 1
                        );
                        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
                        output.WriteBytes(ByteString.CopyFrom(valueBytes));
                    }
                }

                return;
            case "google.protobuf.Empty":
                return;
        }

        if (message.IsWellKnown && message.Fields.Count == 1 && message.Fields[0].Name == "value")
        {
            var valueField = message.Fields[0];
            if (json is JsonValue)
            {
                EncodeSingle(output, schema, valueField, json, warnings, depth + 1);
                return;
            }

            if (json is JsonObject wrapper && wrapper.TryGetPropertyValue("value", out var wrapped))
            {
                EncodeSingle(output, schema, valueField, wrapped, warnings, depth + 1);
                return;
            }
        }

        if (json is not JsonObject objectValue)
        {
            throw new InvalidOperationException($"gRPC 消息 {message.Name} 必须是 JSON 对象。");
        }

        foreach (var property in objectValue)
        {
            var field = FindField(message, property.Key);
            if (field is null)
            {
                if (property.Key != "@type")
                {
                    warnings.Add($"{message.Name} 没有字段 {property.Key}，已忽略。");
                }

                continue;
            }

            if (property.Value is null)
            {
                continue;
            }

            if (field.Map)
            {
                EncodeMap(output, schema, field, property.Value, warnings, depth);
            }
            else if (field.Repeated)
            {
                if (property.Value is not JsonArray array)
                {
                    warnings.Add($"{property.Key} 需要 JSON 数组，已忽略。");
                    continue;
                }

                foreach (var item in array)
                {
                    EncodeSingle(output, schema, field, item, warnings, depth + 1);
                }
            }
            else
            {
                EncodeSingle(output, schema, field, property.Value, warnings, depth + 1);
            }
        }
    }

    private static void EncodeSingle(
        CodedOutputStream output,
        GrpcSchema schema,
        GrpcField field,
        JsonNode? value,
        ICollection<string> warnings,
        int depth
    )
    {
        if (value is null)
        {
            return;
        }

        if (field.Kind == GrpcFieldKind.Message)
        {
            var type = schema.FindMessage(field.TypeName);
            if (type is null)
            {
                throw new InvalidOperationException($"找不到 protobuf 消息类型 {field.TypeName}。");
            }

            var bytes = EncodeNested(schema, type, value, warnings, depth);
            output.WriteTag(field.Number, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(bytes));
            return;
        }

        output.WriteTag(field.Number, WireTypeOf(field.Kind));
        WriteScalar(output, schema, field, value, warnings);
    }

    private static byte[] EncodeNested(
        GrpcSchema schema,
        GrpcMessage message,
        JsonNode? value,
        ICollection<string> warnings,
        int depth
    )
    {
        var buffer = new MemoryStream();
        var nested = new CodedOutputStream(buffer);
        try
        {
            EncodeMessage(nested, schema, message, value, warnings, depth);
        }
        finally
        {
            nested.Flush();
        }

        return buffer.ToArray();
    }

    private static void EncodeMap(
        CodedOutputStream output,
        GrpcSchema schema,
        GrpcField field,
        JsonNode value,
        ICollection<string> warnings,
        int depth
    )
    {
        if (value is not JsonObject map)
        {
            warnings.Add($"{field.JsonName} 需要 JSON 对象。");
            return;
        }

        foreach (var pair in map)
        {
            var buffer = new MemoryStream();
            var entry = new CodedOutputStream(buffer);
            try
            {
                var keyField = new GrpcField
                {
                    Name = "key",
                    JsonName = "key",
                    Number = 1,
                    Kind = field.MapKeyKind,
                };
                entry.WriteTag(1, WireTypeOf(field.MapKeyKind));
                WriteScalar(entry, schema, keyField, JsonValue.Create(pair.Key), warnings);

                var valueField = new GrpcField
                {
                    Name = "value",
                    JsonName = "value",
                    Number = 2,
                    Kind = field.MapValueKind,
                    TypeName = field.MapValueTypeName,
                };
                if (pair.Value is not null)
                {
                    EncodeSingle(entry, schema, valueField, pair.Value, warnings, depth + 1);
                }
            }
            finally
            {
                entry.Flush();
            }

            output.WriteTag(field.Number, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(buffer.ToArray()));
        }
    }

    private static void WriteScalar(
        CodedOutputStream output,
        GrpcSchema schema,
        GrpcField field,
        JsonNode value,
        ICollection<string> warnings
    )
    {
        switch (field.Kind)
        {
            case GrpcFieldKind.Double:
                output.WriteDouble(ToDouble(value));
                break;
            case GrpcFieldKind.Float:
                output.WriteFloat((float)ToDouble(value));
                break;
            case GrpcFieldKind.Int32:
                output.WriteInt32(ToInt32(value));
                break;
            case GrpcFieldKind.Int64:
                output.WriteInt64(ToInt64(value));
                break;
            case GrpcFieldKind.UInt32:
                output.WriteUInt32((uint)ToInt64(value));
                break;
            case GrpcFieldKind.UInt64:
                output.WriteUInt64(ToUInt64(value));
                break;
            case GrpcFieldKind.SInt32:
                output.WriteSInt32(ToInt32(value));
                break;
            case GrpcFieldKind.SInt64:
                output.WriteSInt64(ToInt64(value));
                break;
            case GrpcFieldKind.Fixed32:
                output.WriteFixed32((uint)ToInt64(value));
                break;
            case GrpcFieldKind.Fixed64:
                output.WriteFixed64(ToUInt64(value));
                break;
            case GrpcFieldKind.SFixed32:
                output.WriteSFixed32(ToInt32(value));
                break;
            case GrpcFieldKind.SFixed64:
                output.WriteSFixed64(ToInt64(value));
                break;
            case GrpcFieldKind.Bool:
                output.WriteBool(ToBool(value));
                break;
            case GrpcFieldKind.String:
                output.WriteString(ToStringValue(value));
                break;
            case GrpcFieldKind.Bytes:
                output.WriteBytes(ByteString.CopyFrom(ToBytes(value)));
                break;
            case GrpcFieldKind.Enum:
                output.WriteEnum(ToEnumValue(schema, field, value, warnings));
                break;
            case GrpcFieldKind.Message:
                throw new InvalidOperationException("消息字段不能按标量写入。");
        }
    }

    private static JsonNode DecodeMessage(
        CodedInputStream input,
        GrpcSchema schema,
        GrpcMessage message,
        int depth
    )
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException("gRPC 响应嵌套层级过深。");
        }

        if (message.IsWellKnown)
        {
            return DecodeWellKnown(input, schema, message, depth);
        }

        var result = new JsonObject();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var number = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);
            var field = message.Fields.FirstOrDefault(candidate => candidate.Number == number);
            if (field is null)
            {
                input.SkipLastField();
                continue;
            }

            if (field.Map)
            {
                if (wireType != WireFormat.WireType.LengthDelimited)
                {
                    input.SkipLastField();
                    continue;
                }

                var entryBytes = input.ReadBytes();
                var entry = new CodedInputStream(entryBytes.ToByteArray());
                var key = string.Empty;
                JsonNode? mapValue = null;
                var entryTag = 0u;
                while ((entryTag = entry.ReadTag()) != 0)
                {
                    var entryNumber = WireFormat.GetTagFieldNumber(entryTag);
                    var entryWire = WireFormat.GetTagWireType(entryTag);
                    if (entryNumber == 1 && entryWire == WireTypeOf(field.MapKeyKind))
                    {
                        key = ReadScalarAsString(
                            entry,
                            schema,
                            field.MapKeyKind,
                            field.MapValueTypeName
                        );
                    }
                    else if (entryNumber == 2 && entryWire == WireTypeOf(field.MapValueKind))
                    {
                        var valueField = new GrpcField
                        {
                            Name = "value",
                            JsonName = "value",
                            Number = 2,
                            Kind = field.MapValueKind,
                            TypeName = field.MapValueTypeName,
                        };
                        mapValue = ReadScalar(entry, schema, valueField, depth + 1);
                    }
                    else
                    {
                        entry.SkipLastField();
                    }
                }

                var map = result[field.JsonName] as JsonObject ?? new JsonObject();
                map[key] = mapValue;
                result[field.JsonName] = map;
                continue;
            }

            if (!field.Repeated && result.ContainsKey(field.JsonName))
            {
                // Last one wins for non-repeated fields; keep decoding into the same shape.
            }

            var packedRepeated =
                field.Repeated
                && wireType == WireFormat.WireType.LengthDelimited
                && IsPackable(field.Kind);
            if (!packedRepeated && wireType != WireTypeOf(field.Kind))
            {
                input.SkipLastField();
                continue;
            }

            if (field.Repeated)
            {
                var array = result[field.JsonName] as JsonArray ?? new JsonArray();
                if (packedRepeated)
                {
                    var packed = input.ReadBytes();
                    var packedInput = new CodedInputStream(packed.ToByteArray());
                    while (!packedInput.IsAtEnd)
                    {
                        array.Add(ReadScalar(packedInput, schema, field, depth + 1));
                    }
                }
                else
                {
                    array.Add(ReadScalar(input, schema, field, depth + 1));
                }

                result[field.JsonName] = array;
            }
            else
            {
                result[field.JsonName] = ReadScalar(input, schema, field, depth + 1);
            }
        }

        return result;
    }

    private static JsonNode DecodeWellKnown(
        CodedInputStream input,
        GrpcSchema schema,
        GrpcMessage message,
        int depth
    )
    {
        return message.Name switch
        {
            "google.protobuf.Timestamp" => JsonValue.Create(DecodeTimestamp(input))!,
            "google.protobuf.Duration" => JsonValue.Create(DecodeDuration(input))!,
            "google.protobuf.FieldMask" => JsonValue.Create(DecodeFieldMask(input))!,
            "google.protobuf.Struct" => DecodeStruct(input, schema, depth),
            "google.protobuf.Value" => DecodeValue(input, schema, depth)
                ?? JsonValue.Create(string.Empty)!,
            "google.protobuf.ListValue" => DecodeListValue(input, schema, depth),
            "google.protobuf.Empty" => new JsonObject(),
            _ => DecodeWrapper(input, schema, message, depth) ?? JsonValue.Create(string.Empty)!,
        };
    }

    private static string DecodeTimestamp(CodedInputStream input)
    {
        long seconds = 0;
        var nanos = 0;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1:
                    seconds = input.ReadInt64();
                    break;
                case 2:
                    nanos = input.ReadInt32();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
        return timestamp.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );
    }

    private static string DecodeDuration(CodedInputStream input)
    {
        long seconds = 0;
        var nanos = 0;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1:
                    seconds = input.ReadInt64();
                    break;
                case 2:
                    nanos = input.ReadInt32();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }

        if (nanos == 0)
        {
            return $"{seconds}s";
        }

        return $"{seconds}.{nanos:D9}".TrimEnd('0') + "s";
    }

    private static string DecodeFieldMask(CodedInputStream input)
    {
        var paths = new List<string>();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 1)
            {
                paths.Add(input.ReadString());
            }
            else
            {
                input.SkipLastField();
            }
        }

        return string.Join(",", paths);
    }

    private static JsonObject DecodeStruct(CodedInputStream input, GrpcSchema schema, int depth)
    {
        var result = new JsonObject();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) != 1)
            {
                input.SkipLastField();
                continue;
            }

            var entryBytes = input.ReadBytes();
            var entryInput = new CodedInputStream(entryBytes.ToByteArray());
            var key = string.Empty;
            JsonNode? value = null;
            uint entryTag;
            while ((entryTag = entryInput.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(entryTag))
                {
                    case 1:
                        key = entryInput.ReadString();
                        break;
                    case 2:
                        value = DecodeValue(
                            new CodedInputStream(entryInput.ReadBytes().ToByteArray()),
                            schema,
                            depth + 1
                        );
                        break;
                    default:
                        entryInput.SkipLastField();
                        break;
                }
            }

            result[key] = value;
        }

        return result;
    }

    private static JsonNode? DecodeValue(CodedInputStream input, GrpcSchema schema, int depth)
    {
        JsonNode? result = null;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1:
                    input.ReadEnum();
                    result = null;
                    break;
                case 2:
                    result = JsonValue.Create(input.ReadDouble());
                    break;
                case 3:
                    result = JsonValue.Create(input.ReadString());
                    break;
                case 4:
                    result = JsonValue.Create(input.ReadBool());
                    break;
                case 5:
                    result = DecodeStruct(
                        new CodedInputStream(input.ReadBytes().ToByteArray()),
                        schema,
                        depth + 1
                    );
                    break;
                case 6:
                    result = DecodeListValue(
                        new CodedInputStream(input.ReadBytes().ToByteArray()),
                        schema,
                        depth + 1
                    );
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }

        return result;
    }

    private static JsonArray DecodeListValue(CodedInputStream input, GrpcSchema schema, int depth)
    {
        var result = new JsonArray();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 1)
            {
                result.Add(
                    DecodeValue(
                        new CodedInputStream(input.ReadBytes().ToByteArray()),
                        schema,
                        depth + 1
                    )
                );
            }
            else
            {
                input.SkipLastField();
            }
        }

        return result;
    }

    private static JsonNode? DecodeWrapper(
        CodedInputStream input,
        GrpcSchema schema,
        GrpcMessage message,
        int depth
    )
    {
        if (message.Name == "google.protobuf.Any")
        {
            var result = new JsonObject();
            uint anyTag;
            while ((anyTag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(anyTag))
                {
                    case 1:
                        result["type_url"] = input.ReadString();
                        break;
                    case 2:
                        result["value"] = input.ReadBytes().ToBase64();
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }

            return result;
        }

        if (message.Fields.FirstOrDefault() is not { Name: "value" } field)
        {
            return new JsonObject();
        }

        JsonNode? value = null;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 1)
            {
                value = ReadScalar(input, schema, field, depth + 1);
            }
            else
            {
                input.SkipLastField();
            }
        }

        return value;
    }

    private static JsonNode? ReadScalar(
        CodedInputStream input,
        GrpcSchema schema,
        GrpcField field,
        int depth
    )
    {
        return field.Kind switch
        {
            GrpcFieldKind.Double => JsonValue.Create(input.ReadDouble()),
            GrpcFieldKind.Float => JsonValue.Create(input.ReadFloat()),
            GrpcFieldKind.Int32 => JsonValue.Create(input.ReadInt32()),
            GrpcFieldKind.Int64 => JsonValue.Create(
                input.ReadInt64().ToString(CultureInfo.InvariantCulture)
            ),
            GrpcFieldKind.UInt32 => JsonValue.Create(input.ReadUInt32()),
            GrpcFieldKind.UInt64 => JsonValue.Create(
                input.ReadUInt64().ToString(CultureInfo.InvariantCulture)
            ),
            GrpcFieldKind.SInt32 => JsonValue.Create(input.ReadSInt32()),
            GrpcFieldKind.SInt64 => JsonValue.Create(
                input.ReadSInt64().ToString(CultureInfo.InvariantCulture)
            ),
            GrpcFieldKind.Fixed32 => JsonValue.Create(input.ReadFixed32()),
            GrpcFieldKind.Fixed64 => JsonValue.Create(
                input.ReadFixed64().ToString(CultureInfo.InvariantCulture)
            ),
            GrpcFieldKind.SFixed32 => JsonValue.Create(input.ReadSFixed32()),
            GrpcFieldKind.SFixed64 => JsonValue.Create(
                input.ReadSFixed64().ToString(CultureInfo.InvariantCulture)
            ),
            GrpcFieldKind.Bool => JsonValue.Create(input.ReadBool()),
            GrpcFieldKind.String => JsonValue.Create(input.ReadString()),
            GrpcFieldKind.Bytes => JsonValue.Create(input.ReadBytes().ToBase64()),
            GrpcFieldKind.Enum => ReadEnum(input, schema, field),
            GrpcFieldKind.Message => DecodeNested(input, schema, field, depth),
            _ => null,
        };
    }

    private static JsonNode? DecodeNested(
        CodedInputStream input,
        GrpcSchema schema,
        GrpcField field,
        int depth
    )
    {
        var type = schema.FindMessage(field.TypeName);
        if (type is null)
        {
            input.SkipLastField();
            return JsonValue.Create(string.Empty);
        }

        var bytes = input.ReadBytes();
        return DecodeMessage(new CodedInputStream(bytes.ToByteArray()), schema, type, depth);
    }

    private static JsonNode? ReadEnum(CodedInputStream input, GrpcSchema schema, GrpcField field)
    {
        var number = input.ReadEnum();
        var enumType = schema.FindEnum(field.TypeName);
        return JsonValue.Create(
            enumType?.NamesByNumber.TryGetValue(number, out var name) == true
                ? name
                : number.ToString(CultureInfo.InvariantCulture)
        );
    }

    private static string ReadScalarAsString(
        CodedInputStream input,
        GrpcSchema schema,
        GrpcFieldKind kind,
        string typeName
    )
    {
        var field = new GrpcField
        {
            Name = "key",
            JsonName = "key",
            Number = 1,
            Kind = kind,
            TypeName = typeName,
        };
        return ReadScalar(input, schema, field, 0)?.ToJsonString().Trim('"') ?? string.Empty;
    }

    private static WireFormat.WireType WireTypeOf(GrpcFieldKind kind) =>
        kind switch
        {
            GrpcFieldKind.Double or GrpcFieldKind.Fixed64 or GrpcFieldKind.SFixed64 => WireFormat
                .WireType
                .Fixed64,
            GrpcFieldKind.Float or GrpcFieldKind.Fixed32 or GrpcFieldKind.SFixed32 => WireFormat
                .WireType
                .Fixed32,
            GrpcFieldKind.String or GrpcFieldKind.Bytes or GrpcFieldKind.Message => WireFormat
                .WireType
                .LengthDelimited,
            _ => WireFormat.WireType.Varint,
        };

    private static bool IsPackable(GrpcFieldKind kind) =>
        kind is not (GrpcFieldKind.String or GrpcFieldKind.Bytes or GrpcFieldKind.Message);

    private static GrpcField? FindField(GrpcMessage message, string key) =>
        message.Fields.FirstOrDefault(field =>
            string.Equals(field.JsonName, key, StringComparison.Ordinal)
        )
        ?? message.Fields.FirstOrDefault(field =>
            string.Equals(field.Name, key, StringComparison.Ordinal)
        )
        ?? message.Fields.FirstOrDefault(field =>
            string.Equals(field.JsonName, key, StringComparison.OrdinalIgnoreCase)
        );

    private static double ToDouble(JsonNode value) =>
        double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture);

    private static int ToInt32(JsonNode value) => (int)ToInt64(value);

    private static long ToInt64(JsonNode value)
    {
        var text = value.ToJsonString().Trim('"');
        return long.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result
        )
            ? result
            : (long)double.Parse(text, CultureInfo.InvariantCulture);
    }

    private static ulong ToUInt64(JsonNode value)
    {
        var text = value.ToJsonString().Trim('"');
        return ulong.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result
        )
            ? result
            : (ulong)double.Parse(text, CultureInfo.InvariantCulture);
    }

    private static bool ToBool(JsonNode value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var result)
            ? result
            : bool.Parse(value.ToJsonString());

    private static string ToStringValue(JsonNode value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : value.ToJsonString();

    private static byte[] ToBytes(JsonNode value)
    {
        var text = ToStringValue(value);
        return string.IsNullOrEmpty(text) ? [] : Convert.FromBase64String(text);
    }

    private static int ToEnumValue(
        GrpcSchema schema,
        GrpcField field,
        JsonNode value,
        ICollection<string> warnings
    )
    {
        var text = value.ToJsonString().Trim('"');
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        var enumType = schema.FindEnum(field.TypeName);
        if (enumType?.NumbersByName.TryGetValue(text, out var named) == true)
        {
            return named;
        }

        warnings.Add($"未知枚举值 {text}，按 0 发送。");
        return 0;
    }

    private static void EncodeTimestamp(CodedOutputStream output, JsonNode value)
    {
        if (
            value is JsonObject fields
            && fields.TryGetPropertyValue("seconds", out var secondsNode)
            && fields.TryGetPropertyValue("nanos", out var nanosNode)
        )
        {
            output.WriteTag(1, WireFormat.WireType.Varint);
            output.WriteInt64(ToInt64(secondsNode!));
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteInt32(ToInt32(nanosNode!));
            return;
        }

        var text = value.ToJsonString().Trim('"');
        var timestamp = DateTimeOffset.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
        );
        var unixSeconds = timestamp.ToUnixTimeSeconds();
        var nanos = (int)(
            (timestamp - DateTimeOffset.FromUnixTimeSeconds(unixSeconds)).Ticks * 100
        );
        output.WriteTag(1, WireFormat.WireType.Varint);
        output.WriteInt64(unixSeconds);
        if (nanos != 0)
        {
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteInt32(nanos);
        }
    }

    private static void EncodeDuration(CodedOutputStream output, JsonNode value)
    {
        if (
            value is JsonObject fields
            && fields.TryGetPropertyValue("seconds", out var secondsNode)
            && fields.TryGetPropertyValue("nanos", out var nanosNode)
        )
        {
            output.WriteTag(1, WireFormat.WireType.Varint);
            output.WriteInt64(ToInt64(secondsNode!));
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteInt32(ToInt32(nanosNode!));
            return;
        }

        var text = value.ToJsonString().Trim('"').TrimEnd('s');
        var seconds = decimal.Parse(text, CultureInfo.InvariantCulture);
        var whole = (long)Math.Truncate(seconds);
        var nanos = (int)((seconds - whole) * 1_000_000_000m);
        output.WriteTag(1, WireFormat.WireType.Varint);
        output.WriteInt64(whole);
        if (nanos != 0)
        {
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteInt32(nanos);
        }
    }

    private static void EncodeFieldMask(CodedOutputStream output, JsonNode json)
    {
        IEnumerable<string> paths = json switch
        {
            JsonArray array => array.Select(item => item?.ToJsonString().Trim('"') ?? string.Empty),
            JsonValue => [json.ToJsonString().Trim('"')],
            _ => [],
        };
        foreach (var path in paths)
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteString(path);
        }
    }

    private static void EncodeValue(
        CodedOutputStream output,
        GrpcSchema schema,
        JsonNode? json,
        ICollection<string> warnings,
        int depth
    )
    {
        switch (json)
        {
            case null:
                output.WriteTag(1, WireFormat.WireType.Varint);
                output.WriteEnum(0);
                return;
            case JsonObject objectValue:
            {
                var bytes = EncodeNested(
                    schema,
                    schema.FindMessage("google.protobuf.Struct")!,
                    objectValue,
                    warnings,
                    depth + 1
                );
                output.WriteTag(5, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(bytes));
                return;
            }
            case JsonArray arrayValue:
            {
                var bytes = EncodeNested(
                    schema,
                    schema.FindMessage("google.protobuf.ListValue")!,
                    arrayValue,
                    warnings,
                    depth + 1
                );
                output.WriteTag(6, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(bytes));
                return;
            }
            case JsonValue value:
                if (value.TryGetValue<bool>(out var boolean))
                {
                    output.WriteTag(4, WireFormat.WireType.Varint);
                    output.WriteBool(boolean);
                }
                else if (value.TryGetValue<string>(out var text))
                {
                    output.WriteTag(3, WireFormat.WireType.LengthDelimited);
                    output.WriteString(text);
                }
                else
                {
                    output.WriteTag(2, WireFormat.WireType.Fixed64);
                    output.WriteDouble(ToDouble(value));
                }

                return;
        }
    }

    private static void EncodeStruct(
        CodedOutputStream output,
        GrpcSchema schema,
        JsonObject value,
        ICollection<string> warnings,
        int depth
    )
    {
        foreach (var pair in value)
        {
            using var entryStream = new MemoryStream();
            var entry = new CodedOutputStream(entryStream);
            try
            {
                entry.WriteTag(1, WireFormat.WireType.LengthDelimited);
                entry.WriteString(pair.Key);
                var valueBytes = EncodeNested(
                    schema,
                    schema.FindMessage("google.protobuf.Value")!,
                    pair.Value,
                    warnings,
                    depth + 1
                );
                entry.WriteTag(2, WireFormat.WireType.LengthDelimited);
                entry.WriteBytes(ByteString.CopyFrom(valueBytes));
            }
            finally
            {
                entry.Flush();
            }

            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(entryStream.ToArray()));
        }
    }
}
