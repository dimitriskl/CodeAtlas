using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class DuplicateMultiContextTests
{
    private const string Source = """
        public static class Example {
          public static int First(int[] values) { int total=0; foreach(int value in values) total+=value*2; if(total<0) return -1; return total; }
          public static int Second(int[] values) { int total=0; foreach(int value in values) total+=value*2; if(total<0) return -1; return total; }
        }
        """;

    [Fact]
    public async Task ActualMultiTargetIndexOmitsAmbiguousDuplicateBodiesAndStillPublishes()
    {
        // Arrange
        var directory=Directory.CreateTempSubdirectory("codeatlas-duplicate-multitarget-");
        try
        {
            var solution=Path.Combine(directory.FullName,"Multi.slnx");
            await File.WriteAllTextAsync(solution,"<Solution><Project Path=\"Multi.csproj\" /></Solution>");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName,"Multi.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks></PropertyGroup></Project>");
            var targetSource=Source.Replace("public static class Example {","""
                public static class Example {
                #if WINDOWS
                  public static int Caller(int[] values) => First(values);
                #else
                  public static int Caller(int[] values) => Second(values);
                #endif
                """);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName,"Example.cs"),targetSource);
            var start=new ProcessStartInfo("dotnet") {WorkingDirectory=directory.FullName,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("restore");start.ArgumentList.Add(solution);
            var process=await ChildProcess.RunAsync(start);
            var restoreExitCode=process.ExitCode;
            var restoreOutput=process.Output+process.Error;

            // Act
            var index=await SolutionAnalysis.CreateIndexAsync(solution);
            await IndexStore.SaveAsync(index);
            var roundtrip=await IndexStore.LoadAsync(IndexStore.DefaultPath(solution));

            // Assert
            Assert.True(restoreExitCode==0,restoreOutput);
            Assert.Equal(3,index.Methods.Length);
            Assert.True(index.IncrementalState!.Projects.Count(p=>p.Project=="Multi.csproj")>=2);
            Assert.Empty(roundtrip.Duplicates!.Analysis!.Bodies);
            Assert.Empty(roundtrip.Duplicates.Scan!.Candidates);
            Assert.Equal(3,roundtrip.Duplicates.Analysis.Omissions.Length);
            Assert.All(roundtrip.Duplicates.Analysis.Omissions,o=>Assert.Equal("ambiguous-project-context",o.Reason));
            Assert.Equal(index.Methods.Select(m=>m.Id).Order(),roundtrip.Duplicates.Analysis.Omissions.Select(o=>o.SymbolId).Order());
            Assert.False(roundtrip.Duplicates.Analysis.Coverage.CompleteForRealizedContexts);
            Assert.Contains(roundtrip.InputManifest!.Coverage.Issues,i=>i.Code=="multi_target_partial");
            Assert.Contains(roundtrip.References["Multi.csproj::M:Example.First(System.Int32[])"],reference=>reference.Kind=="invocation");
            Assert.Contains(roundtrip.References["Multi.csproj::M:Example.Second(System.Int32[])"],reference=>reference.Kind=="invocation");
        }
        finally {directory.Delete(recursive:true);}
    }

    [Fact]
    public async Task DirectApiCannotAssignOneHashToTwoLoadedContextsButKeepsUniqueProjects()
    {
        // Arrange
        using var workspace=new AdhocWorkspace();
        var directory=Path.Combine(Path.GetTempPath(),"codeatlas-duplicate-context-public");
        var references=((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p=>MetadataReference.CreateFromFile(p)).ToArray();
        var solution=workspace.CurrentSolution;
        foreach(var name in new[]{"MultiA","MultiB","Unique"})
        {
            var id=ProjectId.CreateNewId();
            var projectFile=name=="Unique"?"Unique.csproj":"Multi.csproj";
            solution=solution.AddProject(ProjectInfo.Create(id,VersionStamp.Create(),name,name,LanguageNames.CSharp,
                filePath:Path.Combine(directory,projectFile),parseOptions:new CSharpParseOptions(LanguageVersion.Preview),
                compilationOptions:new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),metadataReferences:references));
            solution=solution.AddDocument(DocumentId.CreateNewId(id),"Example.cs",SourceText.From(Source,Encoding.UTF8),filePath:Path.Combine(directory,"Example.cs"));
        }

        // Act
        var catalog=await DuplicateFinder.CreateAsync(solution,Path.Combine(directory,"Sample.slnx"));

        // Assert
        Assert.Equal(2,catalog.Analysis!.Bodies.Length);
        Assert.All(catalog.Analysis.Bodies,b=>Assert.Equal("Unique.csproj",b.Project));
        Assert.Equal(2,catalog.Analysis.Omissions.Length);
        Assert.All(catalog.Analysis.Omissions,o=>Assert.StartsWith("Multi.csproj::",o.SymbolId));
        Assert.All(catalog.Analysis.Omissions,o=>Assert.Equal("ambiguous-project-context",o.Reason));
        Assert.Single(catalog.Scan!.Candidates);
        Assert.False(catalog.Scan.Coverage.CompleteForRealizedContexts);
    }
}
