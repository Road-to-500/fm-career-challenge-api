using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;

namespace FmCareerChallenge.UnitTests;

public sealed class GoogleEmailVerificationTests
{
    [Theory]
    [InlineData("{\"email_verified\":true}", true)]
    [InlineData("{\"email_verified\":false}", false)]
    [InlineData("{}", false)]
    [InlineData("{\"verified_email\":true}", false)]
    public void V3UserInfoRequiresExplicitVerifiedEmail(string payload, bool expected)
    {
        using var document = JsonDocument.Parse(payload);
        var identity = new ClaimsIdentity();
        var action = new JsonKeyClaimAction("email_verified", "bool", "email_verified");
        action.Run(document.RootElement, identity, "Google");

        var verified = bool.TryParse(identity.FindFirst("email_verified")?.Value, out var value) && value;

        Assert.Equal(expected, verified);
    }
}
