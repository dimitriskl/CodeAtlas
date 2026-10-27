using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CodeAtlas;

/// <summary> uses only the SAME loaded, compiled solution and realized M5 context hashes.</summary>
public static class DuplicateCatalogBuilder
{
    public static async Task<DuplicateBodyCatalog> CreateAsync(Solution solution, string solutionPath,
        string semanticToolHash, IReadOnlyDictionary<string, string> projectContextHashes,
        DuplicateLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new(); limits.Validate();
        var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        string Relative(string path) => Path.GetRelativePath(directory, path).Replace('\\', '/');
        var vocabulary = new List<string>();
        var vocabularyIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var bodies = new List<DuplicateBody>();
        var omissions = new List<DuplicateOmission>();
        var notices = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var ambiguousProjects = solution.Projects.Where(project => project.FilePath is not null)
            .GroupBy(project => Relative(project.FilePath!),StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var ambiguousOmissions = new HashSet<string>(StringComparer.Ordinal);
        int totalTokens = 0, seen = 0;
        long estimatedBytes = 0;

        foreach (var project in solution.Projects.OrderBy(p => p.FilePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp || project.FilePath is null)
                throw new AnalysisException("Duplicate extraction needs an identified C# project.");
            var projectPath = Relative(project.FilePath);
            var ambiguousContext = ambiguousProjects.Contains(projectPath);
            string? contextHash = null;
            if (!ambiguousContext && !projectContextHashes.TryGetValue(projectPath, out contextHash))
                throw new AnalysisException("Duplicate extraction needs the current realized compilation-context hash.");
            // Bound bodies are collected with this clone. The finally releases them before the next project.
            CSharpCompilation? scratch = null;
            try
            {
            scratch = await BindingScratch.CloneAsync(project, cancellationToken);
            foreach (var document in project.Documents.OrderBy(d => d.FilePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root is null || document.FilePath is null || !scratch.ContainsSyntaxTree(root.SyntaxTree))
                    throw new AnalysisException("Duplicate extraction needs a syntax tree, model and source path.");
                var model = scratch.GetSemanticModel(root.SyntaxTree, ignoreAccessibility: false);
                var text = await document.GetTextAsync(cancellationToken);
                var fileHash = DuplicateDigest.Of(text.ToString()); // decoded-text digest, not M4's byte fingerprint
                DuplicateRange Range(SyntaxNode node)
                {
                    var span = node.GetLocation().GetLineSpan();
                    return new(new(Relative(document.FilePath), span.StartLinePosition.Line + 1,
                        span.StartLinePosition.Character + 1), new(Relative(document.FilePath),
                        span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1));
                }
                foreach (var declaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (model.GetDeclaredSymbol(declaration, cancellationToken) is not { } method) continue;
                    if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation)) continue;
                    var canonical = method.OriginalDefinition.PartialDefinitionPart ?? method.OriginalDefinition;
                    var id = $"{projectPath}::{canonical.GetDocumentationCommentId()}";
                    SyntaxNode? body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody;
                    if (ambiguousContext)
                    {
                        // Public method IDs cannot distinguish these realized contexts.
                        // Never attach one target's hash/body to the merged identity.
                        if (!ambiguousOmissions.Add(id)) continue;
                        if (++seen > limits.MaximumMethods) { notices.Add("method-inventory-limit"); goto CatalogComplete; }
                        omissions.Add(new(id,"ambiguous-project-context",Range(body ?? declaration)));
                        continue;
                    }
                    if (body is null)
                    {
                        // A partial definition's implementation is visited separately; do not claim it missing.
                        if (method.PartialImplementationPart is null)
                        {
                            if (++seen > limits.MaximumMethods) { notices.Add("method-inventory-limit"); goto CatalogComplete; }
                            omissions.Add(new(id, "no-body", Range(declaration)));
                        }
                        continue;
                    }
                    if (!visited.Add(id)) throw new AnalysisException("More than one body maps to a supported method ID.");
                    seen++;
                    if (seen > limits.MaximumMethods)
                    {
                        notices.Add("method-inventory-limit");
                        goto CatalogComplete; // Stop all document/project loops; unvisited scope remains explicitly incomplete.
                    }
                    var range = Range(body);
                    var tokens = body.DescendantTokens().Take(limits.MaximumTokensPerBody + 1).ToArray();
                    string? unsupported = tokens.Length > limits.MaximumTokensPerBody ? "body-token-limit" : null;
                    unsupported ??= totalTokens + tokens.Length > limits.MaximumTotalTokens ? "catalog-token-limit" : null;
                    unsupported ??= Unsupported(declaration, body, method, model, cancellationToken);
                    if (unsupported is not null)
                    {
                        omissions.Add(new(id, unsupported, range));
                        if (unsupported.EndsWith("limit", StringComparison.Ordinal)) notices.Add(unsupported);
                        continue;
                    }
                    try
                    {
                        var normalizer = new Normalizer(solution, model, method, Relative, cancellationToken);
                        var keys = tokens.Select(normalizer.Token).ToArray();
                        var signature = normalizer.Signature();
                        var missing = keys.Distinct(StringComparer.Ordinal).Where(k => !vocabularyIds.ContainsKey(k)).ToArray();
                        // Conservative 2x UTF-8 allowance plus per-token/body overhead; exact JSON bound below.
                        var addition = 1024L + tokens.Length * 48L + missing.Sum(k => 32L + Encoding.UTF8.GetByteCount(k) * 2L);
                        if (estimatedBytes + addition > limits.MaximumCatalogBytes)
                        {
                            omissions.Add(new(id, "catalog-byte-estimate-limit", range));
                            notices.Add("catalog-byte-estimate-limit"); continue;
                        }
                        foreach (var key in missing) { vocabularyIds.Add(key, vocabulary.Count); vocabulary.Add(key); }
                        bodies.Add(new(id, projectPath,
                            method.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), range,
                            fileHash, text.Length, contextHash!, signature,
                            DuplicateDigest.Json(new { signature, Tokens = tokens.Select(t => new { t.RawKind, t.Text }).ToArray() }),
                            DuplicateDigest.Json(new { signature, Tokens = keys }), keys.Select(k => vocabularyIds[k]).ToArray(),
                            tokens.Select(t => t.SpanStart).ToArray(), tokens.Select(t => t.Span.Length).ToArray(), normalizer.Names));
                        totalTokens += tokens.Length; estimatedBytes += addition;
                    }
                    catch (UnsupportedBindingException)
                    {
                        omissions.Add(new(id, "unresolved-or-unsupported-binding", range));
                    }
                }
            }
            }
            finally
            {
                scratch = null;
                BindingScratch.Release();
            }
        }
        CatalogComplete:
        var catalog = new DuplicateBodyCatalog("structural-v1", semanticToolHash, vocabulary.ToArray(),
            bodies.OrderBy(b => b.SymbolId, StringComparer.Ordinal).ToArray(),
            omissions.OrderBy(o => o.SymbolId, StringComparer.Ordinal).ThenBy(o => o.Reason, StringComparer.Ordinal).ToArray(),
            DuplicateCoverage.Current, limits, notices.ToArray());
        // Include omissions/vocabulary/metadata too. Never silently write an oversize catalog.
        if (DuplicateDigest.StoredBytes(catalog) > limits.MaximumCatalogBytes)
            throw new AnalysisException("Duplicate catalog exceeds its serialized-byte budget; reduce extraction scope or limits.");
        return catalog;
    }

    private static string? Unsupported(MethodDeclarationSyntax declaration, SyntaxNode body, IMethodSymbol method,
        SemanticModel model, CancellationToken token)
    {
        if (method.IsGenericMethod) return "generic-method";
        if (method.IsAsync) return "async-method";
        if (declaration.DescendantTrivia(descendIntoTrivia: true).Any(t => t.IsDirective)) return "method-directive";
        if (declaration.Modifiers.Any(SyntaxKind.UnsafeKeyword)) return "unsafe-method";
        foreach (var node in body.DescendantNodesAndSelf())
        {
            token.ThrowIfCancellationRequested();
            if (node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or QueryExpressionSyntax
                or AwaitExpressionSyntax or YieldStatementSyntax or UnsafeStatementSyntax or FixedStatementSyntax
                or StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax
                or PointerTypeSyntax or FunctionPointerTypeSyntax or PatternSyntax or TupleExpressionSyntax
                or TupleTypeSyntax or GotoStatementSyntax or LabeledStatementSyntax)
                return "unsupported-body-syntax";
            if (node is InvocationExpressionSyntax invocation
                && model.GetSymbolInfo(invocation, token).Symbol is not IMethodSymbol)
                return "dynamic-or-unresolved-invocation";
            if (node is ExpressionSyntax expression && model.GetTypeInfo(expression, token).Type?.TypeKind == TypeKind.Dynamic)
                return "dynamic-expression";
        }
        return null;
    }

    private sealed class UnsupportedBindingException : Exception { }

    private sealed class Normalizer
    {
        private readonly Solution solution;
        private readonly SemanticModel model;
        private readonly IMethodSymbol method;
        private readonly Func<string, string> relative;
        private readonly CancellationToken cancellation;
        private readonly Dictionary<ISymbol, string> slots = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeSymbol, string> typeKeys = new(SymbolEqualityComparer.IncludeNullability);
        private readonly Dictionary<ISymbol, string> symbolKeys = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<SyntaxNode, string> semanticContexts = [];
        private readonly List<DuplicateName> names = [];
        internal DuplicateName[] Names => names.ToArray();

        internal Normalizer(Solution solution, SemanticModel model, IMethodSymbol method,
            Func<string, string> relative, CancellationToken cancellation)
        {
            this.solution = solution; this.model = model; this.method = method;
            this.relative = relative; this.cancellation = cancellation;
            foreach (var parameter in method.Parameters)
            {
                var slot = "P" + parameter.Ordinal;
                slots.Add(parameter, slot); names.Add(new(slot, parameter.Name, Type(parameter.Type)));
            }
            var declaration = method.DeclaringSyntaxReferences.Single(r => r.SyntaxTree == model.SyntaxTree
                && r.GetSyntax(cancellation) is MethodDeclarationSyntax m && (m.Body is not null || m.ExpressionBody is not null))
                .GetSyntax(cancellation);
            foreach (var node in declaration.DescendantNodes())
            {
                if (node is not (VariableDeclaratorSyntax or ForEachStatementSyntax or SingleVariableDesignationSyntax
                    or CatchDeclarationSyntax)) continue;
                if (model.GetDeclaredSymbol(node, cancellation) is not ILocalSymbol local) continue;
                var slot = "L" + (names.Count - method.Parameters.Length);
                slots.Add(local, slot); names.Add(new(slot, local.Name, Type(local.Type)));
            }
        }

        internal string Signature() => DuplicateDigest.Json(new
        {
            method.IsStatic, method.IsReadOnly, method.RefKind, Return = Type(method.ReturnType),
            Parameters = method.Parameters.Select(p => new { p.RefKind, Type = Type(p.Type), p.IsParams }).ToArray()
        });

        internal string Token(SyntaxToken token)
        {
            cancellation.ThrowIfCancellationRequested();
            var parent = token.Parent!;
            string binding = "", type = "";
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                ISymbol? symbol = parent switch
                {
                    VariableDeclaratorSyntax or ForEachStatementSyntax or SingleVariableDesignationSyntax
                        or CatchDeclarationSyntax => model.GetDeclaredSymbol(parent, cancellation),
                    SimpleNameSyntax => model.GetSymbolInfo(parent, cancellation).Symbol,
                    _ => null
                };
                // var is a contextual keyword and must retain the actual inferred type.
                if (symbol is null && parent is IdentifierNameSyntax name && name.IsVar)
                    binding = "inferred:" + Type(model.GetTypeInfo(name, cancellation).Type ?? throw new UnsupportedBindingException());
                else if (symbol is null) throw new UnsupportedBindingException();
                else binding = slots.TryGetValue(symbol, out var slot) ? "slot:" + slot : Symbol(symbol);
                return DuplicateDigestToken(token.RawKind, "", binding + SemanticContext(parent), "");
            }
            // Keep spelling of literals, keywords and punctuation; no constant or operator erasure.
            // Preserve overload/conversion result context where a token owns an operation.
            if (parent is ExpressionSyntax expression)
            {
                var info = model.GetTypeInfo(expression, cancellation);
                type = (info.Type is null ? "" : Type(info.Type)) + "->"
                    + (info.ConvertedType is null ? "" : Type(info.ConvertedType));
                if (model.GetSymbolInfo(expression, cancellation).Symbol is { } symbol) binding = Symbol(symbol);
                var conversion = model.GetConversion(expression, cancellation);
                if (conversion.MethodSymbol is { } converter) binding += "|conversion:" + Symbol(converter);
            }
            return DuplicateDigestToken(token.RawKind, token.Text, binding + SemanticContext(parent), type);
        }

        private string SemanticContext(SyntaxNode node)
        {
            if (semanticContexts.TryGetValue(node, out var cached)) return cached;
            // Identifier references also carry conversions. Capturing only punctuation
            // would miss implicit numeric/user conversions and omitted caller constants.
            var expression = node as ExpressionSyntax;
            var info = expression is null ? default : model.GetTypeInfo(expression, cancellation);
            var conversion = expression is null ? default : model.GetConversion(expression, cancellation);
            var constant = expression is null ? default : model.GetConstantValue(expression, cancellation);
            var operation = expression is null ? null : model.GetOperation(expression, cancellation);
            var conversions = new List<object>();
            for (var current = operation; current is not null; current = current.Parent)
            {
                if (current is IConversionOperation converted)
                    conversions.Add(new { converted.IsChecked, converted.IsImplicit,
                        Type = converted.Type is null ? null : Type(converted.Type),
                        Method = converted.OperatorMethod is null ? null : Symbol(converted.OperatorMethod) });
                if (current != operation && !current.IsImplicit) break;
            }
            var call = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var invocation = call is null ? null : model.GetOperation(call, cancellation) as IInvocationOperation;
            object[] implicitArguments = invocation?.Arguments.Where(a => a.IsImplicit).Select(a => (object)new
            {
                Parameter = a.Parameter?.Ordinal, Kind = a.ArgumentKind.ToString(),
                Type = a.Value.Type is null ? null : Type(a.Value.Type),
                HasConstant = a.Value.ConstantValue.HasValue,
                Value = a.Value.ConstantValue.HasValue ? ConstantValue(a.Value.ConstantValue.Value) : null
            }).ToArray() ?? [];
            bool? checkedOperation = operation switch
            {
                IBinaryOperation binary => binary.IsChecked,
                IUnaryOperation unary => unary.IsChecked,
                IIncrementOrDecrementOperation increment => increment.IsChecked,
                IConversionOperation converted => converted.IsChecked,
                _ => null
            };
            var result = JsonSerializer.Serialize(new {
                Type = info.Type is null ? null : Type(info.Type),
                ConvertedType = info.ConvertedType is null ? null : Type(info.ConvertedType),
                Conversion = conversion.MethodSymbol is null ? null : Symbol(conversion.MethodSymbol),
                Constant = constant.HasValue ? ConstantValue(constant.Value) : null,
                HasConstant = constant.HasValue,
                Checked = checkedOperation, Conversions = conversions.ToArray(), ImplicitArguments = implicitArguments
            });
            semanticContexts.Add(node, result);
            return result;
        }

        private static string? ConstantValue(object? value) => value switch
        {
            null => null,
            IFormattable formatted => value.GetType().FullName + ":" + formatted.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.GetType().FullName + ":" + value
        };

        private static string DuplicateDigestToken(int kind, string text, string binding, string type) =>
            JsonSerializer.Serialize(new { kind, text, binding, type });

        private string Type(ITypeSymbol type)
        {
            if (typeKeys.TryGetValue(type, out var cached)) return cached;
            var key = TypeCore(type);
            typeKeys.Add(type, key);
            return key;
        }

        private string TypeCore(ITypeSymbol type)
        {
            if (type.TypeKind is TypeKind.Error or TypeKind.Dynamic or TypeKind.Pointer or TypeKind.FunctionPointer)
                throw new UnsupportedBindingException();
            if (type is IArrayTypeSymbol array)
                return JsonSerializer.Serialize(new { ArrayRank = array.Rank, Element = Type(array.ElementType), type.NullableAnnotation });
            if (type is ITypeParameterSymbol parameter)
                return JsonSerializer.Serialize(new { Parameter = parameter.Ordinal, Owner = parameter.ContainingSymbol.GetDocumentationCommentId(),
                    Origin = SourceOrigin(parameter.ContainingSymbol), parameter.TypeParameterKind, parameter.NullableAnnotation });
            if (type is INamedTypeSymbol named)
            {
                if (named.IsTupleType || named.IsAnonymousType) throw new UnsupportedBindingException();
                return JsonSerializer.Serialize(new { Name = named.OriginalDefinition.GetDocumentationCommentId(),
                    Origin = SourceOrigin(named), Assembly = named.ContainingAssembly?.Identity.ToString(),
                    Containing = named.ContainingType is null ? null : Type(named.ContainingType),
                    Arguments = named.TypeArguments.Select(Type).ToArray(), named.NullableAnnotation });
            }
            throw new UnsupportedBindingException();
        }

        private string SourceOrigin(ISymbol symbol)
        {
            var tree = symbol.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
            if (tree is null) return "metadata";
            var project = solution.GetDocument(tree)?.Project.FilePath ?? throw new UnsupportedBindingException();
            return relative(project);
        }

        private string Symbol(ISymbol symbol)
        {
            if (symbolKeys.TryGetValue(symbol, out var cached)) return cached;
            var key = SymbolCore(symbol);
            symbolKeys.Add(symbol, key);
            return key;
        }

        private string SymbolCore(ISymbol symbol)
        {
            if (symbol is IAliasSymbol alias) symbol = alias.Target;
            if (slots.TryGetValue(symbol, out var slot)) return "slot:" + slot;
            var declaration = symbol.OriginalDefinition.GetDocumentationCommentId();
            if (symbol is IMethodSymbol { MethodKind: MethodKind.BuiltinOperator }) declaration ??= "builtin-operator:" + symbol.Name;
            if (declaration is null && symbol is not INamespaceSymbol) throw new UnsupportedBindingException();
            object? shape = symbol switch
            {
                IMethodSymbol target => new { Containing = Type(target.ContainingType),
                    Return = Type(target.ReturnType), Arguments = target.TypeArguments.Select(Type).ToArray(),
                    Parameters = target.Parameters.Select(p => new { p.RefKind, Type = Type(p.Type) }).ToArray(),
                    target.RefKind, target.IsStatic, Receiver = target.ReceiverType is null ? null : Type(target.ReceiverType) },
                IPropertySymbol property => new { Containing = Type(property.ContainingType), Type = Type(property.Type),
                    Parameters = property.Parameters.Select(p => new { p.RefKind, Type = Type(p.Type) }).ToArray(), property.IsStatic },
                IFieldSymbol field => new { Containing = Type(field.ContainingType), Type = Type(field.Type), field.IsStatic },
                IEventSymbol evt => new { Containing = Type(evt.ContainingType), Type = Type(evt.Type), evt.IsStatic },
                ITypeSymbol targetType => Type(targetType),
                _ => null
            };
            // Constructed signatures/type arguments and assembly/project identities remain significant.
            return JsonSerializer.Serialize(new { Origin = SourceOrigin(symbol),
                Assembly = symbol.ContainingAssembly?.Identity.ToString(), Declaration = declaration,
                Shape = shape, Signature = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) });
        }
    }
}


