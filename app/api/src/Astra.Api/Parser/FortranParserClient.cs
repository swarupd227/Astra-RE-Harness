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

    public void Dispose() => _channel.Dispose();
}
