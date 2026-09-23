using System.IO.Compression;

var files = new[]
{
    "LICENSE",
    "README.md",
    "SECURITY.md",
    "manifest.json",
    "src/SendRepute.MailKit/SendRepute.MailKit.csproj",
    "src/SendRepute.MailKit/Contracts.cs",
    "src/SendRepute.MailKit/MimeNormalizer.cs",
    "src/SendRepute.MailKit/SendReputeClient.cs",
    "src/SendRepute.MailKit/SendReputeMailAdapter.cs",
    "scripts/Package/Package.csproj",
    "scripts/Package/Program.cs"
};

var root = FindRoot();
var outputDirectory = Path.Combine(root, "dist");
Directory.CreateDirectory(outputDirectory);
var output = Path.Combine(outputDirectory, "sendrepute-mailkit-0.1.0-source.zip");
var temporary = output + ".tmp";
File.Delete(temporary);

using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
{
    foreach (var relative in files.Order(StringComparer.Ordinal))
    {
        var source = Path.GetFullPath(Path.Combine(root, relative));
        if (!source.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Allowlisted path escaped package root.");
        var info = new FileInfo(source);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"{relative} is missing or is a link.");
        var entry = archive.CreateEntry($"sendrepute-mailkit-0.1.0/{relative.Replace('\\', '/')}", CompressionLevel.SmallestSize);
        entry.LastWriteTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var input = File.OpenRead(source);
        using var destination = entry.Open();
        input.CopyTo(destination);
    }
}
File.Move(temporary, output, true);
Console.WriteLine(output);

static string FindRoot()
{
    var candidate = Path.GetFullPath(Environment.CurrentDirectory);
    if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
    candidate = Path.Combine(candidate, "integrations", "dotnet");
    if (File.Exists(Path.Combine(candidate, "manifest.json"))) return Path.GetFullPath(candidate);
    throw new InvalidOperationException("Run from integrations/dotnet or the repository root.");
}