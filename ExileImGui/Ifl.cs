using System;
using System.Collections.Generic;
using System.Linq;

namespace ExileImGui;

/// <summary>
/// Tokenizer and completions for ItemFilterLibrary query text, which is System.Linq.Dynamic.Core
/// over ItemData.
/// <para>
/// Takes no compile-time dependency on IFL - the type is resolved by name at runtime, so this file
/// still compiles in a plugin that has never heard of it and simply offers nothing when IFL is not
/// loaded.
/// </para>
/// </summary>
public static class Ifl
{
    // lexer states carried across a line boundary
    const int Normal = 0;
    const int InString = 1;

    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "true", "false", "null", "and", "or", "not", "new", "as", "is", "iif",
    };

    // dynamic linq's own method set. not reflectable off ItemData, so it is hand-kept. it changes
    // when DLINQ changes, which is roughly never.
    static readonly string[] Methods =
    {
        "Any", "All", "Count", "Sum", "Min", "Max", "Average", "Where", "Select", "OrderBy",
        "Contains", "StartsWith", "EndsWith", "ToLower", "ToUpper", "Trim", "Equals",
    };

    /// <summary>
    /// A <see cref="Tokenizer"/> over one line of filter text.
    /// </summary>
    /// <param name="line">The line to lex. Null is treated as empty.</param>
    /// <param name="stateIn">Lexer state this line starts in, as returned for the line above.</param>
    /// <param name="into">Tokens are appended here; the list is never cleared.</param>
    /// <returns>The state the next line starts in - non-zero when a string was left open.</returns>
    public static int Tokenize(string line, int stateIn, List<Token> into)
    {
        line ??= "";
        int i = 0;

        // a string that opened on an earlier line runs until its closing quote
        if (stateIn == InString)
        {
            int close = line.IndexOf('"');
            if (close < 0) { into.Add(new Token(0, line.Length, TokenKind.String)); return InString; }
            into.Add(new Token(0, close + 1, TokenKind.String));
            i = close + 1;
        }

        while (i < line.Length)
        {
            char c = line[i];

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                into.Add(new Token(i, line.Length - i, TokenKind.Comment));
                return Normal;
            }

            if (c == '"')
            {
                int j = i + 1;
                while (j < line.Length && line[j] != '"') j++;
                if (j >= line.Length) { into.Add(new Token(i, line.Length - i, TokenKind.String)); return InString; }
                into.Add(new Token(i, j - i + 1, TokenKind.String));
                i = j + 1;
                continue;
            }

            if (char.IsDigit(c))
            {
                int j = i;
                while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.')) j++;
                into.Add(new Token(i, j - i, TokenKind.Number));
                i = j;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                var word = line.Substring(i, j - i);
                var kind = Keywords.Contains(word) ? TokenKind.Keyword
                    : char.IsUpper(word[0]) ? TokenKind.Identifier : TokenKind.Text;
                into.Add(new Token(i, j - i, kind));
                i = j;
                continue;
            }

            if ("=!<>&|+-*/%()[].,".IndexOf(c) >= 0)
            {
                into.Add(new Token(i, 1, TokenKind.Operator));
                i++;
                continue;
            }

            i++;   // whitespace and anything else stays uncolored
        }

        return Normal;
    }

    // resolved once, lazily. null when IFL is not loaded, and then there are simply no completions.
    static Type _root;
    static Completer _reflectionCompleter;   // built once off _root, not rebuilt every keystroke
    static bool _looked;

    static void EnsureRoot()
    {
        if (_looked) return;
        _looked = true;
        _root = Type.GetType("ItemFilterLibrary.ItemData, ItemFilterLibrary");
        if (_root == null)
            _root = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("ItemFilterLibrary.ItemData"))
                .FirstOrDefault(t => t != null);
        if (_root != null) _reflectionCompleter = Editor.ReflectionCompleter(_root);
    }

    /// <summary>
    /// A <see cref="Completer"/> for filter text: members reflected off ItemData when IFL is loaded,
    /// plus the language keywords and dynamic-linq methods at the top level.
    /// </summary>
    public static void Complete(in CompletionContext ctx, List<Completion> into)
    {
        EnsureRoot();
        _reflectionCompleter?.Invoke(in ctx, into);

        // the language half. only at the top level - after a dot you want members, not keywords.
        if (ctx.Trigger != '\0') return;
        foreach (var k in Keywords) into.Add(new Completion(k, "keyword", TokenKind.Keyword));
        foreach (var m in Methods) into.Add(new Completion(m, "method", TokenKind.Type));
    }
}
