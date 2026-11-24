using System.Diagnostics;
using AVADataMapper.Core.Schema;
using AVADataMapper.Core.Session;

namespace AVADataMapper.Mcp;

/// <summary>
/// What the window shows: the tab (mapping, coverage, checks, profile, dry run or migrate), and the new table and column
/// selected. Agents work on the same mapping the person sees; this is only where the person is looking.
/// </summary>
public sealed record ViewState(string Tab, string? Table, string? Column)
{
    public static readonly ViewState Empty = new("mapping", null, null);
}

/// <summary>What a picture of the view shows: a title, a line under it, and lines, some highlighted.</summary>
public sealed record ViewPicture(string Title, string Subtitle, IReadOnlyList<string> Lines, int FirstLine, int HighlightFrom, int HighlightTo, bool Numbered, IReadOnlyList<string> Notes);

/// <summary>What the server needs from the program that it can't do itself: drawing the view, which needs Avalonia.</summary>
public interface IViewPainter
{
    /// <summary>The view drawn as a PNG, at <paramref name="scale"/>.</summary>
    byte[] Picture(ViewPicture view, double scale);
}

/// <summary>The window (or the headless server's stand-in for one) as an agent works with it. Used on the host's thread only.</summary>
public interface IExplorerSession
{
    /// <summary>What the window shows now.</summary>
    ViewState View { get; }

    /// <summary>
    /// Whether the person lets agents see the old database's values (AI › Let AI Agents See Values, or <c>--real</c>).
    /// Off unless turned on: profiles and rejects show values masked to their shape.
    /// </summary>
    bool SeeRealValues { get; }

    /// <summary>The mapping being worked on: the window's own, so agents change what the person sees.</summary>
    MapSession Work { get; }

    /// <summary>Asks the person a yes-or-no question (the open window: a dialog). Headless, there's no one to ask: no.</summary>
    Task<bool> Ask(string question);

    /// <summary>Selects in the window (headless: just remembers it).</summary>
    void Show(ViewState view);
}

/// <summary>
/// Where the MCP tools' work runs. The handlers (<see cref="MapperTools"/>) touch the window only through
/// <see cref="RunAsync{T}"/>, so the same handlers serve the headless server and the open window.
/// </summary>
public interface IExplorerHost
{
    /// <summary>The working folder: files are read from it, and new files written only inside it.</summary>
    string Root { get; }

    IViewPainter Painter { get; }

    /// <summary>Working in the open window, with the person watching.</summary>
    bool Live { get; }

    /// <summary>Runs <paramref name="work"/> on the thread that owns the window, one call at a time.</summary>
    Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default);
}

public static class ExplorerHostExtensions
{
    public static Task<T> RunAsync<T>(this IExplorerHost host, string action, Func<IExplorerSession, T> work, CancellationToken cancel = default) =>
        host.RunAsync(action, s => Task.FromResult(work(s)), cancel);
}

/// <summary>Runs work on a UI thread (the program's Avalonia dispatcher, or a test's).</summary>
public interface IUiThread
{
    Task<T> InvokeAsync<T>(Func<Task<T>> work);
}

/// <summary>The program as the host sees it: the session, whether it can take a call now, and the status bar.</summary>
public interface IExplorerDesk
{
    IExplorerSession Session { get; }

    /// <summary>Why the program can't take a call just now ("a dialog box is open"), or null. The call waits.</summary>
    string? Busy { get; }

    /// <summary>Tells the person what an agent just did.</summary>
    void Report(string text);
}

/// <summary>
/// The host: calls run against the desk's session on its UI thread, one at a time. While the person is busy (a dialog
/// open) a call waits up to <see cref="BusyWait"/>.
/// </summary>
public sealed class ExplorerHost(IUiThread ui, IExplorerDesk desk, IViewPainter painter, Func<string> root) : IExplorerHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExplorerHost(IUiThread ui, IExplorerDesk desk, IViewPainter painter, string root)
        : this(ui, desk, painter, () => root) { }

    public string Root => Path.GetFullPath(root());

    public IViewPainter Painter { get; } = painter;

    public bool Live { get; init; }

    public TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan BusyPoll { get; init; } = TimeSpan.FromMilliseconds(200);

    public async Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var (ran, result, busy) = await ui.InvokeAsync(async () =>
                {
                    if (desk.Busy is { } why) return (false, default(T), why);
                    try
                    {
                        var r = await work(desk.Session);
                        desk.Report($"AI agent: {action}.");
                        return (true, r, (string?)null);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        desk.Report($"AI agent: {action} failed: {e.Message}");
                        throw;
                    }
                }).ConfigureAwait(false);
                if (ran) return result!;
                if (clock.Elapsed >= BusyWait)
                    throw new ExplorerBusyException($"AVA Data Mapper is busy: {busy}. The call waited {BusyWait.TotalSeconds:0} seconds; ask the person to finish, then try again.");
                await Task.Delay(BusyPoll, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class ExplorerBusyException(string message) : Exception(message);

/// <summary>A session with no window round it, as <c>AVADataMapper --mcp</c> serves it.</summary>
public sealed class HeadlessSession(bool seeRealValues = false, SavedConnections? connections = null) : IExplorerSession
{
    public ViewState View { get; private set; } = ViewState.Empty;

    public bool SeeRealValues { get; set; } = seeRealValues;

    public MapSession Work { get; } = new(connections ?? new SavedConnections(Path.Combine(Path.GetTempPath(), "avadatamapper-headless")));

    public Task<bool> Ask(string question) => Task.FromResult(false);

    public void Show(ViewState view) => View = view;
}

public sealed class HeadlessDesk(TextWriter? log = null, bool seeRealValues = false, SavedConnections? connections = null) : IExplorerDesk
{
    public IExplorerSession Session { get; } = new HeadlessSession(seeRealValues, connections);

    public string? Busy => null;

    public void Report(string text) => log?.WriteLine(text);
}
