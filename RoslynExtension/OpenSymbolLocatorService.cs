using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.PdbSourceDocument;

namespace RoslynExtension;

[Export(typeof(ISourceLinkService)), Shared]
[method: ImportingConstructor]
public class OpenSymbolLocatorService() : ISourceLinkService
{
    public async Task<PdbFilePathResult?> GetPdbFilePathAsync(string dllPath, PEReader peReader, bool useDefaultSymbolServers, CancellationToken cancellationToken)
    {
        if (!useDefaultSymbolServers)
        {
            Console.WriteLine("Sadly I found no way to pass custom configuration values through the language server.");
            return null;
        }

        try
        {
            if (!TryGetCodeViewWithChecksum(peReader, out var codeView, out var checksums))
            {
                return null;
            }

            return await RoslynSymbolStoreClient.GetSymbolFileAsync(codeView.Value, checksums, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unhandled exception occurred while retrieving the symbol file path: {ex}");
            return null;
        }

    }

    public async Task<SourceFilePathResult?> GetSourceFilePathAsync(string url, string relativePath, CancellationToken cancellationToken)
    {
        return await RoslynSourceLinkClient.ResolveSourceLinkContentAsync(url, relativePath, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetCodeViewWithChecksum(PEReader peReader, [NotNullWhen(true)] out CodeViewDebugDirectoryData? codeView, [NotNullWhen(true)] out List<PdbChecksumDebugDirectoryData> checksums)
    {
        var hasCodeViewEntry = false;
        CodeViewDebugDirectoryData codeViewEntry = default;
        checksums = [];
        foreach (var entry in peReader.ReadDebugDirectory())
        {
            if (entry.Type == DebugDirectoryEntryType.PdbChecksum)
            {
                checksums.Add(peReader.ReadPdbChecksumDebugDirectoryData(entry));
            }
            else if (entry.Type == DebugDirectoryEntryType.CodeView && entry.IsPortableCodeView)
            {
                hasCodeViewEntry = true;
                codeViewEntry = peReader.ReadCodeViewDebugDirectoryData(entry);
            }
        }

        if (!hasCodeViewEntry)
        {
            codeView = null;
            return false;
        }

        codeView = codeViewEntry;
        return true;
    }
}

internal static class RoslynSourceLinkClient
{
    private static readonly HttpClient httpClient = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(60), AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly string baseSymbolServerCacheDirectory = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Path.Combine(Path.GetTempPath(), "SymbolCache") : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? string.Empty, ".dotnet", "symbolcache");

    public static async Task<SourceFilePathResult?> ResolveSourceLinkContentAsync(string url, string relativePath, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await httpClient.SendAsync(request, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var content = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        var outputPath = Path.Combine(baseSymbolServerCacheDirectory, relativePath);
        await File.WriteAllBytesAsync(outputPath, content, token).ConfigureAwait(false);

        return new SourceFilePathResult(outputPath);
    }
}

internal static class RoslynSymbolStoreClient
{
    private static readonly HttpClient httpClient = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(60) }) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Uri baseMsdnSymbolServerUri = new("https://msdl.microsoft.com/download/symbols/", UriKind.Absolute);
    private static readonly Uri baseNugetSymbolServerUri = new("https://symbols.nuget.org/download/symbols/", UriKind.Absolute);
    private static readonly string baseSymbolServerCacheDirectory = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Path.Combine(Path.GetTempPath(), "SymbolCache") : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? string.Empty, ".dotnet", "symbolcache");

    public static async ValueTask<PdbFilePathResult?> GetSymbolFileAsync(CodeViewDebugDirectoryData codeView, List<PdbChecksumDebugDirectoryData> checksums, CancellationToken token)
    {
        var symbolFileIndex = CreateSymbolFileIndex(codeView);
        var symbolFilePath = Path.Combine(baseSymbolServerCacheDirectory, symbolFileIndex);
        var symbolCacheDirectory = Path.GetDirectoryName(symbolFilePath);
        if (symbolCacheDirectory is null)
        {
            return null;
        }

        // try to use a cached pdb file
        if (File.Exists(symbolFilePath))
        {
            return new(symbolFilePath);
        }

        // load pdb from symbol server
        var pdbStream = await LoadSymbolFileAsync(symbolFileIndex, checksums, token).ConfigureAwait(false);
        if (pdbStream is null)
        {
            Console.WriteLine("Symbol file not found");
            return null;
        }

        // cache pdb result
        try
        {
            Directory.CreateDirectory(symbolCacheDirectory);
            using FileStream? destinationStream = File.OpenWrite(symbolFilePath);
            await pdbStream.CopyToAsync(destinationStream, token).ConfigureAwait(false);
            await pdbStream.DisposeAsync().ConfigureAwait(false);
            return new(symbolFilePath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is UnauthorizedAccessException || ex is IOException)
        {
            Console.WriteLine("Failure when writing cache entry");
            return null;
        }
    }

    public static async Task<MemoryStream?> LoadSymbolFileAsync(string symbolFileIndex, List<PdbChecksumDebugDirectoryData> checksums, CancellationToken token)
    {
        try
        {
            var needsChecksumCheck = checksums.Count > 0;
            if (!Uri.TryCreate(baseMsdnSymbolServerUri, symbolFileIndex, out var msdnSymbolServerUri) || !Uri.TryCreate(baseNugetSymbolServerUri, symbolFileIndex, out var nugetSymbolServerUri))
                return null;

            using HttpRequestMessage msdnSymbolServerRequest = new(HttpMethod.Get, msdnSymbolServerUri);
            using HttpRequestMessage nugetSymbolServerRequest = new(HttpMethod.Get, nugetSymbolServerUri);
            if (needsChecksumCheck)
            {
                var checksumHeader = string.Join(";", checksums.Select(checksum => $"{checksum.AlgorithmName}:{string.Concat(checksum.Checksum.Select(b => b.ToString("x2")))}"));
                msdnSymbolServerRequest.Headers.Add("SymbolChecksum", checksumHeader);
                nugetSymbolServerRequest.Headers.Add("SymbolChecksum", checksumHeader);
            }

            await foreach (var responseTask in Task.WhenEach([httpClient.SendAsync(msdnSymbolServerRequest, token), httpClient.SendAsync(nugetSymbolServerRequest, token)]).ConfigureAwait(false))
            {
                var response = await responseTask;
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    continue;
                }

                var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                stream.Seek(0, SeekOrigin.Begin);
                if (needsChecksumCheck && !PortablePdbChecksumValidator.Validate(stream, checksums))
                {
                    Console.WriteLine("Invalid symbol file loaded");
                    continue;
                }

                return stream;
            }

            return null;
        }
        catch (HttpRequestException)
        {
            Console.WriteLine("Http request failure");
            return null;
        }
    }

    private static string CreateSymbolFileIndex(CodeViewDebugDirectoryData codeView)
    {
        var fileName = Path.GetFileName(codeView.Path.Replace('\\', '/')).ToLowerInvariant();
        var normalizedFileName = string.Join("/", fileName.Split('/').Select(Uri.EscapeDataString));
        // roslyn only supports portable pdbs (maybe related to https://github.com/dotnet/roslyn/issues/24429)
        // https://github.com/dotnet/diagnostics/blob/main/documentation/symbols/SSQP_Key_Conventions.md#portable-pdb-signature
        return $"{normalizedFileName}/{codeView.Guid:N}FFFFFFFF/{normalizedFileName}";
        
    }

    internal sealed class PortablePdbChecksumValidator
    {
        // https://github.com/dotnet/runtime/blob/main/docs/design/specs/PortablePdb-Metadata.md#pdb-stream
        private const string pdbStreamName = "#Pdb";
        private const uint pdbIdSize = 20;

        private static readonly SHA256 SHA256 = SHA256.Create();
        private static readonly SHA384 SHA384 = SHA384.Create();
        private static readonly SHA512 SHA512 = SHA512.Create();

        internal static bool Validate(Stream pdbStream, IEnumerable<PdbChecksumDebugDirectoryData> pdbChecksums)
        {
            var bytes = new byte[pdbStream.Length];
            var pdbId = new byte[pdbIdSize];
            if (pdbStream.Read(bytes, offset: 0, count: bytes.Length) != bytes.Length || !TryGetPdbStreamOffset(pdbStream, out var offset))
            {
                return false;
            }

            // Make a copy of the pdb Id
            Array.Copy(bytes, offset, pdbId, 0, pdbIdSize);

            // Zero out the pdb Id
            // see https://github.com/dotnet/runtime/blob/main/docs/design/specs/PE-COFF.md#portable-pdb-checksum
            for (var i = 0; i < pdbIdSize; i++)
            {
                bytes[i + offset] = 0;
            }

            foreach (var checksum in pdbChecksums)
            {
                // see https://github.com/dotnet/runtime/blob/main/docs/design/specs/PE-COFF.md#pdb-checksum-debug-directory-entry-type-19
                HashAlgorithm? hashAlgo = checksum.AlgorithmName switch
                {
                    "SHA256" => SHA256,
                    "SHA384" => SHA384,
                    "SHA512" => SHA512,
                    _ => null,
                };

                if (hashAlgo is null)
                {
                    continue;
                }

                var hash = hashAlgo.ComputeHash(bytes);
                if (hash.SequenceEqual(checksum.Checksum))
                {
                    // Restore the pdb Id
                    Array.Copy(pdbId, 0, bytes, offset, pdbIdSize);
                    // Restore the steam position
                    pdbStream.Seek(0, SeekOrigin.Begin);

                    return true;
                }
            }

            Console.WriteLine("ChecksumValidator no match");

            // Restore the pdb Id
            Array.Copy(pdbId, 0, bytes, offset, pdbIdSize);
            // Restore the steam position
            pdbStream.Seek(0, SeekOrigin.Begin);

            return false;
        }

        // see https://ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf#%5B%7B%22num%22%3A2941%2C%22gen%22%3A0%7D%2C%7B%22name%22%3A%22XYZ%22%7D%2C87%2C573%2C0%5D
        private static bool TryGetPdbStreamOffset(Stream pdbStream, out uint offset)
        {
            pdbStream.Position = 0;
            using (BinaryReader reader = new(pdbStream, Encoding.UTF8, leaveOpen: true))
            {
                pdbStream.Seek(4 + // Signature
                               2 + // Version Major
                               2 + // Version Minor
                               4,  // Reserved)
                               SeekOrigin.Begin);

                // skip the version string
                var versionStringSize = reader.ReadUInt32();

                pdbStream.Seek(versionStringSize, SeekOrigin.Current);

                // storage header
                pdbStream.Seek(2, SeekOrigin.Current);

                // read the stream headers
                var streamCount = reader.ReadUInt16();
                uint streamOffset;
                string streamName;

                for (var i = 0; i < streamCount; i++)
                {
                    streamOffset = reader.ReadUInt32();
                    // stream size
                    pdbStream.Seek(4, SeekOrigin.Current);

                    StringBuilder builder = new();
                    char ch;
                    while ((ch = reader.ReadChar()) != 0)
                    {
                        builder.Append(ch);
                    }
                    streamName = builder.ToString();

                    if (streamName == pdbStreamName)
                    {
                        offset = streamOffset;
                        return true;
                    }

                    // streams headers are on a four byte alignment
                    if (pdbStream.Position % 4 != 0)
                    {
                        pdbStream.Seek(4 - pdbStream.Position % 4, SeekOrigin.Current);
                    }
                }
            }

            offset = default;
            return false;
        }
    }
}