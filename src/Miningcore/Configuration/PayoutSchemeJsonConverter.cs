using System;
using Newtonsoft.Json;

namespace Miningcore.Configuration;

public class PayoutSchemeJsonConverter : JsonConverter<PayoutScheme>
{
  public override void WriteJson(JsonWriter writer, PayoutScheme value, JsonSerializer serializer)
  {
    if (writer == null)
      throw new ArgumentNullException(nameof(writer));

    // Canonical string for this scheme: DFPPS+
    if (value == PayoutScheme.DFPPSPlus)
    {
      writer.WriteValue("DFPPS+");
      return;
    }

    writer.WriteValue(value.ToString());
  }

  public override PayoutScheme ReadJson(JsonReader reader, Type objectType, PayoutScheme existingValue, bool hasExistingValue,
      JsonSerializer serializer)
  {
    if (reader == null)
      throw new ArgumentNullException(nameof(reader));

    if (reader.TokenType != JsonToken.String)
      throw new JsonSerializationException($"Unexpected token {reader.TokenType} when parsing {nameof(PayoutScheme)}");

    var text = ((string)reader.Value)?.Trim();

    if (string.IsNullOrEmpty(text))
      throw new JsonSerializationException($"Empty value when parsing {nameof(PayoutScheme)}");

    // DFPPS+ aliases
    if (text.Equals("DFPPS+", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("DFPPSPlus", StringComparison.OrdinalIgnoreCase))
      return PayoutScheme.DFPPSPlus;

    if (Enum.TryParse<PayoutScheme>(text, true, out var parsed))
      return parsed;

    throw new JsonSerializationException($"Unknown {nameof(PayoutScheme)} value '{text}'");
  }
}
