using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace UnifiMcp.Tests;

/// <summary>
/// What a call may cost upstream: the bytes of one answer, the deadline on reading it, how many requests are at the
/// console at once, and what one composite answer may read in all. Every console here is synthetic.
/// </summary>
public sealed class UpstreamBudgetTests : IDisposable
{
    private const string Classic = "/proxy/network/api/s/default";
    private const string Integration = "/proxy/network/integration";
    private const long MiB = 1024 * 1024;

    private readonly string _dir = Directory.CreateTempSubdirectory("unifi-budget").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private ConsoleTransport Transport(HttpMessageHandler console, TimeSpan? deadline = null) =>
        new(Harness.NewSettings(_dir), console, deadline);

    private UnifiClient Client(HttpMessageHandler console) => new(Harness.NewSettings(_dir), Transport(console));

    private static string Text(CallToolResult r) => ((TextContentBlock)Assert.Single(r.Content)).Text;

    // --- one answer ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_answer_without_a_declared_length_is_read_only_up_to_the_limit()
    {
        var console = new ScriptedConsole(_ => new GeneratedStream(40 * MiB));
        var ex = await Assert.ThrowsAsync<UnifiError>(() => Transport(console).SendAsync(HttpMethod.Get, Classic + "/stat/device", null, default));

        Assert.Contains("larger than 16 MB", ex.Message);
        Assert.Contains("/stat/device", ex.Message);
        Assert.True(console.Served <= ConsoleTransport.MaxResponseBytes + MiB, $"{console.Served} bytes read");
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_refused_before_a_byte_is_read()
    {
        var console = new ScriptedConsole(_ => new GeneratedStream(ConsoleTransport.MaxResponseBytes + 1), declareLength: true);
        var ex = await Assert.ThrowsAsync<UnifiError>(() => Transport(console).SendAsync(HttpMethod.Get, Classic + "/stat/device", null, default));

        Assert.Contains("larger than 16 MB", ex.Message);
        Assert.Equal(0, console.Served);
    }

    [Fact]
    public async Task An_answer_within_the_limit_is_read_whole()
    {
        var console = new ScriptedConsole(_ => new GeneratedStream(MiB));
        var r = await Transport(console).SendAsync(HttpMethod.Get, Classic + "/stat/device", null, default);

        Assert.True(r.Ok);
        Assert.Equal((int)MiB, r.Body.Length);
    }

    [Fact]
    public async Task An_answer_that_trickles_in_is_cut_off_at_the_deadline()
    {
        // Headers come at once; the body never ends, one byte at a time. The deadline covers reading the body too.
        var console = new ScriptedConsole(_ => new GeneratedStream(long.MaxValue, chunk: 1, pause: TimeSpan.FromMilliseconds(10)));
        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<UnifiError>(() =>
            Transport(console, TimeSpan.FromMilliseconds(500)).SendAsync(HttpMethod.Get, Classic + "/stat/device", null, default));

        Assert.Contains("did not answer in time", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    // --- requests at once ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Requests_beyond_the_shared_limit_wait_their_turn()
    {
        var release = new TaskCompletionSource();
        var console = new ScriptedConsole(_ => Json("{}")) { Hold = release.Task };
        var transport = Transport(console);

        var calls = Enumerable.Range(0, 3 * ConsoleTransport.MaxInFlight)
            .Select(i => transport.SendAsync(HttpMethod.Get, $"{Integration}/v1/info?n={i}", null, default)).ToList();
        try
        {
            await console.WaitForInFlightAsync(ConsoleTransport.MaxInFlight);
            await Task.Delay(200);
            Assert.Equal(ConsoleTransport.MaxInFlight, console.InFlight);
        }
        finally
        {
            release.SetResult();
        }
        Assert.All(await Task.WhenAll(calls), r => Assert.True(r.Ok));
        Assert.Equal(ConsoleTransport.MaxInFlight, console.MostInFlight);
        Assert.Equal(calls.Count, console.Requests);
    }

    [Fact]
    public async Task A_request_that_gets_no_turn_before_its_deadline_says_the_console_is_busy()
    {
        var release = new TaskCompletionSource();
        var console = new ScriptedConsole(_ => Json("{}")) { Hold = release.Task };
        var transport = Transport(console, TimeSpan.FromMilliseconds(500));

        var held = Enumerable.Range(0, ConsoleTransport.MaxInFlight)
            .Select(_ => transport.SendAsync(HttpMethod.Get, Integration + "/v1/info", null, default)).ToList();
        try
        {
            await console.WaitForInFlightAsync(ConsoleTransport.MaxInFlight);
            var ex = await Assert.ThrowsAsync<UnifiError>(() =>
                transport.SendAsync(HttpMethod.Get, Integration + "/v1/info", null, default).WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Contains("busy", ex.Message);
            Assert.Equal(ConsoleTransport.MaxInFlight, console.Requests);
        }
        finally
        {
            release.SetResult();
            await Task.WhenAll(held.Select(h => h.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    // --- paged lists ----------------------------------------------------------------------------------------------------

    /// <summary>A paged list whose total the console reports from <paramref name="total"/>, given the offset asked for.</summary>
    private static ScriptedConsole Paged(Func<int, long> total, int perPage = 200, string? extra = null) => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("/v1/sites", StringComparison.Ordinal))
            return Json(FakeUnifi.Page($$"""{"id":"{{Harness.SiteId}}","internalReference":"default","name":"Default"}"""));
        if (path.EndsWith("/v1/info", StringComparison.Ordinal)) return Json("""{"applicationVersion":"11.0.97"}""");
        var offset = int.Parse(Query(req, "offset") ?? "0");
        var all = total(offset);
        var count = (int)Math.Clamp(all - offset, 0, perPage);
        var items = Enumerable.Range(offset, count).Select(i => $$"""{"id":"{{i}}","name":"item {{i}}","type":"WIRED","state":"ONLINE"{{extra}}}""");
        return Json($$"""{"offset":{{offset}},"limit":200,"count":{{count}},"totalCount":{{all}},"data":[{{string.Join(",", items)}}]}""");
    });

    private static string? Query(HttpRequestMessage req, string name) =>
        req.RequestUri!.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).FirstOrDefault(p => p[0] == name)?[1];

    [Fact]
    public async Task A_large_site_is_read_to_the_item_limit_and_the_answer_says_it_is_partial()
    {
        var console = Paged(_ => 50_000);
        var client = Client(console);
        List<JsonElement>? list = null;
        var text = Text(await Answer.RunAsync(async a => a.Line($"{(list = await client.GetAllAsync($"/v1/sites/{Harness.SiteId}/clients", default)).Count} read")));

        Assert.Equal(UnifiClient.MaxItems, list!.Count);
        Assert.Equal(UnifiClient.MaxItems / 200, console.Requests);
        Assert.StartsWith("**Partial answer:**", text);
        Assert.Contains("5000 of 50000", text);
        Assert.Contains("Narrow", text);
    }

    [Fact]
    public async Task A_page_count_that_keeps_growing_still_ends()
    {
        // Each page says there are more than before; past 2,000 requests the console gives up, so a loop still ends.
        var calls = 0;
        var growing = Paged(offset => Interlocked.Increment(ref calls) > 2_000 ? 0 : offset + 1_000);
        var list = await Client(growing).GetAllAsync($"/v1/sites/{Harness.SiteId}/clients", default);

        Assert.Equal(UnifiClient.MaxItems, list.Count);
        Assert.True(growing.Requests <= UnifiClient.MaxItems / 200, $"{growing.Requests} requests");
    }

    [Fact]
    public async Task A_complete_list_is_not_called_partial()
    {
        var console = Paged(_ => 450);
        var text = Text(await Answer.RunAsync(async a => a.Line($"{(await Client(console).GetAllAsync("/v1/sites/x/clients", default)).Count} read")));

        Assert.Equal("450 read" + Environment.NewLine, text);
        Assert.DoesNotContain("Partial", text);
    }

    // --- composite answers --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Network_health_on_a_large_site_is_bounded_in_requests_bytes_and_text()
    {
        var console = Paged(_ => 50_000);
        var client = Client(console);
        var result = await new FaqTools(client, new ConsoleReads(client, TimeProvider.System)).NetworkHealth();
        var text = Text(result);

        // The sites, the console's version, and two lists each read to the item limit.
        Assert.True(console.Requests <= 2 + 2 * UnifiClient.MaxItems / 200, $"{console.Requests} requests");
        Assert.True(console.Served <= UpstreamBudget.MaxBytes, $"{console.Served} bytes");
        Assert.True(text.Length <= ToolText.Limit + 200, $"{text.Length} characters");
        Assert.StartsWith("**Partial answer:**", text);
        Assert.Contains($"/v1/sites/{Harness.SiteId}/devices", text);
        Assert.Contains($"/v1/sites/{Harness.SiteId}/clients", text);
        Assert.Contains("[truncated:", text);
        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task An_answer_stops_at_its_request_budget()
    {
        // 300 networks, each with its own detail read: more requests than one answer may make.
        var console = Paged(offset => 300);
        var client = Client(console);
        var text = Text(await new FaqTools(client, new ConsoleReads(client, TimeProvider.System)).WhoIsOnNetwork());

        Assert.Equal(UpstreamBudget.MaxRequests, console.Requests);
        Assert.StartsWith("**Partial answer:**", text);
        Assert.Contains($"its {UpstreamBudget.MaxRequests} requests", text);
        Assert.Contains("**Networks step failed:**", text);
    }

    [Fact]
    public async Task An_answer_stops_at_its_byte_budget()
    {
        // Pages of ten 1 MiB items: under the limit for one answer each, but five of them are more than one call may read.
        var pad = new string('p', (int)MiB);
        var console = Paged(_ => 50, perPage: 10, extra: $$""","pad":"{{pad}}" """);
        var client = Client(console);
        var text = Text(await new FaqTools(client, new ConsoleReads(client, TimeProvider.System)).NetworkHealth());

        Assert.True(console.Served <= UpstreamBudget.MaxBytes + MiB, $"{console.Served} bytes");
        Assert.Contains("32 MB", text);
        Assert.StartsWith("**Partial answer:**", text);
    }

    [Fact]
    public async Task The_device_view_of_a_large_site_is_bounded_and_says_how_to_narrow()
    {
        var devices = string.Join(",", Enumerable.Range(0, 3_000).Select(i =>
            $$"""{"_id":"d{{i}}","mac":"aa:00:00:00:{{i / 256:x2}}:{{i % 256:x2}}","type":"usw","model":"US24","name":"Switch {{i}}","state":1,"version":"7.4.1","uptime":3600}"""));
        var console = new ScriptedConsole(req => req.RequestUri!.AbsolutePath.EndsWith("/v1/sites", StringComparison.Ordinal)
            ? Json(FakeUnifi.Page($$"""{"id":"{{Harness.SiteId}}","internalReference":"default","name":"Default"}"""))
            : Json($$"""{"meta":{"rc":"ok"},"data":[{{devices}}]}"""));
        var client = Client(console);
        var result = await new DiagnosticTools(client, new ConsoleReads(client, TimeProvider.System), TimeProvider.System).DeviceDiagnostics();

        var meta = result.Meta?[ViewResources.ViewMetaKey];
        Assert.NotNull(meta);
        Assert.True(Encoding.UTF8.GetByteCount(meta.ToJsonString()) <= Answer.ViewLimit, $"{meta.ToJsonString().Length} characters");
        Assert.Equal("message", (string?)meta["view"]);
        Assert.Contains("one device", (string?)meta["message"]);
        Assert.True(Text(result).Length <= ToolText.Limit + 2_000, $"{Text(result).Length} characters");
        Assert.Contains("one device", Text(result));
    }

    private sealed record SampleView(string View, string? Message = null, string? Detail = null);

    [Fact]
    public async Task A_view_too_large_to_send_is_replaced_by_the_fallback_or_left_out()
    {
        var big = new SampleView("devices", Detail: new string('d', Answer.ViewLimit));
        var replaced = await Answer.RunAsync(a => { a.View = big; return Task.CompletedTask; }, onFatal: m => new SampleView("message", Message: m));
        var dropped = await Answer.RunAsync(a => { a.View = big; return Task.CompletedTask; });

        Assert.Equal("message", (string?)replaced.Meta?[ViewResources.ViewMetaKey]?["view"]);
        Assert.NotEqual(true, replaced.IsError);
        Assert.Null(dropped.Meta);
        Assert.Contains("too large", Text(dropped));
    }

    private static Stream Json(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));
}

/// <summary>
/// A console that answers from a function, for what <see cref="FakeUnifi"/> cannot give: bodies that are endless, slow
/// or paged by offset. Bodies are sent without a declared length unless asked, as a chunked answer would be, and every
/// byte read from them is counted.
/// </summary>
internal sealed class ScriptedConsole(Func<HttpRequestMessage, Stream> answer, bool declareLength = false) : HttpMessageHandler
{
    private int _requests, _inFlight, _mostInFlight;
    private long _served;

    public int Requests => Volatile.Read(ref _requests);
    public int InFlight => Volatile.Read(ref _inFlight);
    public int MostInFlight => Volatile.Read(ref _mostInFlight);
    public long Served => Interlocked.Read(ref _served);

    /// <summary>When set, each request waits for it before answering.</summary>
    public Task? Hold { get; init; }

    public async Task WaitForInFlightAsync(int count)
    {
        for (var waited = 0; InFlight < count; waited += 10)
        {
            if (waited > 10_000) throw new TimeoutException($"only {InFlight} in flight");
            await Task.Delay(10);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        var now = Interlocked.Increment(ref _inFlight);
        for (int seen; (seen = MostInFlight) < now && Interlocked.CompareExchange(ref _mostInFlight, now, seen) != seen;) { }
        try
        {
            if (Hold is not null) await Hold; // not cancelled: a held request keeps its turn until released
            var body = answer(request);
            var content = new StreamContent(new Counting(body, n => Interlocked.Add(ref _served, n)));
            if (declareLength) content.Headers.ContentLength = body.Length;
            content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>Hides the inner stream's length and counts what is read.</summary>
    private sealed class Counting(Stream inner, Action<int> read) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Counted(await inner.ReadAsync(buffer, ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Counted(int n)
        {
            read(n);
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>A body made on demand: <paramref name="length"/> bytes of 'x', at most <paramref name="chunk"/> per read, after an optional pause.</summary>
internal sealed class GeneratedStream(long length, int chunk = 64 * 1024, TimeSpan pause = default) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (pause > TimeSpan.Zero) await Task.Delay(pause, ct);
        return Fill(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private int Fill(Span<byte> buffer)
    {
        var n = (int)Math.Min(Math.Min(buffer.Length, chunk), length - _position);
        buffer[..n].Fill((byte)'x');
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
