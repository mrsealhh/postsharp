using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed record SoapOperationInfo(
    string Service,
    string Port,
    string Endpoint,
    string Binding,
    string Operation,
    string SoapVersion,
    string SoapAction,
    string RequestElement,
    string? SampleEnvelope,
    IReadOnlyList<string> Warnings
);

public sealed record SoapDescription(
    string Source,
    IReadOnlyList<SoapOperationInfo> Operations,
    IReadOnlyList<string> Warnings
);

/// <summary>
/// SOAP over HTTP: builds the envelope, extracts Fault details, and reads a shallow WSDL description.
/// </summary>
public static class SoapProtocolService
{
    private static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsdl = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Soap11Binding = "http://schemas.xmlsoap.org/wsdl/soap/";
    private static readonly XNamespace Soap12Binding = "http://www.w3.org/2003/05/wsdl/soap12/";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    private static readonly HttpClient Client = CreateClient();

    public static string BuildEnvelope(string version, string? body)
    {
        var text = (body ?? string.Empty).Trim();
        var soapNamespace = version == "1.2" ? Soap12 : Soap11;
        if (text.Length > 0 && IsEnvelope(text))
        {
            return text;
        }

        var envelope = new XElement(
            soapNamespace + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", soapNamespace)
        );
        var bodyElement = new XElement(soapNamespace + "Body");
        envelope.Add(bodyElement);
        if (text.Length > 0)
        {
            bodyElement.Add(ParseFragment(text));
        }

        return envelope.ToString();
    }

    public static HttpResult AnnotateFault(HttpResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Body))
        {
            return result;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(result.Body);
        }
        catch (XmlException)
        {
            return result;
        }

        var fault = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Fault");
        if (fault is null)
        {
            return result;
        }

        var code =
            fault
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName is "faultcode" or "Value")
                ?.Value.Trim()
            ?? string.Empty;
        var message =
            fault
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName is "faultstring" or "Text")
                ?.Value.Trim()
            ?? string.Empty;
        var summary = $"SOAP Fault: {code} {message}".Trim();
        var headers = result
            .Headers.Concat([new ResponseHeaderView("SOAP-Fault", summary)])
            .ToList();
        var reason = string.IsNullOrWhiteSpace(result.ReasonPhrase)
            ? summary
            : $"{result.ReasonPhrase} · {summary}";
        return result with { ReasonPhrase = reason, Headers = headers };
    }

    public static async Task<SoapDescription> DescribeAsync(
        string? wsdlUrl,
        string? wsdlContent,
        bool includeSample,
        CancellationToken cancellationToken = default
    )
    {
        var source = (wsdlUrl ?? string.Empty).Trim();
        var content = wsdlContent;
        if (string.IsNullOrWhiteSpace(content))
        {
            if (source.Length == 0)
            {
                throw new InvalidOperationException("请提供 WSDL 地址或 WSDL 内容。");
            }

            content = await FetchTextAsync(source, cancellationToken).ConfigureAwait(false);
        }

        var baseUri = TryCreateBaseUri(source);
        XDocument document;
        try
        {
            document = XDocument.Parse(content);
        }
        catch (XmlException exception)
        {
            throw new InvalidOperationException(
                $"WSDL 不是合法 XML：{exception.Message}",
                exception
            );
        }

        var definitions = document.Root;
        if (definitions is null || definitions.Name.LocalName != "definitions")
        {
            throw new InvalidOperationException(
                "不是 WSDL 1.1 文档（根节点应为 wsdl:definitions）。"
            );
        }

        var warnings = new List<string>();
        var messages = definitions
            .Elements(Wsdl + "message")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element,
                StringComparer.Ordinal
            );
        var portTypes = definitions
            .Elements(Wsdl + "portType")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element,
                StringComparer.Ordinal
            );
        var bindings = definitions
            .Elements(Wsdl + "binding")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element,
                StringComparer.Ordinal
            );

        var schemas = new XmlSchemaSet { XmlResolver = new HttpXmlResolver() };
        foreach (var schemaElement in definitions.Descendants(Xsd + "schema"))
        {
            try
            {
                using var reader = XmlReader.Create(
                    new StringReader(schemaElement.ToString()),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit },
                    baseUri?.ToString() ?? string.Empty
                );
                schemas.Add(null, reader);
            }
            catch (Exception exception) when (exception is XmlException or XmlSchemaException)
            {
                warnings.Add($"内联 XSD 解析失败：{exception.Message}");
            }
        }

        try
        {
            schemas.Compile();
        }
        catch (XmlSchemaException exception)
        {
            warnings.Add($"XSD 编译失败，示例消息可能不完整：{exception.Message}");
        }

        var operations = new List<SoapOperationInfo>();
        foreach (var service in definitions.Elements(Wsdl + "service"))
        {
            var serviceName = service.Attribute("name")?.Value ?? string.Empty;
            foreach (var port in service.Elements(Wsdl + "port"))
            {
                var portName = port.Attribute("name")?.Value ?? string.Empty;
                var bindingName = LocalName(port.Attribute("binding")?.Value);
                if (bindingName.Length == 0 || !bindings.TryGetValue(bindingName, out var binding))
                {
                    warnings.Add($"端口 {portName} 找不到绑定 {bindingName}。");
                    continue;
                }

                var address = port.Elements()
                    .FirstOrDefault(element =>
                        element.Name.LocalName == "address"
                        && (
                            element.Name.Namespace == Soap11Binding
                            || element.Name.Namespace == Soap12Binding
                        )
                    );
                if (address is null)
                {
                    warnings.Add($"端口 {portName} 没有 SOAP 地址，已跳过。");
                    continue;
                }

                var endpoint = address.Attribute("location")?.Value ?? string.Empty;
                var portVersion =
                    address.Name.Namespace == Soap12Binding ? "1.2"
                    : address.Name.Namespace == Soap11Binding ? "1.1"
                    : "1.1";
                var portTypeName = LocalName(binding.Attribute("type")?.Value);
                portTypes.TryGetValue(portTypeName, out var portType);

                foreach (var bindingOperation in binding.Elements(Wsdl + "operation"))
                {
                    var operationName = bindingOperation.Attribute("name")?.Value ?? string.Empty;
                    if (operationName.Length == 0)
                    {
                        continue;
                    }

                    var soapOperation = bindingOperation
                        .Elements()
                        .FirstOrDefault(element =>
                            element.Name.LocalName == "operation"
                            && (
                                element.Name.Namespace == Soap11Binding
                                || element.Name.Namespace == Soap12Binding
                            )
                        );
                    var soapVersion =
                        soapOperation?.Name.Namespace == Soap12Binding ? "1.2"
                        : soapOperation?.Name.Namespace == Soap11Binding ? "1.1"
                        : portVersion;
                    var action = soapOperation?.Attribute("soapAction")?.Value ?? string.Empty;
                    var requestElement = string.Empty;
                    var sample = default(string);

                    var portTypeOperation = portType
                        ?.Elements(Wsdl + "operation")
                        .FirstOrDefault(element =>
                            element.Attribute("name")?.Value == operationName
                        );
                    var input = portTypeOperation?.Elements(Wsdl + "input").FirstOrDefault();
                    var messageName = LocalName(input?.Attribute("message")?.Value);
                    if (messages.TryGetValue(messageName, out var message))
                    {
                        var part = message.Elements(Wsdl + "part").FirstOrDefault();
                        var elementQName = ResolveQName(part, part?.Attribute("element")?.Value);
                        if (elementQName is not null)
                        {
                            requestElement = elementQName.ToString();
                            if (includeSample)
                            {
                                sample = TryBuildSample(schemas, elementQName, soapVersion);
                            }
                        }
                        else if (part?.Attribute("type") is not null)
                        {
                            requestElement = LocalName(part.Attribute("type")!.Value);
                            warnings.Add(
                                $"操作 {operationName} 使用 type 而不是 element，示例消息需要手工补全。"
                            );
                        }
                    }

                    operations.Add(
                        new SoapOperationInfo(
                            serviceName,
                            portName,
                            endpoint,
                            bindingName,
                            operationName,
                            soapVersion,
                            action,
                            requestElement,
                            sample,
                            warnings.Count == 0 ? [] : warnings.ToArray()
                        )
                    );
                }
            }
        }

        if (operations.Count == 0)
        {
            warnings.Add("WSDL 中没有找到 SOAP 1.1 / 1.2 操作。");
        }

        return new SoapDescription(source, operations, warnings);
    }

    private static string? TryBuildSample(XmlSchemaSet schemas, XName name, string version)
    {
        var qualifiedName = new XmlQualifiedName(name.LocalName, name.NamespaceName);
        if (schemas.GlobalElements[qualifiedName] is not XmlSchemaElement global)
        {
            return null;
        }

        try
        {
            var payload = BuildElement(global, 0);
            return payload is null ? null : BuildEnvelope(version, payload.ToString());
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "WSDL 示例消息生成失败 {Element}", name);
            return null;
        }
    }

    private static XElement? BuildElement(XmlSchemaElement element, int depth)
    {
        if (depth > 5)
        {
            return null;
        }

        var name = element.QualifiedName.Name.Length == 0 ? element.RefName : element.QualifiedName;
        var result =
            name.Name.Length == 0
                ? new XElement("value")
                : new XElement(XName.Get(name.Name, name.Namespace));
        var type = element.ElementSchemaType;
        if (type is null)
        {
            result.Value = "string";
            return result;
        }

        if (type is XmlSchemaSimpleType simple)
        {
            result.Value = SampleForSimpleType(simple);
            return result;
        }

        if (type is not XmlSchemaComplexType complex)
        {
            return result;
        }

        FillParticle(result, complex.ContentTypeParticle, depth);
        return result;
    }

    private static void FillParticle(XElement parent, XmlSchemaParticle? particle, int depth)
    {
        switch (particle)
        {
            case XmlSchemaElement element:
            {
                var child = BuildElement(element, depth + 1);
                if (child is not null)
                {
                    parent.Add(child);
                }

                break;
            }
            case XmlSchemaSequence sequence:
                foreach (var item in sequence.Items.OfType<XmlSchemaParticle>())
                {
                    FillParticle(parent, item, depth);
                }

                break;
            case XmlSchemaAll all:
                foreach (var item in all.Items.OfType<XmlSchemaParticle>())
                {
                    FillParticle(parent, item, depth);
                }

                break;
            case XmlSchemaChoice choice:
                if (choice.Items.OfType<XmlSchemaParticle>().FirstOrDefault() is { } first)
                {
                    FillParticle(parent, first, depth);
                }

                break;
        }
    }

    private static string SampleForSimpleType(XmlSchemaSimpleType type)
    {
        var code = type.Datatype?.TypeCode ?? XmlTypeCode.String;
        return code switch
        {
            XmlTypeCode.Boolean => "false",
            XmlTypeCode.Int
            or XmlTypeCode.Integer
            or XmlTypeCode.Long
            or XmlTypeCode.Short
            or XmlTypeCode.Byte
            or XmlTypeCode.UnsignedInt
            or XmlTypeCode.UnsignedLong
            or XmlTypeCode.UnsignedShort
            or XmlTypeCode.UnsignedByte
            or XmlTypeCode.PositiveInteger
            or XmlTypeCode.NegativeInteger
            or XmlTypeCode.NonNegativeInteger
            or XmlTypeCode.NonPositiveInteger => "0",
            XmlTypeCode.Decimal or XmlTypeCode.Double or XmlTypeCode.Float => "0",
            XmlTypeCode.DateTime => "2000-01-01T00:00:00Z",
            XmlTypeCode.Date => "2000-01-01",
            XmlTypeCode.Time => "00:00:00",
            XmlTypeCode.AnyUri => "https://example.com",
            XmlTypeCode.Base64Binary or XmlTypeCode.HexBinary => string.Empty,
            _ => "string",
        };
    }

    private static IReadOnlyList<XNode> ParseFragment(string text)
    {
        try
        {
            return [XElement.Parse(text)];
        }
        catch (XmlException)
        {
            try
            {
                return XElement.Parse("<root>" + text + "</root>").Nodes().ToList();
            }
            catch (XmlException exception)
            {
                throw new InvalidOperationException(
                    $"SOAP 消息体不是合法 XML：{exception.Message}",
                    exception
                );
            }
        }
    }

    private static bool IsEnvelope(string text)
    {
        try
        {
            return XDocument.Parse(text).Root?.Name.LocalName == "Envelope";
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static async Task<string> FetchTextAsync(
        string url,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await Client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or UriFormatException)
        {
            throw new InvalidOperationException($"无法读取 WSDL：{exception.Message}", exception);
        }
    }

    private static Uri? TryCreateBaseUri(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        return null;
    }

    private static string LocalName(string? qualifiedName)
    {
        var text = qualifiedName ?? string.Empty;
        var separator = text.IndexOf(':');
        return separator >= 0 ? text[(separator + 1)..] : text;
    }

    /// <summary>Resolves a WSDL QName attribute such as tns:GetUser against its element scope.</summary>
    private static XName? ResolveQName(XElement? context, string? qualifiedName)
    {
        var text = (qualifiedName ?? string.Empty).Trim();
        if (text.Length == 0 || context is null)
        {
            return null;
        }

        var separator = text.IndexOf(':');
        if (separator < 0)
        {
            return XName.Get(text);
        }

        var prefix = text[..separator];
        var local = text[(separator + 1)..];
        var ns = context.GetNamespaceOfPrefix(prefix);
        return ns is null ? XName.Get(local) : XName.Get(local, ns.NamespaceName);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PostSharp/0.1");
        return client;
    }

    private sealed class HttpXmlResolver : XmlResolver
    {
        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            if (absoluteUri.Scheme is "http" or "https")
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, absoluteUri);
                request.Headers.TryAddWithoutValidation("User-Agent", "PostSharp/0.1");
                using var response = Client.Send(request);
                response.EnsureSuccessStatusCode();
                var memory = new MemoryStream();
                response.Content.ReadAsStream().CopyTo(memory);
                memory.Position = 0;
                return memory;
            }

            if (absoluteUri.IsFile)
            {
                return File.OpenRead(absoluteUri.LocalPath);
            }

            throw new NotSupportedException($"不支持的 XSD 地址协议：{absoluteUri.Scheme}");
        }
    }
}
