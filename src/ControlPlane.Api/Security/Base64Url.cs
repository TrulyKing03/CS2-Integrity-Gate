using System.Text;

namespace ControlPlane.Api.Security;

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] Decode(string value)
    {
        var normalized = value
            .Replace('-', '+')
            .Replace('_', '/');

        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
        }

        return Convert.FromBase64String(normalized);
    }

    public static string EncodeString(string value) => Encode(Encoding.UTF8.GetBytes(value));
    public static string DecodeToString(string value) => Encoding.UTF8.GetString(Decode(value));
}
