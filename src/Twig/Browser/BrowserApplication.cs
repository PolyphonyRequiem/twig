using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Twig.Domain.Projections;
using Twig.Domain.Services;
using Twig.Rendering;

namespace Twig.Browser;

internal sealed record BrowserIntent(string Command, string? Value = null, string? ProposalFile = null);
internal sealed record BrowserIntentResult(bool Ok, string Message, string View, bool RequiresReconnect, long Generation);

/// <summary>One serialized terminal state owner, with separately serialized short native operations.</summary>
internal sealed class BrowserApplication(BrowserRuntimeFactory factory, BrowserHostContext? hostContext = null)
{
    private readonly ConcurrentQueue<Action> _events = new();
    private readonly HashSet<int> _folded = [];
    private readonly Dictionary<string, string> _edits = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TaskCompletionSource<BrowserIntentResult>> _controls = [];
    private BrowserRuntime? _runtime;
    private BrowserBenchObservation? _bench;
    private BrowserBenchIndex? _index;
    private BrowserCapture? _capture;
    private BrowserDetailObservation? _detail;
    private BrowserEditingSession? _editor;
    private BrowserDetailObservation? _editorDetail;
    private BrowserReviewObservation? _review;
    private IReadOnlyList<Twig.Domain.ValueObjects.AreaPathFilter> _candidates = [];
    private BrowserDetailObservation? _renderedDetail;
    private int _renderedWidth;
    private IReadOnlyList<BrowserSourceRow> _detailRows = [];
    private List<Line> _rows = [];
    private List<DetailControl> _fields = [];
    private Task _operationTail = Task.CompletedTask;
    private CancellationTokenSource? _loading;
    private CancellationToken _lifetime;
    private string _cwd = "", _view = "tree", _screen = "bench", _message = "Opening cached Bench…";
    private int _width = 80, _height = 24, _top, _selected, _configSection, _choice, _field, _cursor;
    private int? _expanded;
    private string? _buffer, _prompt;
    private BrowserBenchChoice? _deletion;
    private SavedViewport? _saved;
    private SavedViewport? _configurationViewport;
    private SavedViewport? _helpViewport;
    private FocusedItem? _focused;
    private Anchor? _editorItemAnchor;
    private long _generation = 1, _serial;
    private string? _lastClick;
    private long _lastClickAt;
    private int _cursorRow;
    private volatile bool _running;
    private bool _busy, _requiresReconnect, _dirty = true, _reviewFull, _exact, _openingScreen, _sectionsFocused;
    private sealed record Line(string Key, int Offset, string Text, int? Item = null, int Choice = -1, BrowserInput? Action = null);
    private sealed record Button(string Text, BrowserInput Input);
    private sealed record Anchor(string Key, int Offset, int ScreenRow);
    private sealed record SavedViewport(int Selected, HashSet<int> Folded, Anchor? Anchor);
    private sealed record FocusedItem(int Id, BrowserCapture Capture, SavedViewport BenchViewport, BrowserDetailObservation? InlineDetail);
    private sealed record EditorContent(BrowserEditingSession Session, BrowserDetailObservation Detail);
    private int ContentHeight => BrowserTerminalFrame.ContentHeight(_height);

    internal Task<BrowserIntentResult> DispatchAsync(BrowserIntent intent, CancellationToken ct = default)
    {
        var reply = new TaskCompletionSource<BrowserIntentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running) { reply.SetResult(Result(false, "Browser is not running.")); return reply.Task; }
        _events.Enqueue(() =>
        {
            if (ct.IsCancellationRequested) { reply.TrySetCanceled(ct); return; }
            if (!_running) { reply.TrySetResult(Result(false, "Browser closed.")); return; }
            _controls.Add(reply);
            Intent(intent, reply, ct);
        });
        return reply.Task.WaitAsync(ct);
    }

    internal async Task<int> RunAsync(IBrowserTerminal terminal, string cwd, string view = "tree", string? proposalFile = null, CancellationToken ct = default)
    {
        if (_running) throw new InvalidOperationException("Browser is already running.");
        if (view is not ("tree" or "table")) throw new ArgumentException("View must be tree or table.");
        _cwd = cwd; _view = view; _lifetime = ct; _running = true;
        try
        {
            Reconnect(proposalFile);
            while (_running && !ct.IsCancellationRequested)
            {
                while (_events.TryDequeue(out var action)) action();
                var geometry = terminal.Geometry();
                if (_width != geometry.Width || _height != geometry.Height)
                {
                    _width = Math.Clamp(geometry.Width, 1, 10000); _height = Math.Max(1, geometry.Height); Reflow();
                }
                var count = 0;
                while (count++ < 128 && terminal.TryRead(out var input)) Input(input);
                if (_dirty)
                {
                    terminal.Paint(_rows.Select(r => r.Text).ToArray(), _top, _width, _height, new(Header(), ContextHeader(), _message, Footer(), hostContext, _screen == "config"));
                    _dirty = false;
                }
                await Task.Delay(8, ct);
            }
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        finally
        {
            _running = false; Cancel(); Clear();
            await _operationTail;
            _runtime?.Dispose(); _runtime = null;
            while (_events.TryDequeue(out var action)) action();
            foreach (var reply in _controls) reply.TrySetResult(Result(false, "Browser closed."));
            _controls.Clear();
        }
    }

    private BrowserIntentResult Result(bool ok, string message) => new(ok, message, _screen is "review" or "item" ? _screen : _view, _requiresReconnect, _generation);
    private void Reply(TaskCompletionSource<BrowserIntentResult>? reply, bool ok, string message)
    { if (reply is not null) { reply.TrySetResult(Result(ok, message)); _controls.Remove(reply); } }
    private void Intent(BrowserIntent intent, TaskCompletionSource<BrowserIntentResult> reply, CancellationToken ct)
    {
        switch (intent.Command.ToLowerInvariant())
        {
            case "status": Reply(reply, true, _message); break;
            case "close":
                if (HasDirtyEditor()) { Reply(reply, false, "Unsaved local edit buffer retained. Stage it or explicitly discard in the editor before closing."); break; }
                _running = false; Cancel(); Reply(reply, true, "Browser closing."); break;
            case "reconnect":
                if (HasDirtyEditor()) { Reply(reply, false, "Unsaved local edit buffer retained. Stage it or explicitly discard before reconnecting."); break; }
                Reconnect(intent.ProposalFile, reply, ct); break;
            case "view":
                if (intent.Value is not ("tree" or "table")) { Reply(reply, false, "View must be tree or table."); break; }
                if (_screen == "review") { ExitReview(() => SetView(intent.Value), reply, ct); break; }
                SetView(intent.Value); Reply(reply, true, _message); break;
            case "review": OpenReview(intent.ProposalFile, reply, ct); break;
            case "exit-review": ExitReview(reply: reply, requestCt: ct); break;
            default: Reply(reply, false, "Unsupported browser intent."); break;
        }
    }

    private void Cancel()
    {
        _serial++; _loading?.Cancel(); _loading?.Dispose(); _loading = null; _busy = false; _openingScreen = false;
    }
    private void Clear()
    {
        _bench = null; _capture = null; _detail = null; _editor = null; _review = null;
        _editorDetail = null;
        _index = null;
        _rows.Clear(); _fields.Clear(); _folded.Clear(); _edits.Clear(); _candidates = [];
        _renderedDetail = null; _renderedWidth = 0; _detailRows = [];
        _saved = null; _configurationViewport = null; _helpViewport = null; _expanded = null; _buffer = null; _prompt = null; _deletion = null;
        _focused = null; _editorItemAnchor = null;
        _lastClick = null; _lastClickAt = 0;
        _top = 0; _selected = 0; _choice = 0; _field = 0; _cursor = 0; _cursorRow = 0; _configSection = 0;
        _screen = "bench"; _reviewFull = false; _exact = false; _sectionsFocused = false;
        _dirty = true;
    }
    private bool Failure(BrowserReadFailure? failure)
    {
        if (failure is null) return false;
        if (failure.Kind == BrowserReadFailureKind.AuthorityInvalidated)
        {
            // Authority invalidation MUST beat ordinary request/generation cancellation fences.
            Cancel(); _generation++; Clear(); _requiresReconnect = true;
        }
        else if (_deletion is not null)
        { _deletion = null; _buffer = null; _prompt = null; }
        _message = failure.Message; _dirty = true;
        return true;
    }

    private void Start<T>(Func<CancellationToken, Task<BrowserOperationResult<T>>> operation,
        Action<BrowserOperationResult<T>> accept, TaskCompletionSource<BrowserIntentResult>? reply = null, Action? failed = null,
        CancellationToken requestCt = default)
    {
        if (_requiresReconnect || _runtime is null)
        { _message = "Explicit reconnect is required before operation controls can run."; Reply(reply, false, _message); failed?.Invoke(); Reflow(); return; }
        Cancel(); _busy = true;
        var serial = _serial; var generation = _generation;
        _loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, requestCt);
        var token = _loading.Token; var predecessor = _operationTail;
        _operationTail = Task.Run(async () =>
        {
            BrowserOperationResult<T> result;
            try { await predecessor; token.ThrowIfCancellationRequested(); result = await operation(token); }
            catch (OperationCanceledException) { result = new(default, new(BrowserReadFailureKind.Canceled, "Operation canceled.")); }
            catch (Exception ex) { result = new(default, new(BrowserReadFailureKind.Unavailable, ex.Message)); }
            _events.Enqueue(() =>
            {
                if (result.Failure?.Kind == BrowserReadFailureKind.AuthorityInvalidated)
                { Failure(result.Failure); Reflow(); Reply(reply, false, _message); failed?.Invoke(); return; }
                if (!_running || token.IsCancellationRequested || serial != _serial || generation != _generation)
                {
                    if (_running && serial == _serial && generation == _generation) { _busy = false; _message = "Operation canceled; no late view change was accepted."; _dirty = true; }
                    Reply(reply, false, "Operation superseded or canceled."); failed?.Invoke(); return;
                }
                _busy = false;
                if (!Failure(result.Failure)) { _capture = result.Capture ?? _capture; accept(result); }
                else failed?.Invoke();
                Reflow(); Reply(reply, result.Failure is null, _message);
            });
        });
        Reflow();
    }

    private void Reconnect(string? proposalFile = null, TaskCompletionSource<BrowserIntentResult>? reply = null, CancellationToken requestCt = default)
    {
        Cancel(); _generation++; Clear(); _requiresReconnect = false; _busy = true; _message = "Reconnecting native runtime…";
        var old = _runtime; _runtime = null;
        var predecessor = _operationTail; var generation = _generation;
        _operationTail = Task.Run(async () =>
        {
            BrowserRuntime? runtime = null; Exception? failure = null;
            try
            {
                await predecessor;
                old?.Dispose();
                _lifetime.ThrowIfCancellationRequested(); requestCt.ThrowIfCancellationRequested();
                runtime = factory.Open(_cwd);
            }
            catch (Exception ex) { failure = ex; }
            _events.Enqueue(() =>
            {
                if (!_running || generation != _generation) { runtime?.Dispose(); Reply(reply, false, "Reconnect superseded."); return; }
                _busy = false;
                if (requestCt.IsCancellationRequested)
                { runtime?.Dispose(); _requiresReconnect = true; _message = "Reconnect canceled; explicit reconnect is required."; Reflow(); Reply(reply, false, _message); return; }
                if (failure is not null) { _requiresReconnect = true; _message = failure.Message; Reflow(); Reply(reply, false, _message); return; }
                _runtime = runtime;
                ReadBench(null, () => { if (proposalFile is not null) OpenReview(proposalFile, reply, requestCt); else Reply(reply, true, _message); }, reply, requestCt);
            });
        });
        Reflow();
    }
    private void ReadBench(BrowserCapture? capture, Action? after = null, TaskCompletionSource<BrowserIntentResult>? failedReply = null,
        CancellationToken requestCt = default)
    {
        var module = _runtime!.Services.GetRequiredService<BrowserBenchModule>();
        Start(token => module.ReadAsync(capture, token), result =>
        {
            _bench = result.Value; _message = "Cache-only Bench · selection does not change the active work item";
            _index = _bench is null ? null : new BrowserBenchIndex(_bench.Items);
            EnsureVisibleSelection();
            after?.Invoke();
        }, failed: () => Reply(failedReply, false, _message), requestCt: requestCt);
    }
    private void SyncBench()
    {
        if (_runtime is null || _capture is null) return;
        if (_busy) { _message = "A native operation is already in progress; wait before syncing this Bench."; _dirty = true; return; }
        var module = _runtime.Services.GetRequiredService<BrowserBenchModule>();
        var capture = _capture;
        _message = "Syncing the captured Bench and permitted relationship context · pull only; pending work remains protected…";
        Start(token => module.SyncAsync(capture, token), result =>
        {
            var counts = result.Value!;
            var message = $"Sync: {counts.ItemCount} items · {counts.MemberCount} Bench members · {counts.RelationshipCount} context · {counts.ProtectedCount} protected · pull only";
            ReadBench(result.Capture, () => _message = message);
        });
    }

    private void SetView(string view)
    {
        if (_screen == "editor") AcceptBuffer();
        if (_busy && _openingScreen) Cancel();
        if (_screen == "config") LeaveConfiguration();
        if (_screen == "help") LeaveHelp();
        LeaveItem();
        CloseDetail(); _screen = "bench"; _view = view; EnsureVisibleSelection(); _message = view == "tree" ? "Tree · local selection" : "Table · local selection"; Reflow();
        Reveal("item:" + _selected);
    }
    private void OpenDetail(bool sync = false, bool refresh = false)
    {
        if (_capture is null || _selected == 0 || _runtime is null) return;
        if (_busy) { _message = "A native operation is already in progress; wait before reading or syncing this item."; _dirty = true; return; }
        var target = sync || refresh ? _expanded ?? _selected : _selected;
        if (_expanded != target)
        {
            if (_expanded is not null) CloseDetail();
            _selected = target;
            _saved = new(_selected, new(_folded), Capture()); _expanded = target; _detail = null;
        }
        var id = _expanded!.Value; var module = _runtime.Services.GetRequiredService<BrowserReadModule>();
        var capture = _capture; var width = DetailWidth(id);
        _message = sync ? $"Explicitly syncing captured #{id} only…" : $"Reading cached #{id}…";
        Start(token => ReadDetail(module, new(id, width, Captured: capture, Sync: sync), token), result =>
        {
            _detail = result.Value;
            _message = $"#{id} · native full Show · R cache / S captured root sync · no publishing";
            if (sync) ReadBench(result.Capture, () => _message = $"#{id} · selected root refreshed; owning Bench row and full Show agree");
        });
    }
    private static async Task<BrowserOperationResult<BrowserDetailObservation>> ReadDetail(BrowserReadModule module, BrowserDetailRequest request, CancellationToken ct)
    {
        var result = await module.ReadDetailAsync(request, ct);
        return new(result.Observation, result.Failure, result.Capture);
    }
    private void OpenItem()
    {
        if (_capture is null || _selected == 0 || _runtime is null || _requiresReconnect) return;
        if (_busy) { _message = "A native operation is already in progress; wait before viewing this item."; _dirty = true; return; }
        _focused = new(_selected, _capture, new(_selected, new(_folded), Capture()), _detail);
        _detail = null; _screen = "item"; _top = 0; _editorItemAnchor = null;
        ReadItem();
    }
    private void ReadItem(bool sync = false)
    {
        if (_focused is not { } focused || _runtime is null) return;
        if (_busy) { _message = "A native operation is already in progress; wait before reading or syncing this item."; _dirty = true; return; }
        var module = _runtime.Services.GetRequiredService<BrowserReadModule>(); var width = _width;
        _message = sync ? $"Explicitly syncing captured #{focused.Id} only…" : $"Reading cached #{focused.Id}…";
        Start(token => ReadDetail(module, new(focused.Id, width, Captured: focused.Capture, Sync: sync), token), result =>
        {
            _detail = result.Value;
            _message = $"#{focused.Id} · read-only full item · E guarded editor · R cache / S captured root sync · no publishing";
            if (sync) ReadBench(result.Capture, () => _message = $"#{focused.Id} · captured item and owning Bench refreshed; nothing published");
        });
    }
    private void LeaveItem()
    {
        if (_focused is not { } focused) return;
        Cancel(); _focused = null; _editorItemAnchor = null; _screen = "bench";
        _detail = focused.InlineDetail; _selected = focused.BenchViewport.Selected;
        _folded.Clear(); _folded.UnionWith(focused.BenchViewport.Folded);
        Reflow(false); Restore(focused.BenchViewport.Anchor);
        _message = "Cache-only Bench · selection does not change the active work item";
    }
    private void LeaveEditor()
    {
        Cancel(); AcceptBuffer(); _screen = _focused is null ? "bench" : "item"; _top = 0;
        _message = "Edit buffer retained; E reopens it. Ctrl+D in editor explicitly discards.";
        Reflow(false);
        if (_focused is not null) Restore(_editorItemAnchor);
    }
    private void CloseDetail()
    {
        if (_expanded is null) return;
        Cancel(); _expanded = null; _detail = null;
        if (_saved is { } saved)
        { _selected = saved.Selected; _folded.Clear(); _folded.UnionWith(saved.Folded); _saved = null; Reflow(false); Restore(saved.Anchor); }
        else Reflow();
    }

    private void OpenConfiguration()
    {
        if (_bench is null || _requiresReconnect) return;
        LeaveItem();
        CloseDetail();
        if (_busy && _openingScreen) Cancel();
        _configurationViewport = new(_selected, new(_folded), Capture());
        _screen = "config"; _configSection = 0; _choice = 0; _top = 0; _sectionsFocused = true;
        _message = "Bench configuration · local selectors only; pending work is preserved";
        Reflow(false);
    }
    private void LeaveConfiguration()
    {
        Cancel(); _screen = "bench"; _buffer = null; _prompt = null; _deletion = null; _candidates = [];
        if (_configurationViewport is { } saved)
        {
            _selected = saved.Selected; _folded.Clear(); _folded.UnionWith(saved.Folded);
            _configurationViewport = null; Reflow(false); Restore(saved.Anchor);
        }
        else Reflow(false);
        _message = "Cache-only Bench · selection does not change the active work item";
    }
    private void OpenHelp()
    {
        if (_requiresReconnect) return;
        LeaveItem();
        CloseDetail();
        _helpViewport = new(_selected, new(_folded), Capture()); _screen = "help"; _top = 0;
        _message = "Read-only browser help · Esc returns to the same Bench viewport"; Reflow(false);
    }
    private void LeaveHelp()
    {
        _screen = "bench";
        if (_helpViewport is { } saved)
        {
            _selected = saved.Selected; _folded.Clear(); _folded.UnionWith(saved.Folded); _helpViewport = null;
            Reflow(false); Restore(saved.Anchor);
        }
        else Reflow(false);
        _message = "Cache-only Bench · selection does not change the active work item";
    }

    private void OpenEditor()
    {
        var id = _focused?.Id ?? _selected;
        if (_capture is null || id == 0 || _runtime is null) return;
        if (_busy) { _message = "A native operation is already in progress; wait before opening the editor."; _dirty = true; return; }
        if (_editor is not null && _editor.WorkItemId != id && HasDirtyEditor())
        { _message = "Another item's unsaved edit buffer is retained. Reopen it and stage or explicitly discard first."; _dirty = true; return; }
        _editorItemAnchor = _screen == "item" ? Capture() : null;
        var retained = _editor is not null && _editor.WorkItemId == id ? _editor : null;
        var capture = _focused?.Capture ?? _capture;
        var module = _runtime.Services.GetRequiredService<BrowserEditingModule>();
        var reader = _runtime.Services.GetRequiredService<BrowserReadModule>();
        var width = _width;
        Start<EditorContent>(async token =>
        {
            BrowserOperationResult<BrowserEditingSession> opened = retained is null ? await module.OpenAsync(id, capture, token) : new(retained, null, capture);
            if (opened.Failure is not null) return new(null, opened.Failure, opened.Capture);
            var detail = await ReadDetail(reader, new(id, width, Captured: capture), token);
            return detail.Failure is not null ? new(null, detail.Failure, detail.Capture)
                : new(new(opened.Value!, detail.Value!), null, detail.Capture);
        }, result =>
        {
            _editor = result.Value!.Session; _editorDetail = result.Value.Detail;
            _screen = "editor"; _top = 0;
            if (retained is null) { _field = 0; _edits.Clear(); }
            _fields = _editor.Document.SystemControls.Concat(_editor.Document.Pages.SelectMany(p => p.AllGroups).SelectMany(g => g.Controls)).ToList();
            _message = retained is null ? "Edit item · Enter edits highlighted field · Tab next field · Ctrl+S stages locally"
                : "Retained edit buffer · Ctrl+S stages locally · Ctrl+D explicitly discards";
            Reflow(false); Restore(_editorItemAnchor);
        });
        _openingScreen = _busy;
    }
    private void SaveEdits()
    {
        if (_editor is null || _runtime is null) return;
        AcceptBuffer(); var session = _editor;
        var changes = new Dictionary<string, string>(_edits, StringComparer.OrdinalIgnoreCase);
        var module = _runtime.Services.GetRequiredService<BrowserEditingModule>();
        Start(token => module.StageAsync(session, changes, token), result =>
        {
            switch (result.Value)
            {
                case Twig.Domain.Projections.Saved:
                    foreach (var (field, submitted) in changes)
                        if (_edits.TryGetValue(field, out var current) && current == submitted) _edits.Remove(field);
                    _message = HasDirtyEditor()
                        ? "Submitted local batch saved; newer unsaved input remains in the buffer. Nothing was published."
                        : "Saved local pending batch; nothing was published.";
                    ReadBench(session.Capture, () => _message = "Saved local pending batch; nothing was published.");
                    break;
                case Refused refused: _message = refused.Reason + " Buffer retained."; break;
                default: _message = "Local staging refused; buffer retained."; break;
            }
        });
    }
    private void OpenReview(string? file = null, TaskCompletionSource<BrowserIntentResult>? reply = null, CancellationToken requestCt = default)
    {
        if (_capture is null || _runtime is null || _requiresReconnect) { Reply(reply, false, "Open an admitted Bench before Review."); return; }
        if (_screen == "editor") AcceptBuffer();
        if (_screen == "config") LeaveConfiguration();
        if (_screen == "help") LeaveHelp();
        LeaveItem();
        CloseDetail(); var capture = _capture; var module = _runtime.Services.GetRequiredService<BrowserReviewModule>();
        Start(token => module.OpenAsync(file, capture, token), result =>
        { _review = result.Value; _screen = "review"; _reviewFull = false; _top = 0; _message = "Read-only Review · D details / B brief · Esc back · never applies or authorizes"; }, reply, requestCt: requestCt);
        _openingScreen = _busy;
    }
    private void ReviewGesture(bool? full, Action? after = null, TaskCompletionSource<BrowserIntentResult>? reply = null, CancellationToken requestCt = default)
    {
        if (_review is null || _runtime is null) { after?.Invoke(); Reply(reply, true, _message); return; }
        var observation = _review; var module = _runtime.Services.GetRequiredService<BrowserReviewModule>();
        Start(token => module.ValidateAsync(observation, token), result =>
        {
            if (result.Value && observation.IsValid)
            {
                if (full is { } show) { _reviewFull = show; _top = 0; }
                else { _review = null; _screen = "bench"; _top = 0; }
                after?.Invoke();
            }
            else { _review = null; _screen = "bench"; _message = "Review changed; prior content cleared. Open a fresh Review explicitly."; after?.Invoke(); }
        }, reply, failed: () =>
        {
            if (ReferenceEquals(_review, observation) && !observation.IsValid)
            { _review = null; _screen = "bench"; _top = 0; }
        }, requestCt: requestCt);
    }
    private void ExitReview(Action? after = null, TaskCompletionSource<BrowserIntentResult>? reply = null, CancellationToken requestCt = default)
    { if (_screen == "review") ReviewGesture(null, after, reply, requestCt); else { after?.Invoke(); Reply(reply, true, _message); } }

    private bool HasDirtyEditor() => _edits.Count > 0 || _buffer is not null && _prompt is null;
    private void RequestLeave(bool reconnect)
    {
        AcceptBuffer();
        if (HasDirtyEditor())
        {
            if (_screen == "item") _editorItemAnchor = Capture();
            _screen = "editor"; _message = "Unsaved buffer retained. Ctrl+S stages; Ctrl+D explicitly discards; Esc returns without discarding."; Reflow();
            return;
        }
        if (reconnect) Reconnect(); else { _running = false; Cancel(); }
    }

    private void Change(Func<BrowserBenchModule, CancellationToken, Task<BrowserOperationResult<BrowserBenchChange>>> change, string? highlightBench = null)
    {
        if (_runtime is null || _bench is null) return;
        if (_busy) { _message = "A native operation is already in progress; wait before changing the Bench."; _dirty = true; return; }
        if (HasDirtyEditor()) { _message = "Unsaved editor buffer retained. Stage or explicitly discard it before changing a Bench arrangement."; _dirty = true; return; }
        var module = _runtime.Services.GetRequiredService<BrowserBenchModule>();
        CloseDetail();
        Start(token => change(module, token), result =>
        {
            _message = result.Value!.Message;
            if (result.Value.Confirmation is { } target)
            { _deletion = target; _prompt = "delete-confirm"; _buffer = ""; _cursor = 0; Reflow(); Reveal("delete"); return; }
            _deletion = null;
            _editor = null; _fields.Clear(); _edits.Clear();
            _candidates = [];
            var message = _message;
            ReadBench(result.Capture, () =>
            {
                _message = message;
                if (highlightBench is null || !result.Value!.Changed || _screen != "config" || _bench is null) return;
                var selected = _bench.Benches.Select((bench, index) => (bench, index)).FirstOrDefault(entry => string.Equals(entry.bench.Name, highlightBench.Trim(), StringComparison.OrdinalIgnoreCase));
                if (selected.bench is null) return;
                _configSection = 3; _choice = selected.index; _sectionsFocused = false; Reflow(); Reveal("choice:" + _choice);
            });
        });
    }

    private void Input(BrowserInput input)
    {
        if (input.Kind == BrowserInputKind.EndOfInput) { _running = false; Cancel(); return; }
        if (input.Kind == BrowserInputKind.Mouse)
        {
            if (input.WheelDelta != 0) Scroll(-Math.Sign(input.WheelDelta) * 3);
            if (input.ButtonDown && _height >= 2 && input.Y == _height - 1)
            { ClickFooter(input.X); return; }
            var contentStart = BrowserTerminalFrame.ContentStart(_height);
            if (input.ButtonDown && _height >= 4 && input.Y == 1)
            { ClickButtons(ContextButtons(), input.X, BrowserTerminalText.CellWidth(ModeLabel()) + 3); return; }
            if (input.ButtonDown && input.Y >= contentStart && input.Y < contentStart + ContentHeight)
            {
                var index = _top + input.Y - contentStart;
                if (index >= 0 && index < _rows.Count)
                {
                    var row = _rows[index];
                    var now = Environment.TickCount64;
                    var doubleClick = _lastClick == row.Key && now - _lastClickAt <= 400;
                    _lastClick = row.Key; _lastClickAt = now;
                    if (_buffer is not null && !row.Key.StartsWith("action:", StringComparison.Ordinal)) return;
                    if (row.Action is { } action) { Input(action); return; }
                    if (_buffer is not null) return;
                    if (_screen == "bench" && row.Item is { } id)
                    {
                        _selected = id;
                        if (row.Key.StartsWith("item:", StringComparison.Ordinal) && row.Offset == 0 && _view == "tree" &&
                            input.X >= Depth(id) * 2 && input.X < Depth(id) * 2 + 2 && _index?.HasChildren(id) == true)
                            Fold(!_folded.Contains(id));
                        else if (doubleClick) { if (_expanded == id) CloseDetail(); else OpenDetail(); }
                        else Reflow();
                    }
                    else if (_screen == "config" && row.Choice >= 0)
                    { _sectionsFocused = false; _choice = row.Choice; Reflow(); if (doubleClick) Input(new(BrowserInputKind.Key, BrowserKey.Enter)); }
                    else if (_screen == "editor" && row.Choice >= 0)
                    { AcceptBuffer(); _field = row.Choice; Reflow(); if (doubleClick) Input(new(BrowserInputKind.Key, BrowserKey.Enter)); }
                }
            }
            return;
        }
        if ((input.Modifiers & BrowserModifiers.Paste) != 0)
        { if (_buffer is not null && input.Key == BrowserKey.Character) Insert(input.Character.ToString()); return; }
        if ((input.Modifiers & BrowserModifiers.Control) != 0 && char.ToLowerInvariant(input.Character) == 'r') { RequestLeave(true); return; }
        if ((input.Modifiers & BrowserModifiers.Control) != 0 && char.ToLowerInvariant(input.Character) == 's' && _screen == "editor") { SaveEdits(); return; }
        if ((input.Modifiers & BrowserModifiers.Control) != 0 && char.ToLowerInvariant(input.Character) == 'd' && _screen == "editor")
        { _edits.Clear(); _buffer = null; _message = "Unsaved buffer explicitly discarded; durable pending work is unchanged."; Reflow(); return; }
        if (_buffer is not null) { BufferInput(input); return; }
        if (input.Key == BrowserKey.Escape)
        {
            if (_screen == "review") ExitReview();
            else if (_screen == "editor") LeaveEditor();
            else if (_screen == "item") LeaveItem();
            else if (_screen == "config") LeaveConfiguration();
            else if (_screen == "help") LeaveHelp();
            else CloseDetail();
            return;
        }
        if (_screen == "config") { ConfigInput(input); return; }
        if (input.Key is BrowserKey.PageUp or BrowserKey.PageDown) { Scroll(input.Key == BrowserKey.PageUp ? -ContentHeight : ContentHeight); return; }
        if (input.Key is BrowserKey.Home or BrowserKey.End)
        { _top = input.Key == BrowserKey.Home ? 0 : Math.Max(0, _rows.Count - ContentHeight); _dirty = true; return; }
        if (input.Key is BrowserKey.Up or BrowserKey.Down || input.Character is 'j' or 'k')
        { Move(input.Key == BrowserKey.Up || input.Character == 'k' ? -1 : 1); return; }
        if (_screen == "help") { if (input.Character == 'q') RequestLeave(false); else if (input.Character == '?') LeaveHelp(); return; }
        if (_screen == "review")
        {
            if (input.Character is 'd' or 'D') ReviewGesture(true);
            else if (input.Character is 'b' or 'B') ReviewGesture(false);
            else if (input.Character is '1' or '2') ExitReview(() => SetView(input.Character == '1' ? "tree" : "table"));
            else if (input.Character is 'c' or 'q') ExitReview();
            return;
        }
        if (_screen == "editor")
        {
            if (input.Key == BrowserKey.Tab) Move(1);
            else if (input.Key == BrowserKey.Enter && _editor is not null && _fields.Count > 0)
            {
                var control = _fields[_field];
                if (_editor.Capability.CanEdit(control.Id) && _editor.CurrentValues.TryGetValue(control.Id, out var value))
                { _buffer = _edits.GetValueOrDefault(control.Id) ?? value ?? ""; _cursor = _buffer.Length; _message = "Edit buffer · Tab accepts field · Esc retains field · Ctrl+S stages all"; Reflow(); RevealCursor(); }
                else { _message = "This served field is not editable by the local sink."; _dirty = true; }
            }
            return;
        }
        if (_screen == "item")
        {
            switch (input.Character)
            {
                case 'e': case 'E': OpenEditor(); break;
                case 'r': case 'R': ReadItem(); break;
                case 'S': ReadItem(true); break;
                case 'q': RequestLeave(false); break;
                case '1': SetView("tree"); break;
                case '2': SetView("table"); break;
                case '3': OpenReview(); break;
                case 'b': case 'c': OpenConfiguration(); break;
                case '?': OpenHelp(); break;
            }
            if (input.Key == BrowserKey.Tab) SetView(_view == "tree" ? "table" : "tree");
            return;
        }
        switch (input.Key)
        {
            case BrowserKey.Enter: if (_expanded == _selected) CloseDetail(); else OpenDetail(); return;
            case BrowserKey.Left: NavigateHierarchy(false); return;
            case BrowserKey.Right: NavigateHierarchy(true); return;
            case BrowserKey.Tab: SetView(_view == "tree" ? "table" : "tree"); return;
        }
        switch (input.Character)
        {
            case 'q': RequestLeave(false); break;
            case '1': SetView("tree"); break;
            case '2': SetView("table"); break;
            case '3': OpenReview(); break;
            case 'b': case 'c': OpenConfiguration(); break;
            case 'e': case 'E': OpenEditor(); break;
            case 'v': case 'V': OpenItem(); break;
            case 'r': case 'R': if (_expanded is not null) OpenDetail(refresh: true); else if (_runtime is not null) ReadBench(_capture); break;
            case 'S': OpenDetail(true); break;
            case 's': SyncBench(); break;
            case 'p': case 'P': if (_bench is { } bench) { var id = _selected; var subtree = input.Character == 'P'; Change((m, t) => m.PinAsync(bench, id, subtree, t)); } break;
            case ' ': Fold(!_folded.Contains(_selected)); break;
            case '?': OpenHelp(); break;
        }
    }

    private void ConfigInput(BrowserInput input)
    {
        if (_bench is not { } bench) return;
        if (input.Key is BrowserKey.PageUp or BrowserKey.PageDown) { Scroll(input.Key == BrowserKey.PageUp ? -ContentHeight : ContentHeight); return; }
        if (input.Key is BrowserKey.Up or BrowserKey.Down || input.Character is 'j' or 'k')
        { Move(input.Key == BrowserKey.Up || input.Character == 'k' ? -1 : 1); return; }
        if (input.Key is BrowserKey.Home or BrowserKey.End)
        {
            if (_sectionsFocused) SelectSection(input.Key == BrowserKey.Home ? 0 : 3, true);
            else { _choice = input.Key == BrowserKey.Home ? 0 : Math.Max(0, ConfigCount() - 1); Reflow(); Reveal("choice:" + _choice); }
            return;
        }
        if (input.Key == BrowserKey.Left) { _sectionsFocused = true; Reflow(); Reveal("section:" + _configSection); return; }
        if (input.Key == BrowserKey.Right || input.Key == BrowserKey.Enter && _sectionsFocused)
        { _sectionsFocused = false; Reflow(); Reveal("choice:" + _choice); return; }
        if (input.Character is 'r' or 'R') { if (_busy) return; _deletion = null; _candidates = []; ReadBench(_capture); return; }
        if (input.Character == 's') { SyncBench(); return; }
        if (input.Character is >= '1' and <= '4') { SelectSection(input.Character - '1', false); return; }
        if (input.Key == BrowserKey.Tab) { SelectSection((_configSection + ((input.Modifiers & BrowserModifiers.Shift) != 0 ? 3 : 1)) % 4, true); return; }
        if (_configSection == 1 && input.Character is 'x' or 'u') { _exact = input.Character == 'x'; _message = _exact ? "Area candidate mode: Exact" : "Area candidate mode: Under"; Reflow(); return; }
        if (_configSection == 0)
        {
            var selected = SelectedPinItem();
            if ((input.Character is 'p' or 'P') && selected is not null)
            { var id = selected.Id; var subtree = input.Character == 'P'; Change((m, t) => m.PinAsync(bench, id, subtree, t)); }
            else if (input.Character is 'a' or 'A' or 'i' or 'I') Prompt(input.Character is 'A' or 'I' ? "pin-tree" : "pin-single", "Enter a positive ID; uncached pins remain unverified. Enter adds the chosen kind; Esc cancels.");
        }
        else if (_configSection is 1 or 2)
        {
            var section = _configSection == 1 ? "area" : "sprint";
            if (input.Character == 'a') Prompt(section, section == "area" ? "Area path · x Exact / u Under before opening buffer" : "Sprint expression: @Current, offset, or absolute path");
            else if (input.Character == 'd' || input.Key == BrowserKey.Delete)
            {
                var value = section == "area" ? _choice < bench.Areas.Count ? bench.Areas[_choice].Path : null : bench.Sprints.ElementAtOrDefault(_choice);
                if (value is not null) Change((m, t) => m.SettingsAsync(bench, section, "remove", value, ct: t));
            }
            else if (input.Character == 't' && section == "area" && _runtime is not null)
            {
                var module = _runtime.Services.GetRequiredService<BrowserBenchModule>();
                if (_busy) return;
                Start(t => module.ReadAreaCandidatesAsync(bench.Capture, t), r => { _candidates = r.Value!; _choice = bench.Areas.Count; _sectionsFocused = false; _message = "Configured-team candidates fetched explicitly · x Exact / u Under · Enter adds selected candidate"; Reflow(); Reveal("choice:" + _choice); });
            }
            else if (input.Key == BrowserKey.Enter && section == "area")
            {
                var index = _choice - bench.Areas.Count;
                if (index >= 0 && index < _candidates.Count)
                {
                    var candidate = _candidates[index];
                    Change((m, t) => m.SettingsAsync(bench, "area", "add", candidate.Path, _exact, t));
                }
            }
        }
        else
        {
            var target = bench.Benches.ElementAtOrDefault(_choice);
            if (input.Character == 'n') Prompt("create", "Name an empty Bench to create (does not switch).");
            else if (input.Key == BrowserKey.Enter && target is not null) Change((m, t) => m.SwitchAsync(bench.Capture, target, t));
            else if ((input.Character == 'd' || input.Key == BrowserKey.Delete) && target is not null) Change((m, t) => m.DeleteAsync(bench.Capture, target, ct: t));
        }
    }
    private void SelectSection(int section, bool focused)
    { _configSection = section; _sectionsFocused = focused; _choice = 0; _top = 0; Reflow(false); Reveal(focused ? "section:" + section : "choice:0"); }
    private int ConfigCount() => _bench is null ? 0 : _configSection switch
    { 0 => _bench.Items.Count(i => i.SinglePin || i.TreePin), 1 => _bench.Areas.Count + _candidates.Count, 2 => _bench.Sprints.Count, _ => _bench.Benches.Count };
    private BrowserBenchItem? SelectedPinItem() => _screen == "config"
        ? _bench?.Items.Where(i => i.SinglePin || i.TreePin).ElementAtOrDefault(_choice) ?? _bench?.Items.FirstOrDefault(i => i.Id == _selected)
        : _bench?.Items.FirstOrDefault(i => i.Id == _selected);
    private void Prompt(string purpose, string message)
    {
        if (_busy) { _message = "Wait for the native operation to finish before opening a form."; _dirty = true; return; }
        _prompt = purpose; _buffer = ""; _cursor = 0; _message = message; Reflow(); RevealCursor();
    }
    private void Insert(string value)
    { _buffer = _buffer!.Insert(_cursor, value); _cursor += value.Length; Reflow(); RevealCursor(); }
    private void AcceptBuffer()
    {
        if (_buffer is not null && _prompt is null && _fields.Count > 0 && _editor is not null)
        {
            var id = _fields[_field].Id;
            if (string.Equals(_buffer, _editor.CurrentValues.GetValueOrDefault(id) ?? "", StringComparison.Ordinal)) _edits.Remove(id);
            else _edits[id] = _buffer;
        }
        _buffer = null;
    }
    private void RevealCursor()
    {
        if (_buffer is null) return;
        if (_prompt is not null) { _top = Math.Max(0, _rows.Count - ContentHeight); _dirty = true; return; }
        if (_cursorRow < _top) _top = Math.Max(0, _cursorRow);
        else if (_cursorRow >= _top + ContentHeight) _top = Math.Max(0, _cursorRow - ContentHeight + 1);
        _dirty = true;
    }

    private void MoveBufferRow(int delta)
    {
        var text = _buffer!;
        var wrapped = BrowserTerminalText.Wrap(text, Math.Max(1, _width - 2));
        var current = 0;
        while (current + 1 < wrapped.Count && wrapped[current + 1].Offset <= _cursor) current++;
        var column = BrowserTerminalText.CellWidth(text[wrapped[current].Offset.._cursor]);
        var target = Math.Clamp(current + delta, 0, wrapped.Count - 1);
        var start = wrapped[target].Offset;
        var end = target + 1 < wrapped.Count ? wrapped[target + 1].Offset : text.Length;
        var position = start; var cells = 0;
        while (position < end)
        {
            var element = StringInfo.GetNextTextElement(text, position);
            if (element.Contains('\n') || cells + BrowserTerminalText.CellWidth(element) > column) break;
            cells += BrowserTerminalText.CellWidth(element); position += element.Length;
        }
        _cursor = position;
    }

    private void BufferInput(BrowserInput input)
    {
        if (input.Key == BrowserKey.Escape)
        {
            if (_prompt is null) { AcceptBuffer(); _message = "Field buffer retained; Ctrl+D explicitly discards unsaved edits."; }
            else { _buffer = null; _prompt = null; _deletion = null; }
            Reflow(); return;
        }
        if (input.Key is BrowserKey.PageUp or BrowserKey.PageDown)
        { Scroll(input.Key == BrowserKey.PageUp ? -ContentHeight : ContentHeight); return; }
        var boundaries = StringInfo.ParseCombiningCharacters(_buffer!);
        if (input.Key is BrowserKey.Up or BrowserKey.Down) MoveBufferRow(input.Key == BrowserKey.Up ? -1 : 1);
        else if (input.Key == BrowserKey.Left) _cursor = boundaries.LastOrDefault(i => i < _cursor);
        else if (input.Key == BrowserKey.Right) _cursor = boundaries.FirstOrDefault(i => i > _cursor, _buffer!.Length);
        else if (input.Key == BrowserKey.Home) _cursor = 0;
        else if (input.Key == BrowserKey.End) _cursor = _buffer!.Length;
        else if (input.Key == BrowserKey.Backspace && _cursor > 0)
        { var prior = boundaries.LastOrDefault(i => i < _cursor); _buffer = _buffer!.Remove(prior, _cursor - prior); _cursor = prior; }
        else if (input.Key == BrowserKey.Delete && _cursor < _buffer!.Length)
        { var next = boundaries.FirstOrDefault(i => i > _cursor, _buffer.Length); _buffer = _buffer.Remove(_cursor, next - _cursor); }
        else if (input.Key == BrowserKey.Tab && _prompt is null) { AcceptBuffer(); Move(1); }
        else if (input.Key == BrowserKey.Enter && _prompt is not null) SubmitPrompt();
        else if (input.Key == BrowserKey.Enter) Insert("\n");
        else if (input.Key == BrowserKey.Character && !char.IsControl(input.Character)) Insert(input.Character.ToString());
        Reflow();
        RevealCursor();
    }
    private void SubmitPrompt()
    {
        if (_bench is not { } bench) return;
        var value = _buffer!; var purpose = _prompt; _buffer = null; _prompt = null;
        switch (purpose)
        {
            case "pin-single":
            case "pin-tree":
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                    Change((m, t) => m.AddPinAsync(bench, id, purpose == "pin-tree", t));
                else _message = "A positive ID is required; no pin was changed.";
                break;
            case "area": case "sprint": Change((m, t) => m.SettingsAsync(bench, purpose, "add", value, _exact, t)); break;
            case "create": Change((m, t) => m.CreateAsync(bench.Capture, value, t), value); break;
            case "delete-confirm": if (_deletion is { } target) Change((m, t) => m.DeleteAsync(bench.Capture, target, value, t)); break;
        }
    }

    private void Move(int delta)
    {
        if (_screen == "bench")
        {
            if (_expanded is not null) { Scroll(delta); return; }
            var ids = _rows.Where(r => r.Item.HasValue && r.Key.StartsWith("item:", StringComparison.Ordinal)).Select(r => r.Item!.Value).Distinct().ToArray();
            if (ids.Length == 0) return;
            var position = Array.IndexOf(ids, _selected); _selected = ids[Math.Clamp(position + delta, 0, ids.Length - 1)]; Reflow();
            Reveal("item:" + _selected);
        }
        else if (_screen == "editor")
        {
            _field = Math.Clamp(_field + delta, 0, Math.Max(0, _fields.Count - 1)); Reflow();
            var row = _rows.FirstOrDefault(line => line.Choice == _field);
            if (row is not null) Reveal(row.Key);
        }
        else if (_screen == "config")
        {
            if (_sectionsFocused) { SelectSection(Math.Clamp(_configSection + delta, 0, 3), true); return; }
            _choice = Math.Clamp(_choice + delta, 0, Math.Max(0, ConfigCount() - 1)); Reflow(); Reveal("choice:" + _choice);
        }
        else Scroll(delta);
    }
    private void Reveal(string key)
    {
        var first = _rows.FindIndex(r => r.Key == key);
        if (first < 0) return;
        if (first < _top) _top = Math.Max(0, first);
        else if (first >= _top + ContentHeight) _top = Math.Max(0, first - ContentHeight + 1);
        _dirty = true;
    }
    private void Scroll(int delta) { _top = Math.Clamp(_top + delta, 0, Math.Max(_top, _rows.Count - ContentHeight)); _dirty = true; }
    private void Fold(bool fold)
    {
        if (_view != "tree" || _index?.HasChildren(_selected) != true) return;
        if (fold) _folded.Add(_selected); else _folded.Remove(_selected); Reflow();
    }
    private void NavigateHierarchy(bool right)
    {
        if (_view != "tree" || _index is null) return;
        if (right && _folded.Contains(_selected) && _index.HasChildren(_selected)) { Fold(false); return; }
        if (!right && !_folded.Contains(_selected) && _index.HasChildren(_selected)) { Fold(true); return; }
        var target = right ? _index.FirstChild(_selected) : _index.Parent(_selected);
        if (target is not { } id) return;
        if (_expanded is not null) CloseDetail();
        _selected = id; Reflow(); Reveal("item:" + id);
    }
    private Anchor? Capture()
    {
        if (_rows.Count == 0) return null;
        var row = _rows[Math.Min(_top, _rows.Count - 1)]; return new(row.Key, row.Offset, 0);
    }
    private void Restore(Anchor? anchor)
    {
        var limit = Math.Max(0, _rows.Count - ContentHeight);
        if (anchor is not null)
        {
            var index = _rows.FindLastIndex(r => r.Key == anchor.Key && r.Offset <= anchor.Offset);
            if (index >= 0) { _top = index - anchor.ScreenRow; limit = Math.Max(limit, _top); }
        }
        _top = Math.Clamp(_top, 0, limit); _dirty = true;
    }
    private int Depth(int id) => _view == "table" ? 0 : _index?.Depth(id, _width) ?? 0;
    private int DetailWidth(int id) => Math.Max(1, _width - Depth(id) * 2 - 2);
    private IEnumerable<BrowserBenchItem> VisibleItems() => _index?.Visible(_view, _folded) ?? [];
    private void EnsureVisibleSelection()
    {
        var first = 0;
        foreach (var item in VisibleItems())
        {
            if (first == 0) first = item.Id;
            if (item.Id == _selected) return;
        }
        _selected = first;
    }
    private static string Column(string value, int width)
    {
        var clipped = BrowserTerminalText.Clip(value, width);
        return clipped + new string(' ', Math.Max(0, width - BrowserTerminalText.CellWidth(clipped)));
    }
    private void Add(List<Line> rows, string key, string text, string prefix = "", int? item = null, int choice = -1, string color = "", BrowserInput? action = null)
    {
        var first = true;
        var indent = new string(' ', Math.Min(BrowserTerminalText.CellWidth(prefix), Math.Max(0, _width - 1)));
        foreach (var chunk in BrowserTerminalText.Wrap(RichHtmlRenderer.SafeText(text), Math.Max(1, _width - indent.Length)))
        {
            var line = (first ? prefix : indent) + chunk.Text;
            if (color.Contains("48;", StringComparison.Ordinal)) line += new string(' ', Math.Max(0, _width - BrowserTerminalText.CellWidth(line)));
            rows.Add(new(key, chunk.Offset, color + line + "\x1b[0m", item, choice, action)); first = false;
        }
    }
    private void Reflow(bool preserve = true)
    {
        var anchor = preserve ? Capture() : null; var next = new List<Line>();
        if (_screen == "review" && _review is not null)
        {
            var lines = BrowserTerminalText.Rows(_review.Render(_width, _reviewFull), _width);
            for (var i = 0; i < lines.Count; i++) next.Add(new("review", i, lines[i]));
        }
        else if (_screen == "editor" && _editor is not null) EditorRows(next);
        else if (_screen == "item" && _focused is { } focused)
        {
            if (_detail is not null)
            {
                foreach (var row in DetailRows(_width))
                    next.Add(new("detail:" + focused.Id + ":" + row.Source, row.Offset, row.Text));
            }
            else Add(next, "item-loading:" + focused.Id, _busy ? $"Reading cached #{focused.Id}…" : _message);
        }
        else if (_screen == "config" && _bench is not null) ConfigRows(next);
        else if (_screen == "help") HelpRows(next);
        else if (_bench is not null)
        {
            if (_view == "table" && _width >= 48)
                Add(next, "columns", Column("ID", 12) + " " + Column("TYPE", 12) + " " + Column("STATE", 12) + " TITLE", color: "\x1b[1;36m");
            var visible = VisibleItems().ToArray();
            if (!visible.Any(item => item.Id == _selected))
            {
                var seen = new HashSet<int>();
                while (seen.Add(_selected) && _index?.Parent(_selected) is { } parent)
                { _selected = parent; if (visible.Any(item => item.Id == parent)) break; }
                if (!visible.Any(item => item.Id == _selected)) _selected = visible.FirstOrDefault()?.Id ?? 0;
            }
            foreach (var item in visible)
            {
                var indent = new string(' ', Depth(item.Id) * 2);
                var prefix = _view == "tree" ? _index!.Prefix(item.Id, _width, _folded) : "";
                if (_view == "tree" && item.Id == _selected && prefix.EndsWith(' ')) prefix = prefix[..^1] + "›";
                foreach (var row in BrowserBenchPresentation.Rows(_bench, item, prefix, _width, item.Id == _selected, _view == "table"))
                    next.Add(new("item:" + item.Id, row.Offset, row.Text, item.Id));
                if (_expanded != item.Id) continue;
                var inset = indent + "  ";
                if (_detail is not null)
                {
                    foreach (var line in DetailRows(DetailWidth(item.Id)))
                        next.Add(new("detail:" + item.Id + ":" + line.Source, line.Offset, inset + line.Text, item.Id));
                }
                else
                {
                    var rule = new string('─', Math.Max(0, _width - inset.Length - 1));
                    next.Add(new("detail:" + item.Id, 0, inset + "┌" + rule, item.Id));
                    Add(next, "detail:" + item.Id, _busy ? "Reading native detail…" : _message, inset + "│ ", item.Id);
                    next.Add(new("detail:" + item.Id, 2, inset + "└" + rule, item.Id));
                }
            }
        }
        else Add(next, "empty", _requiresReconnect ? "Authority unavailable. Old-origin content cleared. Ctrl+R explicitly reconnects." : _message);
        if (_screen == "bench" && _bench is { Items.Count: 0 })
            Add(next, "empty-bench", "This Bench has no cached members. b/c opens configuration: add a pin by ID or choose area and sprint selectors. Seeds and owed work remain visible regardless of selectors.");
        _rows = next;
        if (preserve) Restore(anchor); else _top = Math.Clamp(_top, 0, Math.Max(0, next.Count - ContentHeight));
        _dirty = true;
    }
    private IReadOnlyList<BrowserSourceRow> DetailRows(int width)
    {
        if (!ReferenceEquals(_renderedDetail, _detail) || _renderedWidth != width)
        {
            _detailRows = _detail!.RenderRowsAsync(width).GetAwaiter().GetResult();
            _renderedDetail = _detail; _renderedWidth = width;
        }
        return _detailRows;
    }
    private void EditorRows(List<Line> rows)
    {
        if (_editorDetail is null || _editor is null) return;
        var values = new Dictionary<string, string?>(_editor.CurrentValues, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _edits) values[key] = value;
        for (var index = 0; index < _fields.Count; index++)
        {
            var control = _fields[index];
            if (!_editor.Capability.CanEdit(control.Id) || !values.ContainsKey(control.Id)) continue;
            if (_buffer is not null && _prompt is null && index == _field) values[control.Id] = _buffer;
            if (string.IsNullOrEmpty(values[control.Id])) values[control.Id] = "[empty]";
        }
        var rendered = _editorDetail.RenderEditingRowsAsync(_width, _editor.Snapshot, values, _fields.ElementAtOrDefault(_field)?.Id).GetAwaiter().GetResult();
        var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var controls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _fields.Count; index++) controls.TryAdd(_fields[index].Id, index);
        var inputRendered = false;
        foreach (var row in rendered)
        {
            carried.Add(row.Source);
            var field = controls.GetValueOrDefault(row.Source, -1);
            if (_buffer is not null && _prompt is null && field == _field)
            {
                if (!inputRendered)
                {
                    Add(rows, "field:" + field, _buffer.Insert(_cursor, "▏"), "│ ", choice: field, color: BrowserTerminalFrame.SelectionStyle);
                    _cursorRow = rows.FindLastIndex(line => line.Key == "field:" + field && line.Offset <= _cursor);
                    inputRendered = true;
                }
                continue;
            }
            var text = row.Text;
            if (field == _field)
            {
                var edge = text.IndexOf("│ ", StringComparison.Ordinal);
                if (edge >= 0) text = text.Remove(edge, 1).Insert(edge, "›");
            }
            rows.Add(new("detail:" + _editor.WorkItemId + ":" + row.Source, row.Offset, text, Choice: field));
        }
        var missing = Enumerable.Range(0, _fields.Count).Where(index => !carried.Contains(_fields[index].Id)).ToArray();
        if (missing.Length == 0) return;
        Add(rows, "document-fields", "Other served document fields", color: "\x1b[1;36m");
        foreach (var index in missing)
        {
            var current = index;
            EditorControlRows(rows, _fields[index], ref current);
        }
    }

    private void EditorControlRows(List<Line> rows, DetailControl control, ref int index)
    {
        var currentIndex = index++;
        var editable = _editor!.Capability.CanEdit(control.Id) && _editor.CurrentValues.ContainsKey(control.Id);
        var value = _edits.TryGetValue(control.Id, out var edit) ? edit : _editor.CurrentValues.TryGetValue(control.Id, out var saved) ? saved ?? "[empty on server]" :
            control.IsContribution ? "[unsupported contribution: " + control.ControlType + "]" : control.Value?.State switch
            { DetailFieldState.EmptyOnServer => "[empty on server]", DetailFieldState.NotCarriedByTwig => "[not carried by Twig]", _ => control.Value?.Full ?? "[unsupported control]" };
        if (_buffer is not null && _prompt is null && currentIndex == _field) value = _buffer.Insert(_cursor, "▏");
        Add(rows, "field:" + currentIndex, control.Label + " (" + control.Id + ")" + (editable ? " [editable]" : " [read-only]") + "\n" + value,
            currentIndex == _field ? "› " : "  ", choice: currentIndex, color: currentIndex == _field ? "\x1b[1;37m" : "\x1b[38;2;162;193;207m");
        if (_buffer is not null && _prompt is null && currentIndex == _field)
        {
            var caret = RichHtmlRenderer.SafeText(control.Label + " (" + control.Id + ")" + (editable ? " [editable]" : " [read-only]") + "\n" + _buffer[.._cursor]).Length;
            _cursorRow = rows.FindLastIndex(row => row.Key == "field:" + currentIndex && row.Offset <= caret);
        }
    }
    private void HelpRows(List<Line> rows)
    {
        var origin = _capture?.Origin;
        Add(rows, "help-connection", "CONNECTION · " + DisplayLabel(origin?.Operation.Organization, "unavailable") + "/" + DisplayLabel(origin?.Operation.Project, "unavailable") +
            " · Team: " + DisplayLabel(origin?.Operation.Team, "not configured") + " · Bench: " + DisplayLabel(_capture?.BenchName, "unavailable") +
            " · User: " + DisplayLabel(origin?.Identity.AccountName, DisplayLabel(origin?.Identity.Name, "unavailable")));
        Add(rows, "help-worktree", "WORKTREE · " + DisplayLabel(origin?.WorktreeRoot, "unavailable") + " · Branch: " + DisplayLabel(_bench?.Branch, "unavailable"));
        if (hostContext is not null) Add(rows, "help-host", "HOST · " + hostContext.Details);
        Add(rows, "help-context-gap", "");
        var sections = new[]
        {
            "BROWSER · Native cached Bench; selecting a row never changes the active work item.",
            "1 Tree · 2 Table · Tab toggles · 3 proposal Review. Native view digits are retained; they differ from the original Go browser.",
            "↑/↓ or j/k selects visible work items. ← collapses an expanded parent or selects the parent; → expands a folded parent or selects the first child. Space toggles the selected fold. Click its disclosure to fold.",
            "Enter expands full cached detail inline. While expanded, ↑/↓/j/k, PgUp/PgDn/Home/End and the wheel scroll physical rows through all detail and following work items. Enter or Esc collapses and restores selection, folds and viewport.",
            "v View item opens the selected item's full cached detail on its own read-only screen, without surrounding Bench rows. Arrows/j/k, PgUp/PgDn/Home/End and the wheel scroll every row; resize preserves the source position. Esc restores the exact prior Bench or inline viewport.",
            "p toggles only the selected item's explicit Single pin; Shift+P toggles only its explicit Subtree pin. Both can coexist. Inherited pins, automatic rules and owed membership remain. Local seeds must be published before pinning.",
            "Named footer buttons run the same guarded key actions when clicked. Pin/Unpin labels reflect explicit cached pins, never inherited membership. Busy native mutations cannot be repeated.",
            "b or c opens Bench configuration: Pins, Areas, Sprints, Benches. Tab/Shift+Tab or 1/2/3/4 chooses a section; arrows select sections until Enter/→ focuses items; ← returns to sections. Esc restores the Bench viewport.",
            "Configuration i adds any positive single ID; Shift+I adds a subtree ID. Uncached pins remain unverified. Areas support Exact/Under, and t explicitly fetches configured-team candidates. No sprints disables automatic membership; seeds and owed work still appear.",
            "Benches: n creates empty without switching; Enter selects; d/Delete reviews deletion. Default is protected. Nonempty deletion requires its captured name. All native origin, settings and contents protections remain; pending work is never deleted.",
            "R refreshes the local Bench, expanded item or focused item's cache; S explicitly syncs only the selected/expanded/focused item. Lowercase s on the Bench pulls its saved selectors and permitted relationship context, protecting pending work. Navigation, item viewing, configuration entry, expansion and refresh never implicitly sync.",
            "E edits in the same rendered item layout, with the selected field highlighted. Enter opens an inline input; Tab retains the field and selects the next served field. Ctrl+S stages one local batch. Ctrl+D explicitly discards unsaved buffers. Esc preserves them; leaving the editor restores the focused item's viewport, and another Esc returns to the Bench. Unsupported document controls remain identified below the item. Nothing is published from this browser.",
            "Proposal Review is separate from View item and is read only: D details, B brief, Esc back. It never applies or authorizes a proposal. Origin invalidation clears prior content; Ctrl+R explicitly reconnects. q closes unless an unsaved edit buffer must be retained."
        };
        for (var i = 0; i < sections.Length; i++) { Add(rows, "help:" + i, sections[i]); Add(rows, "help-gap:" + i, ""); }
    }
    private void ConfigRows(List<Line> rows)
    {
        var sections = new[] { "Pins", "Areas", "Sprints", "Benches" };
        for (var section = 0; section < sections.Length; section++)
            Add(rows, "section:" + section, $"[{section + 1} {sections[section]}]", section == _configSection ? _sectionsFocused ? "› " : "• " : "  ",
                color: section == _configSection ? "\x1b[1;48;2;70;49;91m\x1b[38;2;255;246;255m" : "\x1b[38;2;229;163;222m",
                action: new(BrowserInputKind.Key, Character: (char)('1' + section)));
        Add(rows, "navigation", "Sections: ↑/↓ then Enter/→ items · ← returns to sections · Tab/Shift+Tab or 1/2/3/4 choose directly");
        Add(rows, "ownership", (_bench!.Sprints.Count == 0 ? "Automatic off (no sprints) · Ownership: " : "Automatic ownership: ") +
            (_bench.Ownership.Length == 0 ? "none" : _bench.Ownership));
        var choices = _configSection switch
        {
            0 => _bench.Items.Where(i => i.SinglePin || i.TreePin).Select(i => $"#{i.Id} · {(i.SinglePin ? "single" : "")}{(i.SinglePin && i.TreePin ? " + " : "")}{(i.TreePin ? "subtree" : "")} · {(i.Cached ? i.Type + " · " + i.State + " · " + i.Title : "uncached / unverified")}").ToArray(),
            1 => _bench.Areas.Select(a => (a.IncludeChildren ? "Under · " : "Exact · ") + a.Path)
                .Concat(_candidates.Select(a => "Team candidate · " + (_exact ? "Exact · " : "Under · ") + a.Path)).ToArray(),
            2 => _bench.Sprints.ToArray(),
            _ => _bench.Benches.Select(b => b.Name + (b.IsDefault ? " [protected Default]" : "") + (b.Id == _bench.Capture.BenchId ? " [current]" : "") + " · " + b.Contents.Count + " selectors").ToArray()
        };
        _choice = Math.Clamp(_choice, 0, Math.Max(0, choices.Length - 1));
        for (var i = 0; i < choices.Length; i++) Add(rows, "choice:" + i, choices[i], i == _choice ? _sectionsFocused ? "• " : "› " : "  ", choice: i,
            color: i == _choice && !_sectionsFocused ? "\x1b[1;48;2;70;49;91m\x1b[38;2;255;246;255m" : "");
        if (choices.Length == 0) Add(rows, "no-choices", _configSection switch
        {
            0 => "No explicit pins. Use i for any single ID or I for a subtree ID; uncached IDs stay unverified.",
            1 => "No areas: no area restriction. Use a to add a path, or t to fetch configured-team candidates explicitly.",
            2 => "No sprints: automatic membership disabled; seeds and owed work still appear. Use a to add @Current, an offset, or an absolute path.",
            _ => "No admitted Bench list. R refreshes; n creates an empty Bench."
        });
        Add(rows, "instructions", _configSection switch
        {
            0 => "p toggles only Single; Shift+P only Subtree. Both can coexist. Inherited membership, automatic rules and pending work remain. i/I adds any ID without remote verification.",
            1 => "Areas OR together, then AND chosen sprints. No areas does not enable automatic membership. x Exact / u Under chooses manual and team-candidate scope; Enter adds the selected team candidate.",
            2 => "Sprints OR together: @Current, @Current+1, @Current-1, or absolute iteration paths. Saved selectors drive cached reads and scope the explicit s Bench pull; no navigation action syncs automatically.",
            _ => "n creates and highlights an empty Bench without switching. Enter selects. d/Delete reviews deletion; Default is protected. Deleting current selects Default; pending work is preserved."
        });
        foreach (var button in FooterButtons()) Add(rows, "action:" + button.Text, button.Text, action: button.Input);
        if (_deletion is { } deletion)
        {
            Add(rows, "delete", "Delete arrangement: " + deletion.Name + " · retype this name exactly, then Enter; Esc cancels.", color: "\x1b[1;33m");
            foreach (var content in deletion.Contents) Add(rows, "delete-content:" + content, content);
            Add(rows, "delete-digest", "Captured contents digest: " + deletion.ContentsDigest);
        }
        if (_buffer is not null) Add(rows, "prompt", _message + "\n> " + _buffer.Insert(_cursor, "▏"), color: "\x1b[1;33m");
    }
    private static string DisplayLabel(string? value, string unavailable)
        => string.IsNullOrWhiteSpace(value) || Guid.TryParse(value, out _) ? unavailable : RichHtmlRenderer.SafeText(value.Trim());
    private static string Elide(string value, int width)
        => BrowserTerminalText.CellWidth(value) <= width ? value : BrowserTerminalText.Clip(value, Math.Max(0, width - 1)) + "…";
    private string Header()
    {
        if (_requiresReconnect) return "STOPPED — reconnect required";
        var origin = _capture?.Origin;
        var connection = origin is null ? "Connection unavailable" : DisplayLabel(origin.Operation.Organization, "Connection unavailable") + "/" + DisplayLabel(origin.Operation.Project, "Project unavailable");
        var labelWidth = Math.Max(1, (_width - 36) / 3);
        var team = DisplayLabel(origin?.Operation.Team, "not configured");
        var account = DisplayLabel(origin?.Identity.AccountName, DisplayLabel(origin?.Identity.Name, "unavailable"));
        return "\x1b[1m" + Elide(connection, labelWidth) + "\x1b[22m · Team: " + Elide(team, labelWidth) +
            " · Bench: \x1b[1m" + Elide(DisplayLabel(_capture?.BenchName, "loading"), labelWidth) + "\x1b[22m · User: " + account +
            (_busy ? "  ◌ working" : "") + "\x1b[0m";
    }
    private string ModeLabel() => _screen switch { "bench" => _view.ToUpperInvariant(), "item" => "ITEM #" + _focused?.Id, "config" => "BENCH CONFIGURATION", "help" => "BROWSER HELP", _ => _screen.ToUpperInvariant() };
    private IEnumerable<Button> ContextButtons()
    {
        if (_requiresReconnect || _screen is not ("bench" or "item" or "review")) yield break;
        yield return CharacterButton(_width < 90 ? "[1]" : "[1 Tree]", '1'); yield return CharacterButton(_width < 90 ? "[2]" : "[2 Table]", '2');
        if (_screen != "review") yield return CharacterButton(_width < 90 ? "[3]" : "[3 Proposal Review]", '3');
    }
    private string ContextHeader()
    {
        if (_requiresReconnect) return _view.ToUpperInvariant() + " · Prior binding content cleared · Ctrl+R explicitly reconnects";
        var worktree = _capture?.Origin.WorktreeRoot;
        var buttons = string.Join(" ", ContextButtons().Select(button => button.Text));
        var prefix = ModeLabel() + (buttons.Length == 0 ? "" : " · " + buttons);
        var first = _width < 90 ? " · W: " : " · Worktree: ";
        var second = _width < 90 ? " · B: " : " · Branch: ";
        var nameWidth = Math.Max(1, (_width - BrowserTerminalText.CellWidth(prefix + first + second)) / 2);
        var name = string.IsNullOrWhiteSpace(worktree) ? "unavailable" : RichHtmlRenderer.SafeText(Path.GetFileName(Path.TrimEndingDirectorySeparator(worktree)));
        var context = prefix + first + Elide(name, nameWidth);
        var branchWidth = Math.Max(1, _width - BrowserTerminalText.CellWidth(context + second));
        context += second + Elide(DisplayLabel(_bench?.Branch, "unavailable"), branchWidth);
        if (!string.IsNullOrWhiteSpace(worktree)) context += " · " + RichHtmlRenderer.SafeText(worktree);
        if (_screen == "editor" && _editor is not null) context += " · #" + _editor.WorkItemId + (HasDirtyEditor() ? " · buffer modified" : _editor.HasLocalChanges ? " · local pending" : "");
        return context;
    }
    private static Button KeyButton(string text, BrowserKey key) => new(text, new(BrowserInputKind.Key, key));
    private static Button CharacterButton(string text, char character, BrowserModifiers modifiers = BrowserModifiers.None) => new(text, new(BrowserInputKind.Key, Character: character, Modifiers: modifiers));
    private IEnumerable<Button> PinButtons()
    {
        var item = SelectedPinItem();
        if (item is null || item.Seed || item.Id <= 0) yield break;
        yield return CharacterButton("[p " + (item.SinglePin ? "Unpin" : "Pin") + " single]", 'p');
        yield return CharacterButton("[P " + (item.TreePin ? "Unpin" : "Pin") + " subtree]", 'P');
    }
    private IEnumerable<Button> FooterButtons()
    {
        if (_requiresReconnect)
        { yield return CharacterButton("[Ctrl+R Reconnect]", 'r', BrowserModifiers.Control); yield return CharacterButton("[q Close]", 'q'); yield break; }
        if (_buffer is not null)
        {
            yield return _prompt is not null ? KeyButton(_prompt == "create" ? "[Enter Create]" : _prompt == "delete-confirm" ? "[Enter Confirm deletion]" : "[Enter Add]", BrowserKey.Enter)
                : CharacterButton("[Ctrl+S Stage locally]", 's', BrowserModifiers.Control);
            yield return KeyButton(_prompt is null ? "[Esc Retain]" : "[Esc Cancel]", BrowserKey.Escape); yield break;
        }
        if (_screen == "help") { yield return KeyButton("[Esc Back]", BrowserKey.Escape); yield return CharacterButton("[q Close]", 'q'); yield break; }
        if (_screen == "config")
        {
            yield return KeyButton("[Esc Back]", BrowserKey.Escape);
            if (_configSection == 0)
            {
                foreach (var button in PinButtons()) yield return button;
                yield return CharacterButton("[i ID]", 'i'); yield return CharacterButton("[I Subtree ID]", 'I');
            }
            else if (_configSection == 1)
            {
                yield return CharacterButton("[t Team areas]", 't'); yield return CharacterButton("[a Add]", 'a');
                if (_choice < _bench!.Areas.Count) yield return CharacterButton("[d Remove]", 'd');
                if (_choice >= _bench!.Areas.Count && _candidates.Count > 0 && !_sectionsFocused) yield return KeyButton("[Enter Add candidate]", BrowserKey.Enter);
                yield return CharacterButton("[x Exact]", 'x'); yield return CharacterButton("[u Under]", 'u');
            }
            else if (_configSection == 2)
            {
                yield return CharacterButton("[a Add]", 'a');
                if (_bench!.Sprints.Count > 0) yield return CharacterButton("[d Remove]", 'd');
            }
            else
            {
                yield return CharacterButton("[n Create]", 'n');
                if (_bench!.Benches.ElementAtOrDefault(_choice) is { } target)
                {
                    yield return KeyButton(_sectionsFocused ? "[Enter Items]" : "[Enter Select]", BrowserKey.Enter);
                    if (!target.IsDefault) yield return CharacterButton("[d Delete]", 'd');
                }
            }
            yield return CharacterButton("[s Sync Bench]", 's'); yield return CharacterButton("[R Refresh cache]", 'R');
            yield break;
        }
        if (_screen == "editor")
        {
            yield return KeyButton("[Enter Edit]", BrowserKey.Enter); yield return CharacterButton("[Ctrl+S Stage locally]", 's', BrowserModifiers.Control);
            yield return CharacterButton("[Ctrl+D Discard buffer]", 'd', BrowserModifiers.Control); yield return KeyButton("[Esc Back]", BrowserKey.Escape); yield break;
        }
        if (_screen == "review")
        {
            yield return CharacterButton("[D Details]", 'D'); yield return CharacterButton("[B Brief]", 'B'); yield return KeyButton("[Esc Back]", BrowserKey.Escape); yield break;
        }
        if (_screen == "item")
        {
            yield return CharacterButton("[E Edit item]", 'E'); yield return KeyButton("[Esc Back]", BrowserKey.Escape);
            yield return CharacterButton("[R Cache]", 'R'); yield return CharacterButton("[S Sync item]", 'S'); yield break;
        }
        if (_expanded is not null)
        {
            yield return CharacterButton("[v View item]", 'v'); yield return CharacterButton("[E Edit]", 'E');
            yield return CharacterButton("[S Sync item]", 'S'); yield return CharacterButton("[R Refresh cache]", 'R');
            yield return KeyButton("[Enter/Esc Collapse]", BrowserKey.Escape); yield break;
        }
        yield return KeyButton("[Enter Detail]", BrowserKey.Enter);
        yield return CharacterButton("[v View item]", 'v');
        foreach (var button in PinButtons()) yield return button;
        yield return CharacterButton("[b Bench]", 'b'); yield return CharacterButton("[? Help]", '?');
        yield return CharacterButton("[s Sync Bench]", 's'); yield return CharacterButton("[R Refresh cache]", 'R');
    }
    private void ClickFooter(int x) => ClickButtons(FooterButtons(), x, _requiresReconnect ? BrowserTerminalText.CellWidth("STOPPED · ") : 0);
    private void ClickButtons(IEnumerable<Button> buttons, int x, int column)
    {
        foreach (var button in buttons)
        {
            var end = column + BrowserTerminalText.CellWidth(button.Text);
            if (end <= _width && x >= column && x < end) { Input(button.Input); return; }
            column = end + 1;
        }
    }
    private string Footer()
    {
        var buttons = string.Join(" ", FooterButtons().Select(button => button.Text));
        if (_requiresReconnect) return "STOPPED · " + buttons;
        var help = _buffer is not null ? "Enter confirms · Esc cancels/retains · cursor arrows/Home/End" : _screen switch
        {
            "config" => (_sectionsFocused ? "↑/↓ sections · Enter/→ items" : "↑/↓ items · ← sections") + " · Tab/1/2/3/4 sections · s pull Bench · R refresh",
            "editor" => "Tab next · local staging only; no publishing",
            "item" => "READ ONLY · arrows/PgUp/PgDn/Home/End/wheel scroll · 3 proposal Review · q close",
            "review" => "READ ONLY · arrows/PgUp/PgDn/Home/End scroll",
            "help" => "↑/↓/j/k/PgUp/PgDn/Home/End/wheel scroll",
            _ when _expanded is not null => "↑/↓/j/k scroll · PgUp/PgDn/Home/End/wheel · selected item only",
            _ => "1 Tree · 2 Table · 3 proposal Review · E edit · R cache · S item sync · s Bench sync · ←/→/Space hierarchy · q close"
        };
        return buttons + " · " + help;
    }
}
