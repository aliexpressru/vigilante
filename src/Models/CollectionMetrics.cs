using System.Text.Json;
using Vigilante.Constants;

namespace Vigilante.Models;

public sealed class CollectionMetrics : Dictionary<string, object>
{
    public CollectionMetrics()
        : base(StringComparer.Ordinal)
    {
    }

    public CollectionMetrics(IDictionary<string, object> source)
        : base(source, StringComparer.Ordinal)
    {
    }

    public string? PrettySize
    {
        get => GetValue<string>(MetricConstants.PrettySizeKey);
        set
        {
            if (value is null) Remove(MetricConstants.PrettySizeKey);
            else this[MetricConstants.PrettySizeKey] = value;
        }
    }

    public long? SizeBytes
    {
        get => GetNullableInt64(MetricConstants.SizeBytesKey);
        set
        {
            if (value is null) Remove(MetricConstants.SizeBytesKey);
            else this[MetricConstants.SizeBytesKey] = value.Value;
        }
    }

    public string? PrettyRamSize
    {
        get => GetValue<string>(MetricConstants.PrettyRamSizeKey);
        set
        {
            if (value is null) Remove(MetricConstants.PrettyRamSizeKey);
            else this[MetricConstants.PrettyRamSizeKey] = value;
        }
    }

    public long? RamBytes
    {
        get => GetNullableInt64(MetricConstants.RamBytesKey);
        set
        {
            if (value is null) Remove(MetricConstants.RamBytesKey);
            else this[MetricConstants.RamBytesKey] = value.Value;
        }
    }

    public CollectionMemoryReportInfo? MemoryReport
    {
        get => GetValue<CollectionMemoryReportInfo>(MetricConstants.MemoryReportKey);
        set
        {
            if (value is null) Remove(MetricConstants.MemoryReportKey);
            else this[MetricConstants.MemoryReportKey] = value;
        }
    }

    public List<ShardDetails>? Shards
    {
        get => GetValue<List<ShardDetails>>(MetricConstants.ShardsKey);
        set
        {
            if (value is null) Remove(MetricConstants.ShardsKey);
            else this[MetricConstants.ShardsKey] = value;
        }
    }

    public List<OutgoingTransferInfo>? OutgoingTransfers
    {
        get => GetValue<List<OutgoingTransferInfo>>(MetricConstants.OutgoingTransfersKey);
        set
        {
            if (value is null) Remove(MetricConstants.OutgoingTransfersKey);
            else this[MetricConstants.OutgoingTransfersKey] = value;
        }
    }

    public Dictionary<string, string>? ShardStates
    {
        get => GetValue<Dictionary<string, string>>(MetricConstants.ShardStatesKey);
        set
        {
            if (value is null) Remove(MetricConstants.ShardStatesKey);
            else this[MetricConstants.ShardStatesKey] = value;
        }
    }

    private long? GetNullableInt64(string key)
    {
        if (!TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            long l => l,
            int i => i,
            JsonElement json when json.ValueKind == JsonValueKind.Number => json.GetInt64(),
            _ => Convert.ToInt64(value)
        };
    }

    private T? GetValue<T>(string key)
    {
        if (!TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T typed)
            return typed;

        if (value is JsonElement json)
        {
            try
            {
                return json.Deserialize<T>();
            }
            catch
            {
                return default;
            }
        }

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }
}
