using Astra.Api.Parser.Grpc;
using Grpc.Core;
using Grpc.Net.Client;

// The proto's service is named `Parser` and lives in `Astra.Api.Parser.Grpc`,
// while this file lives in `Astra.Api.Parser`. An alias disambiguates so we
// don't have to write `Astra.Api.Parser.Grpc.Parser.ParserClient` everywhere.
using GrpcParser = Astra.Api.Parser.Grpc.Parser;

namespace Astra.Api.Parser;

/// <summary>
/// Default <see cref="IFortranParserClient"/> implementation. Talks to the
/// parser sidecar over gRPC on <c>Parser:GrpcEndpoint</c>.
///
/// A single <see cref="GrpcChannel"/> is held for the process lifetime —
/// gRPC channels are designed to be long-lived and to multiplex many calls.
/// </summary>
public sealed class FortranParserClient : IFortranParserClient, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly GrpcParser.ParserClient _client;
    private readonly ILogger<FortranParserClient> _logger;

    public FortranParserClient(IConfiguration config, ILogger<FortranParserClient> logger)
    {
        var endpoint = config["Parser:GrpcEndpoint"]
            ?? throw new InvalidOperationException("Parser:GrpcEndpoint is not configured.");
        _logger = logger;
        _channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
        {
            // Match the 32 MiB receive/send limits set server-side.
            MaxReceiveMessageSize = 32 * 1024 * 1024,
            MaxSendMessageSize = 32 * 1024 * 1024,
        });
        _client = new GrpcParser.ParserClient(_channel);
    }

    public async Task<ParseOutcome> ParseAsync(
        string filename,
        string content,
        string? form = null,
        CancellationToken ct = default)
    {
        var req = new ParseRequest
        {
            Filename = filename,
            Content = content,
            Form = form ?? "",
        };

        ParseResult resp;
        try
        {
            resp = await _client.ParseAsync(req, cancellationToken: ct);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Parser sidecar Parse RPC failed for {File}", filename);
            // Synthesise a degraded outcome so the ingest pipeline can
            // record a FAILED state with the gRPC status code preserved.
            return new ParseOutcome(
                Filename: filename,
                LineCount: content.AsSpan().Count('\n'),
                Subroutines: Array.Empty<ParsedSubroutine>(),
                Warnings: new[] { $"parser_rpc_failed: {ex.StatusCode}: {ex.Status.Detail}" });
        }

        return ToOutcome(resp);
    }

    public async Task<CorpusParseOutcome> ParseCorpusAsync(IReadOnlyList<CorpusFile> files, CancellationToken ct = default)
    {
        var req = new Astra.Api.Parser.Grpc.ParseCorpusRequest();
        foreach (var f in files)
            req.Files.Add(new Astra.Api.Parser.Grpc.SourceFile { Filename = f.Filename, Content = f.Content });

        Astra.Api.Parser.Grpc.ParseCorpusReply reply;
        try
        {
            reply = await ParseCorpusStreamingAsync(req, ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            _logger.LogWarning("Parser sidecar has no ParseCorpus RPC; parsing {Count} files one by one", files.Count);
            var perFile = new List<ParseOutcome>(files.Count);
            foreach (var f in files)
                perFile.Add(await ParseAsync(f.Filename, f.Content, form: null, ct: ct));
            return new CorpusParseOutcome(
                perFile,
                new[] { "parser sidecar predates ParseCorpus — C++ files parsed in isolation, cross-file calls unresolved" },
                CrossFileResolved: false);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Parser sidecar ParseCorpus RPC failed for {Count} files", files.Count);
            var degraded = files
                .Select(f => Degraded(f.Filename, f.Content, $"parser_rpc_failed: {ex.StatusCode}: {ex.Status.Detail}"))
                .ToList();
            return new CorpusParseOutcome(degraded, Array.Empty<string>(), CrossFileResolved: false);
        }

        var results = new List<ParseOutcome>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            results.Add(i < reply.Results.Count
                ? ToOutcome(reply.Results[i])
                : Degraded(files[i].Filename, files[i].Content, "parser_rpc_failed: sidecar returned fewer results than files"));
        }
        return new CorpusParseOutcome(results, reply.Warnings.ToArray(), CrossFileResolved: true);
    }

    /// <summary>
    /// The streaming corpus parse: progress events keep the stream busy (a
    /// proxy in front of the sidecar drops one idle for 240 s) and are
    /// logged at every tenth of the way; the single `done` event carries
    /// the reply. A sidecar without the streaming RPC gets the unary call.
    /// </summary>
    private async Task<Astra.Api.Parser.Grpc.ParseCorpusReply> ParseCorpusStreamingAsync(
        Astra.Api.Parser.Grpc.ParseCorpusRequest req, CancellationToken ct)
    {
        try
        {
            using var call = _client.ParseCorpusStream(req, cancellationToken: ct);
            Astra.Api.Parser.Grpc.ParseCorpusReply? done = null;
            var nextLogAt = 0;
            await foreach (var evt in call.ResponseStream.ReadAllAsync(ct))
            {
                if (evt.Done is { } reply)
                {
                    done = reply;
                    continue;
                }
                var p = evt.Progress;
                if (p is null || p.TotalFiles == 0) continue;
                var pct = p.ParsedFiles * 100 / p.TotalFiles;
                if (pct >= nextLogAt)
                {
                    _logger.LogInformation("Parser sidecar corpus parse: {Parsed}/{Total} files ({Pct}%)", p.ParsedFiles, p.TotalFiles, pct);
                    nextLogAt = (pct / 10 + 1) * 10;
                }
            }
            return done ?? throw new RpcException(new Status(StatusCode.Internal, "ParseCorpusStream ended without a done event"));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            _logger.LogInformation("Parser sidecar has no ParseCorpusStream RPC; using the unary ParseCorpus");
            return await _client.ParseCorpusAsync(req, cancellationToken: ct);
        }
    }

    private static ParseOutcome ToOutcome(Astra.Api.Parser.Grpc.ParseResult resp)
    {
        var subs = new List<ParsedSubroutine>(resp.Subroutines.Count);
        foreach (var s in resp.Subroutines)
        {
            subs.Add(new ParsedSubroutine(
                Name: s.Name,
                Signature: s.Signature,
                LineStart: s.LineStart,
                LineEnd: s.LineEnd,
                CommonBlockRefs: s.CommonBlockRefs.ToArray(),
                CalledSubroutines: s.CalledSubroutines.ToArray()));
        }

        return new ParseOutcome(
            Filename: resp.Filename,
            LineCount: resp.LineCount,
            Subroutines: subs,
            Warnings: resp.Warnings.ToArray());
    }

    private static ParseOutcome Degraded(string filename, string content, string warning) => new(
        Filename: filename,
        LineCount: content.AsSpan().Count('\n'),
        Subroutines: Array.Empty<ParsedSubroutine>(),
        Warnings: new[] { warning });

    public async Task<ParserPing> PingAsync(CancellationToken ct = default)
    {
        var reply = await _client.PingAsync(new PingRequest(), cancellationToken: ct);
        return new ParserPing(reply.Service, reply.Version);
    }

    public void Dispose() => _channel.Dispose();
}
