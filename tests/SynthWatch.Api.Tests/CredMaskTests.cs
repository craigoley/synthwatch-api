using SynthWatch.Api.Data.Entities;
using SynthWatch.Api.Dtos;
using Xunit;

namespace SynthWatch.Api.Tests;

/// <summary>
/// Model B is WRITE-ONLY: no read path may echo a credential value OR its ciphertext. These lock the single
/// choke point (CredMask) + the CheckSummaryDto/CheckDetailDto projections. Must-go-red: if a projection ever
/// returns c.SecretHeaders/c.LoginCredentials raw, the "ciphertext absent from the DTO" asserts fail.
/// </summary>
public class CredMaskTests
{
    private const string Cipher = "v1:AAECAwQFBgcICQoLJG2kaaCGtjvlLuX41MkaDPei4kaJWywIWReJ4O6At8G"; // looks-like a stored leaf

    [Fact]
    public void CredMask_masks_values_to_set_preserving_keys()
    {
        var masked = CredMask.Of(new Dictionary<string, string> { ["x-api-key"] = Cipher, ["username"] = "v1:zzz" });
        Assert.NotNull(masked);
        Assert.Equal(new[] { "username", "x-api-key" }, masked!.Keys.OrderBy(k => k));
        Assert.All(masked.Values, v => Assert.Equal("set", v));
    }

    [Fact]
    public void CredMask_null_stays_null()
    {
        Assert.Null(CredMask.Of(null));
    }

    [Fact]
    public void CheckSummaryDto_masks_secret_headers_and_login_credentials_never_the_ciphertext()
    {
        var c = new Check
        {
            Id = 1, Name = "b2c", Kind = "browser", TargetUrl = "https://x.test", Method = "GET",
            SecretHeaders = new Dictionary<string, string> { ["x-api-key"] = Cipher },
            LoginCredentials = new Dictionary<string, string> { ["username"] = Cipher, ["password"] = "v1:other" },
        };

        var dto = CheckSummaryDto.From(c, null, CheckMetricsDto.Empty,
            Array.Empty<LocationStatusDto>(), Array.Empty<TagDto>());

        Assert.Equal("set", dto.SecretHeaders!["x-api-key"]);
        Assert.Equal("set", dto.LoginCredentials!["username"]);
        Assert.Equal("set", dto.LoginCredentials!["password"]);
        // ★ must-go-red: the stored ciphertext must NEVER appear anywhere in the serialized DTO.
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.DoesNotContain(Cipher, json);
        Assert.DoesNotContain("v1:", json); // no ciphertext prefix leaks either
    }
}
