using System.Text;

namespace CodeAtlas.Cli;

/// <summary>
/// Standard input and output of the line protocols, <c>mcp</c> and <c>live</c>, as UTF-8 text.
/// <see cref="Console.In"/> and <see cref="Console.Out"/> use the inherited code page or locale charset, so the
/// streams are opened directly: the same request then gets the same bytes back under every console encoding.
/// </summary>
// Task #04: a query decoded with the console code page gets wrong tokens and a successful empty answer.
internal static class ProtocolStreams
{
    // A raw control character is never valid JSON, inside a string or between tokens, so a line with an invalid byte
    // fails to parse and is refused. The default U+FFFD would parse and be searched, which is a wrong answer and no
    // error. U+0000 cannot be the marker: a decoder fallback reads it as the end of the replacement and drops the byte.
    private const string InvalidByteMarker = "\u0001";

    // Encoding.UTF8 has a preamble, and StreamReader skips a leading byte order mark only for an encoding with one.
    private static readonly Encoding Input =
        Encoding.GetEncoding(Encoding.UTF8.CodePage, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback(InvalidByteMarker));

    private static readonly UTF8Encoding Output = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Opens stdin. A leading UTF-8 byte order mark is skipped, and nothing else selects another encoding,
    /// so <c>FF FE</c> is invalid UTF-8 and not a UTF-16 byte order mark.</summary>
    public static TextReader OpenInput() =>
        new StreamReader(Console.OpenStandardInput(), Input, detectEncodingFromByteOrderMarks: false);

    /// <summary>Opens stdout with no byte order mark. Each write is flushed, because a client waits for its answer
    /// before it sends the next line.</summary>
    public static TextWriter OpenOutput() =>
        new StreamWriter(Console.OpenStandardOutput(), Output) { AutoFlush = true };
}
