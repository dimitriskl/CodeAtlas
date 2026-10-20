using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas;

public sealed record DuplicateGroup(string Hash, string[] MemberIds, string Reason);

public sealed record DuplicateCatalog(int Version, DuplicateGroup[] Groups)
{
    public const int CurrentVersion = 1;
}

public static class DuplicateFinder
{
    public static DuplicateCatalog FromBodies(IEnumerable<(string Id, string Body)> bodies)
    {
        var entries = bodies
            .Select(body => (body.Id, Tokens: Tokens(body.Body)))
            .Where(entry => entry.Tokens.Length >= 12)
            .Select(entry => (entry.Id, entry.Tokens, Hash: Hash(entry.Tokens)))
            .ToArray();
        var groups = new List<DuplicateGroup>();
        foreach (var group in entries.GroupBy(entry => entry.Hash, StringComparer.Ordinal).Where(group => group.Count() > 1))
            groups.Add(new(group.Key, group.Select(entry => entry.Id).Order(StringComparer.Ordinal).ToArray(), "exact-normalized-body"));
        var exact = groups.SelectMany(group => group.MemberIds).ToHashSet(StringComparer.Ordinal);
        foreach (var bucket in entries.GroupBy(entry => entry.Tokens[0], StringComparer.Ordinal).Where(bucket => bucket.Count() is > 1 and <= 30))
        {
            var items = bucket.ToArray();
            for (var left = 0; left < items.Length; left++)
            for (var right = left + 1; right < items.Length; right++)
            {
                if (Math.Abs(items[left].Tokens.Length - items[right].Tokens.Length) > 2) continue;
                if (items[left].Hash == items[right].Hash) continue;
                if (exact.Contains(items[left].Id) && exact.Contains(items[right].Id)) continue;
                if (Jaccard(items[left].Tokens, items[right].Tokens) < 0.85) continue;
                var ids = new[] { items[left].Id, items[right].Id }.Order(StringComparer.Ordinal).ToArray();
                groups.Add(new(Hash(items[left].Tokens.Concat(items[right].Tokens).ToArray()), ids, "near-normalized-body"));
            }
        }
        return new(DuplicateCatalog.CurrentVersion, groups.OrderBy(group => group.Reason, StringComparer.Ordinal)
            .ThenBy(group => group.MemberIds[0], StringComparer.Ordinal).ToArray());
    }

    public static async Task<DuplicateCatalog> CreateAsync(Solution solution, string solutionPath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var bodies = new List<(string Id, string Body)>();
        foreach (var project in solution.Projects.OrderBy(project => project.FilePath, StringComparer.Ordinal))
        foreach (var document in project.Documents.OrderBy(document => document.FilePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.FilePath is null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || model is null) continue;
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (body is null) continue;
                var symbol = model.GetDeclaredSymbol(method, cancellationToken);
                if (symbol?.GetDocumentationCommentId() is not { } declarationId) continue;
                var projectPath = Path.GetRelativePath(directory, project.FilePath).Replace('\\', '/');
                bodies.Add(($"{projectPath}::{declarationId}", body.ToString()));
            }
        }
        return FromBodies(bodies);
    }

    private static string[] Tokens(string body)
    {
        var text = body.Replace("\\", " ", StringComparison.Ordinal);
        return SyntaxFactory.ParseTokens(text)
            .Where(token => !token.IsKind(SyntaxKind.None) && !string.IsNullOrWhiteSpace(token.Text))
            .Select(token => token.IsKind(SyntaxKind.IdentifierToken) ? "#" : token.Text)
            .ToArray();
    }

    private static string Hash(string[] tokens) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(" ", tokens))));

    private static double Jaccard(string[] left, string[] right)
    {
        var a = left.ToHashSet(StringComparer.Ordinal);
        var b = right.ToHashSet(StringComparer.Ordinal);
        var intersection = a.Count(item => b.Contains(item));
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
