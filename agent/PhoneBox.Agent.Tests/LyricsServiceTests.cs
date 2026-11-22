using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Lyrics from LRCLIB: asked only for a Synced lyrics widget with its lookup on, once per song, the best result picked,
/// and nothing asked for a while after a failure.
/// </summary>
public sealed class LyricsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-lyrics-" + Guid.NewGuid().ToString("N"));

    public LyricsServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ConfigStore Config(string widget, bool lookup = true)
    {
        var path = Path.Combine(_dir, $"{widget}-{lookup}.json");
        var on = lookup ? "true" : "false";
        File.WriteAllText(path, $$$"""{"pages":[{"id":"p","title":"P","enabled":true,"widgets":[{"id":"w","type":"{{{widget}}}","x":0,"y":0,"w":6,"h":4,"options":{"lookup":{{{on}}}}}]}]}""");
        return new ConfigStore(path);
    }

    /// <summary>What the widget "p/w" of <see cref="Config"/> gets for <paramref name="media"/>.</summary>
    private static Task<LyricsResult> Ask(LyricsService lyrics, MediaSnapshot? media, string? widget = "p/w") =>
        lyrics.ForAsync(media, null, widget, CancellationToken.None);

    private sealed class Lrclib(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Asked) Asked.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static MediaSnapshot Track(string title, string artist, string album = "", long durMs = 200_000) =>
        new("Spotify", title, artist, album, true, 1000, durMs, 0, true);

    [Fact]
    public async Task NothingIsAskedWithoutALyricsWidget()
    {
        var lrclib = new Lrclib(_ => Json("[]"));
        using var lyrics = new LyricsService(Config("clock"), NullLogger<LyricsService>.Instance, lrclib);
        Assert.Equal("off", (await Ask(lyrics, Track("Song", "Artist"))).Status);
        Assert.Empty(lrclib.Asked);
    }

    [Fact]
    public async Task NothingIsAskedOnceTheWidgetTurnsTheLookupOff()
    {
        var lrclib = new Lrclib(_ => Json("[]"));
        using var lyrics = new LyricsService(Config("lyrics", lookup: false), NullLogger<LyricsService>.Instance, lrclib);
        Assert.False(lyrics.Wanted(null, "p/w"));
        Assert.Equal("off", (await Ask(lyrics, Track("Song", "Artist"))).Status);
        Assert.Empty(lrclib.Asked);
    }

    [Fact]
    public async Task NothingPlayingIsIdle()
    {
        using var lyrics = new LyricsService(Config("lyrics"), NullLogger<LyricsService>.Instance, new Lrclib(_ => Json("[]")));
        Assert.Equal("idle", (await Ask(lyrics, null)).Status);
        Assert.Equal("idle", (await Ask(lyrics, Track("  ", "Artist"))).Status);
    }

    [Fact]
    public async Task TheExactMatchIsUsedAndRememberedForTheSong()
    {
        var lrclib = new Lrclib(req => req.RequestUri!.AbsolutePath == "/api/get"
            ? Json("""{"id":1,"trackName":"Song","artistName":"Artist","duration":200,"instrumental":false,"plainLyrics":"one\ntwo","syncedLyrics":"[00:01.00] one\n[00:02.50] two"}""")
            : Json("[]"));
        using var lyrics = new LyricsService(Config("lyrics"), NullLogger<LyricsService>.Instance, lrclib);
        var first = await Ask(lyrics, Track("Song", "Artist", "Album", 200_400));
        Assert.Equal("ok", first.Status);
        Assert.StartsWith("[00:01.00] one", first.Synced);
        Assert.Equal("one\ntwo", first.Plain);
        Assert.Equal("Song", first.Title);
        var again = await Ask(lyrics, Track("Song", "Artist", "Album", 200_400));
        Assert.Equal(first.Synced, again.Synced);
        var asked = Assert.Single(lrclib.Asked);
        Assert.Contains("track_name=Song", asked);
        Assert.Contains("album_name=Album", asked);
        Assert.Contains("duration=200", asked);
    }

    [Fact]
    public async Task WithoutAnAlbumTheSearchPicksTimedLyricsOfTheClosestLength()
    {
        var lrclib = new Lrclib(_ => Json("""
            [
              {"id":1,"duration":200,"instrumental":false,"plainLyrics":"plain only","syncedLyrics":null},
              {"id":2,"duration":260,"instrumental":false,"plainLyrics":"far","syncedLyrics":"[00:01.00] far"},
              {"id":3,"duration":202,"instrumental":false,"plainLyrics":"near","syncedLyrics":"[00:01.00] near"},
              {"id":4,"duration":201,"instrumental":false,"plainLyrics":null,"syncedLyrics":null}
            ]
            """));
        using var lyrics = new LyricsService(Config("lyrics"), NullLogger<LyricsService>.Instance, lrclib);
        var result = await Ask(lyrics, Track("Never Gonna Give You Up (Official Video)", "Rick Astley - Topic"));
        Assert.Equal("ok", result.Status);
        Assert.Equal("[00:01.00] near", result.Synced);
        Assert.Equal("Rick Astley", result.Artist);
        Assert.Equal("Never Gonna Give You Up", result.Title);
        Assert.StartsWith("/api/search?", Assert.Single(lrclib.Asked));
    }

    [Fact]
    public async Task AMissFromTheExactMatchFallsBackToTheSearchAndThenToNotFound()
    {
        var lrclib = new Lrclib(req => req.RequestUri!.AbsolutePath == "/api/get"
            ? Json("""{"code":404,"name":"TrackNotFound"}""", HttpStatusCode.NotFound)
            : Json("[]"));
        using var lyrics = new LyricsService(Config("lyrics"), NullLogger<LyricsService>.Instance, lrclib);
        Assert.Equal("notfound", (await Ask(lyrics, Track("Song", "Artist", "Album"))).Status);
        Assert.Equal(2, lrclib.Asked.Count);
    }

    [Fact]
    public async Task AnErrorIsReportedAndNotRememberedForLong()
    {
        long now = 0;
        var calls = 0;
        var lrclib = new Lrclib(_ => { calls++; return Json("oops", HttpStatusCode.InternalServerError); });
        using var lyrics = new LyricsService(Config("lyrics"), NullLogger<LyricsService>.Instance, lrclib, () => now);
        Assert.Equal("error", (await Ask(lyrics, Track("Song", "Artist"))).Status);
        Assert.Equal("error", (await Ask(lyrics, Track("Song", "Artist"))).Status);
        Assert.Equal(1, calls);
        now += 61_000;
        await Ask(lyrics, Track("Song", "Artist"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void InstrumentalTracksCountAsFound()
    {
        using var doc = JsonDocument.Parse("""{"instrumental":true,"plainLyrics":null,"syncedLyrics":null}""");
        var result = LyricsService.FromEntry(doc.RootElement)!;
        Assert.True(result.Instrumental);
        Assert.Null(result.Synced);
    }

    [Theory]
    [InlineData("Song", "Artist", "Song", "Artist")]
    [InlineData("Artist - Song (Official Music Video)", "ArtistVEVO", "Song", "Artist")]
    [InlineData("Artist - Song [Lyrics]", "", "Song", "Artist")]
    [InlineData("Song - Remastered 2011", "The Beatles", "Song - Remastered 2011", "The Beatles")]
    [InlineData("Song (Live)", "Band - Topic", "Song (Live)", "Band")]
    public void TitlesAreCleanedForTheSearch(string title, string artist, string expectedTitle, string expectedArtist)
    {
        var (t, a) = LyricsService.CleanQuery(title, artist);
        Assert.Equal(expectedTitle, t);
        Assert.Equal(expectedArtist, a);
    }

    [Fact]
    public async Task EachWidgetDecidesForItself()
    {
        var path = Path.Combine(_dir, "two.json");
        File.WriteAllText(path, """
            {"pages":[{"id":"p","title":"P","enabled":true,"widgets":[
              {"id":"on","type":"lyrics","x":0,"y":0,"w":6,"h":4,"options":{"lookup":true}},
              {"id":"off","type":"lyrics","x":6,"y":0,"w":6,"h":4,"options":{"lookup":false}},
              {"id":"plain","type":"lyrics","x":0,"y":4,"w":6,"h":4}]},
              {"id":"q","title":"Q","enabled":false,"widgets":[
              {"id":"on","type":"lyrics","x":0,"y":0,"w":6,"h":4,"options":{"lookup":true}}]}]}
            """);
        var lrclib = new Lrclib(_ => Json("[]"));
        using var lyrics = new LyricsService(new ConfigStore(path), NullLogger<LyricsService>.Instance, lrclib);

        // another widget with the lookup on does not make this one ask LRCLIB
        foreach (var widget in new[] { "p/off", "q/on", "p/missing", "p", "", null })
            Assert.Equal("off", (await Ask(lyrics, Track("Song", "Artist"), widget)).Status);
        Assert.Empty(lrclib.Asked);

        Assert.Equal("notfound", (await Ask(lyrics, Track("Song", "Artist"), "p/on")).Status);
        Assert.Single(lrclib.Asked);
        // a widget whose settings don't mention the option has it on (the catalog's default)
        Assert.True(lyrics.Wanted(null, "p/plain"));
        Assert.Equal("notfound", (await Ask(lyrics, Track("Other", "Artist"), "p/plain")).Status);
        Assert.Equal(2, lrclib.Asked.Count);
        Assert.False(lyrics.Wanted(null, "p/off"));
    }

    [Fact]
    public async Task FailuresInARowKeepLrclibAloneForLongerEachTime()
    {
        long now = 0;
        var lrclib = new Lrclib(_ => Json("oops", HttpStatusCode.ServiceUnavailable));
        using var lyrics = new LyricsService(Config("lyrics"), NullLogger<LyricsService>.Instance, lrclib, () => now);

        Assert.Equal("error", (await Ask(lyrics, Track("A", "Artist"))).Status);
        Assert.Single(lrclib.Asked);
        // during the back-off, another song does not reach LRCLIB either
        Assert.Equal("error", (await Ask(lyrics, Track("B", "Artist"))).Status);
        Assert.Single(lrclib.Asked);

        now = LyricsService.FirstBackoffMs + 1;
        Assert.Equal("error", (await Ask(lyrics, Track("A", "Artist"))).Status);
        Assert.Equal(2, lrclib.Asked.Count);
        // the second failure in a row doubles the wait
        now += LyricsService.FirstBackoffMs + 1;
        await Ask(lyrics, Track("C", "Artist"));
        Assert.Equal(2, lrclib.Asked.Count);
        now += LyricsService.FirstBackoffMs + 1;
        await Ask(lyrics, Track("D", "Artist"));
        Assert.Equal(3, lrclib.Asked.Count);
    }

    [Fact]
    public async Task RetryAfterFromLrclibIsHonoured()
    {
        long now = 0;
        var lrclib = new Lrclib(_ =>
        {
            var res = Json("""{"message":"slow down"}""", HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return res;
        });
        using var lyrics = new LyricsService(Config("lyrics"), NullLogger<LyricsService>.Instance, lrclib, () => now);
        Assert.Equal("error", (await Ask(lyrics, Track("A", "Artist"))).Status);
        now = 9 * 60_000;
        await Ask(lyrics, Track("B", "Artist"));
        Assert.Single(lrclib.Asked);
        now = 10 * 60_000 + 1;
        await Ask(lyrics, Track("C", "Artist"));
        Assert.Equal(2, lrclib.Asked.Count);
    }

    [Fact]
    public async Task ASuccessEndsTheBackOff()
    {
        long now = 0;
        var down = true;
        var lrclib = new Lrclib(_ => down ? Json("oops", HttpStatusCode.BadGateway) : Json("[]"));
        using var lyrics = new LyricsService(Config("lyrics"), NullLogger<LyricsService>.Instance, lrclib, () => now);
        await Ask(lyrics, Track("A", "Artist"));
        down = false;
        now = LyricsService.FirstBackoffMs + 1;
        Assert.Equal("notfound", (await Ask(lyrics, Track("B", "Artist"))).Status);
        // the next failure waits the first step again, not twice as long
        down = true;
        await Ask(lyrics, Track("C", "Artist"));
        now += LyricsService.FirstBackoffMs + 1;
        await Ask(lyrics, Track("D", "Artist"));
        Assert.Equal(4, lrclib.Asked.Count);
    }
}
