using Avalonia.Controls;
using Avalonia.Interactivity;
using PostSharp.Models;

namespace PostSharp.Views;

/// <summary>One environment variable line; the lock marks the value as a secret.</summary>
public partial class EnvironmentVariableRow : UserControl
{
    public EnvironmentVariableRow()
    {
        InitializeComponent();
    }

    private void OnToggleSecretClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is KeyValueRow row)
        {
            row.IsSecret = !row.IsSecret;
        }
    }
}
