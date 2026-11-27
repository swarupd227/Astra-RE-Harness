using Astra.Api.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Password sign-in: hashes, tokens, which persona a request acts as, and what stays open.</summary>
public class AuthTests
{
    [Fact]
    public void A_password_is_stored_as_a_salted_hash_and_verifies()
    {
        var a = PasswordHasher.Hash("correct horse battery");
        var b = PasswordHasher.Hash("correct horse battery");
        Assert.StartsWith("pbkdf2-sha256$", a);
        Assert.NotEqual(a, b);                                       // a new salt each time
        Assert.DoesNotContain("correct horse", a);
        Assert.True(PasswordHasher.Verify("correct horse battery", a));
        Assert.False(PasswordHasher.Verify("correct horse batterY", a));
        Assert.False(PasswordHasher.Verify("correct horse battery", "not a hash"));
        Assert.False(PasswordHasher.Verify("x", "pbkdf2-sha256$5$AAAA$AAAA"));  // too few iterations is refused
    }

    [Fact]
    public void Session_tokens_are_random_and_only_their_hash_is_kept()
    {
        var t1 = SessionTokens.New();
        var t2 = SessionTokens.New();
        Assert.NotEqual(t1, t2);
        Assert.True(t1.Length >= 40);
        Assert.Matches("^[A-Za-z0-9_-]+$", t1);
        Assert.Equal(64, SessionTokens.Hash(t1).Length);
        Assert.Equal(SessionTokens.Hash(t1), SessionTokens.Hash(t1));
        Assert.NotEqual(SessionTokens.Hash(t1), SessionTokens.Hash(t2));
    }

    [Fact]
    public void A_request_acts_only_as_a_persona_its_user_holds()
    {
        var engineerOnly = AuthService.ParsePersonas("engineer");
        Assert.Equal(Persona.Engineer, AuthService.Resolve(engineerOnly, "admin"));   // the header cannot raise a user
        Assert.Equal(Persona.Engineer, AuthService.Resolve(engineerOnly, null));

        var admin = AuthService.ParsePersonas("admin, engineer ,sme,observer,nonsense");
        Assert.Equal(new[] { Persona.Admin, Persona.Engineer, Persona.Sme, Persona.Observer }, admin);
        Assert.Equal(Persona.Sme, AuthService.Resolve(admin, "sme"));
        Assert.Equal(Persona.Admin, AuthService.Resolve(admin, "root"));
        Assert.Equal(Persona.Observer, AuthService.Resolve(Array.Empty<Persona>(), "admin"));
    }

    [Theory]
    [InlineData("/health", true)]
    [InlineData("/health/ready", true)]
    [InlineData("/internal/equivalence-callback", true)]
    [InlineData("/api/v1/auth/login", true)]
    [InlineData("/api/v1/auth/config", true)]
    [InlineData("/api/v1/auth/me", false)]
    [InlineData("/api/v1/settings/llm/key", false)]
    [InlineData("/api/v1/ingest/git", false)]
    [InlineData("/healthy-looking-but-not", false)]
    public void Only_probes_sign_in_and_callbacks_are_open(string path, bool open) =>
        Assert.Equal(open, DevPersonaMiddleware.IsOpenPath(new PathString(path)));

    [Fact]
    public void The_token_comes_from_the_header_or_on_a_get_from_the_query()
    {
        var withHeader = new DefaultHttpContext();
        withHeader.Request.Method = "POST";
        withHeader.Request.Headers.Authorization = "Bearer abc123";
        Assert.Equal("abc123", DevPersonaMiddleware.TokenOf(withHeader.Request));

        var streamGet = new DefaultHttpContext();
        streamGet.Request.Method = "GET";
        streamGet.Request.QueryString = new QueryString("?afterSeq=0&access_token=tok");
        Assert.Equal("tok", DevPersonaMiddleware.TokenOf(streamGet.Request));

        var queryPost = new DefaultHttpContext();
        queryPost.Request.Method = "POST";
        queryPost.Request.QueryString = new QueryString("?access_token=tok");
        Assert.Null(DevPersonaMiddleware.TokenOf(queryPost.Request));               // a write never takes a URL token
    }
}
