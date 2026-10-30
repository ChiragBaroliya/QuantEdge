using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using QuantEdge.Infrastructure.Security;
using Xunit;

namespace QuantEdge.Tests;

public class ApiAccessTokenTests
{
    private const string Key = "unit-test-signing-key-0123456789-abcdef";
    private static readonly DateTime Now = new(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);

    private static IConfiguration Config(string? key = Key, string? enforce = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ApiAccessToken.SigningKeySetting] = key,
            [ApiAccessToken.EnforceSetting] = enforce
        }).Build();

    // ---------------- token ----------------
    [Fact]
    public void ValidToken_GivesTheCaller()
    {
        string token = ApiAccessToken.Create(Key, 7, "User", TimeSpan.FromHours(1), Now);

        Assert.True(ApiAccessToken.TryValidate(token, Key, Now, out var caller));
        Assert.Equal(new ApiCaller(7, "User"), caller);
    }

    [Fact]
    public void ExpiredToken_IsRejected()
    {
        string token = ApiAccessToken.Create(Key, 7, "User", TimeSpan.FromHours(1), Now);

        Assert.True(ApiAccessToken.TryValidate(token, Key, Now.AddMinutes(59), out _));
        Assert.False(ApiAccessToken.TryValidate(token, Key, Now.AddHours(1), out _));
    }

    [Fact]
    public void TokenSignedWithAnotherKey_IsRejected()
    {
        string token = ApiAccessToken.Create("another-signing-key-0123456789-abcdef", 7, "Admin", TimeSpan.FromHours(1), Now);

        Assert.False(ApiAccessToken.TryValidate(token, Key, Now, out _));
    }

    [Fact]
    public void ChangingThePayload_BreaksTheSignature()
    {
        string token = ApiAccessToken.Create(Key, 7, "User", TimeSpan.FromHours(1), Now);
        string forgedPayload = ApiAccessToken.Create(Key, 1, "Admin", TimeSpan.FromHours(1), Now).Split('.')[0];

        Assert.False(ApiAccessToken.TryValidate($"{forgedPayload}.{token.Split('.')[1]}", Key, Now, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    [InlineData("not base64!.also not!")]
    public void MalformedTokens_AreRejectedWithoutThrowing(string? token)
    {
        Assert.False(ApiAccessToken.TryValidate(token, Key, Now, out _));
    }

    [Fact]
    public void ShortOrMissingSigningKey_CountsAsNotConfigured()
    {
        Assert.Null(ApiAccessToken.SigningKey(Config(key: null)));
        Assert.Null(ApiAccessToken.SigningKey(Config(key: "too-short")));
        Assert.Equal(Key, ApiAccessToken.SigningKey(Config()));
    }

    [Fact]
    public void ForUser_CarriesTheLoginsIdAndRole_AndNeedsALogin()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "5"), new Claim(ClaimTypes.Role, "Admin") }, "cookie"));

        Assert.True(ApiAccessToken.TryValidate(ApiAccessToken.ForUser(Config(), user), Key, DateTime.UtcNow, out var caller));
        Assert.Equal(new ApiCaller(5, "Admin"), caller);

        Assert.Equal(string.Empty, ApiAccessToken.ForUser(Config(), new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Equal(string.Empty, ApiAccessToken.ForUser(Config(key: null), user));
    }

    // ---------------- middleware ----------------
    private static async Task<(int Status, bool ReachedEndpoint, HttpContext Context)> Send(
        string? token, string? query = null, bool allowAnonymous = false, bool hasEndpoint = true, IConfiguration? config = null)
    {
        bool reached = false;
        var middleware = new ApiAccessTokenMiddleware(_ => { reached = true; return Task.CompletedTask; }, config ?? Config(), NullLogger<ApiAccessTokenMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (token != null) context.Request.Headers.Authorization = $"Bearer {token}";
        if (query != null) context.Request.QueryString = new QueryString(query);
        if (hasEndpoint)
        {
            var metadata = allowAnonymous ? new EndpointMetadataCollection(new AllowAnonymousAttribute()) : EndpointMetadataCollection.Empty;
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));
        }

        await middleware.InvokeAsync(context);
        return (context.Response.StatusCode, reached, context);
    }

    private static string TokenFor(int userId, string role = "User") => ApiAccessToken.Create(Key, userId, role, TimeSpan.FromHours(1));

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var (status, reached, _) = await Send(token: null);

        Assert.Equal(401, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task ValidToken_Passes_AndLeavesTheUserEmpty()
    {
        var (status, reached, context) = await Send(TokenFor(7));

        Assert.Equal(200, status);
        Assert.True(reached);
        // Auto Paper's user is resolved from these claims: a login here would point its pages away from the bot's data.
        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        Assert.Null(context.User.FindFirst(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task AllowAnonymousEndpoint_NeedsNoToken()
    {
        var (status, reached, _) = await Send(token: null, allowAnonymous: true);

        Assert.Equal(200, status);
        Assert.True(reached);
    }

    [Fact]
    public async Task AnotherUsersData_IsForbidden_ExceptForAdminAndTheWebServer()
    {
        var (status, reached, _) = await Send(TokenFor(7), "?mode=AUTO_REAL&userId=8");
        Assert.Equal(403, status);
        Assert.False(reached);

        Assert.True((await Send(TokenFor(7), "?userId=7")).ReachedEndpoint);
        Assert.True((await Send(TokenFor(7), "?userId=default_user")).ReachedEndpoint);
        Assert.True((await Send(TokenFor(1, ApiAccessToken.RoleAdmin), "?userId=8")).ReachedEndpoint);
        Assert.True((await Send(TokenFor(0, ApiAccessToken.RoleService), "?userId=8")).ReachedEndpoint);
    }

    [Fact]
    public async Task MissingSigningKey_FailsClosed()
    {
        var (status, reached, _) = await Send(TokenFor(7), config: Config(key: null));

        Assert.Equal(503, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task LogOnlyMode_LetsTheRequestThrough()
    {
        var (status, reached, _) = await Send(token: null, config: Config(enforce: "false"));

        Assert.Equal(200, status);
        Assert.True(reached);
    }
}
