using System.Text.Json;
using System.Text.Json.Serialization;
using Vigilante.Models;

namespace Vigilante.Models.Requests;

public class LogLevelFilterJsonConverter : JsonConverter<LogLevelFilter?>
{
    public override LogLevelFilter? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number when reader.TryGetInt32(out var mask) => (LogLevelFilter)mask,
            JsonTokenType.String => ParseString(reader.GetString()),
            JsonTokenType.StartArray => ParseArray(ref reader),
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, LogLevelFilter? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteNumberValue((int)value.Value);
    }

    private static LogLevelFilter? ParseArray(ref Utf8JsonReader reader)
    {
        var mask = LogLevelFilter.None;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return mask;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var parsed = ParseSingleLevel(reader.GetString());
                if (parsed is not null)
                {
                    mask |= parsed.Value;
                }
            }
        }

        return mask;
    }

    private static LogLevelFilter? ParseString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var numeric))
        {
            return (LogLevelFilter)numeric;
        }

        var chunks = value.Split(['|', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mask = LogLevelFilter.None;
        foreach (var chunk in chunks)
        {
            var parsed = ParseSingleLevel(chunk);
            if (parsed is not null)
            {
                mask |= parsed.Value;
            }
        }

        return mask;
    }

    private static LogLevelFilter? ParseSingleLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "TRACE" or "TRC" => LogLevelFilter.Trace,
            "DEBUG" or "DBG" => LogLevelFilter.Debug,
            "INFO" or "INF" => LogLevelFilter.Info,
            "WARN" or "WRN" => LogLevelFilter.Warn,
            "ERROR" or "ERR" => LogLevelFilter.Error,
            "FATAL" or "FTL" => LogLevelFilter.Fatal,
            _ => null
        };
    }
}
