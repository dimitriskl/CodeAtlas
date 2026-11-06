using System.Text;
using CodeAtlas;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeAtlas.Qualification;

internal sealed class PublicFixture : IDisposable
{
    private readonly AdhocWorkspace workspace = new();
    private readonly Solution solution;
    private readonly string directory = Path.Combine(Path.GetTempPath(),"codeatlas-m10-public");
    private PublicFixture(string source)
    {
        var projectId=ProjectId.CreateNewId();
        var references=((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? throw new InvalidOperationException("Trusted references unavailable"))
          .Split(Path.PathSeparator).Select(path=>MetadataReference.CreateFromFile(path));
        var info=ProjectInfo.Create(projectId,VersionStamp.Create(),"Library","Library",LanguageNames.CSharp,
          filePath:Path.Combine(directory,"Library","Library.csproj"),parseOptions:new CSharpParseOptions(LanguageVersion.Preview),
          compilationOptions:new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),metadataReferences:references);
        solution=workspace.CurrentSolution.AddProject(info).AddDocument(DocumentId.CreateNewId(projectId),"Corpus.cs",
          SourceText.From(source,Encoding.UTF8),filePath:Path.Combine(directory,"Library","Corpus.cs"));
    }
    internal static PublicFixture Create(string source)=>new(source);
    internal Solution Solution => solution;
    internal string SolutionPath => Path.Combine(directory,"Sample.slnx");
    internal async Task<DuplicateBodyCatalog> ExtractAsync(DuplicateLimits limits)
    {
        var compilation=await solution.Projects.Single().GetCompilationAsync();
        var errors=compilation!.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error).ToArray();
        if(errors.Length!=0) throw new InvalidOperationException(string.Join("\n",errors.Select(e=>e.ToString())));
        return await DuplicateCatalogBuilder.CreateAsync(solution,Path.Combine(directory,"Sample.slnx"),new string('A',64),
          new Dictionary<string,string> { ["Library/Library.csproj"]=new string('B',64) },limits);
    }
    public void Dispose()=>workspace.Dispose();
}
