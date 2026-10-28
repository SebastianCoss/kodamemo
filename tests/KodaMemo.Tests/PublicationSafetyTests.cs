using KodaMemo.Services;
using KodaMemo.Shared.Models;
using KodaMemo.Shared.Services;
using KodaMemo.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace KodaMemo.Tests;

[TestClass]
public sealed class PublicationSafetyTests
{
    [TestMethod]
    public void ValidateEvidence_ExactQuote_DoesNotConfirmItsInterpretation()
    {
        var meeting = new MeetingSessionDto
        {
            Segments = [new() { Text = "Ana no será responsable de enviar el reporte." }],
            Agreements = [new() {
                Description = "Ana será responsable de enviar el reporte.",
                EvidenceQuote = "Ana no será responsable de enviar el reporte."
            }]
        };
        SummaryEvidenceValidator.ValidateMeetingEvidence(meeting);
        Assert.AreEqual(CitationMatchStatus.QuoteFoundExact, meeting.Agreements[0].CitationStatus);
        Assert.AreEqual(UserConfirmationStatus.PendingReview, meeting.Agreements[0].ConfirmationStatus);
    }

    [TestMethod]
    public void Meeting_ChangedTranscript_RequiresNewSummaryReview()
    {
        var meeting = new MeetingSessionDto {
            ActiveTranscriptVersion = 2, SummarySourceTranscriptVersion = 1
        };
        Assert.IsTrue(meeting.IsSummaryStale);
    }

    [TestMethod]
    public void NormalizeForMatch_SpanishAccentsAndPunctuation_RetainsWords()
    {
        Assert.AreEqual("revision del miercoles", SummaryEvidenceValidator.NormalizeForMatch("Revisión del miércoles."));
    }

    [TestMethod]
    public void RepairWav_InterruptedHeader_PreservesRecordedSamples()
    {
        string file = Path.Combine(Path.GetTempPath(), $"kodamemo-test-{Guid.NewGuid():N}.wav");
        byte[] audio = Enumerable.Range(0, 3200).Select(i => (byte)(i % 251)).ToArray();
        try
        {
            File.WriteAllBytes(file, new byte[44].Concat(audio).ToArray());
            var result = WavHeaderHelper.RepairWavFileOnDisk(file);
            byte[] repaired = File.ReadAllBytes(file);
            Assert.IsTrue(result.Repaired);
            Assert.AreEqual(audio.Length, (int)result.DataBytes);
            Assert.AreEqual(0.1, result.DurationSeconds, 0.00001);
            CollectionAssert.AreEqual(audio, repaired.Skip(44).ToArray());
            Assert.AreEqual((uint)audio.Length, BitConverter.ToUInt32(repaired, 40));
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public void Store_MissingCredentials_RejectsDefaultAccess()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            Assert.ThrowsExactly<InvalidOperationException>(() => new WebMeetingStoreService(new TestHost(directory), config));
        }
        finally { DeleteTemporaryDirectory(directory); }
    }

    [TestMethod]
    public void Store_FreshInstall_HasNoPersonalOrSampleMeetings()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var config = CreateConfiguration();
            var store = new WebMeetingStoreService(new TestHost(directory), config);
            Assert.HasCount(0, store.SearchMeetings(null));
            Assert.IsFalse(store.ValidateBearerToken("invalid"));
            Assert.IsFalse(store.ValidateWebLogin("owner", "incorrect"));
        }
        finally { DeleteTemporaryDirectory(directory); }
    }

    [TestMethod]
    public void Store_NewMeeting_PersistsAcrossInstances()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var config = CreateConfiguration();
            var store = new WebMeetingStoreService(new TestHost(directory), config);
            var meeting = new MeetingSessionDto { Title = "Test meeting" };
            store.SyncFromPhonePayload(new SyncPushPayloadDto { Meetings = [meeting] });
            var reloaded = new WebMeetingStoreService(new TestHost(directory), config);
            Assert.AreEqual(meeting.SessionUid, reloaded.SearchMeetings(null).Single().SessionUid);
        }
        finally { DeleteTemporaryDirectory(directory); }
    }

    private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string,string?> {
            ["KodaMemo:WebUsername"] = "test-owner",
            ["KodaMemo:WebPassword"] = Guid.NewGuid().ToString("N"),
            ["KodaMemo:SyncBearerToken"] = Guid.NewGuid().ToString("N")
        }).Build();

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "KodaMemo.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KodaMemo.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        Directory.Delete(path, recursive: true);
    }

    private sealed class TestHost(string directory) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "KodaMemo.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = directory;
        public string WebRootPath { get; set; } = directory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
