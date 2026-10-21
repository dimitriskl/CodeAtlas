using System.Security.Cryptography;
using System.Text.Json;
using CodeAtlas;

namespace CodeAtlas.Qualification;

internal static class PurposeQualification
{
    private static readonly JsonSerializerOptions Json = new() {PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
    internal static async Task RunAsync(string corpusDirectory,string acceptanceFile,string outputFile)
    {
        using var accepted=JsonDocument.Parse(await File.ReadAllBytesAsync(acceptanceFile));
        var a=accepted.RootElement;
        string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if(!a.GetProperty("accepted").GetBoolean() || a.GetProperty("executableSha256").GetString()!=Hash(typeof(DuplicateDetector).Assembly.Location)
            || a.GetProperty("harnessSha256").GetString()!=Hash(typeof(PurposeQualification).Assembly.Location)
            || a.GetProperty("purposeProtocolSha256").GetString()!=Hash(Path.Combine(AppContext.BaseDirectory,"../../../PURPOSE-PROTOCOL.md")))throw new InvalidOperationException("Root must accept exact final binaries and purpose protocol before probe.");
        using var freeze=JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(corpusDirectory,"FREEZE.json")));
        foreach(var file in new[]{"Corpus.cs","labels.json","PROTOCOL.md"})
            if(Hash(Path.Combine(corpusDirectory,file))!=freeze.RootElement.GetProperty("files").GetProperty(file).GetString())throw new InvalidOperationException("Frozen corpus changed.");
        using var fixture=PublicFixture.Create(await File.ReadAllTextAsync(Path.Combine(corpusDirectory,"Corpus.cs")));
        var symbols=await DocumentationCatalog.CreateAsync(fixture.Solution,fixture.SolutionPath);
        var facts=await StructuralCatalogBuilder.CreateAsync(fixture.Solution,fixture.SolutionPath,symbols);
        var duplicates=await DuplicateFinder.CreateAsync(fixture.Solution,fixture.SolutionPath);
        var rows=new List<object>();
        bool gate=true;
        foreach(var (selectedName,otherName,positive) in new[]{("JoinA","JoinB",true),("ReleaseA","ReleaseB",false)})
        {
            var selected=symbols.Single(s=>s.Name==selectedName && s.Kind=="method");
            var other=symbols.Single(s=>s.Name==otherName && s.Kind=="method");
            var result=PurposeOverlap.Build(symbols,facts,duplicates,"direct-api",selected.Id,new QueryRequest(Limit:50));
            var item=result.Page.Items.SingleOrDefault(c=>c.Right.SymbolId==other.Id);
            int? rank=item is null?null:Array.FindIndex(result.Page.Items,c=>c.Right.SymbolId==other.Id)+1;
            bool exactPair=duplicates.Scan!.Candidates.Any(c=>(c.Left.SymbolId==selected.Id && c.Right.SymbolId==other.Id || c.Right.SymbolId==selected.Id && c.Left.SymbolId==other.Id)
                && c.Kind is "exact-structure" or "renamed-structure");
            bool passes=positive?rank is <=5:item is null || item.Kind=="similar-purpose-candidate" && item.Caveats.Any(c=>c.Contains("No behavioral-equivalence",StringComparison.Ordinal)) && !exactPair;
            gate &= passes;
            rows.Add(new{selectedName,otherName,label=positive?"useful-related-implementation":"misleading-same-documentation-different-rule",rank,passes,
                importantSourceDifference=positive?"Only parameter/local renaming; same bound loop/string operations.":"ReleaseA rejects !approved and requires nonpositive outstanding-2; ReleaseB immediately accepts approved and otherwise tests outstanding+2 with opposite conditions.",result});
        }
        await using var output=new FileStream(outputFile,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        await JsonSerializer.SerializeAsync(output,new{protocol="m10-purpose-public-v1",acceptedProbeGatePassed=gate,
            executableSha256=Hash(typeof(DuplicateDetector).Assembly.Location),harnessSha256=Hash(typeof(PurposeQualification).Assembly.Location),
            acceptanceSha256=Hash(acceptanceFile),sourceSha256=Hash(Path.Combine(corpusDirectory,"Corpus.cs")),
            labelsSha256=Hash(Path.Combine(corpusDirectory,"labels.json")),purposeProtocolSha256=Hash(Path.Combine(AppContext.BaseDirectory,"../../../PURPOSE-PROTOCOL.md")),
            cases=rows,limitations="Two frozen public probes only; direct API context unverified; no private usefulness/general accuracy/production snapshot claim."},Json);
    }
}
