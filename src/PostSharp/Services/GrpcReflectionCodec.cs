using Google.Protobuf;

namespace PostSharp.Services;

public sealed record GrpcReflectionResponse(
    IReadOnlyList<string> Services,
    IReadOnlyList<byte[]> FileDescriptors,
    string? Error
);

/// <summary>Minimal encoder/decoder for the gRPC server reflection protocol (v1alpha/v1).</summary>
public static class GrpcReflectionCodec
{
    public static byte[] ListServicesRequest()
    {
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        output.WriteTag(7, WireFormat.WireType.LengthDelimited);
        output.WriteString(string.Empty);
        output.Flush();
        return stream.ToArray();
    }

    public static byte[] FileContainingSymbolRequest(string symbol)
    {
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        output.WriteTag(4, WireFormat.WireType.LengthDelimited);
        output.WriteString(symbol);
        output.Flush();
        return stream.ToArray();
    }

    public static byte[] FileByFilenameRequest(string filename)
    {
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        output.WriteTag(3, WireFormat.WireType.LengthDelimited);
        output.WriteString(filename);
        output.Flush();
        return stream.ToArray();
    }

    public static GrpcReflectionResponse Parse(byte[] bytes)
    {
        var services = new List<string>();
        var descriptors = new List<byte[]>();
        string? error = null;

        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 4:
                    descriptors.AddRange(
                        ParseFileDescriptorResponse(input.ReadBytes().ToByteArray())
                    );
                    break;
                case 6:
                    services.AddRange(ParseListServicesResponse(input.ReadBytes().ToByteArray()));
                    break;
                case 7:
                    error = ParseErrorResponse(input.ReadBytes().ToByteArray());
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }

        return new GrpcReflectionResponse(services, descriptors, error);
    }

    private static IEnumerable<byte[]> ParseFileDescriptorResponse(byte[] bytes)
    {
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 1)
            {
                yield return input.ReadBytes().ToByteArray();
            }
            else
            {
                input.SkipLastField();
            }
        }
    }

    private static IEnumerable<string> ParseListServicesResponse(byte[] bytes)
    {
        var input = new CodedInputStream(bytes);
        var services = new List<string>();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 1)
            {
                services.Add(ParseServiceResponse(input.ReadBytes().ToByteArray()));
            }
            else
            {
                input.SkipLastField();
            }
        }

        return services;
    }

    private static string ParseServiceResponse(byte[] bytes)
    {
        var input = new CodedInputStream(bytes);
        var name = string.Empty;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 1)
            {
                name = input.ReadString();
            }
            else
            {
                input.SkipLastField();
            }
        }

        return name;
    }

    private static string ParseErrorResponse(byte[] bytes)
    {
        var input = new CodedInputStream(bytes);
        var code = 0;
        var message = string.Empty;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1:
                    code = input.ReadInt32();
                    break;
                case 2:
                    message = input.ReadString();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }

        return message.Length > 0 ? $"reflection error {code}: {message}" : string.Empty;
    }
}
