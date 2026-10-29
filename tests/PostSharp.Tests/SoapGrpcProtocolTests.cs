using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class SoapGrpcProtocolTests
{
    [Fact]
    public void SoapComposerWrapsBodyAndSetsHeaders()
    {
        var request = new RequestItem
        {
            Protocol = RequestProtocol.Soap,
            Url = "https://example.com/User",
            SoapVersion = "1.1",
            SoapAction = "urn:demo/GetUser",
            MessageBody = "<GetUser xmlns=\"urn:demo\"><id>1</id></GetUser>",
        };

        var composed = RequestComposer.Compose(request, null);

        Assert.NotNull(composed.SoapOptions);
        Assert.Null(composed.GrpcOptions);
        Assert.Null(composed.ProtocolOptions);
        Assert.Equal("POST", composed.Method);
        Assert.Contains("Envelope", composed.Body, StringComparison.Ordinal);
        Assert.Contains("GetUser", composed.Body, StringComparison.Ordinal);
        Assert.Equal("text/xml", composed.ContentType);
        Assert.Contains(
            composed.Headers,
            pair => pair.Key == "SOAPAction" && pair.Value == "\"urn:demo/GetUser\""
        );
    }

    [Fact]
    public void Soap12UsesSoap12EnvelopeAndContentType()
    {
        var request = new RequestItem
        {
            Protocol = RequestProtocol.Soap,
            Url = "https://example.com/User",
            SoapVersion = "1.2",
            SoapAction = "urn:demo/GetUser",
            MessageBody = "<GetUser xmlns=\"urn:demo\" />",
        };

        var composed = RequestComposer.Compose(request, null);

        Assert.Contains(
            "http://www.w3.org/2003/05/soap-envelope",
            composed.Body,
            StringComparison.Ordinal
        );
        Assert.Contains("application/soap+xml", composed.ContentType, StringComparison.Ordinal);
        Assert.DoesNotContain(
            composed.Headers,
            pair => pair.Key.Equals("SOAPAction", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void SoapFullEnvelopeIsKeptAsIs()
    {
        const string envelope =
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><Ping xmlns=\"urn:demo\" /></soap:Body></soap:Envelope>";
        var request = new RequestItem
        {
            Protocol = RequestProtocol.Soap,
            Url = "https://example.com/User",
            MessageBody = envelope,
        };

        var composed = RequestComposer.Compose(request, null);

        Assert.Equal(envelope, composed.Body);
    }

    [Fact]
    public void SoapFaultIsExtractedIntoReasonAndHeaders()
    {
        var result = new HttpResult(
            true,
            500,
            "Internal Server Error",
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><soap:Fault><faultcode>soap:Server</faultcode><faultstring>Boom</faultstring></soap:Fault></soap:Body></soap:Envelope>",
            false,
            "text/xml",
            10,
            100,
            [],
            null
        );

        var annotated = SoapProtocolService.AnnotateFault(result);

        Assert.Contains(
            "SOAP Fault: soap:Server Boom",
            annotated.ReasonPhrase,
            StringComparison.Ordinal
        );
        Assert.Contains(annotated.Headers, header => header.Key == "SOAP-Fault");
    }

    [Fact]
    public async Task SoapWsdlDescriptionBuildsSampleEnvelope()
    {
        const string wsdl = """
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                         xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                         xmlns:tns="urn:demo"
                         xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                         targetNamespace="urn:demo">
              <types>
                <xsd:schema targetNamespace="urn:demo" elementFormDefault="qualified">
                  <xsd:element name="GetUser">
                    <xsd:complexType>
                      <xsd:sequence>
                        <xsd:element name="id" type="xsd:int" />
                        <xsd:element name="name" type="xsd:string" />
                      </xsd:sequence>
                    </xsd:complexType>
                  </xsd:element>
                </xsd:schema>
              </types>
              <message name="GetUserRequest">
                <part name="parameters" element="tns:GetUser" />
              </message>
              <portType name="UserPortType">
                <operation name="GetUser"><input message="tns:GetUserRequest" /></operation>
              </portType>
              <binding name="UserBinding" type="tns:UserPortType">
                <soap:binding style="document" transport="http://schemas.xmlsoap.org/soap/http" />
                <operation name="GetUser">
                  <soap:operation soapAction="urn:demo/GetUser" />
                  <input><soap:body use="literal" /></input>
                </operation>
              </binding>
              <service name="UserService">
                <port name="UserPort" binding="tns:UserBinding">
                  <soap:address location="https://example.com/User" />
                </port>
              </service>
            </definitions>
            """;

        var description = await SoapProtocolService.DescribeAsync(null, wsdl, includeSample: true);

        var operation = Assert.Single(description.Operations);
        Assert.Equal("UserService", operation.Service);
        Assert.Equal("https://example.com/User", operation.Endpoint);
        Assert.Equal("urn:demo/GetUser", operation.SoapAction);
        Assert.Equal("1.1", operation.SoapVersion);
        Assert.Contains("GetUser", operation.RequestElement, StringComparison.Ordinal);
        Assert.NotNull(operation.SampleEnvelope);
        Assert.Contains("<GetUser", operation.SampleEnvelope, StringComparison.Ordinal);
        Assert.Contains("<id>0</id>", operation.SampleEnvelope, StringComparison.Ordinal);
        Assert.Contains("<name>string</name>", operation.SampleEnvelope, StringComparison.Ordinal);
    }

    [Fact]
    public void GrpcComposerBuildsOptionsWithoutHttpBody()
    {
        var request = new RequestItem
        {
            Protocol = RequestProtocol.Grpc,
            Url = "https://localhost:5001",
            GrpcService = "demo.Greeter",
            GrpcMethod = "SayHello",
            GrpcDeadlineSeconds = 10,
            MessageBody = "{\"name\":\"PostSharp\"}",
        };

        var composed = RequestComposer.Compose(request, null);

        Assert.Null(composed.Body);
        Assert.Null(composed.SoapOptions);
        var options = Assert.IsType<GrpcSendOptions>(composed.GrpcOptions);
        Assert.Equal("demo.Greeter", options.Service);
        Assert.Equal("SayHello", options.Method);
        Assert.Equal(10, options.DeadlineSeconds);
        Assert.Equal("{\"name\":\"PostSharp\"}", options.RequestJson);
    }

    [Fact]
    public void GrpcSchemaFindsServiceAndMessages()
    {
        var schema = BuildSchema();

        var service = Assert.IsType<GrpcService>(schema.FindService("demo.Greeter"));
        var method = Assert.Single(service.Methods);
        Assert.Equal("SayHello", method.Name);
        Assert.Equal("demo.HelloRequest", method.InputType);
        Assert.NotNull(schema.FindMessage("demo.HelloRequest"));
        Assert.NotNull(schema.FindEnum("demo.Status"));
    }

    [Fact]
    public void GrpcCodecRoundTripsScalarsRepeatedEnumAndMap()
    {
        var schema = BuildSchema();
        var message = Assert.IsType<GrpcMessage>(schema.FindMessage("demo.HelloRequest"));
        var warnings = new List<string>();
        var json = JsonNode.Parse(
            """
            {
              "name": "PostSharp",
              "count": 3,
              "tags": ["a", "b"],
              "status": "ACTIVE",
              "labels": { "env": "test" }
            }
            """
        );

        var bytes = GrpcMessageCodec.Encode(schema, message, json, warnings);
        var decoded = GrpcMessageCodec.Decode(schema, message, bytes);

        Assert.Empty(warnings);
        var result = Assert.IsType<JsonObject>(decoded);
        Assert.Equal("PostSharp", result["name"]!.GetValue<string>());
        Assert.Equal(3, result["count"]!.GetValue<int>());
        Assert.Equal(2, result["tags"]!.AsArray().Count);
        Assert.Equal("ACTIVE", result["status"]!.GetValue<string>());
        Assert.Equal("test", result["labels"]!["env"]!.GetValue<string>());
    }

    [Fact]
    public void GrpcCodecHandlesTimestamp()
    {
        var schema = BuildSchema();
        var message = Assert.IsType<GrpcMessage>(schema.FindMessage("demo.HelloRequest"));
        var json = JsonNode.Parse("""{ "createdAt": "2000-01-01T00:00:00Z" }""");

        var bytes = GrpcMessageCodec.Encode(schema, message, json, new List<string>());
        var decoded = Assert.IsType<JsonObject>(GrpcMessageCodec.Decode(schema, message, bytes));

        Assert.StartsWith("2000-01-01T00:00:00", decoded["createdAt"]!.GetValue<string>());
    }

    [Fact]
    public void ReflectionCodecParsesServicesAndDescriptors()
    {
        var descriptor = BuildSchemaFile().ToByteArray();
        using var servicesStream = new MemoryStream();
        var servicesOutput = new CodedOutputStream(servicesStream);
        servicesOutput.WriteTag(1, WireFormat.WireType.LengthDelimited);
        using (var serviceStream = new MemoryStream())
        {
            var serviceOutput = new CodedOutputStream(serviceStream);
            serviceOutput.WriteTag(1, WireFormat.WireType.LengthDelimited);
            serviceOutput.WriteString("demo.Greeter");
            serviceOutput.Flush();
            servicesOutput.WriteBytes(ByteString.CopyFrom(serviceStream.ToArray()));
        }

        servicesOutput.Flush();

        using var descriptorsStream = new MemoryStream();
        var descriptorsOutput = new CodedOutputStream(descriptorsStream);
        descriptorsOutput.WriteTag(1, WireFormat.WireType.LengthDelimited);
        descriptorsOutput.WriteBytes(ByteString.CopyFrom(descriptor));
        descriptorsOutput.Flush();

        using var responseStream = new MemoryStream();
        var responseOutput = new CodedOutputStream(responseStream);
        responseOutput.WriteTag(6, WireFormat.WireType.LengthDelimited);
        responseOutput.WriteBytes(ByteString.CopyFrom(servicesStream.ToArray()));
        responseOutput.WriteTag(4, WireFormat.WireType.LengthDelimited);
        responseOutput.WriteBytes(ByteString.CopyFrom(descriptorsStream.ToArray()));
        responseOutput.Flush();

        var parsed = GrpcReflectionCodec.Parse(responseStream.ToArray());

        Assert.Contains("demo.Greeter", parsed.Services);
        Assert.Single(parsed.FileDescriptors);
        var file = FileDescriptorProto.Parser.ParseFrom(parsed.FileDescriptors[0]);
        Assert.Equal("test.proto", file.Name);
    }

    private static GrpcSchema BuildSchema() => GrpcSchema.FromFiles([BuildSchemaFile()]);

    private static FileDescriptorProto BuildSchemaFile()
    {
        var file = new FileDescriptorProto
        {
            Name = "test.proto",
            Package = "demo",
            Syntax = "proto3",
        };

        var mapEntry = new DescriptorProto
        {
            Name = "LabelsEntry",
            Options = new MessageOptions { MapEntry = true },
        };
        mapEntry.Field.Add(
            new FieldDescriptorProto
            {
                Name = "key",
                Number = 1,
                Label = FieldDescriptorProto.Types.Label.Optional,
                Type = FieldDescriptorProto.Types.Type.String,
            }
        );
        mapEntry.Field.Add(
            new FieldDescriptorProto
            {
                Name = "value",
                Number = 2,
                Label = FieldDescriptorProto.Types.Label.Optional,
                Type = FieldDescriptorProto.Types.Type.String,
            }
        );

        var request = new DescriptorProto { Name = "HelloRequest" };
        request.NestedType.Add(mapEntry);
        request.Field.Add(
            new FieldDescriptorProto
            {
                Name = "name",
                Number = 1,
                Label = FieldDescriptorProto.Types.Label.Optional,
                Type = FieldDescriptorProto.Types.Type.String,
            }
        );
        request.Field.Add(
            new FieldDescriptorProto
            {
                Name = "count",
                Number = 2,
                Label = FieldDescriptorProto.Types.Label.Optional,
                Type = FieldDescriptorProto.Types.Type.Int32,
            }
        );
        request.Field.Add(
            new FieldDescriptorProto
            {
                Name = "tags",
                Number = 3,
                Label = FieldDescriptorProto.Types.Label.Repeated,
                Type = FieldDescriptorProto.Types.Type.String,
            }
        );
        request.Field.Add(
            new FieldDescriptorProto
            {
                Name = "status",
                Number = 4,
                Label = FieldDescriptorProto.Types.Label.Optional,
                Type = FieldDescriptorProto.Types.Type.Enum,
                TypeName = ".demo.Status",
            }
        );
        request.Field.Add(
            new FieldDescriptorProto
            {
                Name = "labels",
                Number = 5,
                Label = FieldDescriptorProto.Types.Label.Repeated,
                Type = FieldDescriptorProto.Types.Type.Message,
                TypeName = ".demo.HelloRequest.LabelsEntry",
            }
        );
        request.Field.Add(
            new FieldDescriptorProto
            {
                Name = "createdAt",
                Number = 6,
                Label = FieldDescriptorProto.Types.Label.Optional,
                Type = FieldDescriptorProto.Types.Type.Message,
                TypeName = ".google.protobuf.Timestamp",
            }
        );

        var reply = new DescriptorProto { Name = "HelloReply" };
        reply.Field.Add(
            new FieldDescriptorProto
            {
                Name = "message",
                Number = 1,
                Label = FieldDescriptorProto.Types.Label.Optional,
                Type = FieldDescriptorProto.Types.Type.String,
            }
        );

        var status = new EnumDescriptorProto { Name = "Status" };
        status.Value.Add(new EnumValueDescriptorProto { Name = "UNKNOWN", Number = 0 });
        status.Value.Add(new EnumValueDescriptorProto { Name = "ACTIVE", Number = 1 });

        var service = new ServiceDescriptorProto { Name = "Greeter" };
        service.Method.Add(
            new MethodDescriptorProto
            {
                Name = "SayHello",
                InputType = ".demo.HelloRequest",
                OutputType = ".demo.HelloReply",
            }
        );

        file.MessageType.Add(request);
        file.MessageType.Add(reply);
        file.EnumType.Add(status);
        file.Service.Add(service);
        return file;
    }
}
