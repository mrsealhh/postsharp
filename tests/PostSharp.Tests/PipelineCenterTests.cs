using PostSharp.ViewModels;

namespace PostSharp.Tests;

public sealed class PipelineCenterTests
{
    [Fact]
    public void SavesAndReloadsPipelineFiles()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"postsharp-pipeline-center-{Guid.NewGuid():N}"
        );
        try
        {
            var center = new PipelineCenterViewModel(directory);
            Assert.Equal(4, center.Templates.Count);

            center.NameText = "demo-flow";
            center.Content = center.Templates[0].Content;
            center.SavePipelineCommand.Execute(null);

            var saved = Assert.Single(center.Files);
            Assert.Equal("demo-flow", saved.Name);
            Assert.True(File.Exists(saved.Path));

            var reloaded = new PipelineCenterViewModel(directory);
            Assert.Equal("demo-flow", Assert.Single(reloaded.Files).Name);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
