using System.Text;

namespace Lexfield.Contracts;

/// <summary>
/// Decodes the required tenantId Kafka header at the shared wire boundary.
/// </summary>
/// <remarks>
/// The method accepts raw bytes so this dependency-free project does not depend
/// on a Kafka client. Consumers use their client only to obtain the bytes.
/// </remarks>
public static class TenantHeader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Returns a valid, nonblank tenant identifier without changing its text.
    /// </summary>
    /// <param name="value">The raw UTF-8 header bytes, or <see langword="null"/> when absent.</param>
    /// <exception cref="InvalidDataException">
    /// Thrown when the header is absent, empty, not valid UTF-8, or blank after
    /// decoding.
    /// </exception>
    public static string Decode(byte[]? value)
    {
        if (value is not { Length: > 0 })
        {
            throw new InvalidDataException(
                $"The Kafka message is missing required header '{Headers.TenantId}'.");
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"The Kafka message header '{Headers.TenantId}' is not valid UTF-8.",
                exception);
        }

        if (string.IsNullOrWhiteSpace(decoded))
        {
            throw new InvalidDataException(
                $"The Kafka message header '{Headers.TenantId}' must not be blank.");
        }

        return decoded;
    }
}
