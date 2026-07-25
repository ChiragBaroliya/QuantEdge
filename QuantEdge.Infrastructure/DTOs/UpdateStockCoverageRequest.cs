using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Request DTO for updating stock active status and history stored flags via sp_update_stock_coverage_flags.
/// Values:
///   null = History missing / disabled
///   0    = History enabled via Web UI (pending worker job backfill)
///   1    = History stored by worker job
/// </summary>
public class UpdateStockCoverageRequest
{
    public int Id { get; set; }
    public bool IsActive { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? IsHistryStored1m { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? IsHistryStored5m { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? IsHistryStored15m { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? IsHistryStored60m { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? IsHistryStored1d { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? History1M { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? History5M { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? History15M { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? History60M { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? History1D { get; set; }

    public int? Get1mValue() => IsHistryStored1m ?? History1M;
    public int? Get5mValue() => IsHistryStored5m ?? History5M;
    public int? Get15mValue() => IsHistryStored15m ?? History15M;
    public int? Get60mValue() => IsHistryStored60m ?? History60M;
    public int? Get1dValue() => IsHistryStored1d ?? History1D;
}

public class FlexibleNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if (reader.TokenType == JsonTokenType.True)
        {
            return 0; // Enabling from Web UI sets 0 for pending worker backfill
        }
        if (reader.TokenType == JsonTokenType.False)
        {
            return null; // Disabling sets null
        }
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out int val))
            {
                return val;
            }
        }
        if (reader.TokenType == JsonTokenType.String)
        {
            string? str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str) || str.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (int.TryParse(str, out int intVal))
            {
                return intVal;
            }
            if (bool.TryParse(str, out bool boolVal))
            {
                return boolVal ? 0 : null;
            }
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
