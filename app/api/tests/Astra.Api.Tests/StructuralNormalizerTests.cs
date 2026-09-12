using Astra.Api.Llm.PatternAnalysis;
using Xunit;

namespace Astra.Api.Tests;

public class StructuralNormalizerTests
{
    [Fact]
    public void DelphiGetters_HashEqual_AcrossRenamesCommentsWhitespace()
    {
        var a = StructuralNormalizer.Normalize("delphi",
            "function TOrder.GetTotal: Integer;\nbegin\n  Result := FTotal; // the total\nend;");
        var b = StructuralNormalizer.Normalize("delphi",
            "function TCustomer.GetName: Integer;\n{ returns the name }\nbegin\n\n\n    Result := FName;\nend;");

        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal("trivial-getter", a.TrivialShape);
        Assert.Equal("trivial-getter", b.TrivialShape);
    }

    [Fact]
    public void DelphiSetter_IsTrivialSetter()
    {
        var r = StructuralNormalizer.Normalize("delphi",
            "procedure TOrder.SetTotal(const Value: Integer);\nbegin\n  FTotal := Value;\nend;");
        Assert.Equal("trivial-setter", r.TrivialShape);
    }

    [Fact]
    public void CppGetters_HashEqual_AndTrivial()
    {
        var a = StructuralNormalizer.Normalize("cpp", "int Foo::bar() const {\n  return m_bar; /* accessor */\n}");
        var b = StructuralNormalizer.Normalize("cpp", "int Baz::qux() const\n{\n    return m_qux;\n}");
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal("trivial-getter", a.TrivialShape);
    }

    [Fact]
    public void JavaGetter_IsTrivial_ButRealMethodIsNot()
    {
        var getter = StructuralNormalizer.Normalize("java", "public int getX() {\n    return x;\n}");
        var real = StructuralNormalizer.Normalize("java",
            "public int total(List<Item> items) {\n    int t = 0;\n    for (Item i : items) t += i.price();\n    return t;\n}");
        Assert.Equal("trivial-getter", getter.TrivialShape);
        Assert.Null(real.TrivialShape);
        Assert.NotEqual(getter.Hash, real.Hash);
    }

    [Fact]
    public void DifferentBodies_DifferentHashes()
    {
        var a = StructuralNormalizer.Normalize("cpp", "int f(int a) {\n  return a + 1;\n}");
        var b = StructuralNormalizer.Normalize("cpp", "int f(int a) {\n  return a * 2;\n}");
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void StringLiterals_DoNotAffectHash()
    {
        var a = StructuralNormalizer.Normalize("cpp", "void f() {\n  log(\"hello world\");\n}");
        var b = StructuralNormalizer.Normalize("cpp", "void f() {\n  log(\"completely different\");\n}");
        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void Vb6_LineComments_Stripped()
    {
        var a = StructuralNormalizer.Normalize("vb6", "Public Function GetId() As Long\n    ' returns id\n    GetId = mId\nEnd Function");
        var b = StructuralNormalizer.Normalize("vb6", "Public Function GetId() As Long\n    GetId = mId\nEnd Function");
        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void Fortran_ColumnOneComments_Stripped()
    {
        var a = StructuralNormalizer.Normalize("fortran-f77",
            "      SUBROUTINE ADD(A,B,C)\nC     adds two numbers\n      C = A + B\n      RETURN\n      END");
        var b = StructuralNormalizer.Normalize("fortran-f77",
            "      SUBROUTINE ADD(A,B,C)\n      C = A + B\n      RETURN\n      END");
        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void TokenCount_IsPositive_AndNormalizedUsesPlaceholders()
    {
        var r = StructuralNormalizer.Normalize("cpp", "int f(int a) { return a; }");
        Assert.True(r.TokenCount > 0);
        Assert.Contains("$1", r.Normalized);
        Assert.DoesNotContain("int f", r.Normalized.Replace("$", ""));
    }
}
