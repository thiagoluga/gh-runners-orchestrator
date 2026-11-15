using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RunnerOrchestrator.GitHub;

namespace RunnerOrchestrator.UnitTests.GitHub;

public sealed class GitHubAppJwtTests
{
    [Fact]
    public void Create_SignsRs256WithBackdatedIssueAndNineMinuteExpiry()
    {
        using var key = RSA.Create(2048);
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        var jwt = GitHubAppJwt.Create(5770327, key, now);

        var parts = jwt.Split('.');
        parts.Should().HaveCount(3);
        using var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
        header.RootElement.GetProperty("alg").GetString().Should().Be("RS256");
        using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        payload.RootElement.GetProperty("iss").GetString().Should().Be("5770327");
        payload.RootElement.GetProperty("iat").GetInt64().Should().Be(now.AddSeconds(-60).ToUnixTimeSeconds());
        payload.RootElement.GetProperty("exp").GetInt64().Should().Be(now.AddMinutes(9).ToUnixTimeSeconds());
        key.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).Should().BeTrue();
    }
}
