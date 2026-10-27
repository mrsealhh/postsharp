using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class CodeSampleServiceTests
{
    [Fact]
    public void GeneratesMultipartSamplesForEveryLanguageWithoutExistingFiles()
    {
        var request = new RequestItem
        {
            Name = "上传",
            Method = "POST",
            Url = "https://api.example.com/upload",
            BodyMode = "Multipart",
        };
        request.FormFields.AddRow("note", "hello");
        request.UploadFiles.Add(
            new UploadFileItem
            {
                FieldName = "file",
                FilePath = Path.Combine(Path.GetTempPath(), "postsharp-not-exists.txt"),
            }
        );

        foreach (var language in CodeSampleService.Languages)
        {
            var code = CodeSampleService.Generate(language.Id, request, null);
            Assert.False(
                code.StartsWith("# 无法生成", StringComparison.Ordinal),
                $"{language.Id} 不应因文件不存在而失败：{code}"
            );
        }

        var csharp = CodeSampleService.Generate("csharp", request, null);
        Assert.Contains("MultipartFormDataContent", csharp, StringComparison.Ordinal);
        var javaScript = CodeSampleService.Generate("javascript", request, null);
        Assert.Contains("new FormData()", javaScript, StringComparison.Ordinal);
        var python = CodeSampleService.Generate("python", request, null);
        Assert.Contains("files = {", python, StringComparison.Ordinal);
        var java = CodeSampleService.Generate("java", request, null);
        Assert.Contains("multipart/form-data; boundary=", java, StringComparison.Ordinal);
        var go = CodeSampleService.Generate("go", request, null);
        Assert.Contains("multipart.NewWriter", go, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratesJsonSamplesWithContentTypeAndBody()
    {
        var request = new RequestItem
        {
            Name = "创建",
            Method = "POST",
            Url = "https://api.example.com/items",
            BodyMode = "Json",
            Body = """{"name":"Tom"}""",
        };

        foreach (var language in CodeSampleService.Languages)
        {
            var code = CodeSampleService.Generate(language.Id, request, null);
            Assert.False(string.IsNullOrWhiteSpace(code));
            Assert.DoesNotContain("上传文件不存在", code, StringComparison.Ordinal);
        }

        Assert.Contains("StringContent", CodeSampleService.Generate("csharp", request, null));
        Assert.Contains(
            "await fetch(",
            CodeSampleService.Generate("javascript", request, null),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "requests.post(",
            CodeSampleService.Generate("python", request, null),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void GetSampleDoesNotImportUnusedBodyPackages()
    {
        var request = new RequestItem
        {
            Name = "查询",
            Method = "GET",
            Url = "https://api.example.com/items",
        };

        Assert.DoesNotContain(
            "\"strings\"",
            CodeSampleService.Generate("go", request, null),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "requests.get(",
            CodeSampleService.Generate("python", request, null),
            StringComparison.Ordinal
        );
    }
}
