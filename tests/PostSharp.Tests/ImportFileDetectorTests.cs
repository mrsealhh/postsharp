using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class ImportFileDetectorTests
{
    [Theory]
    [InlineData("openapi.json", "{\"openapi\":\"3.0.0\"}", ImportFileKind.OpenApi)]
    [InlineData(
        "postman.json",
        "{\"info\":{\"schema\":\"https://schema.getpostman.com/json/collection/v2.1.0/collection.json\"},\"item\":[]}",
        ImportFileKind.PostmanCollection
    )]
    [InlineData(
        "postman-env.json",
        "{\"name\":\"env\",\"values\":[]}",
        ImportFileKind.PostmanEnvironment
    )]
    [InlineData("capture.har", "{\"log\":{\"entries\":[]}}", ImportFileKind.Har)]
    [InlineData(
        "insomnia.json",
        "{\"_type\":\"export\",\"resources\":[]}",
        ImportFileKind.Insomnia
    )]
    [InlineData(
        "hoppscotch.json",
        "{\"name\":\"c\",\"folders\":[],\"requests\":[]}",
        ImportFileKind.Hoppscotch
    )]
    [InlineData("request.sh", "curl 'https://example.com'", ImportFileKind.Curl)]
    public void DetectsSupportedFormats(string path, string content, ImportFileKind expected)
    {
        Assert.Equal(expected, ImportFileDetector.Detect(path, content));
    }

    [Fact]
    public void ReturnsUnknownForUnrecognizedJson()
    {
        Assert.Equal(
            ImportFileKind.Unknown,
            ImportFileDetector.Detect("unknown.json", "{\"foo\":1}")
        );
    }
}
