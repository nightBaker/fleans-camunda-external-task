using System.Collections;
using System.Dynamic;
using System.Globalization;
using System.Text.Json;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// Converts between Camunda typed values (<c>{value, type, valueInfo}</c>) and Fleans workflow
/// variables (<see cref="ExpandoObject"/> trees of string / long / double / bool / null /
/// <see cref="ExpandoObject"/> / <see cref="List{T}"/>).
/// </summary>
public static class CamundaVariables
{
    private const string JsonDataFormat = "application/json";

    /// <summary>
    /// Key of the marker object a <c>Bytes</c> or <c>File</c> value is stored as:
    /// <c>{ camundaType: "Bytes" | "File", value: &lt;base64&gt;, filename, mimeType, encoding }</c>.
    /// It is returned to workers with its original Camunda type.
    /// </summary>
    public const string BinaryTypeKey = "camundaType";

    /// <summary>Thrown for a typed value Fleans cannot store (e.g. a Java-serialized Object).</summary>
    public sealed class UnsupportedVariableException(string message) : Exception(message);

    public static Dictionary<string, object?> ToFleans(Dictionary<string, VariableValueDto>? variables)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (variables is null) return result;
        foreach (var (name, typed) in variables)
            result[name] = ToFleans(name, typed);
        return result;
    }

    public static object? ToFleans(string name, VariableValueDto? typed)
    {
        if (typed is null) return null;
        var value = typed.Value is JsonElement element ? element : JsonSerializer.SerializeToElement(typed.Value);
        var type = typed.Type?.ToLowerInvariant();

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || type == "null")
            return null;

        switch (type)
        {
            case null:
                return FromJson(value); // untyped: infer from the JSON token, like Camunda
            case "string":
            case "date":
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            case "boolean":
                return value.ValueKind == JsonValueKind.String ? bool.Parse(value.GetString()!) : value.GetBoolean();
            case "integer":
            case "short":
            case "long":
                return value.ValueKind == JsonValueKind.String
                    ? long.Parse(value.GetString()!, CultureInfo.InvariantCulture)
                    : value.GetInt64();
            case "double":
                return value.ValueKind == JsonValueKind.String
                    ? double.Parse(value.GetString()!, CultureInfo.InvariantCulture)
                    : value.GetDouble();
            case "json":
                return ParseSerializedJson(value);
            case "object" when IsJsonDataFormat(typed.ValueInfo):
                return ParseSerializedJson(value);
            case "bytes":
                return Binary("Bytes", value, null);
            case "file":
                return Binary("File", value, typed.ValueInfo);
            default:
                throw new UnsupportedVariableException(
                    $"Variable '{name}' has type '{typed.Type}', which Fleans cannot store. " +
                    "Supported: String, Boolean, Integer, Short, Long, Double, Date, Null, Json, Bytes, File, " +
                    "and Object with serializationDataFormat 'application/json'.");
        }
    }

    public static Dictionary<string, VariableValueDto> ToCamundaVariables(IEnumerable<KeyValuePair<string, object?>> variables)
    {
        var result = new Dictionary<string, VariableValueDto>(StringComparer.Ordinal);
        foreach (var (name, value) in variables)
            result[name] = ToCamunda(value);
        return result;
    }

    public static VariableValueDto ToCamunda(object? value) => value switch
    {
        null => Typed(null, "Null"),
        string s => Typed(s, "String"),
        bool b => Typed(b, "Boolean"),
        int or short or byte or sbyte or ushort => Typed(Convert.ToInt32(value, CultureInfo.InvariantCulture), "Integer"),
        long l when l is >= int.MinValue and <= int.MaxValue => Typed((int)l, "Integer"),
        long or uint or ulong => Typed(Convert.ToInt64(value, CultureInfo.InvariantCulture), "Long"),
        double or float or decimal => Typed(Convert.ToDouble(value, CultureInfo.InvariantCulture), "Double"),
        DateTime dt => Typed(FormatDate(dt), "Date"),
        DateTimeOffset dto => Typed(FormatDate(dto.UtcDateTime), "Date"),
        Guid g => Typed(g.ToString("D"), "String"),
        IDictionary<string, object?> map when TryGetBinary(map, out var binary) => binary.Type == "Bytes"
            ? Typed(binary.Base64, "Bytes")
            // As in Camunda, a file's content is not inlined: clients download it from
            // /execution/{id}/localVariables/{name}/data.
            : new VariableValueDto(null, "File", new Dictionary<string, object?>
            {
                ["filename"] = binary.Filename,
                ["mimeType"] = binary.MimeType,
                ["encoding"] = binary.Encoding,
            }),
        _ => Typed(JsonSerializer.Serialize(ToSerializable(value)), "Json"),
    };

    /// <summary>
    /// Camunda's REST date format, <c>yyyy-MM-dd'T'HH:mm:ss.SSSZ</c>, rendered in UTC. A Local value
    /// is converted; an Unspecified one (EF Core reads stored UTC values back that way) is taken as UTC.
    /// </summary>
    public static string FormatDate(DateTime value) =>
        (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime())
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + "+0000";

    /// <summary>A stored <c>Bytes</c> or <c>File</c> value.</summary>
    public sealed record BinaryValue(string Type, string Base64, string? Filename, string? MimeType, string? Encoding);

    /// <summary>Recognises the marker object written by <see cref="ToFleans(string, VariableValueDto?)"/>.</summary>
    public static bool TryGetBinary(object? value, out BinaryValue binary)
    {
        binary = null!;
        if (value is not IDictionary<string, object?> map
            || !map.TryGetValue(BinaryTypeKey, out var type) || type is not ("Bytes" or "File")
            || !map.TryGetValue("value", out var content) || content is not string base64)
            return false;
        binary = new BinaryValue((string)type, base64,
            map.TryGetValue("filename", out var f) ? f as string : null,
            map.TryGetValue("mimeType", out var m) ? m as string : null,
            map.TryGetValue("encoding", out var e) ? e as string : null);
        return true;
    }

    private static ExpandoObject Binary(string type, JsonElement value, Dictionary<string, object?>? valueInfo)
    {
        var base64 = value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"A {type} value must be a base64 string.");
        Convert.FromBase64String(base64); // validates; FormatException → 400
        var marker = new ExpandoObject();
        var map = (IDictionary<string, object?>)marker;
        map[BinaryTypeKey] = type;
        map["value"] = base64;
        if (type == "File")
        {
            map["filename"] = Info(valueInfo, "filename");
            map["mimeType"] = Info(valueInfo, "mimetype");
            map["encoding"] = Info(valueInfo, "encoding");
        }
        return marker;
    }

    /// <summary>
    /// Case-insensitive: Camunda's Java API names the key <c>mimeType</c>, while some clients send
    /// <c>mimetype</c>.
    /// </summary>
    private static string? Info(Dictionary<string, object?>? valueInfo, string key)
    {
        if (valueInfo is null) return null;
        foreach (var (name, v) in valueInfo)
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
                return v is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString() : v?.ToString();
        return null;
    }

    private static VariableValueDto Typed(object? value, string type) =>
        new(value, type, new Dictionary<string, object?>());

    private static bool IsJsonDataFormat(Dictionary<string, object?>? valueInfo)
    {
        if (valueInfo is null || !valueInfo.TryGetValue("serializationDataFormat", out var format))
            return false;
        var text = format is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString() : format?.ToString();
        return string.Equals(text, JsonDataFormat, StringComparison.OrdinalIgnoreCase);
    }

    private static object? ParseSerializedJson(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return FromJson(value); // already structured JSON
        using var doc = JsonDocument.Parse(value.GetString()!);
        return FromJson(doc.RootElement);
    }

    /// <summary>JSON → Fleans variable tree (objects become <see cref="ExpandoObject"/>).</summary>
    public static object? FromJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new ExpandoObject();
                var dict = (IDictionary<string, object?>)obj;
                foreach (var prop in element.EnumerateObject())
                    dict[prop.Name] = FromJson(prop.Value);
                return obj;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(FromJson).ToList();
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? (object)l : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary>Normalises engine-side objects (<see cref="ExpandoObject"/>, lists) for System.Text.Json.</summary>
    private static object? ToSerializable(object? value) => value switch
    {
        null or string => value,
        IDictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => ToSerializable(kv.Value)),
        IEnumerable list => list.Cast<object?>().Select(ToSerializable).ToList(),
        _ => value,
    };
}
