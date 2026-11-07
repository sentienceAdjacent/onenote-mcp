using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.LargeResults;

/// <summary>
/// Measures responses the way the server will write them (AD-9, AD-20), so Core can size index pages, runs and parts
/// without knowing the wire serializer. Server implements it with the envelope serializer; nothing else may.
/// </summary>
public interface ITextBlockMeasure
{
    /// <summary>The length, in UTF-16 characters, of the text content block the server builds for <paramref name="success"/>.</summary>
    int TextBlock(ToolSuccess success);

    /// <summary>The length of <paramref name="value"/> serialized as the envelope serializer writes it, escaping included.</summary>
    int Json<T>(T value);
}
