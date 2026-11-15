using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RunnerOrchestrator.GitHub;

/// <summary>The App's own JWT (RS256), only used to ask for installation tokens.</summary>
internal static class GitHubAppJwt
{
    /// <summary>GitHub accepts at most 10 minutes; 9 leaves room for clock drift.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(9);

    /// <summary>Issued in the past to tolerate a host clock slightly ahead of GitHub's.</summary>
    public static readonly TimeSpan Backdate = TimeSpan.FromSeconds(60);

    public static string Create(long appId, RSA key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        var issuedAt = (now - Backdate).ToUnixTimeSeconds();
        var expires = (now + Lifetime).ToUnixTimeSeconds();
        var header = Encode("""{"alg":"RS256","typ":"JWT"}""");
        var payload = Encode(string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"iat":{{issuedAt}},"exp":{{expires}},"iss":"{{appId}}"}"""));
        var signingInput = $"{header}.{payload}";
        // RS256 is by definition RSASSA-PKCS1-v1_5 with SHA-256 (RFC 7518 3.3), the only algorithm
        // GitHub accepts for App JWTs. This is a signature, not PKCS#1 encryption padding.
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); // NOSONAR S5542
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private static string Encode(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
