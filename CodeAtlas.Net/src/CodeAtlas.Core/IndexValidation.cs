using System.Diagnostics.CodeAnalysis;

namespace CodeAtlas;

public static partial class IndexStore
{
    private static void Validate(CodebaseIndex? index, bool requireCatalogs)
    {
        ValidateIdentity(index);
        ValidateCollections(index);
        if (requireCatalogs) ValidateCatalogs(index);
    }

    private static void ValidateIdentity([NotNull] CodebaseIndex? index)
    {
        if (index?.InputManifest is { } manifest) SnapshotInputModel.Validate(manifest);
        if (index is null) ThrowInvalidContents();
        if (index.VirtualSnapshot is not null)
            throw new IndexFormatException("invalid_index", "A virtual benchmark snapshot cannot be loaded or published as a disk index.");
        if (index.SchemaVersion is not (1 or 2 or SchemaVersion))
            throw new IndexFormatException("unsupported_index", "Unsupported index version. Rebuild the index with this version of CodeAtlas.");
    }

    private static void ValidateCollections(CodebaseIndex index)
    {
        if (!ValidSourceIdentity(index)) ThrowInvalidContents();
        if (!HasCollections(index)) ThrowInvalidContents();
        ValidateContent(index);
    }

    private static bool ValidSourceIdentity(CodebaseIndex index) =>
        !string.IsNullOrWhiteSpace(index.IndexId) && Path.IsPathFullyQualified(index.Solution);

    private static void ValidateContent(CodebaseIndex index)
    {
        if (!ValidHeader(index) || !ValidInputPaths(index) || !ValidFingerprints(index) || !ValidProjects(index)
            || !ValidTypes(index) || !ValidMethods(index) || !ValidReferences(index)) ThrowInvalidContents();
    }

    private static bool HasCollections(CodebaseIndex index) => index.Warnings is not null && index.Projects is not null
        && index.Types is not null && index.Methods is not null && index.References is not null && index.InputRoots is not null
        && index.ExplicitInputs is not null && index.Fingerprints is not null;

    private static bool ValidHeader(CodebaseIndex index) => index.InputRoots.Length != 0 && index.Fingerprints.Count != 0
        && index.Warnings.All(warning => warning is not null);

    private static bool ValidInputPaths(CodebaseIndex index) => index.InputRoots.Concat(index.ExplicitInputs)
        .All(path => HasText(path) && Path.IsPathFullyQualified(path));

    private static bool ValidFingerprints(CodebaseIndex index) => index.Fingerprints.All(pair => Path.IsPathFullyQualified(pair.Key)
        && pair.Value is { Length: 64 } && pair.Value.All(Uri.IsHexDigit));

    private static bool ValidProjects(CodebaseIndex index) => index.Projects.All(project => project is not null
        && HasText(project.Path) && HasText(project.Name) && project.Dependencies is not null && project.Files is not null
        && project.Dependencies.Concat(project.Files).All(HasText));

    private static bool ValidTypes(CodebaseIndex index) => index.Types.All(type => type is not null && HasText(type.Id)
        && HasText(type.Name) && HasText(type.Kind) && HasText(type.Project) && ValidPosition(type.Location))
        && index.Types.Select(type => type.Id).Distinct(StringComparer.Ordinal).Count() == index.Types.Length;

    private static bool ValidMethods(CodebaseIndex index) => index.Methods.All(method => method is not null && HasText(method.Id)
        && HasText(method.Name) && HasText(method.Signature) && HasText(method.Project) && ValidPosition(method.Location)
        && index.References.ContainsKey(method.Id))
        && index.Methods.Select(method => method.Id).Distinct(StringComparer.Ordinal).Count() == index.Methods.Length;

    private static bool ValidReferences(CodebaseIndex index) => index.References.Count == index.Methods.Length
        && index.References.All(pair => pair.Value is not null && pair.Value.All(reference => reference is not null
            && HasText(reference.DefinitionId) && HasText(reference.Project) && HasText(reference.Kind) && ValidPosition(reference.Location)));

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool ValidPosition(SourcePosition? value) => value is not null && HasText(value.File) && value.Line > 0 && value.Column > 0;

    [DoesNotReturn]
    private static void ThrowInvalidContents() => throw new IndexFormatException("invalid_index", "Invalid index contents. Rebuild the index.");

    private static void ValidateCatalogs(CodebaseIndex index)
    {
        if (index.SchemaVersion >= 2)
        {
            if (index.Symbols is null)
                throw new IndexFormatException("invalid_index", "The documentation catalog is missing. Rebuild the index.");
            DocumentationValidation.Validate(index);
        }
        if (index.SchemaVersion == SchemaVersion) StructuralValidation.Validate(index);
        if (index.Duplicates is not null) DuplicateValidation.Validate(index, index.Duplicates);
    }
}
