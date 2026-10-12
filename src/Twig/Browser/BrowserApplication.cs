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
internal sealed class BrowserApplication(BrowserRuntimeFactory factory)
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
    private BrowserReviewObservation? _review;
    private IReadOnlyList<Twig.Domain.ValueObjects.AreaPathFilter> _candidates = [];
    private BrowserDetailObservation? _renderedDetail;
    private int _renderedWidth;
    private IReadOnlyList<string> _detailRows = [];
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
    private long _generation = 1, _serial;
    private string? _lastClick;
    private long _lastClickAt;
    private int _cursorRow;
    private volatile bool _running;
    private bool _busy, _requiresReconnect, _dirty = true, _reviewFull, _exact, _openingScreen;
    private sealed record Line(string Key, int Offset, string Text, int? Item = null, int Choice = -1);
    private sealed record Anchor(string Key, int Offset, int ScreenRow);
    private sealed record SavedViewport(int Selected, HashSet<int> Folded, Anchor? Anchor);
    private int ContentHeight => Math.Max(1, _height - 2);

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
                    terminal.Paint(_rows.Select(r => r.Text).ToArray(), _top, _width, _height, Header(), Footer());
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

    private BrowserIntentResult Result(bool ok, string message) => new(ok, message, _screen == "review" ? "review" : _view, _requiresReconnect, _generation);
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
        _index = null;
        _rows.Clear(); _fields.Clear(); _folded.Clear(); _edits.Clear(); _candidates = [];
        _renderedDetail = null; _renderedWidth = 0; _detailRows = [];
        _saved = null; _expanded = null; _buffer = null; _prompt = null; _deletion = null;
        _lastClick = null; _lastClickAt = 0;
        _top = 0; _selected = 0; _choice = 0; _field = 0; _cursor = 0; _cursorRow = 0; _configSection = 0;
        _screen = "bench"; _reviewFull = false; _exact = false;
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
            try { await predecessor; old?.Dispose(); _lifetime.ThrowIfCancellationRequested(); requestCt.ThrowIfCancellationRequested(); runtime = factory.Open(_cwd); }
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
            if (_bench is not null && !_bench.Items.Any(i => i.Id == _selected)) _selected = _bench.Items.FirstOrDefault()?.Id ?? 0;
            after?.Invoke();
        }, failed: () => Reply(failedReply, false, _message), requestCt: requestCt);
    }
    private void SetView(string view)
    {
        if (_screen == "editor") AcceptBuffer();
        if (_busy && _openingScreen) Cancel();
        CloseDetail(); _screen = "bench"; _view = view; _message = view == "tree" ? "Tree · local selection" : "Table · local selection"; Reflow();
    }
    private void OpenDetail(bool sync = false, bool refresh = false)
    {
        if (_capture is null || _selected == 0 || _runtime is null) return;
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
    private void CloseDetail()
    {
        if (_expanded is null) return;
        Cancel(); _expanded = null; _detail = null;
        if (_saved is { } saved)
        { _selected = saved.Selected; _folded.Clear(); _folded.UnionWith(saved.Folded); _saved = null; Reflow(false); Restore(saved.Anchor); }
        else Reflow();
    }

    private void OpenEditor()
    {
        if (_capture is null || _selected == 0 || _runtime is null) return;
        if (_editor is not null && _editor.WorkItemId == _selected)
        { _screen = "editor"; _top = 0; _message = "Retained edit buffer · Ctrl+S stages locally · Ctrl+D explicitly discards"; Reflow(); return; }
        if (HasDirtyEditor()) { _message = "Another item's unsaved edit buffer is retained. Reopen it and stage or explicitly discard first."; _dirty = true; return; }
        var id = _selected; var capture = _capture; var module = _runtime.Services.GetRequiredService<BrowserEditingModule>();
        Start(token => module.OpenAsync(id, capture, token), result =>
        {
            _editor = result.Value; _screen = "editor"; _top = 0; _field = 0; _edits.Clear();
            _fields = _editor!.Document.SystemControls.Concat(_editor.Document.Pages.SelectMany(p => p.AllGroups).SelectMany(g => g.Controls)).ToList();
            _message = "Document fields · Enter edit permitted field · Ctrl+S stages one local batch · Esc cancel";
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
            _screen = "editor"; _message = "Unsaved buffer retained. Ctrl+S stages; Ctrl+D explicitly discards; Esc returns without discarding."; Reflow();
            return;
        }
        if (reconnect) Reconnect(); else { _running = false; Cancel(); }
    }

    private void Change(Func<BrowserBenchModule, CancellationToken, Task<BrowserOperationResult<BrowserBenchChange>>> change)
    {
        if (_runtime is null || _bench is null) return;
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
            ReadBench(result.Capture, () => _message = message);
        });
    }

    private void Input(BrowserInput input)
    {
        if (input.Kind == BrowserInputKind.EndOfInput) { _running = false; Cancel(); return; }
        if (input.Kind == BrowserInputKind.Mouse)
        {
            if (input.WheelDelta != 0) Scroll(-Math.Sign(input.WheelDelta) * 3);
            if (input.ButtonDown && input.Y > 0 && input.Y < _height - 1)
            {
                var index = _top + input.Y - 1;
                if (index >= 0 && index < _rows.Count)
                {
                    var row = _rows[index];
                    var now = Environment.TickCount64;
                    var doubleClick = _lastClick == row.Key && now - _lastClickAt <= 400;
                    _lastClick = row.Key; _lastClickAt = now;
                    if (_screen == "bench" && row.Item is { } id)
                    {
                        _selected = id;
                        if (row.Key.StartsWith("item:", StringComparison.Ordinal) && row.Offset == 0 && _view == "tree" &&
                            input.X >= Depth(id) * 2 && input.X < Depth(id) * 2 + 2 && _index?.HasChildren(id) == true)
                            Fold(!_folded.Contains(id));
                        else if (doubleClick) { if (_expanded == id) CloseDetail(); else OpenDetail(); }
                        else Reflow();
                    }
                    else if (_screen == "config" && row.Key == "sections")
                    { _configSection = Math.Clamp(input.X / 11, 0, 3); _choice = 0; Reflow(); }
                    else if (_screen == "config" && row.Choice >= 0)
                    { _choice = row.Choice; Reflow(); if (doubleClick) ConfigInput(new(BrowserInputKind.Key, BrowserKey.Enter)); }
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
            else if (_screen == "editor") { Cancel(); AcceptBuffer(); _screen = "bench"; _top = 0; _message = "Edit buffer retained; E reopens it. Ctrl+D in editor explicitly discards."; Reflow(); }
            else if (_screen == "config") { Cancel(); _screen = "bench"; _top = 0; _deletion = null; Reflow(); }
            else CloseDetail();
            return;
        }
        if (input.Key is BrowserKey.PageUp or BrowserKey.PageDown) { Scroll(input.Key == BrowserKey.PageUp ? -ContentHeight : ContentHeight); return; }
        if (input.Key is BrowserKey.Home or BrowserKey.End)
        { _top = input.Key == BrowserKey.Home ? 0 : Math.Max(0, _rows.Count - ContentHeight); _dirty = true; return; }
        if (input.Key is BrowserKey.Up or BrowserKey.Down || input.Character is 'j' or 'k')
        { Move(input.Key == BrowserKey.Up || input.Character == 'k' ? -1 : 1); return; }
        if (_screen == "review")
        {
            if (input.Character is 'd' or 'D') ReviewGesture(true);
            else if (input.Character is 'b' or 'B') ReviewGesture(false);
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
        if (_screen == "config") { ConfigInput(input); return; }
        switch (input.Key)
        {
            case BrowserKey.Enter: if (_expanded == _selected) CloseDetail(); else OpenDetail(); return;
            case BrowserKey.Left: Fold(true); return;
            case BrowserKey.Right: Fold(false); return;
            case BrowserKey.Tab: SetView(_view == "tree" ? "table" : "tree"); return;
        }
        switch (input.Character)
        {
            case 'q': RequestLeave(false); break;
            case '1': SetView("tree"); break;
            case '2': SetView("table"); break;
            case '3': OpenReview(); break;
            case 'c': CloseDetail(); _screen = "config"; _choice = 0; _top = 0; Reflow(); break;
            case 'e': case 'E': OpenEditor(); break;
            case 'R': if (_expanded is not null) OpenDetail(refresh: true); else if (_runtime is not null) ReadBench(_capture); break;
            case 'S': OpenDetail(true); break;
            case 'p': case 'P': if (_bench is { } bench) { var id = _selected; var subtree = input.Character == 'P'; Change((m, t) => m.PinAsync(bench, id, subtree, t)); } break;
            case ' ': Fold(!_folded.Contains(_selected)); break;
            case '?': _message = "1 Tree / 2 Table / 3 Review / c Configure / E Edit / Enter Show / R cache / S sync / p single / P subtree / Ctrl+R reconnect / q quit"; _dirty = true; break;
        }
    }

    private void ConfigInput(BrowserInput input)
    {
        if (_bench is not { } bench) return;
        if (input.Character == 'R') { _deletion = null; _candidates = []; ReadBench(_capture); return; }
        if (input.Character is >= '1' and <= '4') { _configSection = input.Character - '1'; _choice = 0; _top = 0; Reflow(); return; }
        if (input.Key == BrowserKey.Tab) { _configSection = (_configSection + 1) % 4; _choice = 0; _top = 0; Reflow(); return; }
        if (input.Character is 'x' or 'u') { _exact = input.Character == 'x'; _message = _exact ? "Area candidate mode: Exact" : "Area candidate mode: Under"; Reflow(); return; }
        if (_configSection == 0)
        {
            var pinned = bench.Items.Where(i => i.SinglePin || i.TreePin).ToArray();
            if ((input.Character is 'p' or 'P') && _choice < pinned.Length)
            { var id = pinned[_choice].Id; var subtree = input.Character == 'P'; Change((m, t) => m.PinAsync(bench, id, subtree, t)); }
            else if (input.Character is 'a' or 'A') Prompt(input.Character == 'A' ? "pin-tree" : "pin-single", "Enter a positive ID; uncached pins remain unverified.");
        }
        else if (_configSection is 1 or 2)
        {
            var section = _configSection == 1 ? "area" : "sprint";
            if (input.Character == 'a') Prompt(section, section == "area" ? "Area path · x Exact / u Under before opening buffer" : "Sprint expression: @Current, offset, or absolute path");
            else if (input.Character == 'd')
            {
                var value = section == "area" ? _choice < bench.Areas.Count ? bench.Areas[_choice].Path : null : bench.Sprints.ElementAtOrDefault(_choice);
                if (value is not null) Change((m, t) => m.SettingsAsync(bench, section, "remove", value, ct: t));
            }
            else if (input.Character == 't' && section == "area" && _runtime is not null)
            {
                var module = _runtime.Services.GetRequiredService<BrowserBenchModule>();
                Start(t => module.ReadAreaCandidatesAsync(bench.Capture, t), r => { _candidates = r.Value!; _choice = bench.Areas.Count; _message = "Configured-team candidates fetched explicitly · x Exact / u Under · Enter selects candidate"; });
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
            else if (input.Character == 'd' && target is not null) Change((m, t) => m.DeleteAsync(bench.Capture, target, ct: t));
        }
    }
    private void Prompt(string purpose, string message)
    { _prompt = purpose; _buffer = ""; _cursor = 0; _message = message; Reflow(); Reveal("prompt"); }
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
            case "create": Change((m, t) => m.CreateAsync(bench.Capture, value, t)); break;
            case "delete-confirm": if (_deletion is { } target) Change((m, t) => m.DeleteAsync(bench.Capture, target, value, t)); break;
        }
    }

    private void Move(int delta)
    {
        if (_screen == "bench")
        {
            var ids = _rows.Where(r => r.Item.HasValue && r.Key.StartsWith("item:", StringComparison.Ordinal)).Select(r => r.Item!.Value).Distinct().ToArray();
            if (ids.Length == 0) return;
            var position = Array.IndexOf(ids, _selected); _selected = ids[Math.Clamp(position + delta, 0, ids.Length - 1)]; Reflow();
            Reveal("item:" + _selected);
        }
        else if (_screen == "editor")
        { _field = Math.Clamp(_field + delta, 0, Math.Max(0, _fields.Count - 1)); Reflow(); Reveal("field:" + _field); }
        else if (_screen == "config")
        { var count = _rows.Select(r => r.Choice).DefaultIfEmpty(-1).Max() + 1; _choice = Math.Clamp(_choice + delta, 0, Math.Max(0, count - 1)); Reflow(); Reveal("choice:" + _choice); }
        else Scroll(delta);
    }
    private void Reveal(string key)
    {
        var first = _rows.FindIndex(r => r.Key == key);
        if (first < _top) _top = Math.Max(0, first);
        else if (first >= _top + ContentHeight) _top = Math.Max(0, first - ContentHeight + 1);
        _dirty = true;
    }
    private void Scroll(int delta) { _top = Math.Clamp(_top + delta, 0, Math.Max(0, _rows.Count - ContentHeight)); _dirty = true; }
    private void Fold(bool fold)
    {
        if (_view != "tree" || _index?.HasChildren(_selected) != true) return;
        if (fold) _folded.Add(_selected); else _folded.Remove(_selected); Reflow();
    }
    private Anchor? Capture()
    {
        if (_rows.Count == 0) return null;
        var row = _rows[Math.Min(_top, _rows.Count - 1)]; return new(row.Key, row.Offset, 0);
    }
    private void Restore(Anchor? anchor)
    {
        if (anchor is not null)
        {
            var index = _rows.FindLastIndex(r => r.Key == anchor.Key && r.Offset <= anchor.Offset);
            if (index >= 0) _top = index - anchor.ScreenRow;
        }
        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count - ContentHeight)); _dirty = true;
    }
    private int Depth(int id) => _view == "table" ? 0 : _index?.Depth(id, _width) ?? 0;
    private int DetailWidth(int id) => Math.Max(1, _width - Depth(id) * 2 - 2);
    private IEnumerable<BrowserBenchItem> VisibleItems() => _index?.Visible(_view, _folded) ?? [];
    private static string Column(string value, int width)
    {
        var clipped = BrowserTerminalText.Clip(value, width);
        return clipped + new string(' ', Math.Max(0, width - BrowserTerminalText.CellWidth(clipped)));
    }
    private void Add(List<Line> rows, string key, string text, string prefix = "", int? item = null, int choice = -1, string color = "")
    {
        var first = true;
        var indent = new string(' ', Math.Min(prefix.Length, Math.Max(0, _width - 1)));
        foreach (var chunk in BrowserTerminalText.Wrap(RichHtmlRenderer.SafeText(text), Math.Max(1, _width - indent.Length)))
        { rows.Add(new(key, chunk.Offset, color + (first ? prefix : indent) + chunk.Text + "\x1b[0m", item, choice)); first = false; }
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
        else if (_screen == "config" && _bench is not null) ConfigRows(next);
        else if (_bench is not null)
        {
            if (_view == "table" && _width >= 48)
                Add(next, "columns", Column("ID", 12) + " " + Column("TYPE", 12) + " " + Column("STATE", 12) + " TITLE", color: "\x1b[1;36m");
            foreach (var item in VisibleItems())
            {
                var depth = Depth(item.Id); var indent = new string(' ', depth * 2);
                var disclosure = _view == "tree" && _index?.HasChildren(item.Id) == true ? (_folded.Contains(item.Id) ? "▶ " : "▼ ") : "· ";
                var tags = (item.SinglePin ? " p" : "") + (item.TreePin ? " P" : "") + (item.SubtreeOwners.Count > 0 ? " inherited" : "") + (item.Owed ? " owed" : "") + (item.Seed ? " seed" : "") + (!item.Member ? " ancestor" : "");
                var table = _view == "table" && _width >= 48;
                var prefix = table ? Column("#" + item.Id, 12) + " " + Column(item.Type, 12) + " " + Column(item.State, 12) + " " : indent + disclosure;
                Add(next, "item:" + item.Id, table ? item.Title + tags : $"#{item.Id} {item.Type} [{item.State}]{tags}  {item.Title}", prefix, item.Id,
                    color: item.Id == _selected ? "\x1b[48;2;27;55;74m\x1b[38;2;222;241;250m" : item.Owed ? "\x1b[38;2;239;188;92m" : "\x1b[38;2;162;193;207m");
                if (_expanded != item.Id) continue;
                var inset = indent + "  ";
                if (_detail is not null)
                {
                    var detailWidth = DetailWidth(item.Id);
                    if (!ReferenceEquals(_renderedDetail, _detail) || _renderedWidth != detailWidth)
                    {
                        _detailRows = BrowserTerminalText.Rows(_detail.RenderAsync(detailWidth).GetAwaiter().GetResult().Ansi, detailWidth);
                        _renderedDetail = _detail; _renderedWidth = detailWidth;
                    }
                    var lines = _detailRows;
                    for (var i = 0; i < lines.Count; i++) next.Add(new("detail:" + item.Id, i, inset + lines[i], item.Id));
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
        _rows = next;
        if (preserve) Restore(anchor); else _top = Math.Clamp(_top, 0, Math.Max(0, next.Count - ContentHeight));
        _dirty = true;
    }
    private void EditorRows(List<Line> rows)
    {
        var index = 0;
        foreach (var control in _editor!.Document.SystemControls) EditorControlRows(rows, control, ref index);
        foreach (var page in _editor!.Document.Pages)
        {
            Add(rows, "page:" + page.Id, page.Label + (page.Visible ? "" : " [hidden in server layout]") +
                (page.CarriesFieldControls ? "" : " [server-rendered page; unsupported here]"), color: "\x1b[1;36m");
            foreach (var group in page.AllGroups)
            {
                Add(rows, "group:" + group.Id, group.Label + (group.IsContribution ? " [unsupported contribution]" : ""), color: "\x1b[36m");
                foreach (var control in group.Controls)
                {
                    EditorControlRows(rows, control, ref index);
                }
            }
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
    private void ConfigRows(List<Line> rows)
    {
        Add(rows, "sections", "1 Pins    2 Areas    3 Sprints    4 Benches", color: "\x1b[1;36m");
        Add(rows, "ownership", "Automatic ownership: " + (_bench!.Ownership.Length == 0 ? "none" : _bench.Ownership));
        var choices = _configSection switch
        {
            0 => _bench.Items.Where(i => i.SinglePin || i.TreePin).Select(i => $"#{i.Id} {(i.SinglePin ? "single p" : "")} {(i.TreePin ? "subtree P" : "")} · {i.Title}").ToArray(),
            1 => _bench.Areas.Select(a => a.Path + (a.IncludeChildren ? " · Under" : " · Exact")).Concat(_candidates.Select(a => "Candidate: " + a.Path + (_exact ? " · Exact" : " · Under"))).ToArray(),
            2 => _bench.Sprints.ToArray(),
            _ => _bench.Benches.Select(b => b.Name + (b.IsDefault ? " [protected Default]" : "") + (b.Id == _bench.Capture.BenchId ? " [current]" : "") + " · " + b.Contents.Count + " selectors").ToArray()
        };
        for (var i = 0; i < choices.Length; i++) Add(rows, "choice:" + i, choices[i], i == _choice ? "› " : "  ", choice: i, color: i == _choice ? "\x1b[1;37m" : "");
        if (choices.Length == 0) Add(rows, "no-choices", _configSection == 2 ? "No sprints: automatic membership disabled; seeds/owed work still appear." : "No saved entries in this section.");
        if (_deletion is { } deletion)
        {
            Add(rows, "delete", "Delete arrangement: " + deletion.Name, color: "\x1b[1;33m");
            foreach (var content in deletion.Contents) Add(rows, "delete-content:" + content, content);
            Add(rows, "delete-digest", "Captured contents digest: " + deletion.ContentsDigest);
        }
        if (_buffer is not null) Add(rows, "prompt", _message + "\n> " + _buffer.Insert(_cursor, "▏"), color: "\x1b[1;33m");
    }
    private string Header() => "\x1b[1mTWIG  /  " + (_screen == "bench" ? _view.ToUpperInvariant() : _screen.ToUpperInvariant()) + "  /  " +
        (_capture?.BenchName ?? "disconnected") + (_screen == "editor" && _editor is not null
            ? "  /  #" + _editor.WorkItemId + (HasDirtyEditor() ? " · buffer modified" : _editor.HasLocalChanges ? " · local pending" : "") : "") +
        (_busy ? "  ◌ working" : "") + "\x1b[0m";
    private string Footer()
    {
        if (_requiresReconnect) return "STOPPED · " + _message + " · Ctrl+R reconnect / q close";
        var help = _screen switch
        {
            "config" => _configSection switch { 0 => "p/P toggle · a/A manual single/tree ID", 1 => "a add · d remove · t team candidates · x Exact/u Under · Enter select", 2 => "a add @Current/offset/path · d remove", _ => "n create · Enter switch · d delete" } + " · Tab section · Esc back",
            "editor" => "Enter edit · Tab accept/next · Ctrl+S local batch · Ctrl+D discard buffer · Esc back",
            "review" => "D details · B brief · Esc/c back · READ ONLY",
            _ => "Enter Show · ←/→ folds · p/P pins · E edit · c configure · R cache · S sync · 1/2 views · 3 Review · q quit"
        };
        return _message + "  |  " + help;
    }
}
