namespace PostSharp.Models;

/// <summary>Minimum log levels exposed by the settings window; values match Serilog's level names.</summary>
public static class LogLevels
{
    public const string Debug = "Debug";
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";

    public const string Default = Debug;
}
