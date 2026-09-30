using System.Collections.Generic;
using System.Text;
using ImGuiNET;

namespace ExileImGui;

/// <summary>
/// Shared text helpers for the foreground-drawlist widgets (overlays, toasts, richtext).
/// </summary>
public static class Text
{
    /// <summary>
    /// Maps common Unicode to ASCII (dropping the rest) so text renders in ImGui's ASCII-only
    /// default font. Anything with no mapping becomes '?'.
    /// </summary>
    /// <param name="s">Text to fold. Null and empty pass through untouched.</param>
    /// <returns>The folded text, or <paramref name="s"/> itself when there was nothing to fold.</returns>
    public static string Ascii(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c < 128) { sb.Append(c); continue; }
            switch (c)
            {
                case '–': case '—': case '−': sb.Append('-'); break;   // en/em dash, minus
                case '→': sb.Append("->"); break;
                case '←': sb.Append("<-"); break;
                case '•': case '·': case '●': case '◇': case '▶': sb.Append('*'); break;
                case '✓': case '✔': case '×': sb.Append('x'); break;
                case '“': case '”': sb.Append('"'); break;
                case '‘': case '’': sb.Append('\''); break;
                case '…': sb.Append("..."); break;
                default: sb.Append('?'); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Greedy word wrap, for the foreground widgets that don't get ImGui's own wrapping.
    /// </summary>
    /// <param name="text">Text to wrap. Split on spaces only, so a single long word never breaks.</param>
    /// <param name="maxWidth">Pixel budget for the text at <paramref name="scale"/>.</param>
    /// <param name="scale">Draw size over the current font size, since CalcTextSize measures at the latter.</param>
    /// <returns>One entry per line, never empty - an unwrappable input comes back as a single line.</returns>
    public static List<string> Wrap(string text, float maxWidth, float scale)
    {
        var words = (text ?? "").Split(' ');
        var lines = new List<string>();
        var cur = "";
        foreach (var w in words)
        {
            var trial = cur.Length == 0 ? w : cur + " " + w;
            if (cur.Length > 0 && ImGui.CalcTextSize(trial).X * scale > maxWidth)
            {
                lines.Add(cur);
                cur = w;
            }
            else cur = trial;
        }
        if (cur.Length > 0) lines.Add(cur);
        if (lines.Count == 0) lines.Add(text ?? "");
        return lines;
    }
}
