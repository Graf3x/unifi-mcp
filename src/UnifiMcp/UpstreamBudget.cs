using System.Collections.Concurrent;
using System.Globalization;

namespace UnifiMcp;

/// <summary>
/// What one composite answer may ask of the console: a number of requests and a number of bytes, shared by every read
/// the answer makes. <see cref="Answer.RunAsync"/> opens one for each answer, and it travels with the answer's async
/// flow, so every read is charged without each tool passing it along. The transport charges each request and its bytes;
/// a paged list notes where it stopped early. The answer then starts by saying it is partial and how to narrow it.
/// </summary>
/// <remarks>
/// A tool call that is not a composite answer (one generated operation) has no budget of its own: its request is one
/// console answer, held to <see cref="ConsoleTransport.MaxResponseBytes"/>, and the site lookup before it opens its own
/// budget (<see cref="UnifiClient.FindSiteAsync(string?, bool, CancellationToken)"/>). The numbers are well above what a home site needs: a list is read
/// to <see cref="UnifiClient.MaxItems"/> items, and the largest composite (who is on each network) makes a few dozen
/// requests on a big site.
/// </remarks>
internal sealed class UpstreamBudget
{
    /// <summary>Requests to the console one answer may make.</summary>
    public const int MaxRequests = 100;

    /// <summary>Bytes one answer may read from the console, over all its requests (32 MiB).</summary>
    public const long MaxBytes = 32 * 1024 * 1024;

    /// <summary>What the answer tells the caller to do when it is partial.</summary>
    public const string Narrow = "Narrow the question: one network, client or device, a filter, or a shorter window.";

    private const int MaxNotes = 5;

    private static readonly AsyncLocal<UpstreamBudget?> Ambient = new();

    private readonly ConcurrentQueue<string> _notes = new();
    private int _requests;
    private long _bytes;

    private UpstreamBudget() { }

    /// <summary>The budget of the answer this code runs in, or null outside one.</summary>
    public static UpstreamBudget? Current => Ambient.Value;

    /// <summary>A new budget for the calling async flow and what it starts; the caller's own flow is unchanged after it returns.</summary>
    public static UpstreamBudget Open() => Ambient.Value = new UpstreamBudget();

    /// <summary>Bytes the answer may still read.</summary>
    public long BytesLeft => Math.Max(0, MaxBytes - Interlocked.Read(ref _bytes));

    /// <summary>A request was refused or the bytes are used up, so every further read would fail the same way.</summary>
    public bool Exhausted => Volatile.Read(ref _requests) > MaxRequests || BytesLeft == 0;

    /// <summary>Why the answer stopped early, each said once, at most <see cref="MaxNotes"/>.</summary>
    public IReadOnlyList<string> Notes => [.. _notes.Distinct().Take(MaxNotes)];

    /// <summary>Charges one request, or refuses it when the answer has none left.</summary>
    public void Request(string what)
    {
        if (BytesLeft == 0) throw OutOfBytes(what);
        if (Interlocked.Increment(ref _requests) > MaxRequests)
            throw Spent($"This answer has made its {MaxRequests} requests to the console, so {what} was not sent. {Narrow}");
    }

    /// <summary>
    /// Charges bytes read, and says whether the answer is still within its bytes. Charge and check are one atomic step,
    /// so reads at once cannot both pass on the same bytes left: each stops at the first chunk that goes past them.
    /// </summary>
    public bool Read(long bytes) => Interlocked.Add(ref _bytes, bytes) <= MaxBytes;

    /// <summary>The refusal for a request whose answer would go past the bytes left.</summary>
    public UnifiError OutOfBytes(string what) =>
        Spent($"This answer has read the {Megabytes(MaxBytes)} it may read from the console, so it stopped at {what}. {Narrow}");

    public void Note(string note)
    {
        if (_notes.Count < 4 * MaxNotes) _notes.Enqueue(note);
    }

    internal static string Megabytes(long bytes) => $"{(bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB";

    private UnifiError Spent(string message)
    {
        Note(message);
        return new UnifiError(message);
    }
}
