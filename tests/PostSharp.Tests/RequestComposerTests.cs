using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class RequestComposerTests
{
    [Fact]
    public void MultipartComposeIncludesAllUploadFiles()
    {
        var first = CreateTempFile("first.txt", "one");
        var second = CreateTempFile("second.txt", "two");
        try
        {
            var request = new RequestItem
            {
                Name = "upload",
                Method = "POST",
                Url = "https://example.com/upload",
                BodyMode = "Multipart",
            };
            request.UploadFiles.Add(
                new UploadFileItem
                {
                    FieldName = "avatar",
                    FilePath = first,
                    SizeBytes = 3,
                }
            );
            request.UploadFiles.Add(
                new UploadFileItem
                {
                    FieldName = "document",
                    FilePath = second,
                    SizeBytes = 3,
                }
            );

            var composed = RequestComposer.Compose(request, null);
            var curl = RequestComposer.BuildCurl(composed);

            Assert.True(composed.IsMultipart);
            Assert.Equal(2, composed.UploadFiles?.Count);
            Assert.Contains("avatar=@", curl);
            Assert.Contains(first, curl);
            Assert.Contains("document=@", curl);
            Assert.Contains(second, curl);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void LegacyUploadPathCreatesOneFileItem()
    {
        var path = CreateTempFile("legacy.txt", "data");
        try
        {
            var request = new RequestItem { UploadFilePath = path, UploadFieldName = "file" };

            Assert.Single(request.UploadFiles);
            Assert.Equal(path, request.UploadFiles[0].FilePath);
            Assert.Equal("file", request.UploadFiles[0].FieldName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CloneCopiesUploadFiles()
    {
        var first = CreateTempFile("clone-first.txt", "one");
        var second = CreateTempFile("clone-second.txt", "two");
        try
        {
            var request = new RequestItem { BodyMode = "Multipart" };
            request.UploadFiles.Add(new UploadFileItem { FieldName = "one", FilePath = first });
            request.UploadFiles.Add(new UploadFileItem { FieldName = "two", FilePath = second });

            var clone = request.Clone();

            Assert.Equal(2, clone.UploadFiles.Count);
            Assert.Equal("one", clone.UploadFiles[0].FieldName);
            Assert.Equal(second, clone.UploadFiles[1].FilePath);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void MaskAuthorizationHidesQueryTokenAndApiKeyHeader()
    {
        var composed = new ComposedRequest(
            "GET",
            "https://example.com/api?access_token=secret&page=1",
            new[]
            {
                new KeyValuePair<string, string>("X-Api-Key", "secret"),
                new KeyValuePair<string, string>("Authorization", "Bearer secret"),
                new KeyValuePair<string, string>("Accept", "application/json"),
            },
            null,
            null,
            null
        );

        var masked = RequestComposer.MaskAuthorization(composed);

        Assert.DoesNotContain("secret", masked.Url);
        Assert.Contains("access_token=****", masked.Url);
        Assert.Contains(masked.Headers, pair => pair.Key == "X-Api-Key" && pair.Value == "****");
        Assert.Contains(
            masked.Headers,
            pair => pair.Key == "Authorization" && pair.Value == "Bearer ****"
        );
        Assert.Contains(
            masked.Headers,
            pair => pair.Key == "Accept" && pair.Value == "application/json"
        );
    }

    private static string CreateTempFile(string fileName, string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"postsharp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
