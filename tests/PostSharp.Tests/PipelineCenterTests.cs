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
            center.YamlText = center.Templates[0].Content;
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

    [Fact]
    public void FiltersFilesAndTracksSelectionAndDirtyState()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"postsharp-pipeline-center-{Guid.NewGuid():N}"
        );
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "alpha.yaml"), "name: alpha\nsteps: []");
            File.WriteAllText(Path.Combine(directory, "beta.yaml"), "name: beta\nsteps: []");

            var center = new PipelineCenterViewModel(directory);
            Assert.Equal(2, center.VisibleFiles.Count);

            center.SearchText = "alp";
            Assert.Single(center.VisibleFiles);
            Assert.Equal("alpha", center.VisibleFiles[0].Name);

            center.OpenFileCommand.Execute(center.VisibleFiles[0]);
            Assert.True(center.VisibleFiles[0].IsSelected);

            center.YamlText = "name: alpha\nsteps: []\n# changed";
            Assert.True(center.HasUnsavedChanges);

            center.SavePipelineCommand.Execute(null);
            Assert.False(center.HasUnsavedChanges);

            center.SearchText = "missing";
            Assert.True(center.HasNoMatches);
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
