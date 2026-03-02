using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlPlane.Api.Options;
using Microsoft.Extensions.Options;
using Shared.Contracts;

namespace ControlPlane.Api.Security;

public interface IJoinTokenService
{
    string Issue(JoinTokenPayload payload);
    bool TryValidate(string token, out JoinTokenPayload? payload, out string? reason);
}

public sealed class JoinTokenService : IJoinTokenService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] _secret;

    public JoinTokenService(IOptions<AcPolicyOptions> policyOptions)
    {
        var secret = policyOptions.Value.JoinTokenSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("AcPolicy:JoinTokenSecret must be configured.");
        }

        _secret = Encoding.UTF8.GetBytes(secret);
    }

    public string Issue(JoinTokenPayload payload)
    {
        var headerJson = JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" }, JsonOptions);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var header = Base64Url.EncodeString(headerJson);
        var body = Base64Url.EncodeString(payloadJson);
        var signingInput = $"{header}.{body}";
        var signature = Base64Url.Encode(Sign(signingInput));
        return $"{signingInput}.{signature}";
    }

    public bool TryValidate(string token, out JoinTokenPayload? payload, out string? reason)
    {
        payload = null;
        reason = null;

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            reason = "malformed_token";
            return false;
        }

        var signingInput = $"{parts[0]}.{parts[1]}";
        var actualSig = Base64Url.Decode(parts[2]);
        var expectedSig = Sign(signingInput);
        if (!CryptographicOperations.FixedTimeEquals(actualSig, expectedSig))
        {
            reason = "invalid_signature";
            return false;
        }

        try
        {
            var payloadJson = Base64Url.DecodeToString(parts[1]);
            payload = JsonSerializer.Deserialize<JoinTokenPayload>(payloadJson, JsonOptions);
            if (payload is null)
            {
                reason = "invalid_payload";
                return false;
            }

            if (payload.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                reason = "expired";
                return false;
            }

            return true;
        }
        catch
        {
            reason = "invalid_payload";
            return false;
        }
    }

    private byte[] Sign(string signingInput)
    {
        using var hmac = new HMACSHA256(_secret);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
    }
}
