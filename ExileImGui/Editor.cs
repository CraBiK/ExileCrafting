using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using SColor = SharpDX.Color;

namespace ExileImGui;

/// <summary>
/// What a tokenizer can label a run of characters as. The palette maps these to colours, so two
/// grammars don't each invent their own colour vocabulary.
/// </summary>
public enum TokenKind
{
    /// <summary>Uncoloured. Falls back to <see cref="EditorOpts.Foreground"/>.</summary>
    Text,
    /// <summary>Language keyword.</summary>
    Keyword,
    /// <summary>Type name, including builtin type keywords.</summary>
    Type,
    /// <summary>Numeric literal.</summary>
    Number,
    /// <summary>String or char literal. Also counts as a literal for bracket matching.</summary>
    String,
    /// <summary>Comment. Also counts as a literal for bracket matching.</summary>
    Comment,
    /// <summary>Punctuation and operators.</summary>
    Operator,
    /// <summary>A plain name. Falls back to <see cref="EditorOpts.Foreground"/>.</summary>
    Identifier,
    /// <summary>Error colour, also used for the error underline.</summary>
    Error,
}

/// <summary>
/// One coloured run within a single line. Zero-length tokens are legal; they just paint nothing.
/// </summary>
public readonly struct Token
{
    /// <summary>Char offset within the LINE, not the document.</summary>
    public readonly int Start;

    /// <summary>Run length in chars. Must not reach past the end of the line.</summary>
    public readonly int Length;

    /// <summary>Which palette entry paints this run.</summary>
    public readonly TokenKind Kind;

    /// <summary>Labels the run [start, start + length) on one line.</summary>
    public Token(int start, int length, TokenKind kind) { Start = start; Length = length; Kind = kind; }
}

/// <summary>
/// Lexes one line at a time, carrying an int of state across the line boundary so a block comment or
/// an unterminated string can span lines.
/// </summary>
/// <param name="line">The line to lex, with no trailing newline.</param>
/// <param name="stateIn">State returned for the line above. 0 for the first line.</param>
/// <param name="into">Append tokens here in ascending Start order; the list arrives cleared.</param>
/// <returns>The state the next line starts in.</returns>
public delegate int Tokenizer(string line, int stateIn, List<Token> into);

/// <summary>
/// Everything a <see cref="Completer"/> is told about where the caret is.
/// </summary>
public readonly struct CompletionContext
{
    /// <summary>The whole document.</summary>
    public readonly string Text;

    /// <summary>
    /// Caret offset. This is ImGui's own utf-8 BYTE offset reused as a utf-16 char index, so it
    /// matches ASCII text exactly but sits short of the real caret past any multi-byte character - a
    /// PoE1 base name with an accented letter is enough to trigger that, so clamp before indexing
    /// with it.
    /// </summary>
    public readonly int Caret;

    /// <summary>Start of the word being completed. A commit replaces [WordStart, Caret).</summary>
    public readonly int WordStart;

    /// <summary>The text in [WordStart, Caret) - what the user has typed so far.</summary>
    public readonly string Word;

    /// <summary>'.' when member access opened the popup, otherwise '\0'.</summary>
    public readonly char Trigger;

    /// <summary>Packages one completion query.</summary>
    public CompletionContext(string text, int caret, int wordStart, string word, char trigger)
    {
        Text = text; Caret = caret; WordStart = wordStart; Word = word; Trigger = trigger;
    }
}

/// <summary>
/// One row offered in the completion popup.
/// </summary>
public readonly struct Completion
{
    /// <summary>Text that replaces [WordStart, Caret) on commit.</summary>
    public readonly string Insert;

    /// <summary>What the row shows. Null falls back to <see cref="Insert"/>.</summary>
    public readonly string Label;

    /// <summary>Dim right-hand column, usually a type name. Optional.</summary>
    public readonly string Detail;

    /// <summary>Colours the row's label from the palette.</summary>
    public readonly TokenKind Kind;

    /// <summary>
    /// Describes one candidate. Pass <paramref name="label"/> when the row should read differently
    /// from what gets inserted, for example "Substring()" inserting "Substring(".
    /// </summary>
    public Completion(string insert, string detail = null, TokenKind kind = TokenKind.Identifier, string label = null)
    {
        Insert = insert; Detail = detail; Kind = kind; Label = label ?? insert;
    }
}

/// <summary>
/// Produces the completion candidates for a caret position. Append to <c>into</c>, and never throw -
/// a completer that throws is switched off for that editor's lifetime.
/// </summary>
public delegate void Completer(in CompletionContext ctx, List<Completion> into);

/// <summary>
/// Options for <see cref="Editor.Code(string, ref string, EditorOpts)"/>. Build one once and reuse
/// it - a fresh instance per frame allocates inside the render loop.
/// </summary>
public sealed class EditorOpts
{
    /// <summary>Grammar for highlighting. Null gives plain text.</summary>
    public Tokenizer Tokenize;

    /// <summary>Completion provider. Null gives no popup.</summary>
    public Completer Complete;

    /// <summary>
    /// Optional row drawn above the scrolling text, outside it, with a separator under it, so it
    /// never scrolls away. Its height comes out of <see cref="Size"/>. A throw here is swallowed and
    /// retried next frame rather than taking the editor down.
    /// </summary>
    public Action Toolbar;

    /// <summary>Editor size. A zero width or height fills the remaining content region on that axis.</summary>
    public Vector2 Size;

    /// <summary>Spaces per indent level.</summary>
    public int TabWidth = 4;

    /// <summary>Insert spaces rather than tab chars, so the overlay never has to measure a tab glyph.</summary>
    public bool SoftTabs = true;

    /// <summary>Draw the line-number gutter.</summary>
    public bool LineNumbers = true;

    /// <summary>Box the bracket at the caret and its mate.</summary>
    public bool BracketMatch = true;

    /// <summary>
    /// 0-based line to message, drawn as an underline plus a hover tooltip. Held by reference, so
    /// clearing your dictionary clears the underlines. Safe to refresh from a background compiler -
    /// a mutation caught mid-draw skips one frame rather than throwing.
    /// </summary>
    public IReadOnlyDictionary<int, string> Errors;

    /// <summary>Token colours. Null uses <see cref="Editor.DefaultPalette"/>.</summary>
    public IReadOnlyDictionary<TokenKind, SColor> Palette;

    /// <summary>
    /// Editor background. The editor owns its own surface rather than inheriting the host theme - a
    /// syntax palette tuned for a dark background reads wrong on whatever FrameBg the user's theme
    /// picked. Still overridable for a caller that genuinely wants to track the theme.
    /// </summary>
    public SColor Background = new SColor(18, 18, 20, 255);

    /// <summary>
    /// Text colour for <see cref="TokenKind.Text"/> and <see cref="TokenKind.Identifier"/>, and for
    /// the caret, the bracket boxes and the popup chrome.
    /// </summary>
    public SColor Foreground = new SColor(212, 212, 212, 255);
}

/// <summary>
/// A multi-line code editor built on InputTextMultiline, with pluggable syntax highlighting and
/// autocomplete.
/// <para>
/// The widget still owns the caret, selection, mouse and undo. This control hides its glyphs behind
/// a transparent frame and paints coloured tokens over them, and takes scrolling away from the
/// widget so the gutter, the caret and the error underline all stay in step with what it painted.
/// </para>
/// <para>
/// All of it lives in one file on purpose: the toolkit ships by copying sources into a plugin, so one
/// file to drop in beats a tidier split. The grammar providers are the exception, one optional file
/// each, so a plugin copies only the languages it cares about.
/// </para>
/// </summary>
public static class Editor
{
    /// <summary>
    /// Collapses CRLF and lone CR to LF. ImGui counts a \r as a char in its buffer but the line model
    /// drops it, and that gap is what walks the caret off the real line the longer a document gets.
    /// <para>
    /// Called from the <see cref="Code(string, ref string, EditorOpts)"/> entry points every frame,
    /// so the common already-LF case returns the same reference without allocating.
    /// </para>
    /// </summary>
    public static string NormalizeLineEndings(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        int i = text.IndexOf('\r');
        if (i < 0) return text;   // nothing to do, hand back the same string

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, i);
        for (int j = i; j < text.Length; j++)
        {
            char c = text[j];
            if (c != '\r') { sb.Append(c); continue; }
            sb.Append('\n');
            if (j + 1 < text.Length && text[j + 1] == '\n') j++;   // crlf collapses to one \n, not two
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits on \n and drops a trailing \r, so a CRLF file lines up the same as an LF one. Always
    /// leaves at least one line, since an empty document still has a caret on line 0.
    /// </summary>
    /// <param name="into">Cleared and refilled.</param>
    public static void SplitLines(string text, List<string> into)
    {
        into.Clear();
        text ??= "";
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                int end = i;
                if (end > start && text[end - 1] == '\r') end--;
                into.Add(text.Substring(start, end - start));
                start = i + 1;
            }
        }
        if (into.Count == 0) into.Add("");
    }

    /// <summary>
    /// Char index the given line starts at. Counts one separator per line, which is why the control
    /// normalizes CRLF out of the buffer before anything reaches here.
    /// </summary>
    public static int LineStart(List<string> lines, int line)
    {
        int n = 0;
        for (int i = 0; i < line && i < lines.Count; i++) n += lines[i].Length + 1;
        return n;
    }

    /// <summary>
    /// Inverse of <see cref="LineStart"/>. A caret sitting exactly on a boundary belongs to the LATER
    /// line, which is what ImGui does when you press End then Right. A caret past the end clamps to
    /// the last line.
    /// </summary>
    public static (int line, int col) CaretLineCol(List<string> lines, int caret)
    {
        if (caret < 0) return (0, 0);
        int n = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            int len = lines[i].Length;
            if (caret <= n + len) return (i, caret - n);
            n += len + 1;
        }
        int last = lines.Count - 1;
        return (last, lines[last].Length);
    }

    /// <summary>
    /// Adds or removes one indent level on every line the selection touches.
    /// <para>
    /// Rejoins with a bare \n. That is not a behaviour change for a caller: the Code entry points
    /// normalize CRLF out before text ever reaches here, so the input is already LF-only.
    /// </para>
    /// </summary>
    /// <param name="soft">Insert spaces rather than a tab char.</param>
    /// <param name="remove">Outdent instead of indent. Strips whatever leading whitespace there is,
    /// even when it is less than one full level.</param>
    /// <param name="newSelStart">Where the selection start should move to.</param>
    /// <param name="newSelEnd">Where the selection end should move to.</param>
    /// <returns>The new document text.</returns>
    public static string IndentBlock(string text, int selStart, int selEnd, int tabWidth, bool soft,
        bool remove, out int newSelStart, out int newSelEnd)
    {
        text ??= "";
        if (selStart > selEnd) (selStart, selEnd) = (selEnd, selStart);
        selStart = Math.Clamp(selStart, 0, text.Length);
        selEnd = Math.Clamp(selEnd, 0, text.Length);

        var lines = new List<string>();
        SplitLines(text, lines);
        var (firstLine, firstCol) = CaretLineCol(lines, selStart);
        var (lastLine, lastCol) = CaretLineCol(lines, selEnd);
        // a selection ending exactly at a line start did not really reach that line
        if (lastLine > firstLine && lastCol == 0) lastLine--;

        int firstLineStart = LineStart(lines, firstLine);   // snapshot before any line changes length

        string pad = soft ? new string(' ', Math.Max(1, tabWidth)) : "\t";
        int delta = 0;
        int firstLineStrip = 0;   // what came off the first touched line specifically, unindent only
        for (int i = firstLine; i <= lastLine && i < lines.Count; i++)
        {
            if (!remove) { lines[i] = pad + lines[i]; delta += pad.Length; continue; }

            int strip = 0;
            if (lines[i].StartsWith("\t", StringComparison.Ordinal)) strip = 1;
            else while (strip < pad.Length && strip < lines[i].Length && lines[i][strip] == ' ') strip++;
            if (strip > 0) { lines[i] = lines[i].Substring(strip); delta -= strip; }
            if (i == firstLine) firstLineStrip = strip;
        }

        // a column at the line start stays pinned there (selection just grows to cover the new
        // pad); one further in shifts by the full pad length, clamped to 0 rather than negative.
        int newFirstCol = remove
            ? (firstCol >= firstLineStrip ? firstCol - firstLineStrip : 0)
            : (firstCol == 0 ? 0 : firstCol + pad.Length);
        newSelStart = firstLineStart + newFirstCol;
        newSelEnd = Math.Max(0, selEnd + delta);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Replaces every tab with <paramref name="tabWidth"/> spaces in one pass over the whole buffer -
    /// the shape that lets a tab-indented paste land as one pending edit and one undo step, rather
    /// than one edit per tab per frame. A non-positive width still expands to at least one space.
    /// </summary>
    public static string ExpandTabs(string text, int tabWidth)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\t') < 0) return text ?? "";
        return text.Replace("\t", new string(' ', Math.Max(1, tabWidth)));
    }

    /// <summary>
    /// Where a caret sitting in <paramref name="text"/> ends up once <see cref="ExpandTabs"/> has run
    /// over it. Both the argument and the result are utf-8 BYTE offsets, since that is what ImGui's
    /// callback deals in.
    /// <para>
    /// Without this the whole-buffer replace that does the expansion parks the caret at the end of
    /// the document - imgui's own DeleteChars/InsertChars walk CursorPos along with the edit - so
    /// pressing Tab to indent would fling you to the bottom of the file.
    /// </para>
    /// </summary>
    public static int ExpandTabsCaret(string text, int caretBytes, int tabWidth)
    {
        text ??= "";
        if (caretBytes <= 0) return Math.Max(0, caretBytes);
        int w = Math.Max(1, tabWidth);
        int seen = 0;    // bytes of the original walked so far
        int shift = 0;   // extra bytes the expansion put in ahead of the caret
        for (int i = 0; i < text.Length && seen < caretBytes; i++)
        {
            char c = text[i];
            if (c == '\t') { seen++; shift += w - 1; continue; }
            // utf-8 byte length of this char, surrogate pair counted once as 4
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { seen += 4; i++; }
            else if (c < 0x80) seen += 1;
            else if (c < 0x800) seen += 2;
            else seen += 3;
        }
        return caretBytes + shift;
    }

    // per-id retained view state. same shape as ListEditor._filters - two editors sharing an id
    // share their popup, find text and scroll, so give each one its own.
    internal sealed class State
    {
        public readonly List<string> Lines = new();
        public readonly List<List<Token>> Tokens = new();
        public readonly List<int> LexState = new();   // lexer state AFTER line i
        public string LastText;                       // change detection, cheaper than a hash
        public float MaxLineWidth;                    // widest line, drives horizontal scroll
        public bool TokenFailed;              // latched once a tokenizer throws, kills highlighting for this id
        public bool[] LiteralMask = Array.Empty<bool>();   // per char: inside a string or comment

        // cumulative glyph width per char index of whichever line ColumnX last measured. rebuilt
        // only when the line reference changes - see ColumnWidths - so a visible line's prefix
        // widths get summed once per frame instead of remeasured by every token, the caret and
        // the bracket boxes separately.
        public string ColXLine;
        public float[] ColX = Array.Empty<float>();

        public int Caret;                      // utf-8 byte offset from imgui, used below as a utf-16 char index - same thing for ascii, off for anything else
        public int SelStart, SelEnd;           // written by the callback each frame
        public int PrevCaret;                  // caret before the widget ran, for the arrow-cancel
        public int PrevSelStart, PrevSelEnd;   // selection before the widget ran, same reason - a stolen arrow restores all three together
        public bool CancelCaretMove;           // set by the popup to swallow an up/down
        public int SelectFrom = -1, SelectTo = 0;  // a selection we want imgui to adopt, or -1
        // a queued edit applied via DeleteChars/InsertChars in the callback, since writing
        // `text` directly is ignored once the widget is active. -1 means nothing pending - one
        // slot covers a whole-buffer replace (indent) or a small in-place one (tabs, completion).
        public int PendingEditAt = -1;
        public int PendingEditRemove;      // chars to delete from PendingEditAt first, int.MaxValue means to the end
        public string PendingEditInsert;   // text to put in their place
        public int PendingEditBaseLen = -1;   // byte length of the text the edit above was computed against - see PendingEditStale
        // where the caret should land once the edit above has been applied, given where it was
        // before. null leaves imgui's own answer alone, which for a whole-buffer replace is the
        // end of the document - fine for a SetText, very much not for a tab expansion.
        public Func<int, int> PendingEditCaretMap;

        // a whole-buffer replace queued by Editor.SetText, kept separate from the edit channel above
        // because it has to survive an unfocused frame. held until we actually see it land - see the
        // block at the top of Draw.
        public string PendingSetText;
        public int PendingSetCaret = -1;   // where the caret should land once it does, -1 leaves imgui's answer alone

        // sets all three together so a caller can't set two and leave the third stale from
        // a previous edit.
        public void SetPendingEdit(int at, int remove, string insert, Func<int, int> caretMap = null)
        {
            PendingEditAt = at;
            PendingEditRemove = remove;
            PendingEditInsert = insert;
            PendingEditCaretMap = caretMap;
        }

        public float ChildScrollY;             // the scrolling child's own scroll last frame
        public bool FollowCaret;               // scroll the caret back into view later this same Draw
        public int CaretLine;
        public bool Focused;           // whether the input had focus last frame
        public double CaretMovedAt;    // ImGui.GetTime() when the caret last moved, resets the blink

        // completion popup - view state, never touches the caller's settings json.
        public bool PopupOpen;
        public readonly List<Completion> Items = new();
        public readonly List<Completion> Raw = new();   // provider output before ranking
        public int Sel;
        public int WordStart;
        public string Word = "";
        public char Trigger;
        public bool CompleteFailed;
        public bool SuppressPopupReopen;   // set by CommitCompletion, consumed by UpdatePopup the same frame

        // popup scrolling. the window only moves for the arrow keys and the wheel - a hover sets
        // Sel and nothing else, which is what stopped the list running away under a resting mouse.
        public int PopupScroll;        // first visible row
        public int PopupSelShown;      // the Sel the window was last moved for, so an unchanged Sel never drags it back
        public float PopupWheelAcc;    // leftover fractional wheel, so a trackpad's small deltas add up instead of rounding to nothing
        public Vector2 PopupMin, PopupMax;   // last frame's popup rect, for claiming the wheel before the child scrolls with it

        // the native buffer imgui reads and writes directly - persists across frames so it can
        // grow instead of getting rebuilt (and re-encoded) every single one. AllocHGlobal'd, so
        // it needs an explicit free - see the finalizer below.
        public IntPtr NativeBuf;
        public int NativeBufCap;        // bytes currently allocated at NativeBuf, including the null terminator
        public string NativeBufText;    // what NativeBuf currently holds - only re-encoded when this stops matching
        public int NativeTextLen;       // live text length in bytes, refreshed off d.BufTextLen every callback tick

        // _states is process-lifetime today and nothing ever removes a State from it (see Draw's
        // report for the caller of this), so in practice this only fires if the whole assembly
        // gets unloaded (a collectible plugin reload). still the correct minimal safety net for a
        // class holding a raw unmanaged pointer with no other cleanup path.
        ~State()
        {
            if (NativeBuf != IntPtr.Zero) Marshal.FreeHGlobal(NativeBuf);
        }
    }

    // imgui calls this from inside InputTextMultiline, so the state it acts on is stashed in
    // _active around the call rather than passed through user_data.
    [ThreadStatic] static State _active;
    static unsafe readonly ImGuiInputTextCallback _cb = Callback;

    // "##text"'s bytes, encoded once - the native call needs a label pointer every frame and
    // this never changes, so there is no reason to re-encode a constant string per frame.
    static readonly byte[] TextLabelUtf8 = Encoding.UTF8.GetBytes("##text\0");

    /// <summary>
    /// Smallest capacity covering <paramref name="minBytes"/> that also keeps a typing session from
    /// reallocating on every keystroke once it sits right at the wall. Doubling, same idea as
    /// List&lt;T&gt;'s own growth, with a small floor for the first allocation.
    /// </summary>
    public static int NextBufCap(int minBytes, int currentCap)
    {
        return Math.Max(minBytes, Math.Max(currentCap * 2, 256));
    }

    // grows the owned native buffer in place if it is not already big enough. a no-op most
    // frames - the actual size check is one comparison, so calling it unconditionally is cheap.
    static void GrowNativeBuf(State s, int minBytes)
    {
        if (minBytes <= s.NativeBufCap) return;
        int next = NextBufCap(minBytes, s.NativeBufCap);
        s.NativeBuf = s.NativeBuf == IntPtr.Zero
            ? Marshal.AllocHGlobal(next)
            : Marshal.ReAllocHGlobal(s.NativeBuf, (IntPtr)next);
        s.NativeBufCap = next;
    }

    // encodes text into the owned buffer, null terminated - imgui scans that terminator on
    // activation to learn the starting length. caller gates this on text actually differing
    // from what the buffer already holds, this itself does the encoding unconditionally.
    static unsafe void SyncNativeBuf(State s, string text)
    {
        int bytes = Encoding.UTF8.GetByteCount(text);
        GrowNativeBuf(s, bytes + 1);
        var span = new Span<byte>((void*)s.NativeBuf, s.NativeBufCap);
        int written = Encoding.UTF8.GetBytes((ReadOnlySpan<char>)text, span);
        span[written] = 0;
        s.NativeTextLen = written;
    }

    // reads the live edited text back out once imgui reports it changed.
    static unsafe string ReadNativeBuf(State s) => Encoding.UTF8.GetString((byte*)s.NativeBuf, s.NativeTextLen);

    /// <summary>
    /// Whether inserting <paramref name="insertBytes"/> utf-8 bytes fits after removing
    /// <paramref name="rm"/>, mirroring the check dear imgui's own InsertChars runs. Landing exactly
    /// on <paramref name="bufSize"/> is rejected, same as imgui's own comparison.
    /// </summary>
    public static bool EditFits(int bufTextLen, int bufSize, int rm, int insertBytes)
    {
        return insertBytes + (bufTextLen - rm) < bufSize;
    }

    /// <summary>
    /// How big to size ImGui's buffer so a queued edit lands even when it outgrows the text currently
    /// on screen.
    /// </summary>
    /// <param name="pad">Slack for typing before the next resize.</param>
    public static int RequiredBufSize(int textLen, int pendingRemove, int pendingInsertLen, int pad)
    {
        int afterEdit = textLen - Math.Min(Math.Max(pendingRemove, 0), textLen) + Math.Max(pendingInsertLen, 0);
        return Math.Max(textLen, afterEdit) + pad;
    }

    /// <summary>
    /// Whether a queued edit computed against a buffer of <paramref name="pendingBaseLen"/> bytes is
    /// still safe to apply to a live buffer of <paramref name="bufTextLen"/> bytes.
    /// <para>
    /// The two disagreeing means ImGui already applied something - a paste, a held-repeat key - in
    /// the same frame, AFTER the edit's offsets were worked out against the pre-input text. Applying
    /// anyway would delete or insert at the wrong spot, so the edit is dropped whole and reconverges
    /// next frame.
    /// </para>
    /// </summary>
    public static bool PendingEditStale(int bufTextLen, int pendingBaseLen)
    {
        return bufTextLen != pendingBaseLen;
    }

    static unsafe int Callback(ImGuiInputTextCallbackData* data)
    {
        var d = new ImGuiInputTextCallbackDataPtr(data);
        var s = _active;
        if (s == null) return 0;

        // a resize can arrive mid-call - our own DeleteChars/InsertChars below re-enters this
        // same callback when the pending edit does not fit the current capacity - so this has
        // to be handled before anything else runs. letting the prologue run too would replay
        // (or corrupt) an edit that is still in flight, since PendingEditAt has not been
        // cleared yet at that point. nothing below this belongs to a resize event, so return
        // right away instead of falling into the prologue.
        if (d.EventFlag == ImGuiInputTextFlags.CallbackResize)
        {
            GrowNativeBuf(s, d.BufTextLen + 1);
            d.Buf = s.NativeBuf;
            d.BufSize = s.NativeBufCap;
            return 0;
        }

        // clamp everything we write - a stale caret/selection from a shorter previous
        // text (same id, different string bound to it) must not walk past the buffer.
        int n = d.BufTextLen;

        // applied before the completion check below, not just in CallbackAlways - imgui raises
        // one event per frame (Completion, History, Edit, Always), so an apply living only in
        // Always would miss every frame a Tab or history key wins the event instead.
        if (s.PendingEditAt >= 0)
        {
            // dropped whole, not just clamped, when something already changed the buffer this
            // frame ahead of us - a paste or a held-repeat key landing in the same 16-33ms frame
            // as the tab that queued this edit. the offsets above were worked out against the
            // pre-input text, so applying them now would delete or insert at the wrong spot
            // (soft tab: eats a real char instead of the tab; block indent: wipes the same-frame
            // input under a stale whole-buffer replace). converges next frame instead, same as
            // the EditFits bail right below.
            if (!PendingEditStale(n, s.PendingEditBaseLen))
            {
                int at = Math.Clamp(s.PendingEditAt, 0, n);
                int rm = Math.Clamp(s.PendingEditRemove, 0, n - at);
                string insert = s.PendingEditInsert ?? "";
                int insertBytes = Encoding.UTF8.GetByteCount(insert);
                // fail-safe for a request that can't fully land - should be unreachable now that
                // Draw's buffer sizing always reserves worst-case headroom, but drop it whole
                // rather than deleting and then finding out the insert has nowhere to go.
                if (EditFits(n, d.BufSize, rm, insertBytes))
                {
                    // grabbed before the edit: DeleteChars pulls CursorPos back to the delete
                    // point and InsertChars pushes it past everything inserted there, so after a
                    // whole-buffer replace imgui's own answer is always "end of document".
                    int caretBefore = d.CursorPos;
                    if (rm > 0) d.DeleteChars(at, rm);
                    if (insert.Length > 0) d.InsertChars(at, insert);
                    n = d.BufTextLen;
                    if (s.PendingEditCaretMap != null)
                    {
                        int c = Math.Clamp(s.PendingEditCaretMap(caretBefore), 0, n);
                        d.CursorPos = c;
                        d.SelectionStart = d.SelectionEnd = c;
                    }
                }
            }
            s.PendingEditAt = -1;
            s.PendingEditCaretMap = null;
        }

        // also Draw-side requests rather than CallbackAlways-only, so they land on a Tab frame
        // too - selection indices are checked against n above, refreshed by the edit just applied.
        if (s.CancelCaretMove)
        {
            // restores the whole selection, not just the caret - leaving SelectionStart/End
            // wherever a stolen shift-arrow put them would disagree with the caret snapping back.
            d.CursorPos = Math.Clamp(s.PrevCaret, 0, n);
            d.SelectionStart = Math.Clamp(s.PrevSelStart, 0, n);
            d.SelectionEnd = Math.Clamp(s.PrevSelEnd, 0, n);
            s.CancelCaretMove = false;
        }
        if (s.SelectFrom >= 0)
        {
            d.SelectionStart = Math.Clamp(s.SelectFrom, 0, n);
            d.SelectionEnd = Math.Clamp(s.SelectTo, 0, n);
            d.CursorPos = Math.Clamp(s.SelectTo, 0, n);   // last, so the view follows the match end
            s.SelectFrom = -1;
        }

        // mirrored before the completion branch, not just on the Always path - imgui raises one
        // event per frame, and a Tab frame is Completion, so leaving this to the tail meant every
        // tab press read the buffer back one byte short and clipped the last char off the document.
        // CommitCompletion re-mirrors after its own edit.
        s.Caret = d.CursorPos;
        s.SelStart = d.SelectionStart;
        s.SelEnd = d.SelectionEnd;
        s.NativeTextLen = d.BufTextLen;

        if (d.EventFlag == ImGuiInputTextFlags.CallbackCompletion) CommitCompletion(s, d);
        return 0;
    }

    // done inline against the live callback data rather than through the pending-edit channel -
    // that channel exists for edits queued from Draw-side logic before the widget even runs
    // (soft tabs, block indent), so the prologue can replay them into whichever event imgui
    // happens to raise this frame. a completion commit is decided here, already inside
    // CallbackCompletion, so there is nothing to defer: DeleteChars/InsertChars run directly,
    // and CallbackResize (already in Draw's flags) reenters this same callback to grow the
    // buffer if the inserted text does not fit, same as it does for the pending-edit path.
    static unsafe void CommitCompletion(State s, ImGuiInputTextCallbackDataPtr d)
    {
        if (!s.PopupOpen || s.Sel < 0 || s.Sel >= s.Items.Count) return;
        string insert = s.Items[s.Sel].Insert;
        s.PopupOpen = false;
        // consumed by UpdatePopup below, same frame - the word right after a commit is the text
        // we just inserted, which is its own prefix match, so without this the popup reopens
        // immediately offering back the identifier that was just accepted.
        s.SuppressPopupReopen = true;
        if (string.IsNullOrEmpty(insert)) return;   // a provider handed us nothing to insert, just close the popup

        // s.WordStart is a char index - WordAt works over the managed string - but DeleteChars
        // and InsertChars count utf-8 bytes, same reason the soft-tab path in Draw converts
        // explicitly before calling either. skipping the conversion here would land the delete
        // short on any non-ascii before the word, and could split a multi-byte sequence in two.
        string live = Encoding.UTF8.GetString((byte*)d.Buf, d.BufTextLen);
        int wordStartChars = Math.Clamp(s.WordStart, 0, live.Length);
        int from = Encoding.UTF8.GetByteCount(live.Substring(0, wordStartChars));
        int len = Math.Clamp(d.CursorPos - from, 0, d.BufTextLen - from);
        if (len > 0) d.DeleteChars(from, len);
        d.InsertChars(from, insert);

        // re-mirror after our own edit - the caller already mirrored pre-commit values, and the
        // popup and bracket match run later this same frame needing the caret where the commit
        // left it (past the inserted text), not where it was before.
        s.Caret = d.CursorPos;
        s.SelStart = d.SelectionStart;
        s.SelEnd = d.SelectionEnd;
        s.NativeTextLen = d.BufTextLen;
    }

    static readonly Dictionary<string, State> _states = new();

    /// <summary>
    /// Drops the retained state for an id and frees its native buffer right away.
    /// <para>
    /// Every id retains a buffer for the life of the process and there is no automatic eviction, so
    /// an id built off something that changes shape - one editor per row of a user-editable list, or
    /// a name the user can rename - otherwise leaks one unmanaged allocation per abandoned id. Ids
    /// that stay stable and bounded, like a fixed settings screen, never need this.
    /// </para>
    /// <para>
    /// This is also the only way to clear a latched tokenizer or completer failure, since those hold
    /// for the life of the id's state.
    /// </para>
    /// </summary>
    /// <param name="id">Safe for an id that was never seen, and safe to call twice.</param>
    public static void Forget(string id)
    {
        if (id == null) return;
        if (!_states.Remove(id, out var s)) return;
        if (s.NativeBuf != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(s.NativeBuf);
            s.NativeBuf = IntPtr.Zero;
        }
        GC.SuppressFinalize(s);
    }

    /// <summary>
    /// Replaces the whole buffer from outside the control - loading a file, a Reset button.
    /// <para>
    /// Use this, never <c>_code = text</c>. ImGui copies the caller's buffer into its own state once
    /// the widget activates and ignores the caller's string from then on, so a plain assignment while
    /// the editor has focus is silently discarded on the next keystroke. The text goes through
    /// <see cref="NormalizeLineEndings"/> first, a file off disk being the likeliest source of CRLF
    /// there is.
    /// </para>
    /// </summary>
    /// <param name="id">Safe for an id that has never been drawn - the state is created here and the
    /// id's first draw applies the edit. Safe to call twice before a draw too; the second call just
    /// replaces the first.</param>
    /// <param name="caret">Where to leave the caret once the text lands, as an index into
    /// <paramref name="text"/>. The default -1 keeps imgui's own answer, which for a whole-buffer
    /// replace is the end of the document - right for loading a file, wrong for anything that only
    /// rewrites part of one.</param>
    public static void SetText(string id, string text, int caret = -1)
    {
        if (id == null) return;
        if (!_states.TryGetValue(id, out var s)) _states[id] = s = new State();
        s.PendingSetText = NormalizeLineEndings(text) ?? "";
        s.PendingSetCaret = caret;
    }

    /// <summary>
    /// Splices a snippet in at the caret, replacing the selection if there is one, and leaves the
    /// caret just past what was inserted.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="SetText"/> rather than the edit channel on purpose: a toolbar button
    /// or a picker popup takes focus off the editor as it is clicked, and the edit channel only
    /// survives while the widget is active.
    /// </remarks>
    /// <param name="text">The caller's buffer, rewritten in place.</param>
    /// <param name="snippet">Null or empty does nothing.</param>
    public static void Insert(string id, ref string text, string snippet)
    {
        if (id == null || string.IsNullOrEmpty(snippet)) return;

        var current = text ?? "";

        // an id that has never been drawn has no caret to speak of, so append
        if (!_states.TryGetValue(id, out var s))
        {
            text = current + snippet;
            SetText(id, text, text.Length);
            return;
        }

        var from = Math.Clamp(Math.Min(s.SelStart, s.SelEnd), 0, current.Length);
        var to = Math.Clamp(Math.Max(s.SelStart, s.SelEnd), from, current.Length);
        if (from == to) from = to = Math.Clamp(s.Caret, 0, current.Length);

        text = current.Substring(0, from) + snippet + current.Substring(to);
        SetText(id, text, from + snippet.Length);
    }

    /// <summary>
    /// Fixed dark-editor token colours. <see cref="TokenKind.Text"/> and
    /// <see cref="TokenKind.Identifier"/> are absent on purpose and fall through to
    /// <see cref="EditorOpts.Foreground"/>. Every entry is tuned against the editor's own fixed
    /// background rather than whatever theme the user picked, so it is a known target.
    /// </summary>
    public static readonly IReadOnlyDictionary<TokenKind, SColor> DefaultPalette =
        new Dictionary<TokenKind, SColor>
        {
            [TokenKind.Keyword]  = new SColor(197, 134, 192, 255),
            [TokenKind.Type]     = new SColor(78, 201, 176, 255),
            [TokenKind.Number]   = new SColor(181, 206, 168, 255),
            [TokenKind.String]   = new SColor(206, 145, 120, 255),
            [TokenKind.Comment]  = new SColor(106, 153, 85, 255),
            // saturated blue on purpose: a blue-grey sits between the foreground grey and Type's
            // teal and reads as faint against both.
            [TokenKind.Operator] = new SColor(86, 156, 214, 255),
            [TokenKind.Error]    = new SColor(244, 71, 71, 255),
        };

    // normalizes, then reports whether that alone counts as a change - if it does not, the caller
    // keeps handing back crlf and this renormalizes it forever without their copy ever settling.
    static (string local, bool normalized) Normalized(string text)
    {
        string original = text ?? "";
        string local = NormalizeLineEndings(original);
        return (local, !ReferenceEquals(local, original));
    }

    // shared across every caller that passes no opts - Draw only ever reads from o, never writes
    // it, so one instance is safe to reuse instead of allocating a fresh default per frame on the
    // null path.
    static readonly EditorOpts DefaultOpts = new();

    /// <summary>
    /// Draws the editor over a caller-owned string.
    /// </summary>
    /// <param name="id">Keys the retained state: the native buffer, the line model, the popup and the
    /// scroll. Two editors sharing an id share all of it. See <see cref="Forget"/> for the cleanup
    /// rule on ids that come and go.</param>
    /// <param name="text">The buffer. Written back on a change, including a pure line-ending
    /// normalization, so the caller's copy settles instead of being renormalized forever.</param>
    /// <param name="opts">Null uses plain-text defaults - no highlighting and no popup.</param>
    /// <returns>True the frame the text changes.</returns>
    public static bool Code(string id, ref string text, EditorOpts opts = null)
    {
        var (local, normalized) = Normalized(text);
        bool changed = Draw(id, ref local, opts ?? DefaultOpts) || normalized;
        if (changed) text = local;
        return changed;
    }

    /// <summary>
    /// Same editor bound through accessors, for a buffer that isn't a field.
    /// </summary>
    public static bool Code(string id, Func<string> get, Action<string> set, EditorOpts opts = null)
    {
        var (local, normalized) = Normalized(get());
        bool changed = Draw(id, ref local, opts ?? DefaultOpts) || normalized;
        if (changed) set(local);
        return changed;
    }

    const string Openers = "([{";
    const string Closers = ")]}";

    /// <summary>
    /// Which bracket, if any, sits at the caret: the char AT the caret when it opens, otherwise the
    /// char BEFORE it when that closes. Bracket matching and the bracket boxes both go through here,
    /// so there is one answer to "which one" rather than two that can disagree.
    /// </summary>
    /// <param name="literal">Says which char indices the tokenizer labelled string or comment, so a
    /// brace inside a literal is skipped.</param>
    /// <param name="at">Index of the bracket found, or -1.</param>
    /// <param name="dir">1 to scan forward for the mate, -1 to scan back, 0 when none was found.</param>
    public static bool FindAnchor(string text, int caret, Func<int, bool> literal, out int at, out int dir)
    {
        at = -1; dir = 0;
        if (string.IsNullOrEmpty(text)) return false;

        if (caret < text.Length && Openers.IndexOf(text[caret]) >= 0 && !literal(caret)) { at = caret; dir = 1; return true; }
        // caret <= text.Length too - caret can come in past the end of the text (utf-8 byte
        // offset from imgui used as a char index) and text[caret - 1] must stay in bounds.
        if (caret > 0 && caret <= text.Length && Closers.IndexOf(text[caret - 1]) >= 0 && !literal(caret - 1)) { at = caret - 1; dir = -1; return true; }
        return false;
    }

    /// <summary>
    /// Index of the bracket matching the one at, or just before, the caret. The pure matching rule,
    /// if you want to drive it yourself.
    /// </summary>
    /// <param name="literal">Says which char indices the tokenizer labelled string or comment, so a
    /// brace inside <c>"("</c> never pairs.</param>
    /// <returns>-1 when there is no bracket at the caret, or no mate for it.</returns>
    public static int BracketMate(string text, int caret, Func<int, bool> literal)
    {
        if (!FindAnchor(text, caret, literal, out int at, out int dir)) return -1;

        char self = text[at];
        char mate = dir > 0 ? Closers[Openers.IndexOf(self)] : Openers[Closers.IndexOf(self)];
        int depth = 0;
        for (int i = at; i >= 0 && i < text.Length; i += dir)
        {
            if (literal(i)) continue;
            if (text[i] == self) depth++;
            else if (text[i] == mate && --depth == 0) return i;
        }
        return -1;
    }

    const int MaxChars = 1024 * 1024;   // past this, highlighting is off and it is a plain box

    // caps how many ranked matches the popup keeps - a completer can hand back thousands of
    // candidates, this bounds both ranking passes below and the selection arithmetic in DrawPopup
    // to the same number either way. comfortably past anything a user would ever scroll to; the
    // visible window is PopupRows rows, this is just the total kept in memory.
    const int MaxCompletionItems = 500;

    // recomputes the completion list off the word at the caret. called once per Draw, after the
    // widget has already run this frame - so s.Caret and text are both already current.
    static void UpdatePopup(State s, string text, EditorOpts o, bool edited)
    {
        // clears rather than just gating a reader - AllowTabInput and CommitCompletion's guard
        // both key off PopupOpen, so this is the one place that has to go false for both of them
        // to agree the popup is gone the same frame focus is. called with this frame's real
        // s.Focused (set just before this in Draw), not last frame's.
        if (!s.Focused) { s.PopupOpen = false; s.SuppressPopupReopen = false; return; }
        if (o.Complete == null || s.CompleteFailed) { s.PopupOpen = false; return; }
        // MaxChars is meant to turn the whole editor plain past a size where per-frame work stops
        // being free - Draw's own `plain` gate already covers highlighting and bracket match this
        // way, completion just never consulted it and kept offering a popup over a document too
        // big to also be lexing every frame.
        if (text.Length > MaxChars) { s.PopupOpen = false; return; }

        // CommitCompletion closed the popup this same frame - the word under the caret right now
        // is the text it just inserted, which would rank as its own prefix match below and reopen
        // the popup on the identifier the user just accepted. skip the query for this one frame
        // only; the cache (s.WordStart/Word/Trigger) is left untouched, so real typing right after
        // a commit is still recognized as a change and queries normally next frame.
        if (s.SuppressPopupReopen)
        {
            s.SuppressPopupReopen = false;
            s.PopupOpen = false;
            return;
        }

        // s.Caret can arrive here out of range - a caller can shorten text while the editor is
        // unfocused, or it is a utf-8 byte offset from imgui reused as a utf-16 char index and any
        // multi-byte char before it pushes it past text.Length. clamp once and use this local for
        // both WordAt and the substring below, rather than trusting WordAt's own internal clamp
        // (which only protects its own return value) to cover the raw substring too.
        int caret = Math.Clamp(s.Caret, 0, text.Length);
        var (ws, _) = WordAt(text, caret);
        string word = WordText(text, ws, caret);
        char trig = TriggerAt(text, ws);

        // nothing to complete from: no word typed and no dot behind us
        if (word.Length == 0 && trig == '\0') { s.PopupOpen = false; return; }

        // only ask the provider when the word we are completing actually changed. this is the
        // difference between one call per keystroke and one per frame.
        if (ws != s.WordStart || word != s.Word || trig != s.Trigger)
        {
            s.WordStart = ws; s.Word = word; s.Trigger = trig;

            // the word under the caret changed without the document changing, so the caret was
            // moved by a click or an arrow rather than by typing. landing in the middle of an
            // existing word is not a request to complete it - close instead of offering.
            if (!edited) { s.Items.Clear(); s.PopupOpen = false; return; }

            s.Raw.Clear();
            // a completer is caller-supplied and can throw, or return anything - switched off
            // for the session rather than throwing once per keystroke out of a render loop, same
            // deal as a tokenizer in Reflow. latches on State, so it does not recover on its own -
            // DrawErrors surfaces this once as an underline on line 0, but clearing it for real
            // needs Editor.Forget(id) (a fresh State) or a process restart.
            try { o.Complete(new CompletionContext(text, caret, ws, word, trig), s.Raw); }
            catch { s.CompleteFailed = true; s.PopupOpen = false; return; }

            // cache key above is (wordStart, word, trigger) only, no document text - a completer
            // keyed on surrounding context can go stale if an edit elsewhere leaves those three
            // unchanged. re-querying every keystroke is the point though, so the key stays this
            // narrow on purpose.

            // two passes rather than a sort. List.Sort is unstable, so equal-ranked candidates
            // would shuffle between keystrokes and the highlight would jump around. capped so a
            // provider handing back thousands of matches cannot make either pass, or the popup
            // that walks s.Items afterward, unbounded work.
            s.Items.Clear();
            for (int i = 0; i < s.Raw.Count && s.Items.Count < MaxCompletionItems; i++)
                if (Rank(word, s.Raw[i].Insert) == 0) s.Items.Add(s.Raw[i]);
            for (int i = 0; i < s.Raw.Count && s.Items.Count < MaxCompletionItems; i++)
                if (Rank(word, s.Raw[i].Insert) > 0) s.Items.Add(s.Raw[i]);
            // a fresh list starts at the top, and the scroll goes with it - keeping the old row
            // window over a list that is now a different length shows the wrong rows for a frame.
            s.Sel = 0;
            s.PopupSelShown = 0;
            s.PopupScroll = 0;
            s.PopupWheelAcc = 0f;
        }

        s.PopupOpen = s.Items.Count > 0;
        if (s.Sel >= s.Items.Count) s.Sel = Math.Max(0, s.Items.Count - 1);
    }

    static bool Draw(string id, ref string text, EditorOpts o)
    {
        if (!_states.TryGetValue(id, out var s)) _states[id] = s = new State();

        // an Editor.SetText waiting to land. two routes, because imgui only runs the input callback
        // while the widget is ACTIVE: a focused editor has to go through the callback (imgui owns its
        // own copy of the buffer and ignores the caller's string), while an unfocused one has to be
        // written straight into the caller's string, since the callback never fires and the edit
        // channel would just be cleared at the end of this frame. held until we see it actually land,
        // so focus changing on the very frame it was queued cannot drop it.
        bool externalSet = false;
        if (s.PendingSetText != null)
        {
            int wantCaret = s.PendingSetCaret;

            if (text == s.PendingSetText)
            {
                s.PendingSetText = null;   // landed, by whichever route
                s.PendingSetCaret = -1;
            }
            else if (s.Focused)
            {
                s.SetPendingEdit(0, int.MaxValue, s.PendingSetText,
                    wantCaret < 0 ? null : _ => wantCaret);
            }
            else
            {
                text = s.PendingSetText;
                s.PendingSetText = null;
                s.PendingSetCaret = -1;
                externalSet = true;
                // nothing is running the callback, so park the caret ourselves - imgui reads it
                // back off this state the next time the widget goes active.
                if (wantCaret >= 0) s.Caret = s.SelStart = s.SelEnd = Math.Clamp(wantCaret, 0, text.Length);
            }
        }

        bool plain = o.Tokenize == null || s.TokenFailed || text.Length > MaxChars;

        if (s.LastText != text) { Reflow(s, text, o, plain); s.LastText = text; }

        // the native buffer only gets rewritten when the caller handed us text it does not
        // already hold - an idle frame (nothing changed) does zero utf8 encoding work, and the
        // frame right after our own edit skips it too, since NativeBufText gets set from the
        // same read that produced `text` in the first place.
        if (s.NativeBufText != text) { SyncNativeBuf(s, text); s.NativeBufText = text; }

        // imgui's AllowTabInput only inserts a raw tab char, so queue a rewrite through the
        // pending-edit channel once the widget is active - a caller-supplied tab sits untouched
        // until then. whole-buffer replace, same shape block indent uses below, so a tab-indented
        // paste is one pending edit and one undo step instead of one edit (and one Reflow, one
        // caret jump) per tab per frame.
        if (o.SoftTabs && text.IndexOf('\t') >= 0)
        {
            string expanded = ExpandTabs(text, o.TabWidth);
            if (expanded != text)
            {
                // the caret map is the whole point here: without it the replace below dumps the
                // caret at the end of the document, so Tab-to-indent flings you to the bottom of
                // the file. one closure per frame, but only on the frames a tab actually exists.
                string src = text;
                int w = o.TabWidth;
                s.SetPendingEdit(0, int.MaxValue, expanded, c => ExpandTabsCaret(src, c, w));
            }
        }

        var style = ImGui.GetStyle();
        float lineH = ImGui.GetTextLineHeight();
        float gutterW = o.LineNumbers
            ? ImGui.CalcTextSize(s.Lines.Count.ToString()).X + style.ItemSpacing.X * 2f
            : 0f;

        bool changed = false;
        // declared out here (not inside the try below) because DrawPopup needs it after the
        // child's own try/finally has already closed - see EndChild's finally further down.
        Vector2 textOrigin = default;
        ImGui.PushID(id);
        // try/finally is a deliberate one-off here, not the pattern for the rest of the file:
        // DrawErrors below reads a caller-owned dictionary the readme invites a background
        // compiler to refresh, so a mutation mid-frame can throw between this PushID/BeginChild
        // and their EndChild/PopID. nothing else in this repo pushes an imgui id/window stack
        // around code that reads caller data every frame the way this does. the finally below
        // guarantees the push above is still matched even if something in between throws, so one
        // bad frame degrades this editor instead of unbalancing the stack and taking the whole
        // overlay down with it.
        try
        {
        var size = o.Size;
        if (size.X <= 0f) size.X = ImGui.GetContentRegionAvail().X;
        bool autoHeight = size.Y <= 0f;
        if (autoHeight) size.Y = ImGui.GetContentRegionAvail().Y;

        // an optional row above the scrolling child, outside it so it never scrolls away with the
        // text underneath. its height comes out of size right here, not off to the side, so a
        // zero-height (fill the tab) Size still fills correctly and a fixed Size never overflows
        // its container by however tall the toolbar drew.
        if (o.Toolbar != null)
        {
            float beforeY = ImGui.GetCursorPosY();
            // caller-supplied, same deal as Tokenize/Complete below - a throw here must not take
            // the whole editor down with it. not latched like TokenFailed/CompleteFailed though:
            // this runs once a frame, not once per line or keystroke, so just retrying next frame
            // costs nothing.
            try { o.Toolbar(); } catch { /* toolbar threw - skip it this frame, editor still draws */ }
            ImGui.Separator();
            float toolbarH = ImGui.GetCursorPosY() - beforeY;
            // auto height: the content region already shrank by whatever the toolbar and the
            // separator just consumed, so re-reading it is simpler and exactly right. fixed height:
            // there is nothing to re-read, so subtract what we measured instead.
            size.Y = autoHeight ? Math.Max(0f, ImGui.GetContentRegionAvail().Y) : Math.Max(0f, size.Y - toolbarH);
        }

        // caught before BeginChild moves the current window to the child - the popup draws on the
        // foreground list later, past any clipping, so a focused editor scrolled out of view in a
        // scrolling host window would otherwise still float its popup over whatever is visible there.
        var childOrigin = ImGui.GetCursorScreenPos();
        bool editorVisible = ImGui.IsRectVisible(childOrigin, childOrigin + size);

        // a wheel notch over the popup belongs to the popup, not to the text under it. imgui applies
        // the wheel to the hovered window inside the BeginChild below, so the only way to keep the
        // code from scrolling too is to pin the child's scroll for this frame before it begins -
        // SetScrollY after the fact lands a frame later and shows one frame of the scroll it undoes.
        // x stays -1, "leave this axis alone".
        if (s.PopupOpen && ImGui.GetIO().MouseWheel != 0f
            && ImGui.IsMouseHoveringRect(s.PopupMin, s.PopupMax, false))
            ImGui.SetNextWindowScroll(new Vector2(-1f, s.ChildScrollY));

        ImGui.BeginChild("##ed", size, ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);
        try
        {

        // the widget is submitted at exactly its content height, so it never scrolls itself and
        // the child above owns the scroll. that is what keeps the gutter and the overlay in step.
        float innerH = s.Lines.Count * lineH + style.FramePadding.Y * 2f;
        float innerW = Math.Max(s.MaxLineWidth + style.FramePadding.X * 2f + 4f,
                                ImGui.GetContentRegionAvail().X - gutterW);

        var gutterOrigin = ImGui.GetCursorScreenPos();
        if (o.LineNumbers) ImGui.Dummy(new Vector2(gutterW, innerH));
        if (o.LineNumbers) ImGui.SameLine(0f, 0f);

        // textOrigin itself is declared up in Draw's outer scope - the completion popup below is
        // drawn after EndChild's finally has already run and still needs to place itself against
        // the text.
        var origin = ImGui.GetCursorScreenPos();
        s.PrevCaret = s.Caret;
        s.PrevSelStart = s.SelStart;
        s.PrevSelEnd = s.SelEnd;

        // imgui picks a double-clicked word by whitespace, so "Stacked comes back with the quote
        // attached and ItemRarity. keeps its dot. take the identifier under the mouse instead,
        // the way an editor does. this has to be queued before the widget call so the callback
        // overwrites the selection stb just made, on the same frame - a frame later would flash.
        // punctuation and blank space fall through to imgui untouched, WordAt returns empty there.
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)
            && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
            && ImGui.IsMouseHoveringRect(origin, origin + new Vector2(innerW, innerH))
            && s.Lines.Count > 0)
        {
            var hit = ImGui.GetMousePos() - (origin + style.FramePadding);
            int ln = Math.Clamp((int)MathF.Floor(hit.Y / lineH), 0, s.Lines.Count - 1);
            var (ws, we) = WordAt(text, LineStart(s.Lines, ln) + ColumnAt(s, s.Lines[ln], hit.X));
            if (we > ws) { s.SelectFrom = ws; s.SelectTo = we; }
        }

        // a Tab with a real selection indents the block instead of replacing it. single-caret Tab
        // stays with imgui, which inserts a tab char (or spaces once SoftTabs rewrites it below).
        // we own this keypress either way once the condition below is true, so AllowTabInput gets
        // turned off for this one frame further down - otherwise imgui inserts its own raw tab
        // over the same selection on top of the edit we are about to queue.
        bool blockIndent = false;
        if (s.SelStart != s.SelEnd && ImGui.IsKeyPressed(ImGuiKey.Tab) && ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows))
        {
            bool shift = ImGui.GetIO().KeyShift;
            string indented = IndentBlock(text, s.SelStart, s.SelEnd, o.TabWidth, o.SoftTabs, shift, out int ns, out int ne);
            blockIndent = true;
            // skip queuing when nothing changed so `changed` doesn't lie, but AllowTabInput stays off either way.
            // remove is int.MaxValue, not text.Length - a char count would undershoot the byte count on non-ascii and leave a tail.
            if (indented != text)
                s.SetPendingEdit(0, int.MaxValue, indented);
            s.SelectFrom = ns;
            s.SelectTo = ne;
            // a drag-selection ending mid-word can leave the completion popup open at the same
            // time - both want this same tab press, and block indent wins. closing it here means
            // CommitCompletion's own PopupOpen guard no-ops instead of deleting a span computed
            // against the pre-indent text.
            s.PopupOpen = false;
        }

        // CallbackHistory is single-line only in imgui, so the arrows never reach a callback here.
        // let the caret move, then put it back from CallbackAlways, and move the popup instead.
        // PopupOpen alone is enough to gate this - UpdatePopup clears it the instant the editor
        // is not focused, so a stale popup left over from something else having focus now can't
        // still be here to eat arrow keys. only an unmodified arrow gets stolen: shift/ctrl-arrow
        // are for the editor (extend selection, word jump), not the popup.
        if (s.PopupOpen && s.Items.Count > 0)
        {
            var io = ImGui.GetIO();
            bool plainArrow = !io.KeyShift && !io.KeyCtrl;
            if (plainArrow && ImGui.IsKeyPressed(ImGuiKey.DownArrow)) { s.Sel = (s.Sel + 1) % s.Items.Count; s.CancelCaretMove = true; }
            else if (plainArrow && ImGui.IsKeyPressed(ImGuiKey.UpArrow)) { s.Sel = (s.Sel - 1 + s.Items.Count) % s.Items.Count; s.CancelCaretMove = true; }
        }

        _active = s;
        // imgui's multiline input lives in its own child window whose background renders on
        // top of this window's draw list, so the frame is ours to paint and both of imgui's
        // fills have to go transparent or they cover the overlay.
        ImGui.GetWindowDrawList().AddRectFilled(origin, origin + new Vector2(innerW, innerH),
            EColor.U32(o.Background), style.FrameRounding);
        uint transparent = EColor.U32(new SColor(0, 0, 0, 0));
        using (new EColor.StyleColorScope(
            (ImGuiCol.Text, transparent), (ImGuiCol.FrameBg, transparent), (ImGuiCol.ChildBg, transparent)))
        {
            // grown ahead of the call when there is a pending edit, so the common case (a tab
            // expansion, a block indent) lands without a nested resize mid-callback. this is a
            // head start now, not a hard ceiling - CallbackResize below covers the rest, and it
            // is what makes anything typed or pasted straight into the widget work regardless
            // of size, so pad only needs to be small typing slack, not a worst-case reservation.
            int pendingRemove = s.PendingEditAt >= 0 ? s.PendingEditRemove : 0;
            int pendingInsertBytes = s.PendingEditAt >= 0 && !string.IsNullOrEmpty(s.PendingEditInsert)
                ? Encoding.UTF8.GetByteCount(s.PendingEditInsert) : 0;
            const int pad = 64;
            int textLenBytes = Encoding.UTF8.GetByteCount(text);
            int required = RequiredBufSize(textLenBytes, pendingRemove, pendingInsertBytes, pad);
            GrowNativeBuf(s, required);
            // same byte count RequiredBufSize just used, reused here rather than measured twice -
            // this is what the callback prologue checks the live buffer against before trusting
            // the offsets in a queued edit (see PendingEditStale).
            if (s.PendingEditAt >= 0) s.PendingEditBaseLen = textLenBytes;

            var flags = ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCompletion
                | ImGuiInputTextFlags.CallbackResize;
            // off on the frame a block indent fires (imgui's own tab would fight the edit we just
            // queued) and off while the popup is open (Tab needs to reach CallbackCompletion
            // instead of imgui inserting a raw tab). AllowTabInput and CallbackCompletion both
            // bind Tab, so only one of them gets to own the keypress at a time.
            if (!blockIndent && !s.PopupOpen) flags |= ImGuiInputTextFlags.AllowTabInput;

            // the ref-string overload never sets CallbackResize and re-encodes its whole managed
            // buffer every frame no matter the size, so it cannot back a buffer that is both
            // growable and cheap when idle. this is the native entry point it wraps underneath,
            // called directly against our own persistent buffer instead of a fresh one per frame.
            unsafe
            {
                fixed (byte* label = TextLabelUtf8)
                {
                    changed = ImGuiNative.igInputTextMultiline(label, (byte*)s.NativeBuf, (uint)s.NativeBufCap,
                        new Vector2(innerW, innerH), flags, _cb, null) != 0;
                }
            }
        }
        _active = null;
        // read back only on a reported change - an idle frame leaves NativeBufText (and text) alone.
        if (changed) { text = ReadNativeBuf(s); s.NativeBufText = text; }
        s.CancelCaretMove = false;   // an unconsumed cancel must not outlive its frame
        s.PendingEditAt = -1;        // ditto for an edit that never got applied this frame
        s.PendingEditCaretMap = null;
        s.SelectFrom = -1;           // ditto for a selection request the callback never saw
        var (caretLn, caretCol) = CaretLineCol(s.Lines, s.Caret);
        s.CaretLine = caretLn;
        bool moved = changed || s.Caret != s.PrevCaret;
        if (moved) s.FollowCaret = true;
        bool wasFocused = s.Focused;
        s.Focused = ImGui.IsItemActive();
        // also reset on a fresh focus, so clicking back to the same offset doesn't land mid-blink
        if (moved || (s.Focused && !wasFocused)) s.CaretMovedAt = ImGui.GetTime();
        // after s.Focused, not before - UpdatePopup clears PopupOpen on the same frame focus is
        // lost only if it sees this frame's real value, not last frame's.
        UpdatePopup(s, text, o, changed);

        // measured once here and reused for the caret draw and the horizontal follow-scroll below.
        // through ColumnX so the caret lines up with the glyphs DrawOverlay actually painted.
        float caretX = caretLn < s.Lines.Count ? ColumnX(s, s.Lines[caretLn], caretCol) : 0f;

        textOrigin = origin + style.FramePadding;
        DrawOverlay(s, o, textOrigin, lineH, plain);
        if (o.BracketMatch && !plain) DrawBrackets(s, o, text, textOrigin, lineH);
        DrawErrors(s, o, textOrigin, lineH);
        DrawCaret(s, o, textOrigin, lineH, caretX);
        if (o.LineNumbers) DrawGutter(s, gutterOrigin + new Vector2(0f, style.FramePadding.Y), gutterW, lineH);

        // imgui cannot scroll the widget for us any more, since the widget has no scroll of its
        // own. keep the caret inside the view ourselves, applied right here in this same Draw.
        if (s.FollowCaret)
        {
            float caretY = s.CaretLine * lineH;
            float scrollY = FollowScroll(caretY, ImGui.GetScrollY(), ImGui.GetWindowHeight(), lineH * 2f);
            if (scrollY >= 0f) ImGui.SetScrollY(scrollY);

            // caretX is measured from the text area's left edge, but GetScrollX is measured from
            // the child's content origin, and the gutter sits in front of the text in that space -
            // add gutterW back in so both sides of the comparison agree on where zero is.
            float caretContentX = gutterW + caretX;
            // slack so the caret doesn't end up flush against the edge. a few chars wide, measured
            // rather than a hardcoded pixel count so it scales with the font.
            float marginX = ImGui.CalcTextSize("MMMM").X;
            float scrollX = FollowScroll(caretContentX, ImGui.GetScrollX(), ImGui.GetWindowWidth(), marginX);
            if (scrollX >= 0f) ImGui.SetScrollX(scrollX);

            s.FollowCaret = false;
        }

        s.ChildScrollY = ImGui.GetScrollY();   // what the wheel claim above pins the child back to
        }
        finally
        {
            // matches the BeginChild above no matter what happened in the body - see the comment
            // by the outer try for why this file breaks from the rest of the repo's no-try/finally
            // norm.
            ImGui.EndChild();
        }
        DrawPopup(s, o, textOrigin, lineH, editorVisible);
        }
        finally
        {
            ImGui.PopID();
        }
        // changed comes straight from imgui - a queued edit goes through Delete/InsertChars, which
        // imgui reports itself, so this is already correct, including false for a no-op unindent.
        // externalSet covers the one case imgui cannot report: a SetText applied to the caller's
        // string above, on a frame where the widget was never active to report anything.
        return changed || externalSet;
    }

    const int PopupRows = 8;

    // one wheel notch moves this many rows, same as a browser's list
    const float PopupWheelRows = 3f;

    /// <summary>
    /// Which row the popup's visible window starts at. Scrolls the least it can to keep
    /// <paramref name="sel"/> inside a <paramref name="cap"/>-row band, so a window already showing
    /// the selection stays exactly where <paramref name="prevStart"/> left it. Pure, so it can be
    /// asserted without a live popup.
    /// <para>
    /// Deliberately NOT a centring rule any more. Centring re-anchored the whole list on every
    /// selection change, and since a hover also changes the selection, the row under a resting mouse
    /// pointer kept becoming a different item - the list scrolled itself as fast as it could redraw.
    /// </para>
    /// </summary>
    /// <param name="prevStart">Where the window sat last frame. Clamped, so a stale one is safe.</param>
    public static int PopupScrollStart(int count, int sel, int cap, int prevStart)
    {
        if (cap <= 0 || count <= cap) return 0;
        sel = Math.Clamp(sel, 0, count - 1);
        int start = Math.Clamp(prevStart, 0, count - cap);
        if (sel < start) return sel;
        if (sel >= start + cap) return sel - cap + 1;
        return start;
    }

    // dropdown for the completion list, positioned under the word being completed. drawn straight
    // onto the foreground draw list instead of submitted as a window - a real window sorts by
    // focus order, and NoFocusOnAppearing (required, since taking focus would break typing) then
    // left whatever window the user is actually typing in drawing over it. reads the editor's own
    // Background/Foreground rather than the ambient theme - same reasoning as the syntax palette,
    // this popup sits on the editor's fixed surface, not on whatever FrameBg the host theme picked.
    static void DrawPopup(State s, EditorOpts o, Vector2 textOrigin, float lineH, bool editorVisible)
    {
        // no separate focus check needed - UpdatePopup already clears PopupOpen the instant the
        // editor stops being focused, so PopupOpen true here already means s.Focused is too.
        // editorVisible still matters on its own: covers a focused editor scrolled out of view in
        // the host window - the anchor below would compute a screen position fine, it just would
        // not sit near anything the user can currently see, and a foreground draw has nothing
        // above it to clip it away on its own.
        if (!s.PopupOpen || s.Items.Count == 0 || !editorVisible) return;

        var (ln, cl) = CaretLineCol(s.Lines, s.WordStart);
        float x = ln < s.Lines.Count ? ColumnX(s, s.Lines[ln], cl) : 0f;
        // no scroll term: textOrigin came from GetCursorScreenPos inside the child, which already
        // has the scroll baked in - same reason DrawOverlay places lines at plain textOrigin + y.
        var wordTop = textOrigin + new Vector2(x, ln * lineH);

        var palette = o.Palette ?? DefaultPalette;
        var style = ImGui.GetStyle();
        float rowH = ImGui.GetTextLineHeightWithSpacing();
        float pad = style.FramePadding.X;
        float gap = style.ItemSpacing.X;

        int visible = Math.Min(PopupRows, s.Items.Count);
        int maxScroll = Math.Max(0, s.Items.Count - visible);

        // wheel scrolls the list without touching the selection, hit-tested against LAST frame's
        // rect - this frame's is not known until the rows below have been measured, and the box
        // does not move between frames anyway. Draw claims the same wheel off the child underneath
        // so the code behind the popup does not scroll along with it.
        float wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0f && ImGui.IsMouseHoveringRect(s.PopupMin, s.PopupMax, false))
        {
            s.PopupWheelAcc += wheel * PopupWheelRows;
            int rows = (int)s.PopupWheelAcc;
            if (rows != 0) { s.PopupWheelAcc -= rows; s.PopupScroll -= rows; }
        }
        else s.PopupWheelAcc = 0f;

        // and the keyboard drags the window only when the selection actually moved. a hover sets
        // Sel to a row that is on screen by definition, so it never moves anything.
        s.PopupScroll = Math.Clamp(s.PopupScroll, 0, maxScroll);
        if (s.Sel != s.PopupSelShown)
        {
            s.PopupScroll = PopupScrollStart(s.Items.Count, s.Sel, PopupRows, s.PopupScroll);
            s.PopupSelShown = s.Sel;
        }
        int start = Math.Clamp(s.PopupScroll, 0, maxScroll);

        // width off the widest visible row only, not the whole list - a provider can hand back
        // thousands of entries and this runs every frame.
        float rowWidth = 0f;
        for (int i = start; i < start + visible; i++)
        {
            var it = s.Items[i];
            // Label null-falls-back to Insert same as the Completion constructor documents - a
            // completer built through a path that skips the constructor (default(Completion), an
            // object initializer) can still leave Label null with Insert set, so this guards the
            // same way at the draw site instead of trusting every construction path.
            float w = ImGui.CalcTextSize(Text.Ascii(it.Label ?? it.Insert ?? "")).X;
            if (!string.IsNullOrEmpty(it.Detail))
                w += gap + ImGui.CalcTextSize(Text.Ascii(it.Detail)).X;
            rowWidth = Math.Max(rowWidth, w);
        }
        float boxW = Math.Clamp(rowWidth + pad * 2f, 160f, 420f);
        float boxH = visible * rowH + pad * 2f;

        var display = ImGui.GetIO().DisplaySize;
        var min = wordTop + new Vector2(0f, lineH);              // the existing anchor: just under the word
        if (min.Y + boxH > display.Y) min.Y = wordTop.Y - boxH;  // flip above when it would run off the bottom
        if (min.X + boxW > display.X) min.X = display.X - boxW; // clamp off the right edge
        min.X = Math.Max(min.X, 0f);
        min.Y = Math.Max(min.Y, 0f);
        var max = min + new Vector2(boxW, boxH);
        s.PopupMin = min; s.PopupMax = max;   // next frame's wheel hit test, and Draw's wheel claim

        // hover moves the selection, same as the Selectable it replaces. click-to-commit is
        // skipped on purpose - this is a foreground draw, not a real window, so nothing here
        // captures the click and it would still land on the focused InputText underneath (moving
        // the caret, maybe losing the selection) at the same time we tried to commit off it.
        var mouse = ImGui.GetMousePos();
        if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y + pad && mouse.Y <= max.Y)
        {
            int hoverIdx = start + (int)((mouse.Y - min.Y - pad) / rowH);
            if (hoverIdx >= start && hoverIdx < start + visible) s.Sel = hoverIdx;
        }

        var dl = ImGui.GetForegroundDrawList();
        Overlay.Chrome(dl, min, max, o.Background, EColor.Fade(o.Foreground, 0.35f), 1f, style.FrameRounding);

        uint highlight = EColor.U32(EColor.Fade(o.Foreground, 0.18f));
        uint dim = EColor.U32(EColor.Fade(o.Foreground, 0.55f));
        for (int i = start; i < start + visible; i++)
        {
            var it = s.Items[i];
            float rowY = min.Y + pad + (i - start) * rowH;
            var rowMin = new Vector2(min.X, rowY);
            var rowMax = new Vector2(max.X, rowY + rowH);
            if (i == s.Sel) dl.AddRectFilled(rowMin, rowMax, highlight);

            float textY = rowY + (rowH - ImGui.GetTextLineHeight()) * 0.5f;
            string label = Text.Ascii(it.Label ?? it.Insert ?? "");
            uint col = palette.TryGetValue(it.Kind, out var c) ? EColor.U32(c) : EColor.U32(o.Foreground);
            dl.AddText(new Vector2(min.X + pad, textY), col, label);

            if (string.IsNullOrEmpty(it.Detail)) continue;
            string detail = Text.Ascii(it.Detail);
            float labelW = ImGui.CalcTextSize(label).X;
            float detailX = Math.Max(min.X + pad + labelW + gap, max.X - pad - ImGui.CalcTextSize(detail).X);
            dl.AddText(new Vector2(detailX, textY), dim, detail);
        }
    }

    /// <summary>
    /// Scroll offset that brings <paramref name="pos"/> into view along one axis. Axis-agnostic - the
    /// same math whether the arguments are x's or y's - and pure, so it is testable without a real
    /// ImGui context.
    /// </summary>
    /// <param name="margin">Slack kept between the target and the far edge.</param>
    /// <returns>-1 when it is already visible and no scroll is needed. Never negative otherwise.</returns>
    public static float FollowScroll(float pos, float scroll, float view, float margin)
    {
        float far = scroll + view - margin;
        if (pos < scroll) return Math.Max(0f, pos);
        if (pos > far) return Math.Max(0f, pos - (far - scroll));
        return -1f;
    }

    // re-splits and re-lexes. only ever called on a frame where the text actually moved.
    static void Reflow(State s, string text, EditorOpts o, bool plain)
    {
        SplitLines(text, s.Lines);
        s.MaxLineWidth = 0f;
        // through ColumnX, not CalcTextSize - CalcTextSize rounds its total up a whole pixel,
        // ColumnX floors the same raw advance sum the glyphs actually painted with, and this
        // drives the horizontal scroll extent that has to agree with where those glyphs really
        // end, not a rounded-up guess.
        for (int i = 0; i < s.Lines.Count; i++)
        {
            var line = s.Lines[i];
            s.MaxLineWidth = Math.Max(s.MaxLineWidth, ColumnX(s, line, line.Length));
        }

        while (s.Tokens.Count < s.Lines.Count) s.Tokens.Add(new List<Token>());
        while (s.LexState.Count < s.Lines.Count) s.LexState.Add(0);
        if (plain) { for (int i = 0; i < s.Lines.Count; i++) s.Tokens[i].Clear(); return; }

        // below the plain-mode return on purpose: nothing reads LiteralMask in plain mode, so a
        // document past MaxChars shouldn't pay for a 1MB LOH bool[] and a clear per reflow.
        if (s.LiteralMask.Length != text.Length) s.LiteralMask = new bool[text.Length];
        Array.Clear(s.LiteralMask, 0, s.LiteralMask.Length);

        // ponytail: re-lexes every line on each edit. microseconds at 500 lines, and it dodges a
        // whole class of cascade bugs. if this stops holding around 2k lines, re-lex from the
        // edited line until the carried state reconverges instead.
        int carry = 0;
        for (int i = 0; i < s.Lines.Count; i++)
        {
            s.Tokens[i].Clear();
            try { carry = o.Tokenize(s.Lines[i], carry, s.Tokens[i]); }
            catch
            {
                // a tokenizer that throws is switched off for the session rather than throwing
                // once per line per frame out of a render loop. latches on State, so it does not
                // recover on its own - DrawErrors surfaces this once as an underline on line 0,
                // but clearing it for real needs Editor.Forget(id) (a fresh State) or a process
                // restart.
                s.TokenFailed = true;
                for (int j = 0; j < s.Lines.Count; j++) s.Tokens[j].Clear();
                return;
            }
            s.LexState[i] = carry;
        }
        BuildLiteralMask(s.Lines, s.Tokens, s.LiteralMask);
    }

    /// <summary>
    /// Stamps every string and comment token's char range into a flat whole-document mask, converting
    /// per-line token offsets with a running accumulator where each line contributes its own length
    /// plus one newline. This is what makes bracket matching O(1) per char.
    /// </summary>
    /// <param name="mask">Must already be sized to the whole document and cleared - this only ever
    /// sets bits.</param>
    public static void BuildLiteralMask(List<string> lines, List<List<Token>> tokens, bool[] mask)
    {
        int lineStart = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i] ?? "";
            var toks = i < tokens.Count ? tokens[i] : null;
            if (toks != null)
            {
                // same validity rule DrawOverlay paints with - a tokenizer returning garbage must
                // not stamp literal flags onto some other line, or spin this loop on a bogus length.
                int cursor = 0;
                for (int t = 0; t < toks.Count; t++)
                {
                    if (!ValidToken(toks[t], cursor, line.Length)) continue;
                    if (toks[t].Kind == TokenKind.String || toks[t].Kind == TokenKind.Comment)
                    {
                        for (int c = 0; c < toks[t].Length; c++)
                        {
                            int at = lineStart + toks[t].Start + c;
                            if (at >= 0 && at < mask.Length) mask[at] = true;
                        }
                    }
                    cursor = toks[t].Start + toks[t].Length;
                }
            }
            lineStart += line.Length + 1;
        }
    }

    static bool WordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// The identifier run the caret sits in, letters, digits and underscores. Completion only uses
    /// [start, caret) - the word being typed - but the full span is what a name like this should
    /// return, and it is what backs double-click-to-select-word.
    /// </summary>
    public static (int start, int end) WordAt(string text, int caret)
    {
        text ??= "";
        caret = Math.Clamp(caret, 0, text.Length);
        int start = caret;
        while (start > 0 && WordChar(text[start - 1])) start--;
        int end = caret;
        while (end < text.Length && WordChar(text[end])) end++;
        return (start, end);
    }

    /// <summary>
    /// The text between wordStart and caret, with both clamped into range first. That clamp is the
    /// point: the caret can arrive past the end of the text - a utf-8 byte offset reused as a char
    /// index, or one gone stale against a buffer swapped out from under an unfocused editor - and a
    /// raw substring call would throw.
    /// </summary>
    public static string WordText(string text, int wordStart, int caret)
    {
        text ??= "";
        caret = Math.Clamp(caret, 0, text.Length);
        wordStart = Math.Clamp(wordStart, 0, caret);
        return caret > wordStart ? text.Substring(wordStart, caret - wordStart) : "";
    }

    /// <summary>
    /// '.' when the word at <paramref name="wordStart"/> is a member access, otherwise '\0'. Skips no
    /// whitespace, since "a . b" is not member access anyone is mid-typing.
    /// </summary>
    public static char TriggerAt(string text, int wordStart)
    {
        text ??= "";
        return wordStart > 0 && wordStart <= text.Length && text[wordStart - 1] == '.' ? '.' : '\0';
    }

    /// <summary>
    /// Ranks a candidate against the word being typed. Lower sorts first.
    /// <para>
    /// Rides on <see cref="Combo.Matches"/> rather than its own IndexOf, so the popup uses the same
    /// contains rule as every other picker in the library.
    /// </para>
    /// </summary>
    /// <returns>0 for a prefix hit, any positive number for a contains hit, negative for no match.
    /// Callers only ever bucket on the sign, never the magnitude.</returns>
    public static int Rank(string word, string candidate)
    {
        if (string.IsNullOrEmpty(word)) return 0;
        if (candidate == null) return -1;
        if (candidate.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return 0;
        return Combo.Matches(word, candidate) ? 1 : -1;
    }

    /// <summary>
    /// Completes members off a single root type, walking an <c>a.b.c.</c> chain back from the caret.
    /// No hand-kept schema, so it cannot drift from whatever assembly is actually loaded.
    /// <para>
    /// Methods are NOT offered - a dynamic linq filter has no use for them. Bare enum type names
    /// reachable from the root are, so <c>ItemRarity.Rare</c> resolves even though the enum lives in
    /// a different assembly than the root.
    /// </para>
    /// </summary>
    /// <param name="root">The implicit object every chain hangs off, like IFL's ItemData.</param>
    public static Completer ReflectionCompleter(Type root)
    {
        return (in CompletionContext ctx, List<Completion> into) =>
        {
            if (root == null) return;
            try
            {
                var type = Resolve(root, ctx.Text, ctx.WordStart);
                if (type == null) return;

                Members(type, into, false);

                // top level only (no dot behind the caret, ctx.Trigger == '\0' means Resolve
                // never left root) - offer the bare type names too, so "Item" suggests
                // ItemRarity alongside ItemLevel and ItemQuality.
                if (ctx.Trigger != '.')
                    foreach (var kv in BareTypesForRoot(root))
                        into.Add(new Completion(kv.Key, "enum", TokenKind.Type));
            }
            catch
            {
                // reflection over a type graph we don't control can surprise us in ways this
                // can't enumerate up front. a completer must never throw out of a draw call.
                // scoped to this one query rather than latched like TokenFailed/CompleteFailed -
                // a single bad hop just yields no completions this keystroke and the next query
                // gets a clean try, so there is no persistent failure here worth surfacing to the
                // user the way a tokenizer or completer dying for the whole session is.
            }
        };
    }

    /// <summary>
    /// The same walk, but starting from any of several NAMED types rather than one implicit root, and
    /// offering methods too. This is what C# needs: there is no single object every chain hangs off,
    /// only whatever names happen to be in scope, so the first hop has to name a seed and everything
    /// after it is ordinary member access.
    /// <para>
    /// There is no semantic model behind this. <c>var e = list.First(); e.</c> resolves nothing -
    /// only chains rooted at a seed name do. That is the price of this being about a hundred lines of
    /// reflection instead of a hosted compiler.
    /// </para>
    /// </summary>
    /// <param name="seeds">Name to type. The lookup uses whatever comparer you built the map with, so
    /// pass an OrdinalIgnoreCase one to let a local named <c>entity</c> start a chain as well as the
    /// type name itself does.</param>
    public static Completer ReflectionCompleter(IReadOnlyDictionary<string, Type> seeds)
    {
        return (in CompletionContext ctx, List<Completion> into) =>
        {
            if (seeds == null || seeds.Count == 0) return;
            try
            {
                // no dot behind the caret, so nothing is resolved yet and the names themselves are
                // the only thing we can honestly offer
                if (ctx.Trigger != '.')
                {
                    foreach (var kv in seeds)
                        if (kv.Value != null) into.Add(new Completion(kv.Key, Pretty(kv.Value), TokenKind.Type));
                    return;
                }

                var hops = Hops(ctx.Text, ctx.WordStart);
                if (hops.Count == 0 || !seeds.TryGetValue(hops[0], out var type) || type == null) return;
                for (int i = 1; i < hops.Count; i++)
                {
                    type = Step(type, hops[i]);
                    if (type == null) return;   // a hop we cannot resolve means no completions, not a guess
                }
                Members(type, into, true);
            }
            catch
            {
                // same reasoning as the single-root overload above - a completer must never throw
                // out of a draw call, and one bad hop is not worth latching the session off for.
            }
        };
    }

    // one definition of what a type contributes to the popup. methods are opt-in: a dynamic linq
    // filter has no use for them, c# very much does.
    static void Members(Type type, List<Completion> into, bool methods)
    {
        foreach (var p in type.GetProperties())
            into.Add(new Completion(p.Name, Pretty(p.PropertyType), TokenKind.Identifier));
        foreach (var f in type.GetFields())
            if (f.IsPublic && !f.IsSpecialName)
                into.Add(new Completion(f.Name, Pretty(f.FieldType), TokenKind.Identifier));
        if (type.IsEnum)
            foreach (var n in Enum.GetNames(type))
                into.Add(new Completion(n, type.Name, TokenKind.Type));
        if (!methods) return;

        // one row per name rather than per overload - GetComponent on its own would otherwise fill
        // the popup. object's four come off everything and say nothing, so they go too. a generic
        // method inserts its opening angle bracket: nothing here can infer the type argument, so
        // the user has to name it themselves anyway.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in type.GetMethods())
        {
            if (m.IsSpecialName || m.DeclaringType == typeof(object)) continue;   // property accessors and operators
            if (!seen.Add(m.Name)) continue;
            bool generic = m.IsGenericMethodDefinition;
            into.Add(new Completion(m.Name + (generic ? "<" : "("), Pretty(m.ReturnType),
                TokenKind.Identifier, m.Name + (generic ? "<>()" : "()")));
        }
    }

    // walks "a.b.c." leftwards from wordStart and returns the type the last dot hangs off. bounded
    // by how many dots the user actually typed, not by the size of the type graph - a self
    // referencing type just resolves to itself at whatever depth the chain reaches.
    static Type Resolve(Type root, string text, int wordStart)
    {
        var hops = Hops(text, wordStart);
        var type = root;
        for (int hopIndex = 0; hopIndex < hops.Count; hopIndex++)
        {
            var next = Step(type, hops[hopIndex]);
            if (next != null) { type = next; continue; }
            // a bare type name (ItemRarity.Rare) only makes sense as the first hop off the root -
            // past that a failed hop just means the chain does not exist.
            if (hopIndex == 0 && BareTypesForRoot(root).TryGetValue(hops[hopIndex], out var t))
            {
                type = t; continue;
            }
            return null;   // a hop we cannot resolve means no completions, not a guess
        }
        return type;
    }

    // the "a.b.c." chain sitting immediately left of wordStart, outermost first. empty when there
    // is no dot behind the caret at all.
    static List<string> Hops(string text, int wordStart)
    {
        var hops = new List<string>();
        int i = Math.Clamp(wordStart, 0, (text ?? "").Length);
        while (i > 0 && text[i - 1] == '.')
        {
            int end = i - 1;
            int start = end;
            while (start > 0 && WordChar(text[start - 1])) start--;
            if (start == end) break;
            hops.Insert(0, text.Substring(start, end - start));
            i = start;
        }
        return hops;
    }

    // one member hop: the type of `hop` read off `type`, or null when there is no such member.
    static Type Step(Type type, string hop)
    {
        var p = type.GetProperty(hop);
        if (p != null) return p.PropertyType;
        var f = type.GetField(hop);
        return f?.FieldType;
    }

    // bare type names a user could plausibly write off the root, e.g. ItemRarity in ItemRarity.Rare.
    // scanning the root's own assembly by name misses those - ItemRarity lives in ExileCore, ItemData
    // in ItemFilterLibrary - so this walks what the root actually EXPOSES: its public members, plus
    // one hop deeper for a nested type like ItemData.GemInfo whose own member is the enum wanted.
    // only enums are kept, that being what a user actually bare-types in a filter expression.
    // cached per root, since the completer runs on every change to the completed word.
    static readonly Dictionary<Type, Dictionary<string, Type>> _bareTypesByRoot = new();

    // bumped only on an actual build, not a cache hit - a self-check hook, so a regression that
    // rebuilds per call fails a test instead of being a quiet perf loss.
    internal static int BareTypesBuildCount;

    static Dictionary<string, Type> BareTypesForRoot(Type root)
    {
        if (_bareTypesByRoot.TryGetValue(root, out var cached)) return cached;
        BareTypesBuildCount++;

        var map = new Dictionary<string, Type>(StringComparer.Ordinal);
        var visited = new HashSet<Type> { root };
        foreach (var t1 in MemberTypes(root))
        {
            if (t1.IsEnum) { map[t1.Name] = t1; continue; }
            if (t1.IsPrimitive || t1 == typeof(string) || !visited.Add(t1)) continue;
            // one hop deeper only - far enough to catch ItemData.GemInfo.QualityType, not so far
            // that Entity or GameController drag their whole object graph of enums along.
            foreach (var t2 in MemberTypes(t1))
                if (t2.IsEnum) map[t2.Name] = t2;
        }

        _bareTypesByRoot[root] = map;
        return map;
    }

    static IEnumerable<Type> MemberTypes(Type t)
    {
        foreach (var p in t.GetProperties()) yield return p.PropertyType;
        foreach (var f in t.GetFields())
            if (f.IsPublic && !f.IsSpecialName) yield return f.FieldType;
    }

    static string Pretty(Type t) => t == null ? "" : t.IsGenericType
        ? t.Name.Substring(0, t.Name.IndexOf('`')) + "<>"
        : t.Name;

    /// <summary>
    /// One definition of what will actually be painted, since a tokenizer is caller-supplied and can
    /// return anything. A token has to start at or after the cursor, have a non-negative length, and
    /// stay inside the line.
    /// <para>
    /// Zero-length tokens are valid - they paint nothing and don't move the cursor.
    /// </para>
    /// </summary>
    /// <param name="cursor">End of the previous accepted token, so runs can't overlap or go backward.</param>
    public static bool ValidToken(Token t, int cursor, int lineLen)
    {
        if (t.Start < cursor) return false;
        if (t.Length < 0) return false;
        // t.Start + t.Length > lineLen would overflow for a huge Length. this form can't.
        if (t.Length > lineLen - t.Start) return false;
        return true;
    }

    // ---- drawing. everything below only paints; none of it feeds back into Draw's own logic.
    // kept in this file on purpose: the toolkit ships by copying sources into a plugin, so one
    // file to drop in beats a tidier split.

    /// <summary>
    /// Whether a line at <paramref name="y"/> is fully outside the visible band, with one line of
    /// slack on each side so a scroll doesn't pop text in and out mid-frame. Shared by the glyph,
    /// error and gutter passes so they cannot disagree about what is on screen.
    /// </summary>
    public static bool LineClipped(float y, float lineH, float top, float bottom)
    {
        return y + lineH < top - lineH || y > bottom + lineH;
    }

    // x offset of a column, floored the way imgui's own text rendering floors it. every overlay
    // draw goes through this so the glyphs, the caret and the bracket boxes cannot disagree by
    // a pixel. confirmed against the pinned ImGui.NET 1.90.0.1 native binary: AddText's start x
    // lands on a whole pixel regardless of the fractional part handed in, AddRect's does not.
    static float ColumnX(State s, string line, int col)
    {
        line ??= "";
        col = Math.Clamp(col, 0, line.Length);
        return MathF.Floor(ColumnWidths(s, line)[col]);
    }

    // rough inverse of ColumnX: the column whose glyph sits under x. the char itself, not the
    // nearest boundary - this feeds WordAt, which wants "what did they point at".
    static int ColumnAt(State s, string line, float x)
    {
        line ??= "";
        if (line.Length == 0) return 0;
        var xs = ColumnWidths(s, line);
        for (int i = 0; i < line.Length; i++) if (x < xs[i + 1]) return i;
        return line.Length;   // past the end of the line, where WordAt still finds the last word
    }

    // cumulative width up to each column, summed once per line and reused, so a line with twenty
    // tokens measures once instead of twenty times.
    //
    // sums raw glyph advances rather than calling CalcTextSize per char. CalcTextSize rounds its
    // total UP to a whole pixel, so per-char calls bank a rounding error per character and the
    // text visibly spreads out the further along the line you get. GetCharAdvance is the same
    // unrounded number imgui's own renderer steps by, which is the thing we need to agree with.
    static float[] ColumnWidths(State s, string line)
    {
        if (ReferenceEquals(s.ColXLine, line)) return s.ColX;
        if (s.ColX.Length < line.Length + 1) s.ColX = new float[line.Length + 1];

        var font = ImGui.GetFont();
        float scale = font.FontSize > 0f ? ImGui.GetFontSize() / font.FontSize : 1f;
        float w = 0f;
        s.ColX[0] = 0f;
        for (int i = 0; i < line.Length; i++)
        {
            w += font.GetCharAdvance(line[i]) * scale;
            s.ColX[i + 1] = w;
        }
        s.ColXLine = line;
        return s.ColX;
    }

    // paints the glyphs the transparent InputText did not. only lines inside the clip rect, so a
    // long document costs memory rather than frame time.
    static void DrawOverlay(State s, EditorOpts o, Vector2 textOrigin, float lineH, bool plain)
    {
        var dl = ImGui.GetWindowDrawList();
        uint fallback = EColor.U32(o.Foreground);
        float top = ImGui.GetScrollY();
        float bottom = top + ImGui.GetWindowHeight();

        for (int i = 0; i < s.Lines.Count; i++)
        {
            float y = i * lineH;
            if (LineClipped(y, lineH, top, bottom)) continue;
            var at = textOrigin + new Vector2(0f, y);
            var toks = plain || i >= s.Tokens.Count ? null : s.Tokens[i];
            if (toks == null || toks.Count == 0) { dl.AddText(at, fallback, s.Lines[i]); continue; }

            var line = s.Lines[i];
            var palette = o.Palette ?? DefaultPalette;
            // positioned by each run's own start column, not by summing what came before - that
            // is what keeps this in step with DrawCaret and DrawBrackets, which measure the same way.
            int cursor = 0;
            for (int t = 0; t < toks.Count; t++)
            {
                var tok = toks[t];
                if (!ValidToken(tok, cursor, line.Length)) continue;   // bad token, skip it
                if (tok.Start > cursor)
                {
                    var gap = line.Substring(cursor, tok.Start - cursor);
                    dl.AddText(at + new Vector2(ColumnX(s, line, cursor), 0f), fallback, gap);
                }
                var run = line.Substring(tok.Start, tok.Length);
                uint col = palette.TryGetValue(tok.Kind, out var c) ? EColor.U32(c) : fallback;
                dl.AddText(at + new Vector2(ColumnX(s, line, tok.Start), 0f), col, run);
                cursor = tok.Start + tok.Length;
            }
            if (cursor < line.Length)
                dl.AddText(at + new Vector2(ColumnX(s, line, cursor), 0f), fallback, line.Substring(cursor));
        }
    }

    // imgui's own caret went transparent along with the glyphs, so draw it. 2s period, 1.2s on
    // and 0.8s off, and it stays solid for the first 0.4s after it moves regardless of phase.
    // caretX comes in pre-measured from Draw, since the horizontal follow-scroll needs the same value.
    static void DrawCaret(State s, EditorOpts o, Vector2 textOrigin, float lineH, float caretX)
    {
        if (!s.Focused) return;
        if (s.CaretLine >= s.Lines.Count) return;

        if (!CaretVisible(ImGui.GetTime() - s.CaretMovedAt)) return;

        var a = textOrigin + new Vector2(caretX, s.CaretLine * lineH);
        ImGui.GetWindowDrawList().AddLine(a, a + new Vector2(0f, lineH), EColor.U32(o.Foreground), 1f);
    }

    /// <summary>
    /// Whether the caret is in the visible half of its blink cycle: a 2s period, 1.2s on and 0.8s
    /// off, staying solid for the first 0.4s after it moves regardless of phase.
    /// </summary>
    /// <param name="sinceMoved">Seconds since the caret last moved.</param>
    public static bool CaretVisible(double sinceMoved)
    {
        if (sinceMoved <= 0.4) return true;
        return sinceMoved % 2.0 <= 1.2;
    }

    // "is this char inside a string or comment", answered from a mask built once per edit.
    // BracketMate scans to the end of the document on an unmatched bracket, which is the normal
    // state while you are still typing the pair, so this has to be O(1) per char.
    static Func<int, bool> Literals(State s)
    {
        var mask = s.LiteralMask;
        return i => i >= 0 && i < mask.Length && mask[i];
    }

    static void DrawBrackets(State s, EditorOpts o, string text, Vector2 textOrigin, float lineH)
    {
        var literal = Literals(s);
        int mate = BracketMate(text, s.Caret, literal);
        if (mate < 0) return;
        FindAnchor(text, s.Caret, literal, out int self, out _);
        var dl = ImGui.GetWindowDrawList();
        // Foreground, not a theme color: this paints onto the editor's own fixed surface, and
        // Foreground is the one thing guaranteed to contrast Background. same as the caret.
        uint col = EColor.U32(o.Foreground);
        Box(self); Box(mate);

        void Box(int idx)
        {
            var (ln, cl) = CaretLineCol(s.Lines, idx);
            if (ln >= s.Lines.Count) return;
            var line = s.Lines[ln];
            // both edges through ColumnX, right one included - a box whose left edge floors but
            // whose right edge doesn't lands a pixel off.
            float x0 = ColumnX(s, line, cl);
            float x1 = ColumnX(s, line, cl + 1);
            var a = textOrigin + new Vector2(x0, ln * lineH);
            dl.AddRect(a, a + new Vector2(x1 - x0, lineH), col);
        }
    }

    // red underline plus a hover tooltip for every line the caller flagged. the control never
    // decides what an error is, it just draws the map it was handed.
    static void DrawErrors(State s, EditorOpts o, Vector2 textOrigin, float lineH)
    {
        var dl = ImGui.GetWindowDrawList();
        // through o.Palette first, so a caller overriding Error gets it on the underline too and
        // not just on the tokens
        var palette = o.Palette ?? DefaultPalette;
        uint col = palette.TryGetValue(TokenKind.Error, out var errColor) ? EColor.U32(errColor) : EColor.U32(DefaultPalette[TokenKind.Error]);
        float top = ImGui.GetScrollY();
        float bottom = top + ImGui.GetWindowHeight();

        // a tokenizer or completer that threw would otherwise go quiet - it switches off for the
        // session and the caller never finds out. surface it on line 0 through the same
        // underline-plus-tooltip this already draws. both flags latch for the life of this id's
        // State, so clearing the message needs Editor.Forget(id) or a restart, not just a fixed
        // tokenizer.
        if ((s.TokenFailed || s.CompleteFailed) && s.Lines.Count > 0 && !LineClipped(0f, lineH, top, bottom))
        {
            string msg = s.TokenFailed && s.CompleteFailed
                ? "tokenizer and completer both threw - highlighting and completion are off for this editor"
                : s.TokenFailed
                    ? "tokenizer threw - syntax highlighting is off for this editor"
                    : "completer threw - autocomplete is off for this editor";
            var line0 = s.Lines[0];
            float w0 = Math.Max(ColumnX(s, line0, line0.Length), 8f);
            var a0 = textOrigin + new Vector2(0f, lineH - 1f);
            dl.AddLine(a0, a0 + new Vector2(w0, 0f), col);
            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseHoveringRect(textOrigin, textOrigin + new Vector2(w0, lineH)))
                ImGui.SetTooltip(Text.Ascii(msg));
        }

        if (o.Errors == null || o.Errors.Count == 0) return;

        // o.Errors is caller-owned, and the readme invites it to be refreshed off a background
        // compiler (a Task), so a foreach here can land mid-mutation and throw. that is a
        // this-frame hiccup, not a broken map, so just skip painting errors for this one frame and
        // try again next frame - unlike TokenFailed/CompleteFailed below this does not latch, since
        // the caller's dictionary is expected to keep changing under us for as long as the editor
        // is open.
        try
        {
            foreach (var kv in o.Errors)
            {
                int line = kv.Key;
                if (line < 0 || line >= s.Lines.Count) continue;   // a stale map outlives an edit
                float y = line * lineH;
                if (LineClipped(y, lineH, top, bottom)) continue;

                // through ColumnX, not CalcTextSize - same reason Reflow's MaxLineWidth switched:
                // CalcTextSize rounds up, ColumnX floors the same sum the glyphs paint with, and
                // this underline has to end where the line's glyphs actually end.
                var lineText = s.Lines[line];
                float w = Math.Max(ColumnX(s, lineText, lineText.Length), 8f);
                var a = textOrigin + new Vector2(0f, y + lineH - 1f);
                // a flat 1px rule rather than a real squiggle. the font has no wave glyph and
                // stitching one out of line segments costs more than it reads.
                dl.AddLine(a, a + new Vector2(w, 0f), col);

                var hot = textOrigin + new Vector2(0f, y);
                // IsMouseHoveringRect only clips to the current clip rect, not window z-order - a
                // popup drawn on top would not stop it, so gate on the window actually being hovered.
                // ChildWindows here because the multiline input is its own nested child window -
                // without it this is false exactly when the mouse sits over the text.
                if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseHoveringRect(hot, hot + new Vector2(w, lineH)))
                    ImGui.SetTooltip(Text.Ascii(kv.Value));
            }
        }
        catch { /* caller's error map changed mid-draw - skip this frame, not fatal */ }
    }

    static void DrawGutter(State s, Vector2 at, float width, float lineH)
    {
        // kept theme-tracked on purpose - the frame fill above only paints the text area, so the
        // gutter sits on the outer child's own ChildBg, not on o.Background. a fixed Foreground
        // here would just move the theme-contrast problem onto a background we don't control.
        var dl = ImGui.GetWindowDrawList();
        uint dim = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        float top = ImGui.GetScrollY();
        float bottom = top + ImGui.GetWindowHeight();
        for (int i = 0; i < s.Lines.Count; i++)
        {
            float y = i * lineH;
            if (LineClipped(y, lineH, top, bottom)) continue;
            string n = (i + 1).ToString();
            float w = ImGui.CalcTextSize(n).X;
            dl.AddText(at + new Vector2(width - w - 6f, y), dim, n);   // right-aligned numbers
        }
    }
}
