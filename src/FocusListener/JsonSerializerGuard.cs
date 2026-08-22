using System.Text.Json;

namespace FocusListener;

internal static class JsonSerializer
{
    public static T? Deserialize<T>(string? json, JsonSerializerOptions options) =>
        string.IsNullOrWhiteSpace(json)
            ? default
            : System.Text.Json.JsonSerializer.Deserialize<T>(json, options);

    public static string Serialize<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    public static string Serialize(object? value, Type inputType) =>
        System.Text.Json.JsonSerializer.Serialize(value, inputType);
}
