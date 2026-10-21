using System.Security.Cryptography;
using System.Text.Json;
using CodeAtlas;
using CodeAtlas.Qualification;

if (args.Length == 4 && args[0] == "--purpose")
{
    await PurposeQualification.RunAsync(args[1],args[2],args[3]);
    return;
}

if (args.Length == 1 && args[0] == "--show-hashes")
{
    string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    Console.WriteLine(JsonSerializer.Serialize(new {
        executableSha256=Hash(typeof(DuplicateDetector).Assembly.Location),
        harnessSha256=Hash(typeof(PublicQualification).Assembly.Location),
        detectorVersion=DuplicateDetector.Version,
        limits=new DuplicateLimits()
    },new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true }));
    return;
}
if (args.Length != 3) throw new ArgumentException("Usage: <frozen-public-directory> <reviewer-accepted-config.json> <new-output.json>, --purpose <frozen-public-directory> <reviewer-accepted-config.json> <new-output.json>, or --show-hashes");
await PublicQualification.ExportAsync(args[0],args[1],args[2]);
