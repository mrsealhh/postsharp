using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace PostSharp.Styles;

public partial class ToolTipStyles : Avalonia.Styling.Styles
{
    public ToolTipStyles()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
