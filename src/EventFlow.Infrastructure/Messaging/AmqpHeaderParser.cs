using System.Text;

namespace EventFlow.Infrastructure.Messaging;

/// <summary>
/// Resilient extraction helper for AMQP BasicProperties headers, handling byte arrays, strings, and memory buffers.
/// </summary>
public static class AmqpHeaderParser
{
    /// <summary>
    /// Safely extracts a string header value from the RabbitMQ properties headers dictionary.
    /// </summary>
    public static string? GetString(IDictionary<string, object?>? headers, string key)
    {
        if (headers == null || !headers.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return ConvertToString(value);
    }

    /// <summary>
    /// Converts all AMQP headers to a sanitized dictionary of string key-value pairs.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ExtractAll(IDictionary<string, object?>? headers)
    {
        if (headers == null || headers.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var result = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            if (value != null)
            {
                var strVal = ConvertToString(value);
                if (strVal != null)
                {
                    result[key] = strVal;
                }
            }
        }

        return result;
    }

    private static string? ConvertToString(object value) =>
        value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> rom => Encoding.UTF8.GetString(rom.Span),
            string str => str,
            _ => value.ToString()
        };
}
