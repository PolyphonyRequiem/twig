using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Twig.Browser;

/// <summary>Incremental, bounded VT decoder. Paste is literal text, never a shortcut or control command.</summary>
internal sealed class BrowserVtInputParser
{
    private readonly Queue<BrowserInput> _ready = new();
    private readonly StringBuilder _escape = new(64);
    private bool _paste;
    private long _escapeStarted;
    private BrowserModifiers _modifiers;
    private const string PasteEnd = "\x1b[201~";

    internal void Feed(char value, BrowserModifiers modifiers = BrowserModifiers.None)
    {
        if (_ready.Count >= 4096) throw new IOException("Terminal input queue exceeded its bounded capacity.");
        if (_escape.Length == 0)
        {
            if (value == '\x1b') { _escape.Append(value); _escapeStarted = Stopwatch.GetTimestamp(); _modifiers = modifiers; }
            else Emit(value, modifiers);
            return;
        }
        _escape.Append(value);
        _escapeStarted = Stopwatch.GetTimestamp();
        if (_paste)
        {
            if (PasteEnd.StartsWith(_escape.ToString(), StringComparison.Ordinal))
            {
                if (_escape.Length == PasteEnd.Length) { _escape.Clear(); _paste = false; }
                return;
            }
            FlushLiteral();
            return;
        }
        if (_escape.Length == 2 && value is not ('[' or 'O'))
        {
            _escape.Clear(); Emit(value, modifiers | BrowserModifiers.Alt); return;
        }
        if (_escape.Length < 3) return;
        if (_escape.Length >= 64) { _escape.Clear(); return; }
        if (value is not (>= '@' and <= '~')) return;
        var sequence = _escape.ToString();
        _escape.Clear();
        if (sequence == "\x1b[200~") { _paste = true; return; }
        if (sequence.StartsWith("\x1b[<", StringComparison.Ordinal))
        {
            var fields = sequence.AsSpan(3, sequence.Length - 4);
            Span<Range> parts = stackalloc Range[3];
            if (fields.Split(parts, ';') != 3 || !int.TryParse(fields[parts[0]], out var buttons) ||
                !int.TryParse(fields[parts[1]], out var x) || !int.TryParse(fields[parts[2]], out var y) || x < 1 || y < 1) return;
            var mouseModifiers = ((buttons & 4) != 0 ? BrowserModifiers.Shift : 0) |
                ((buttons & 8) != 0 ? BrowserModifiers.Alt : 0) | ((buttons & 16) != 0 ? BrowserModifiers.Control : 0);
            _ready.Enqueue(new BrowserInput(BrowserInputKind.Mouse, Modifiers: mouseModifiers, X: x - 1, Y: y - 1,
                WheelDelta: (buttons & 64) != 0 ? ((buttons & 1) == 0 ? 1 : -1) : 0,
                ButtonDown: value == 'M' && (buttons & (64 | 32 | 3)) == 0));
            return;
        }
        var body = sequence.AsSpan(2, sequence.Length - 3);
        var separator = body.LastIndexOf(';');
        var keyModifiers = _modifiers;
        if (separator >= 0 && int.TryParse(body[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
        {
            bits--;
            keyModifiers |= ((bits & 1) != 0 ? BrowserModifiers.Shift : 0) |
                ((bits & 2) != 0 ? BrowserModifiers.Alt : 0) | ((bits & 4) != 0 ? BrowserModifiers.Control : 0);
            body = body[..separator];
        }
        BrowserKey? key = value switch
        {
            'A' => BrowserKey.Up,
            'B' => BrowserKey.Down,
            'C' => BrowserKey.Right,
            'D' => BrowserKey.Left,
            'H' => BrowserKey.Home,
            'F' => BrowserKey.End,
            'Z' => BrowserKey.Tab,
            '~' => body switch
            {
                "1" or "7" => BrowserKey.Home,
                "4" or "8" => BrowserKey.End,
                "3" => BrowserKey.Delete,
                "5" => BrowserKey.PageUp,
                "6" => BrowserKey.PageDown,
                _ => null
            },
            _ => null
        };
        if (key is not null) _ready.Enqueue(new BrowserInput(BrowserInputKind.Key, key.Value,
            Modifiers: keyModifiers | (value == 'Z' ? BrowserModifiers.Shift : 0)));
    }

    internal bool TryRead(out BrowserInput input)
    {
        if (_escape.Length > 0 && Stopwatch.GetElapsedTime(_escapeStarted).TotalMilliseconds >= (_escape.Length == 1 ? 40 : 250))
        {
            if (_paste) FlushLiteral();
            else
            {
                if (_escape.Length == 1)
                    _ready.Enqueue(new BrowserInput(BrowserInputKind.Key, BrowserKey.Escape, Modifiers: _modifiers));
                _escape.Clear();
            }
        }
        return _ready.TryDequeue(out input);
    }

    internal void EndInput()
    {
        // A truncated escape/paste must never become an executable shortcut.
        _escape.Clear(); _paste = false;
        _ready.Enqueue(new BrowserInput(BrowserInputKind.EndOfInput));
    }

    private void FlushLiteral()
    {
        foreach (var item in _escape.ToString()) Emit(item, BrowserModifiers.Paste);
        _escape.Clear();
    }

    private void Emit(char value, BrowserModifiers modifiers)
    {
        if (_paste || (modifiers & BrowserModifiers.Paste) != 0)
        {
            // Preserve whitespace/newlines in the edit buffer but discard terminal controls.
            if (value is '\r' or '\n' or '\t' || !char.IsControl(value))
                _ready.Enqueue(new BrowserInput(BrowserInputKind.Key, Character: value, Modifiers: BrowserModifiers.Paste));
            return;
        }
        var key = value switch
        {
            '\r' or '\n' => BrowserKey.Enter,
            '\x1b' => BrowserKey.Escape,
            '\b' or '\x7f' => BrowserKey.Backspace,
            '\t' => BrowserKey.Tab,
            _ => BrowserKey.Character
        };
        if (value is >= '\x01' and <= '\x1a' && key == BrowserKey.Character)
        { value = (char)('a' + value - 1); modifiers |= BrowserModifiers.Control; }
        _ready.Enqueue(new BrowserInput(BrowserInputKind.Key, key, value, modifiers));
    }
}
