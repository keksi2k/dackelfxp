using System.Text.Json;
using System.Text.Json.Serialization;

namespace DackelFxp.Core.Infrastructure;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? Load<T>(string file) where T : class
    {
        if (!File.Exists(file)) return null;
        using var stream = File.OpenRead(file);
        return JsonSerializer.Deserialize<T>(stream, Options);
    }

    /// <summary>Writes to a temp file first, so a crash never leaves a half-written file.</summary>
    public static void Save<T>(string file, T value)
    {
        var tmp = file + ".tmp";
        using (var stream = File.Create(tmp))
            JsonSerializer.Serialize(stream, value, Options);
        File.Move(tmp, file, overwrite: true);
    }
}
