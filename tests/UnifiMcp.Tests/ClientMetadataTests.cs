using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace UnifiMcp.Tests;

public sealed class ClientMetadataTests
{
    private const string ClientId = "https://claude.ai/oauth/client.json";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (HttpClientMetadataDocuments Docs, FakeHandler Handler, FakeTimeProvider Time) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var time = new FakeTimeProvider();
        return (new HttpClientMetadataDocuments(new FakeFactory(handler), NullLogger<HttpClientMetadataDocuments>.Instance, time), handler, time);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ValidDoc(string clientId = ClientId) =>
        $$"""{"client_id":"{{clientId}}","client_name":"Claude","redirect_uris":["https://claude.ai/api/mcp/auth_callback"],"token_endpoint_auth_method":"none"}""";

    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("0.1.2.3", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("100.128.0.1", true)]
    public void Non_public_addresses_are_refused(string ip, bool expectedPublic) =>
        Assert.Equal(expectedPublic, HttpClientMetadataDocuments.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public void Document_must_name_itself()
    {
        var doc = new ClientMetadataDocument { ClientId = "https://evil.example/x.json", RedirectUris = ["https://claude.ai/cb"] };
        var problem = HttpClientMetadataDocuments.Validate(doc, ClientId);
        Assert.NotNull(problem);
        Assert.Contains("self-referential", problem);

        doc.ClientId = ClientId;
        Assert.Null(HttpClientMetadataDocuments.Validate(doc, ClientId));
    }

    [Fact]
    public async Task Valid_document_resolves_and_is_cached_for_an_hour()
    {
        var (docs, handler, time) = Build(_ => Json(ValidDoc()));

        var (doc, problem) = await docs.ResolveAsync(ClientId);
        Assert.Null(problem);
        Assert.Equal("claude.ai", doc!.Host);

        await docs.ResolveAsync(ClientId);
        Assert.Equal(1, handler.Calls);

        time.Advance(TimeSpan.FromMinutes(61));
        await docs.ResolveAsync(ClientId);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_document_the_client_rules_refuse_is_not_cached()
    {
        var (docs, handler, _) = Build(_ => Json($$"""{"client_id":"{{ClientId}}","redirect_uris":["https://evil.example/cb"]}"""));

        var (doc, problem) = await docs.ResolveAsync(ClientId);
        Assert.Null(doc);
        Assert.Equal("redirect_uri 'https://evil.example/cb' is not same-origin with the client_id URL.", problem);

        // Fetched again: a publisher who fixes the document is not kept waiting for the cache.
        await docs.ResolveAsync(ClientId);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task The_fetch_names_this_server_in_its_user_agent()
    {
        string? userAgent = null;
        var (docs, _, _) = Build(request =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            return Json(ValidDoc());
        });

        var (_, problem) = await docs.ResolveAsync(ClientId);
        Assert.Null(problem);
        Assert.Equal("unifi-mcp/0.2", userAgent);
    }

    [Fact]
    public async Task Oversized_document_is_refused()
    {
        var padding = new string('a', 65 * 1024);
        var (docs, _, _) = Build(_ => Json($$"""{"client_id":"{{ClientId}}","client_name":"{{padding}}","redirect_uris":["https://claude.ai/cb"]}"""));

        var (doc, problem) = await docs.ResolveAsync(ClientId);
        Assert.Null(doc);
        Assert.Contains("too large", problem);
    }

    [Fact]
    public async Task Redirecting_document_is_refused()
    {
        var (docs, handler, _) = Build(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil.example/x.json");
            return r;
        });

        var (doc, problem) = await docs.ResolveAsync(ClientId);
        Assert.Null(doc);
        Assert.NotNull(problem);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("http://claude.ai/x.json")]
    [InlineData("https://claude.ai/")]
    [InlineData("https://127.0.0.1/x.json")]
    [InlineData("https://claude.ai/x.json#frag")]
    public async Task Non_client_id_urls_are_refused_without_fetching(string url)
    {
        var (docs, handler, _) = Build(_ => Json(ValidDoc(url)));
        var (doc, problem) = await docs.ResolveAsync(url);
        Assert.Null(doc);
        Assert.NotNull(problem);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class HangingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Trickling() => new(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) };

    [Fact]
    public async Task Stalled_body_times_out_as_a_problem()
    {
        var (docs, _, time) = Build(_ => Trickling());

        var pending = docs.ResolveAsync(ClientId);
        time.Advance(TimeSpan.FromSeconds(6));

        var (doc, problem) = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(doc);
        Assert.Contains("timed out", problem);
    }

    [Fact]
    public async Task Caller_cancellation_still_propagates()
    {
        var (docs, _, _) = Build(_ => Trickling());
        using var cts = new CancellationTokenSource();

        var pending = docs.ResolveAsync(ClientId, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}