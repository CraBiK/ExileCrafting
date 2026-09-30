using System;
using System.Collections.Generic;

namespace ExileImGui;

/// <summary>
/// C# tokenizer for the editor. Highlighting only - no completions and no compile check - so it
/// never has to be right about what a name means, only about where a literal starts and stops.
/// Pairs with <see cref="CoreApi.Complete"/>.
/// </summary>
public static class CSharp
{
    // lexer states carried across a line boundary
    const int Normal = 0;
    const int InBlockComment = 1;
    const int InVerbatim = 2;

    // internal rather than private so CoreApi can offer the same set as completions - one list,
    // so the popup and the coloring cannot drift apart
    // yes record and value are left out on purpose.
    internal static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "break", "case", "catch", "checked", "class", "const", "continue",
        "default", "delegate", "do", "else", "enum", "event", "explicit", "extern", "false",
        "finally", "fixed", "for", "foreach", "goto", "if", "implicit", "in", "interface",
        "internal", "is", "lock", "namespace", "new", "null", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sealed",
        "sizeof", "stackalloc", "static", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "unchecked", "unsafe", "using", "virtual", "volatile", "while",
        "async", "await", "get", "init", "nameof", "partial", "set", "when", "where", "yield", 
    };

    // kept as their own set rather than folded into Keywords because CoreApi offers them as a
    // separate completion group. they COLOR as keywords though - see Tokenize. `bool` and `string`
    // are keywords in c#, and giving them the type color put them in the same bucket as every
    // PascalCase name, so `bool Draw(` came out one flat colour end to end.
    internal static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "bool", "byte", "char", "decimal", "double", "dynamic", "float", "int", "long", "nint",
        "nuint", "object", "sbyte", "short", "string", "uint", "ulong", "ushort", "var", "void",
    };

    /// <summary>
    /// A <see cref="Tokenizer"/> over one line of C#.
    /// </summary>
    /// <param name="line">The line to lex. Null is treated as empty.</param>
    /// <param name="stateIn">Lexer state this line starts in, as returned for the line above.</param>
    /// <param name="into">Tokens are appended here; the list is never cleared.</param>
    /// <returns>The state the next line starts in - non-zero inside an open block comment or
    /// verbatim string, the only two literals that survive a line break.</returns>
    public static int Tokenize(string line, int stateIn, List<Token> into)
    {
        line ??= "";
        int i = 0;

        // a block comment or a verbatim string opened on an earlier line runs until its closer
        if (stateIn == InBlockComment)
        {
            int close = line.IndexOf("*/", StringComparison.Ordinal);
            if (close < 0)
            {
                if (line.Length > 0) into.Add(new Token(0, line.Length, TokenKind.Comment));
                return InBlockComment;
            }
            into.Add(new Token(0, close + 2, TokenKind.Comment));
            i = close + 2;
        }
        else if (stateIn == InVerbatim)
        {
            int end = VerbatimEnd(line, 0);
            if (end < 0)
            {
                if (line.Length > 0) into.Add(new Token(0, line.Length, TokenKind.String));
                return InVerbatim;
            }
            into.Add(new Token(0, end, TokenKind.String));
            i = end;
        }

        while (i < line.Length)
        {
            char c = line[i];

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                into.Add(new Token(i, line.Length - i, TokenKind.Comment));
                return Normal;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                int close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) { into.Add(new Token(i, line.Length - i, TokenKind.Comment)); return InBlockComment; }
                into.Add(new Token(i, close + 2 - i, TokenKind.Comment));
                i = close + 2;
                continue;
            }

            // a whole directive line, #if DEBUG and friends
            if (c == '#')
            {
                into.Add(new Token(i, line.Length - i, TokenKind.Keyword));
                return Normal;
            }

            // string prefixes. @ in any of them means verbatim, which is the only literal that
            // can survive a line break
            if (c == '@' || c == '$')
            {
                int p = i;
                bool at = false;
                while (p < line.Length && (line[p] == '@' || line[p] == '$')) { at |= line[p] == '@'; p++; }
                if (p < line.Length && line[p] == '"')
                {
                    int end = at ? VerbatimEnd(line, p + 1) : QuoteEnd(line, p + 1, '"');
                    if (end < 0)
                    {
                        into.Add(new Token(i, line.Length - i, TokenKind.String));
                        return at ? InVerbatim : Normal;
                    }
                    into.Add(new Token(i, end - i, TokenKind.String));
                    i = end;
                    continue;
                }
                i = p;   // @name is just a name wearing a prefix, let the word scan have it
                continue;
            }

            // ponytail: raw string literals ("""...""") lex as an empty string followed by another
            // one. colors come out wrong inside them, nothing hangs. add a third state if it bites.
            if (c == '"' || c == '\'')
            {
                int end = QuoteEnd(line, i + 1, c);
                int len = (end < 0 ? line.Length : end) - i;
                into.Add(new Token(i, len, TokenKind.String));
                i += len;
                continue;
            }

            if (char.IsDigit(c))
            {
                int j = i;
                while (j < line.Length)
                {
                    char d = line[j];
                    // letters and underscores cover hex digits, 1_000 and the f/u/L suffixes
                    if (char.IsLetterOrDigit(d) || d == '_' || d == '.') { j++; continue; }
                    if ((d == '+' || d == '-') && (line[j - 1] == 'e' || line[j - 1] == 'E')) { j++; continue; }
                    break;
                }
                into.Add(new Token(i, j - i, TokenKind.Number));
                i = j;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                var word = line.Substring(i, j - i);
                var kind = Keywords.Contains(word) || Builtins.Contains(word) ? TokenKind.Keyword
                    // no semantic model here, so PascalCase stands in for "names a type". catches
                    // class names, colors method calls the same way, misses a local named Foo.
                    : char.IsUpper(word[0]) ? TokenKind.Type
                    : TokenKind.Text;
                into.Add(new Token(i, j - i, kind));
                i = j;
                continue;
            }

            if ("=!<>&|+-*/%()[]{}.,;:?~^".IndexOf(c) >= 0)
            {
                into.Add(new Token(i, 1, TokenKind.Operator));
                i++;
                continue;
            }

            i++;   // whitespace and anything else stays uncolored
        }

        return Normal;
    }

    // index just past the closing quote of a verbatim body, or -1 if the line ends inside it.
    // "" is an escaped quote and keeps the string open.
    static int VerbatimEnd(string line, int start)
    {
        int j = start;
        while (j < line.Length)
        {
            if (line[j] != '"') { j++; continue; }
            if (j + 1 < line.Length && line[j + 1] == '"') { j += 2; continue; }
            return j + 1;
        }
        return -1;
    }

    // same for a regular string or char body, backslash escapes and all. a regular literal cannot
    // span lines, so -1 here means unterminated rather than carried.
    static int QuoteEnd(string line, int start, char quote)
    {
        int j = start;
        while (j < line.Length)
        {
            if (line[j] == '\\') { j += 2; continue; }
            if (line[j] == quote) return j + 1;
            j++;
        }
        return -1;
    }
}
