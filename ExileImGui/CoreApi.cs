using System;
using System.Collections.Generic;
using System.Linq;

namespace ExileImGui;

/// <summary>
/// Completions for C# written against ExileCore. Pairs with <see cref="CSharp.Tokenize"/> the way
/// <see cref="Ifl.Complete"/> pairs with <see cref="Ifl.Tokenize"/>.
/// <para>
/// Takes no compile-time dependency on ExileCore - the types are resolved by name at runtime, so
/// this still compiles in a plugin that has never referenced it and offers the language half on its
/// own when ExileCore is not loaded.
/// </para>
/// <para>
/// Named CoreApi rather than Core on purpose: ExileCore.Core exists, and a plugin with both
/// <c>using ExileCore;</c> and <c>using ExileImGui;</c> could not say which one it meant.
/// </para>
/// </summary>
public static class CoreApi
{
    // the names a chain can start from. simple type names, matched against whatever the ExileCore
    // assembly actually declares, so a namespace move upstream does not break this list. these are
    // the ones people write in plugin code - the rest of the graph is reached by walking members
    // off them, which needs no list at all.
    static readonly string[] SeedTypeNames =
    {
        "GameController", "Graphics", "Input", "IngameState", "Entity", "Element",
    };

    static Dictionary<string, Type> _seeds;
    static Completer _reflectionCompleter;
    static bool _looked;

    // resolved once, lazily, off the assemblies already in the process - nothing here loads one.
    static void EnsureSeeds()
    {
        if (_looked) return;
        _looked = true;

        var wanted = new HashSet<string>(SeedTypeNames, StringComparer.Ordinal);
        // OrdinalIgnoreCase so a local named `entity` or `graphics` - which is what plugin code
        // usually has in hand - starts a chain just like the type name itself does
        var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name != "ExileCore" && name != "ExileCore2") continue;
            foreach (var t in SafeTypes(asm))
                if (t != null && t.IsPublic && wanted.Contains(t.Name) && !map.ContainsKey(t.Name))
                    map[t.Name] = t;
        }

        if (map.Count == 0) return;   // ExileCore is not loaded, so there is nothing to reflect over
        _seeds = map;
        _reflectionCompleter = Editor.ReflectionCompleter(map);
    }

    // a partly loadable assembly hands back what it could load through the exception rather than
    // through the return value. one missing dependency must not cost us every other seed.
    static IEnumerable<Type> SafeTypes(System.Reflection.Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        catch { return Array.Empty<Type>(); }
    }

    /// <summary>
    /// A <see cref="Completer"/> for C#: members walked off whichever seed type started the chain,
    /// plus the keywords and builtin type names at the top level.
    /// </summary>
    public static void Complete(in CompletionContext ctx, List<Completion> into)
    {
        EnsureSeeds();
        _reflectionCompleter?.Invoke(in ctx, into);

        // the language half. only at the top level - after a dot you want members, not keywords.
        if (ctx.Trigger != '\0') return;
        foreach (var k in CSharp.Keywords) into.Add(new Completion(k, "keyword", TokenKind.Keyword));
        // Keyword, matching how CSharp.Tokenize colors them in the buffer - a row that draws in a
        // different color from the text it inserts reads as a different kind of thing.
        foreach (var b in CSharp.Builtins) into.Add(new Completion(b, "type", TokenKind.Keyword));
    }

    /// <summary>
    /// Whether the reflection half found anything, for a caller that wants to say so in its UI.
    /// False only means completions are language-only, never that <see cref="Complete"/> is unsafe
    /// to call.
    /// </summary>
    public static bool CoreLoaded
    {
        get { EnsureSeeds(); return _seeds != null; }
    }
}
