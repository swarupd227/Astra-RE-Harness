using Astra.Api.Llm.Dependency;
using Astra.Api.Persistence.Entities;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>VB6 calls resolve the way VB6 scopes them, never to a same-named routine of another form.</summary>
public class Vb6CallScopeTests
{
    private static Subroutine Sub(SourceFile file, string name, string signature = "Public Sub X()") =>
        new() { Id = Guid.NewGuid(), Name = name, Signature = signature, SourceFileId = file.Id, SourceFile = file, SourceLanguage = "vb6" };

    private static SourceFile File(string path) => new() { Id = Guid.NewGuid(), RelativePath = path };

    [Fact]
    public void A_call_resolves_to_its_own_module_then_a_standard_module_then_a_unique_class_method()
    {
        var formA = File("Form/A.frm");
        var formB = File("Form/B.frm");
        var mod = File("Module/Main.bas");
        var cls = File("Class/Tallies.cls");
        var aDone = Sub(formA, "cmdDone_Click", "Private Sub cmdDone_Click()");
        var aSave = Sub(formA, "SaveAll", "Private Sub SaveAll()");
        var bDone = Sub(formB, "cmdDone_Click", "Private Sub cmdDone_Click()");
        var helper = Sub(mod, "OpenDataFile");
        var hidden = Sub(mod, "Secret", "Private Function Secret()");
        var run = Sub(cls, "RunTallies");
        var scope = new DependencyGraphBuilder.Vb6Scope([aDone, aSave, bDone, helper, hidden, run]);

        Assert.Same(aDone, scope.Resolve(aSave, "cmdDone_Click"));   // own form first
        Assert.Same(bDone, scope.Resolve(bDone, "cmdDone_Click"));
        Assert.Null(scope.Resolve(helper, "cmdDone_Click"));        // a form's handler is never reached from outside
        Assert.Same(helper, scope.Resolve(aDone, "OpenDataFile"));   // public routine of a .bas is global
        Assert.Null(scope.Resolve(aDone, "Secret"));                 // a private one is not
        Assert.Same(run, scope.Resolve(aDone, "RunTallies"));        // moTallies.RunTallies, recorded by name
        Assert.Null(scope.Resolve(aDone, "NoSuchRoutine"));
    }

    [Fact]
    public void A_feature_slice_is_the_seeds_and_everything_they_call_once()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid(), e = Guid.NewGuid();
        var calls = new[] { (a, b), (b, c), (c, b), (d, e) };   // b and c call each other; d is not reached from a
        var slice = Astra.Api.Llm.Dependency.CallClosure.Of([a], calls);
        Assert.Equal(new HashSet<Guid> { a, b, c }, slice);
        Assert.Equal(new HashSet<Guid> { a, b, c, d, e }, Astra.Api.Llm.Dependency.CallClosure.Of([a, d], calls));
    }

    [Theory]
    [InlineData("Draft specs for ConflictMatrix and everything it calls", "ConflictMatrix", false)]
    [InlineData("Migrate `SignUpLists` and what it calls", "SignUpLists", true)]
    [InlineData("extract SimpleTallies and all it calls, end to end", "SimpleTallies", true)]
    public void The_mock_brain_routes_a_feature_slice(string text, string path, bool throughGates)
    {
        var routed = Astra.Api.Copilot.MockCopilotBrain.RouteOrNull(text);
        Assert.Equal("extract_many", routed?.Tool);
        var input = routed!.Value.Input.ToString()!;
        Assert.Contains($"path = {path}", input);
        Assert.Contains("withCallees = True", input);
        Assert.Contains($"throughGates = {throughGates}", input);
    }

    [Fact]
    public void A_name_two_modules_define_stays_unresolved()
    {
        var m1 = File("Module/One.bas");
        var m2 = File("Module/Two.bas");
        var f = File("Form/F.frm");
        var caller = Sub(f, "Form_Load", "Private Sub Form_Load()");
        var scope = new DependencyGraphBuilder.Vb6Scope([Sub(m1, "Init"), Sub(m2, "Init"), caller]);
        Assert.Null(scope.Resolve(caller, "Init"));
    }
}
