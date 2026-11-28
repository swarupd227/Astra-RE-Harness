using Astra.Api.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Whose inbox a request reads: a person when signed in with a password, else the shared persona.</summary>
public class InboxScopeTests
{
    [Fact]
    public void A_shared_dev_persona_reads_the_persona_inbox()
    {
        var scope = InboxScope.For(new DefaultHttpContext(), new DevPersonaContext { Persona = Persona.Sme });
        Assert.False(scope.Personal);
        Assert.Equal("persona:sme", scope.Key);
        Assert.Equal("sme", scope.Persona);
    }

    [Fact]
    public void A_signed_in_person_reads_their_own_inbox()
    {
        var id = Guid.NewGuid();
        var http = new DefaultHttpContext();
        http.Items["user"] = new SignedInUser(id, "priya", "Priya", new[] { Persona.Sme, Persona.Engineer });
        var scope = InboxScope.For(http, new DevPersonaContext { Persona = Persona.Engineer });
        Assert.True(scope.Personal);
        Assert.Equal($"user:{id}", scope.Key);
        Assert.Equal("engineer", scope.Persona);
    }
}
