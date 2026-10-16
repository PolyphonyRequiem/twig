using System.Collections.Concurrent;

namespace Twig.TuiAotPrototype1143;

// All mutable view state is owned by the terminal thread. Background work only enqueues closures.
internal sealed class Browser(SourceContext source, string initialView) : IDisposable
{
    internal readonly ConcurrentQueue<Action> Events = new();
    private readonly HashSet<int> folded = [];
    private List<ScreenLine> rows = [];
    private int selected = 1, top, width = 80, height = 24;
    private string view = initialView;
    private int? detail;
    private IReadOnlyList<(string Key, string Text)>? bodyBlocks;
    private CancellationTokenSource? loading;
    private long generation = 1, requestSerial;
    private int lateDropped, canceled, loads, keys, mouse, resizes, reflows;
    private bool nonCooperative, dirty = true;
    private uint previousButtons;
    private SavedViewport? saved;
    private Anchor? loadingAnchor;
    private string message = "Fixture-only; no store/auth/network. Enter detail; ? instructions";
    internal bool Running { get; private set; } = true;
    private sealed record Anchor(string Key, int Offset, int ScreenRow);
    private sealed record SavedViewport(int Selected, HashSet<int> Folded, Anchor? Anchor);

    internal void Run(WindowsTerminal terminal, ControlServer? server)
    {
        Reflow();
        while (Running)
        {
            while (Events.TryDequeue(out var apply)) apply();
            var geometry = terminal.Geometry();
            if (geometry.Width != width || geometry.Height != height)
            {
                width = geometry.Width; height = geometry.Height; resizes++;
                Reflow();
            }
            if (dirty)
            {
                WindowsTerminal.Paint(rows, top, width, height, selected,
                    $"AOT1143 THROWAWAY | {view} | source {source.Workspace}/{source.Tab}/{source.Pane} | {source.Cwd}",
                    $"sel#{selected} scroll{top} gen{generation} load{loads} late{lateDropped} cancel{canceled} key{keys} mouse{mouse} resize{resizes} | {message}");
                dirty = false;
            }
            if (!terminal.Read(out var input)) continue;
            if (input.Type == 1 && input.KeyDown != 0)
            {
                keys++;
                for (var i = 0; i < Math.Max(1, (int)input.Repeat); i++) Key(input.VirtualKey, input.Character, input.KeyModifiers);
            }
            else if (input.Type == 2)
            {
                mouse++;
                var y = input.MouseY - geometry.Top - 1;
                if (input.MouseFlags == 4) Scroll((short)(input.Buttons >> 16) > 0 ? -3 : 3);
                else if ((input.MouseFlags == 0 || input.MouseFlags == 2) && (input.Buttons & 1) != 0 && (previousButtons & 1) == 0)
                {
                    var index = top + y;
                    if (y >= 0 && y < ContentHeight && index >= 0 && index < rows.Count)
                    {
                        var row = rows[index];
                        if (row.Bench)
                        {
                            if (detail is not null) CloseDetail();
                            selected = row.ItemId;
                            if (input.MouseX - geometry.Left < 4 && HasChildren(selected)) ToggleFold();
                            else if (input.MouseFlags == 2) OpenDetail();
                            dirty = true;
                        }
                    }
                }
                previousButtons = input.Buttons;
            }
            dirty = true;
        }
        _ = server;
    }

    private int ContentHeight => Math.Max(1, height - 2);
    private bool HasChildren(int id) => Fixtures.Items.Any(i => i.ParentId == id);
    private List<FixtureItem> Visible() => Fixtures.Items.Where(i => view == "table" || i.ParentId is null || !folded.Contains(i.ParentId.Value)).ToList();

    private void Key(ushort key, char character, uint modifiers)
    {
        if ((modifiers & 12) != 0 && key == 67 || character == '\x03' || character == 'q') { Stop(); return; }
        if (key == 27) { if (detail is not null) CloseDetail(); else Stop(); return; }
        if (key == 13) { if (detail is not null) CloseDetail(); else OpenDetail(); return; }
        if (key == 33) { Scroll(-ContentHeight); return; }
        if (key == 34) { Scroll(ContentHeight); return; }
        if (key == 36) { loadingAnchor = null; top = 0; dirty = true; return; }
        if (key == 35) { loadingAnchor = null; top = Math.Max(0, rows.Count - ContentHeight); dirty = true; return; }
        if (key == 38 || character == 'k') { if (detail is null) Move(-1); else Scroll(-1); return; }
        if (key == 40 || character == 'j') { if (detail is null) Move(1); else Scroll(1); return; }
        if (key == 37 || key == 39 || character == ' ')
        {
            if (detail is not null) return;
            if (key == 37) folded.Add(selected);
            else if (key == 39) folded.Remove(selected);
            else ToggleFold();
            Reflow(); return;
        }
        switch (character)
        {
            case '1': SetView("table"); break;
            case '2': SetView("tree"); break;
            case 'r': case 'R': if (detail is not null) BeginLoad(); else message = "R: fixture memory only; no cache/store read"; break;
            case 's': case 'S': message = "S deliberately unavailable: fixture-only, no root/link transport or publication"; break;
            case 'g': generation++; CancelLoad(); bodyBlocks = null; if (detail is not null) BeginLoad(); message = "Fixture generation replaced; not an authority/principal test"; Reflow(); break;
            case 'l': nonCooperative = !nonCooperative; message = "Next fixture result ignores cancellation: " + nonCooperative; break;
            case 'a':
                // Put the first following Bench row at screen row 3 before a height-change exercise.
                if (detail is not null)
                {
                    loadingAnchor = null;
                    var following = rows.FindIndex(r => r.Bench && r.ItemId > detail.Value);
                    if (following >= 0) top = Math.Clamp(following - 3, 0, Math.Max(0, rows.Count - ContentHeight));
                }
                message = "Following-row anchor positioned; R reload changes height"; break;
            case '?': message = "j/k arrows; Enter/Esc detail; wheel/Pg/Home/End; fold Space;1 Table2 Tree;R load;L late;G gen;A anchor;Q quit"; break;
        }
    }

    private void Move(int delta)
    {
        var visible = Visible();
        var index = visible.FindIndex(i => i.Id == selected);
        selected = visible[Math.Clamp(index + delta, 0, visible.Count - 1)].Id;
        var line = rows.FindIndex(r => r.Bench && r.ItemId == selected);
        if (line < top) top = line;
        else if (line >= top + ContentHeight) top = line - ContentHeight + 1;
        dirty = true;
    }

    private void Scroll(int delta) { loadingAnchor = null; top = Math.Clamp(top + delta, 0, Math.Max(0, rows.Count - ContentHeight)); dirty = true; }
    private void ToggleFold() { if (!HasChildren(selected)) return; if (!folded.Add(selected)) folded.Remove(selected); Reflow(); }

    private Anchor? Capture(bool preferFollowing)
    {
        if (rows.Count == 0) return null;
        if (bodyBlocks is null && loadingAnchor is not null) return loadingAnchor;
        if (preferFollowing && detail is not null && !rows.Skip(top).Take(ContentHeight).Any(r => r.Bench && r.ItemId == detail.Value))
            for (var i = top; i < Math.Min(rows.Count, top + ContentHeight); i++)
                if (rows[i].Bench && rows[i].ItemId > detail.Value) return new Anchor(rows[i].Key, rows[i].Offset, i - top);
        var first = rows[Math.Min(top, rows.Count - 1)];
        return new Anchor(first.Key, first.Offset, 0);
    }

    private void Restore(Anchor? anchor)
    {
        if (anchor is not null)
        {
            var candidates = rows.Select((row, index) => (row, index)).Where(r => r.row.Key == anchor.Key && r.row.Offset <= anchor.Offset).ToArray();
            if (candidates.Length > 0) top = candidates[^1].index - anchor.ScreenRow;
        }
        top = Math.Clamp(top, 0, Math.Max(0, rows.Count - ContentHeight));
    }

    private void Reflow(bool preserve = true)
    {
        var anchor = preserve ? Capture(true) : null;
        var next = new List<ScreenLine>();
        foreach (var item in Visible())
        {
            var indent = view == "tree" && item.ParentId is not null ? "  " : "";
            var disclosure = HasChildren(item.Id) && view == "tree" ? folded.Contains(item.Id) ? "▶ " : "▼ " : "· ";
            var prefix = indent + disclosure;
            var text = $"#{item.Id} {item.Type} [{item.State}] {item.Title}";
            foreach (var part in CellText.Wrap(text, Math.Max(1, width - prefix.Length)))
                next.Add(new ScreenLine("bench:" + item.Id, part.Offset, prefix + part.Text, item.Id, true));
            if (detail != item.Id) continue;
            var inset = indent + "  ";
            var ruleWidth = Math.Max(1, width - inset.Length - 1);
            next.Add(new ScreenLine("detail:top", 0, inset + "┌" + new string('─', ruleWidth), item.Id, false));
            var body = bodyBlocks ?? new List<(string Key, string Text)> { ("loading", $"Loading fixture #{item.Id} generation{generation}; no ADO/network/store") };
            foreach (var block in body)
                foreach (var part in CellText.Wrap(block.Text, Math.Max(1, width - inset.Length - 2)))
                    next.Add(new ScreenLine("detail:" + item.Id + ":" + block.Key, part.Offset, inset + "│ " + part.Text, item.Id, false));
            next.Add(new ScreenLine("detail:bottom", 0, inset + "└" + new string('─', ruleWidth), item.Id, false));
        }
        rows = next; reflows++;
        if (preserve) Restore(anchor);
        dirty = true;
    }

    private void OpenDetail()
    {
        saved = new SavedViewport(selected, new HashSet<int>(folded), Capture(false));
        detail = selected; bodyBlocks = null; BeginLoad(); Reflow();
    }

    private void CancelLoad()
    {
        requestSerial++;
        if (loading is null) return;
        loading.Cancel(); loading.Dispose(); loading = null;
    }

    private void BeginLoad()
    {
        CancelLoad();
        if (detail is null) return;
        loadingAnchor = Capture(true);
        bodyBlocks = null;
        var itemId = detail.Value;
        var capturedGeneration = generation;
        var serial = ++requestSerial;
        var ignoreCancellation = nonCooperative;
        loading = new CancellationTokenSource();
        var ct = loading.Token;
        loads++;
        Reflow();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1800, ignoreCancellation ? CancellationToken.None : ct);
                var result = Fixtures.Html(itemId);
                Events.Enqueue(() =>
                {
                    if (!Running || ct.IsCancellationRequested || serial != requestSerial || generation != capturedGeneration || detail != itemId)
                    { lateDropped++; dirty = true; return; }
                    var completedAnchor = Capture(true);
                    bodyBlocks = NativeDetail.Body(Fixtures.Items.First(i => i.Id == itemId), 10000, result);
                    message = "Native HTML complete; scroll to HTML_TAIL; A then R tests following anchor"; Reflow(false); Restore(completedAnchor); loadingAnchor = null;
                });
            }
            catch (OperationCanceledException) { Events.Enqueue(() => { canceled++; dirty = true; }); }
        });
    }

    private void CloseDetail()
    {
        CancelLoad(); detail = null; bodyBlocks = null; loadingAnchor = null;
        if (saved is { } old)
        {
            selected = old.Selected; folded.Clear(); folded.UnionWith(old.Folded);
            Reflow(false); Restore(old.Anchor); saved = null;
        }
        else Reflow();
        message = "Selection/folds/semantic viewport restored";
    }

    private void SetView(string next)
    {
        if (next is not ("tree" or "table")) throw new ArgumentException("Only tree/table view intents are supported.");
        if (detail is not null) CloseDetail();
        generation++; CancelLoad(); view = next;
        if (!Visible().Any(i => i.Id == selected)) selected = Fixtures.Items.First(i => i.ParentId is null).Id;
        Reflow(); message = "External/local view intent applied; same immutable source";
    }

    internal ViewSnapshot Snapshot() => new(Environment.ProcessId, source, view, selected, detail, top, width, height,
        generation, requestSerial, bodyBlocks is not null, loads, lateDropped, canceled, keys, mouse, resizes, reflows,
        rows.Skip(top).Take(ContentHeight).Select(r => r.Text).ToArray(), message);

    internal ControlReply Apply(ControlRequest request)
    {
        if (request.Source != source) return new ControlReply(false, "Captured source context mismatch; reopen explicitly from the original source.", Snapshot());
        switch (request.Command)
        {
            case "status": break;
            case "view": SetView(request.View ?? ""); break;
            case "close": Stop(); break;
            default: return new ControlReply(false, "Unsupported command.", Snapshot());
        }
        return new ControlReply(true, null, Snapshot());
    }

    private void Stop() { generation++; CancelLoad(); Running = false; }
    public void Dispose() { Stop(); }
}
