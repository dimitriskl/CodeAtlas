using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CodeAtlas;

internal static class BodySurface
{
    internal const int Version = 1;

    // The caller must use exactly the compilation tree/options from the indexed snapshot.
    // Equal results permit reuse only together with ALL context/inventory gates in PLAN.md.
    internal static string? TryHash(SyntaxNode root, SourceText text)
    {
        if (root.ContainsDiagnostics && root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            return null;
        if (root.DescendantTrivia(descendIntoTrivia: true).Any(t =>
                t.IsKind(SyntaxKind.SkippedTokensTrivia) || t.IsKind(SyntaxKind.DisabledTextTrivia)))
            return null;

        var replacements = new List<(TextSpan Span, byte Kind)>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            SyntaxNode? body = method.Body ?? (SyntaxNode?)method.ExpressionBody;
            if (body is null)
                continue;
            // Directives inside removed text can leak state into later declarations.
            if (body.DescendantTrivia(descendIntoTrivia: true).Any(t =>
                    t.IsDirective && body.Span.Contains(t.Span)))
                return null;
            replacements.Add((body.Span, method.Body is null ? (byte)2 : (byte)1));
        }

        var source = text.ToString();
        using var projected = new MemoryStream();
        using var writer = new BinaryWriter(projected, Encoding.UTF8, leaveOpen: true);
        writer.Write(Version);
        writer.Write(replacements.Count);
        var cursor = 0;
        foreach (var (span, kind) in replacements.OrderBy(item => item.Span.Start))
        {
            if (span.Start < cursor || span.End > source.Length)
                return null;
            WriteSource(writer, source.AsSpan(cursor, span.Start - cursor));
            writer.Write(kind);
            cursor = span.End;
        }
        WriteSource(writer, source.AsSpan(cursor));
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(projected.GetBuffer().AsSpan(0, checked((int)projected.Length))));
    }

    private static void WriteSource(BinaryWriter writer, ReadOnlySpan<char> text)
    {
        // Length-frame exact UTF-16 code units; marker-looking source and malformed
        // surrogate sequences cannot collide through delimiter/encoding replacement.
        writer.Write(text.Length);
        foreach (var character in text)
            writer.Write((ushort)character);
    }
}
