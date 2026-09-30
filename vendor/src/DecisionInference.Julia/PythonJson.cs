using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace DecisionInference.Julia;

// Matches Python json.loads -> json.dumps(ensure_ascii=False): insertion order and spaced separators.
internal static class PythonJson
{
    internal static string RenderState(JsonElement state)
    {
        if (state.ValueKind == JsonValueKind.String) return state.GetString()!;
        if (state.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new NotSupportedException("Julia state must be text, object or array.");
        var output = new StringBuilder();
        Write(state, output);
        return output.ToString();
    }

    private static void Write(JsonElement value, StringBuilder output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw new ArgumentException("Duplicate JSON keys cannot be preserved by Julia's Python recipe.");
                    if (keys.Count > 1) output.Append(", ");
                    WriteString(property.Name, output);
                    output.Append(": ");
                    Write(property.Value, output);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                int count = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (count++ > 0) output.Append(", ");
                    Write(item, output);
                }
                output.Append(']');
                break;
            case JsonValueKind.String: WriteString(value.GetString()!, output); break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            case JsonValueKind.Number: output.Append(Number(value)); break;
            default: throw new ArgumentException("Undefined JSON value.");
        }
    }

    private static void WriteString(string text, StringBuilder output)
    {
        output.Append('"');
        foreach (char c in text)
            output.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture), _ => c.ToString()
            });
        output.Append('"');
    }

    private static string Number(JsonElement value)
    {
        string raw = value.GetRawText();
        if (!raw.Contains('.') && !raw.Contains('e', StringComparison.OrdinalIgnoreCase))
            return BigInteger.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        double number = value.GetDouble();
        if (!double.IsFinite(number)) throw new ArgumentException("Julia JSON numbers must be finite.");
        string sign = double.IsNegative(number) ? "-" : "";
        if (number == 0) return sign + "0.0";
        string[] parts = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture).Split('E');
        int exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        int point = parts[0].IndexOf('.');
        if (point < 0) point = parts[0].Length;
        string digits = parts[0].Replace(".", "");
        int leading = digits.Length - digits.TrimStart('0').Length;
        exponent += point - leading - 1;
        digits = digits.TrimStart('0').TrimEnd('0');
        if (exponent is < -4 or >= 16)
            return sign + digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" +
                (exponent >= 0 ? "+" : "-") + Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
        int decimalPosition = exponent + 1;
        if (decimalPosition <= 0) return sign + "0." + new string('0', -decimalPosition) + digits;
        if (decimalPosition >= digits.Length) return sign + digits + new string('0', decimalPosition - digits.Length) + ".0";
        return sign + digits.Insert(decimalPosition, ".");
    }
}
