using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server;

/// <summary>
/// Runs one tool call: creates the correlationId, runs the use case, hands an oversized result to the result store
/// (AD-20), turns every outcome into the AD-9 envelope and never throws. Logs one stderr line per call with the
/// correlationId, tool, code, HRESULT and duration, and the result store's IDs and sizes, never XML or page content
/// (AD-13).
/// </summary>
public sealed partial class ToolRunner
{
    private readonly ILogger<ToolRunner> logger;
    private readonly ResultDelivery delivery;

    public ToolRunner(ILogger<ToolRunner> logger, ResultDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(delivery);
        this.logger = logger;
        this.delivery = delivery;
    }

    /// <summary>A runner with its own result store on the default settings, for tests and tools built by hand.</summary>
    public ToolRunner(ILogger<ToolRunner> logger)
        : this(logger, new ResultDelivery(ResultStore.WithDefaults(), EnvelopeMeasure.Instance))
    {
    }

    public async Task<CallToolResult> RunAsync(
        string tool,
        IUseCase useCase,
        Func<ToolCallContext, CancellationToken, Task<ToolSuccess>> run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(useCase);
        ArgumentNullException.ThrowIfNull(run);

        // results{} is read once per call (AD-10), so the store, the sizing and the oversize check agree.
        var settings = delivery.Store.Settings;
        var context = new ToolCallContext(Guid.NewGuid().ToString("N"), settings);
        var stopwatch = Stopwatch.StartNew();
        ToolEnvelope envelope;
        Exception? failure = null;
        StoredEvent? stored = null;
        try
        {
            var outcome = delivery.Deliver(await run(context, cancellationToken).ConfigureAwait(false), settings);
            stored = outcome.Stored;
            envelope = ToolEnvelope.Success(outcome.Success);
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

        if (stored is not null)
        {
            LogStored(logger, context.CorrelationId, tool, stored.ResultId, stored.TotalChars, stored.Change.TotalBytes);
            foreach (var evicted in stored.Change.Evicted)
            {
                LogEvicted(logger, evicted, stored.Change.TotalBytes);
            }
        }

        var result = ToResult(envelope);
        var textChars = ((TextContentBlock)result.Content[0]).Text.Length;
        var inlineMax = settings.InlineMaxChars;
        if (textChars > inlineMax)
        {
            // AD-20 sizes every response to fit; only an envelope whose meta alone is over the threshold gets here (Deferred).
            LogOversize(logger, context.CorrelationId, tool, textChars, inlineMax);
        }

        return result;
    }

    /// <summary>structuredContent with the full envelope, plus the AD-9 text block.</summary>
    public static CallToolResult ToResult(ToolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var element = JsonSerializer.SerializeToElement(envelope, EnvelopeJson.Options);
        return new CallToolResult
        {
            StructuredContent = element,
            Content = [new TextContentBlock { Text = TextBlock(envelope, element) }],
            IsError = !envelope.Ok,
        };
    }

    /// <summary>
    /// AD-9's text content block. A success with a body (<c>xml</c>, or <c>meta.binary</c>) is the compact JSON of the
    /// envelope without the body, a <c>\n</c>, then the body unchanged. Anything else is the whole envelope JSON.
    /// </summary>
    public static string TextBlock(ToolEnvelope envelope) =>
        TextBlock(envelope, JsonSerializer.SerializeToElement(envelope, EnvelopeJson.Options));

    /// <summary>The length of <see cref="TextBlock(ToolEnvelope)"/>, without building the body into a string.</summary>
    public static int TextBlockLength(ToolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Body(envelope) is { } body
            ? JsonSerializer.Serialize(Header(envelope), EnvelopeJson.Options).Length + 1 + body.Length
            : JsonSerializer.Serialize(envelope, EnvelopeJson.Options).Length;
    }

    private static string TextBlock(ToolEnvelope envelope, JsonElement whole) =>
        Body(envelope) is { } body
            ? JsonSerializer.Serialize(Header(envelope), EnvelopeJson.Options) + "\n" + body
            : whole.GetRawText();

    private static string? Body(ToolEnvelope envelope) => envelope.Ok ? envelope.Xml ?? envelope.Meta.Binary : null;

    /// <summary>The envelope without its body: everything else, <c>meta.basis</c>, <c>meta.result</c> and <c>warnings</c> included, stays.</summary>
    private static ToolEnvelope Header(ToolEnvelope envelope) => new()
    {
        Ok = envelope.Ok,
        Error = envelope.Error,
        Index = envelope.Index,
        Meta = envelope.Meta with { Binary = null },
        Warnings = envelope.Warnings,
    };

    private static ToolEnvelope Failure(OneNoteException ex, OperationClass operationClass, ToolCallContext context) =>
        ToolEnvelope.Failure(ToolErrors.From(ex, operationClass), new ToolMeta(context.CorrelationId));

    [LoggerMessage(Level = LogLevel.Information, Message = "tool call correlationId={CorrelationId} tool={Tool} result={Result} hresult={HResult} ms={ElapsedMs}")]
    private static partial void LogCall(ILogger logger, string correlationId, string tool, string result, string hresult, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "tool call failed with an unmapped error correlationId={CorrelationId} tool={Tool}")]
    private static partial void LogInternal(ILogger logger, string correlationId, string tool, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "result stored correlationId={CorrelationId} tool={Tool} resultId={ResultId} totalChars={TotalChars} storeBytes={StoreBytes}")]
    private static partial void LogStored(ILogger logger, string correlationId, string tool, string resultId, int totalChars, long storeBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "result evicted resultId={ResultId} storeBytes={StoreBytes}")]
    private static partial void LogEvicted(ILogger logger, string resultId, long storeBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "text block over the inline threshold correlationId={CorrelationId} tool={Tool} chars={Chars} inlineMaxChars={InlineMaxChars}")]
    private static partial void LogOversize(ILogger logger, string correlationId, string tool, int chars, int inlineMaxChars);
}

/// <summary>The server's <see cref="ITextBlockMeasure"/>: sizes are those of <see cref="ToolRunner.ToResult"/>'s output.</summary>
public sealed class EnvelopeMeasure : ITextBlockMeasure
{
    public static EnvelopeMeasure Instance { get; } = new();

    public int TextBlock(ToolSuccess success) => ToolRunner.TextBlockLength(ToolEnvelope.Success(success));

    public int Json<T>(T value) => JsonSerializer.Serialize(value, EnvelopeJson.Options).Length;
}
