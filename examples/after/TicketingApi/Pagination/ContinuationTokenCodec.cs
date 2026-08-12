using System.Text;

namespace TicketingApi.Pagination;

public static class ContinuationTokenCodec
{
    public static string? Encode(string? token) => string.IsNullOrEmpty(token)
        ? null
        : Convert.ToBase64String(Encoding.UTF8.GetBytes(token))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static string? Decode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException exception)
        {
            throw new InvalidContinuationTokenException("The continuation token is invalid.", exception);
        }
    }
}
