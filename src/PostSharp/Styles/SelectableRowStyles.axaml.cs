using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace PostSharp.Styles;

public partial class SelectableRowStyles : Avalonia.Styling.Styles
{
    public SelectableRowStyles()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
