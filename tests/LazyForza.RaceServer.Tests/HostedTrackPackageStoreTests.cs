using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class HostedTrackPackageStoreTests
{
    [TestMethod]
    public async Task SavesMatchesReadsAndDeletesValidatedEstatePackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "LazyForza-RaceServer-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var trackId = Guid.NewGuid();
            const string trackName = "Test Circuit";
            const string revision = "test-revision";
            var trackBytes = Encoding.UTF8.GetBytes("{\"track\":\"test\"}");
            var payloadHash = Convert.ToHexString(SHA256.HashData(trackBytes));
            var package = Package(trackId, trackName, revision, payloadHash, trackBytes);
            var store = new HostedTrackPackageStore(new RaceServerOptions { DataDirectory = root });

            await using var source = new MemoryStream(package);
            var saved = await store.SaveAsync(
                source, "test.lfzestate", trackId.ToString("D"), trackName, revision,
                payloadHash, CancellationToken.None);

            Assert.AreEqual(package.LongLength, saved.SizeBytes);
            Assert.IsNotNull(store.Matching(trackId.ToString("D"), revision, payloadHash));
            CollectionAssert.AreEqual(package, await store.ReadAsync(
                trackId.ToString("D"), revision, payloadHash, CancellationToken.None));
            Assert.IsNull(store.Matching(Guid.NewGuid().ToString("D"), revision, payloadHash));

            await store.DeleteAsync(CancellationToken.None);
            Assert.IsNull(store.Current);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RejectsPackageWhenManifestDoesNotMatchConfiguredIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "LazyForza-RaceServer-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var trackId = Guid.NewGuid();
            var trackBytes = Encoding.UTF8.GetBytes("{}");
            var payloadHash = Convert.ToHexString(SHA256.HashData(trackBytes));
            var package = Package(trackId, "Actual Name", "1", payloadHash, trackBytes);
            var store = new HostedTrackPackageStore(new RaceServerOptions { DataDirectory = root });
            await using var source = new MemoryStream(package);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(
                source, "bad.lfzestate", trackId.ToString("D"), "Wrong Name", "1",
                payloadHash, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Package(
        Guid trackId,
        string trackName,
        string revision,
        string payloadHash,
        byte[] trackBytes)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("manifest.json");
            using (var stream = manifest.Open())
            {
                JsonSerializer.Serialize(stream, new
                {
                    format = "lazyforza-estate-track",
                    formatVersion = 1,
                    trackId,
                    trackName,
                    mapRevision = revision,
                    payloadSha256 = payloadHash
                });
            }
            var track = archive.CreateEntry("track.json");
            using var trackStream = track.Open();
            trackStream.Write(trackBytes);
        }
        return output.ToArray();
    }
}
