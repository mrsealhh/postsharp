using System.Text.Json;
using System.Text.Json.Serialization;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Writes and reads <see cref="AppSettings"/> without reflection. The MVVM generator produces the
/// properties after the System.Text.Json source generator runs, so they are invisible to it; this
/// converter is the explicit, AOT-safe mapping and keeps unknown keys ignored on read.
/// </summary>
public sealed class AppSettingsJsonConverter : JsonConverter<AppSettings>
{
    public override AppSettings Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("设置文档必须是 JSON 对象。");
        }

        var settings = new AppSettings();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case nameof(AppSettings.Theme):
                    settings.Theme = reader.GetString() ?? settings.Theme;
                    break;
                case nameof(AppSettings.UiScale):
                    settings.UiScale = reader.GetDouble();
                    break;
                case nameof(AppSettings.UiLayout):
                    settings.UiLayout = reader.GetString() ?? settings.UiLayout;
                    break;
                case nameof(AppSettings.TabOverflow):
                    settings.TabOverflow = reader.GetString() ?? settings.TabOverflow;
                    break;
                case nameof(AppSettings.RenderingMode):
                    settings.RenderingMode = reader.GetString() ?? settings.RenderingMode;
                    break;
                case nameof(AppSettings.CornerScale):
                    settings.CornerScale = reader.GetDouble();
                    break;
                case nameof(AppSettings.UiFont):
                    settings.UiFont = reader.GetString() ?? settings.UiFont;
                    break;
                case nameof(AppSettings.CodeFont):
                    settings.CodeFont = reader.GetString() ?? settings.CodeFont;
                    break;
                case nameof(AppSettings.CodeFontSize):
                    settings.CodeFontSize = reader.GetString() ?? settings.CodeFontSize;
                    break;
                case nameof(AppSettings.RequestTimeoutSeconds):
                    settings.RequestTimeoutSeconds = reader.GetInt32();
                    break;
                case nameof(AppSettings.DefaultBodyMode):
                    settings.DefaultBodyMode = reader.GetString() ?? settings.DefaultBodyMode;
                    break;
                case nameof(AppSettings.SseThinkingPath):
                    settings.SseThinkingPath = reader.GetString() ?? settings.SseThinkingPath;
                    break;
                case nameof(AppSettings.SseContentPath):
                    settings.SseContentPath = reader.GetString() ?? settings.SseContentPath;
                    break;
                case nameof(AppSettings.AutoFormatResponse):
                    settings.AutoFormatResponse = reader.GetBoolean();
                    break;
                case nameof(AppSettings.LargeResponseThresholdKb):
                    settings.LargeResponseThresholdKb = reader.GetInt32();
                    break;
                case nameof(AppSettings.AuditLimit):
                    settings.AuditLimit = reader.GetInt32();
                    break;
                case nameof(AppSettings.LogDirectory):
                    settings.LogDirectory = reader.GetString() ?? settings.LogDirectory;
                    break;
                case nameof(AppSettings.LogLevel):
                    settings.LogLevel = reader.GetString() ?? settings.LogLevel;
                    break;
                case nameof(AppSettings.FontDirectories):
                    settings.FontDirectories =
                        JsonSerializer.Deserialize(ref reader, DataJsonContext.Default.ListString)
                        ?? new List<string>();
                    break;
                case nameof(AppSettings.TreePaneWidth):
                    settings.TreePaneWidth = reader.GetDouble();
                    break;
                case nameof(AppSettings.RequestPaneRatio):
                    settings.RequestPaneRatio = reader.GetDouble();
                    break;
                case nameof(AppSettings.WorkspaceLayout):
                    settings.WorkspaceLayout = reader.GetString();
                    break;
                case nameof(AppSettings.HistoryCollapsed):
                    settings.HistoryCollapsed = reader.GetBoolean();
                    break;
                case nameof(AppSettings.AutoCheckUpdates):
                    settings.AutoCheckUpdates = reader.GetBoolean();
                    break;
                case nameof(AppSettings.McpServiceMode):
                    settings.McpServiceMode = reader.GetString() ?? settings.McpServiceMode;
                    break;
                case nameof(AppSettings.McpRequireToken):
                    settings.McpRequireToken = reader.GetBoolean();
                    break;
                case nameof(AppSettings.McpReadOnly):
                    settings.McpReadOnly = reader.GetBoolean();
                    break;
                case nameof(AppSettings.McpTokenCipher):
                    settings.McpTokenCipher = reader.GetString() ?? settings.McpTokenCipher;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return settings;
    }

    public override void Write(
        Utf8JsonWriter writer,
        AppSettings value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(AppSettings.Theme), value.Theme);
        writer.WriteNumber(nameof(AppSettings.UiScale), value.UiScale);
        writer.WriteString(nameof(AppSettings.UiLayout), value.UiLayout);
        writer.WriteString(nameof(AppSettings.TabOverflow), value.TabOverflow);
        writer.WriteString(nameof(AppSettings.RenderingMode), value.RenderingMode);
        writer.WriteNumber(nameof(AppSettings.CornerScale), value.CornerScale);
        writer.WriteString(nameof(AppSettings.UiFont), value.UiFont);
        writer.WriteString(nameof(AppSettings.CodeFont), value.CodeFont);
        writer.WriteString(nameof(AppSettings.CodeFontSize), value.CodeFontSize);
        writer.WriteNumber(nameof(AppSettings.RequestTimeoutSeconds), value.RequestTimeoutSeconds);
        writer.WriteString(nameof(AppSettings.DefaultBodyMode), value.DefaultBodyMode);
        writer.WriteString(nameof(AppSettings.SseThinkingPath), value.SseThinkingPath);
        writer.WriteString(nameof(AppSettings.SseContentPath), value.SseContentPath);
        writer.WriteBoolean(nameof(AppSettings.AutoFormatResponse), value.AutoFormatResponse);
        writer.WriteNumber(
            nameof(AppSettings.LargeResponseThresholdKb),
            value.LargeResponseThresholdKb
        );
        writer.WriteNumber(nameof(AppSettings.AuditLimit), value.AuditLimit);
        writer.WriteString(nameof(AppSettings.LogDirectory), value.LogDirectory);
        writer.WriteString(nameof(AppSettings.LogLevel), value.LogLevel);
        writer.WritePropertyName(nameof(AppSettings.FontDirectories));
        JsonSerializer.Serialize(writer, value.FontDirectories, DataJsonContext.Default.ListString);
        writer.WriteNumber(nameof(AppSettings.TreePaneWidth), value.TreePaneWidth);
        writer.WriteNumber(nameof(AppSettings.RequestPaneRatio), value.RequestPaneRatio);
        writer.WriteString(nameof(AppSettings.WorkspaceLayout), value.WorkspaceLayout);
        writer.WriteBoolean(nameof(AppSettings.HistoryCollapsed), value.HistoryCollapsed);
        writer.WriteBoolean(nameof(AppSettings.AutoCheckUpdates), value.AutoCheckUpdates);
        writer.WriteString(nameof(AppSettings.McpServiceMode), value.McpServiceMode);
        writer.WriteBoolean(nameof(AppSettings.McpRequireToken), value.McpRequireToken);
        writer.WriteBoolean(nameof(AppSettings.McpReadOnly), value.McpReadOnly);
        writer.WriteString(nameof(AppSettings.McpTokenCipher), value.McpTokenCipher);
        writer.WriteEndObject();
    }
}
