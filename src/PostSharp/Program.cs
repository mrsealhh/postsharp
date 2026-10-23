using AtomUI;
using AtomUI.Desktop.Controls;
using Avalonia;
using System.Text;
using TKCode.Arc;
using TKCode.Arc.Fonts;

namespace PostSharp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Chinese APIs still answer in GBK/GB18030 often enough that the code page encodings must be available.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .WithArcFonts()
                  .UseAtomUI(builder =>
                  {
                      builder.UseDesktopControls();
                      builder.UseArc();
                  });
}
