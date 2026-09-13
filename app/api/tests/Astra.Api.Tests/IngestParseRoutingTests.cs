using Astra.Api.Ingest;
using Xunit;

namespace Astra.Api.Tests;

public class IngestParseRoutingTests
{
    [Fact]
    public void Cpp_corpus_uses_corpus_mode()
    {
        var paths = new[] { "src/oatpp/Environment.cpp", "src/oatpp/Environment.hpp", "README.md" };
        Assert.True(IngestParseRouting.UseCorpusMode(paths, totalBytes: 3_000_000));
    }

    [Fact]
    public void Corpus_without_cpp_stays_per_file()
    {
        var paths = new[] { "src/lmder.f", "src/enorm.f", "Unit1.pas" };
        Assert.False(IngestParseRouting.UseCorpusMode(paths, totalBytes: 1_000));
    }

    [Fact]
    public void Cpp_corpus_above_the_message_cap_stays_per_file()
    {
        var paths = new[] { "src/a.cpp", "include/a.h" };
        Assert.False(IngestParseRouting.UseCorpusMode(paths, totalBytes: IngestParseRouting.CorpusParseMaxBytes + 1));
        Assert.True(IngestParseRouting.UseCorpusMode(paths, totalBytes: IngestParseRouting.CorpusParseMaxBytes));
    }
}
