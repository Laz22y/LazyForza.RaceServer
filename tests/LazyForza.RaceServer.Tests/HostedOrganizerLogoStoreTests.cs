using System.Security.Cryptography;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class HostedOrganizerLogoStoreTests
{
    private static readonly byte[] TinyPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x00
    ];

    [TestMethod]
    public async Task SavesReadsReloadsAndDeletesValidatedLogo()
    {
        var root = Path.Combine(Path.GetTempPath(), "LazyForza-RaceLogo-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new RaceServerOptions { DataDirectory = root };
            var store = new HostedOrganizerLogoStore(options);
            await using var source = new MemoryStream(TinyPng);
            var saved = await store.SaveAsync(source, "race.png", "image/png", CancellationToken.None);

            Assert.AreEqual("image/png", saved.MimeType);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(TinyPng)), saved.Sha256);
            CollectionAssert.AreEqual(TinyPng, await store.ReadAsync(CancellationToken.None));
            Assert.AreEqual(saved, new HostedOrganizerLogoStore(options).Current);

            await store.DeleteAsync(CancellationToken.None);
            Assert.IsNull(store.Current);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RejectsUnsupportedImageSignature()
    {
        var root = Path.Combine(Path.GetTempPath(), "LazyForza-RaceLogo-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HostedOrganizerLogoStore(new RaceServerOptions { DataDirectory = root });
            await using var source = new MemoryStream([0x47, 0x49, 0x46, 0x38]);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                store.SaveAsync(source, "bad.gif", "image/gif", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
