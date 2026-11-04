using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server;

/// <summary>
/// Runs one tool call: creates the correlationId, runs the use case, turns every outcome into the AD-9 envelope
/// and never throws. Logs one stderr line per call with the correlationId, tool, code, HRESULT and duration,
/// never XML or page content (AD-13).
/// </summary>
public sealed partial class ToolRunner(ILogger<ToolRunner> logger)
{
    public async Task<CallToolResult> RunAsync(
        string tool,
        IUseCase useCase,
        Func<ToolCallContext, CancellationToken, Task<ToolSuccess>> run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(useCase);
        ArgumentNullException.ThrowIfNull(run);

        var context = new ToolCallContext(Guid.NewGuid().ToString("N"));
        var stopwatch = Stopwatch.StartNew();
        ToolEnvelope envelope;
        Exception? failure = null;
        try
        {
            envelope = ToolEnvelope.Success(await run(context, cancellationToken).ConfigureAwait(false));
        }
        catch (OneNoteException ex)
        {
            failure = ex;
            envelope = Failure(ex, useCase.Class, context);
        }
        catch (OperationCanceledException ex)
        {
            failure = ex;
            envelope = Failure(new OneNoteException(ErrorCode.Internal, "The call was cancelled.", innerException: ex), useCase.Class, context);
        }
#pragma warning disable CA1031 // Tools never throw (AD-9): anything unexpected becomes Internal.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
            envelope = Failure(new OneNoteException(ErrorCode.Internal, $"Unexpected {ex.GetType().Name}.", innerException: ex), useCase.Class, context);
        }

        stopwatch.Stop();
        LogCall(
            logger,
            context.CorrelationId,
            tool,
            envelope.Error is { } e ? e.Code.ToString() : "ok",
            envelope.Error?.HResult ?? "-",
            stopwatch.ElapsedMilliseconds);
        if (envelope.Error?.Code == ErrorCode.Internal && failure is not null)
        {
            // Unmapped failures keep their exception in the log so a reported correlationId can be traced.
            LogInternal(logger, context.CorrelationId, tool, failure);
        }

        return ToResult(envelope);
    }

    /// <summary>structuredContent, plus one text block carrying the same JSON for clients that ignore structured content.</summary>
    public static CallToolResult ToResult(ToolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var element = JsonSerializer.SerializeToElement(envelope, EnvelopeJson.Options);
        return new CallToolResult
        {
            StructuredContent = element,
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            IsError = !envelope.Ok,
        };
    }

    private static ToolEnvelope Failure(OneNoteException ex, OperationClass operationClass, ToolCallContext context) =>
        ToolEnvelope.Failure(ToolErrors.From(ex, operationClass), new ToolMeta(context.CorrelationId));

    [LoggerMessage(Level = LogLevel.Information, Message = "tool call correlationId={CorrelationId} tool={Tool} result={Result} hresult={HResult} ms={ElapsedMs}")]
    private static partial void LogCall(ILogger logger, string correlationId, string tool, string result, string hresult, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "tool call failed with an unmapped error correlationId={CorrelationId} tool={Tool}")]
    private static partial void LogInternal(ILogger logger, string correlationId, string tool, Exception exception);
}
