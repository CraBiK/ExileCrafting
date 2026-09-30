using ExileCore.PoEMemory.FilesInMemory;
using ExileCore.Shared.Enums;
using ExileImGui;
using ImGuiNET;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System;

namespace ExileCrafting;

#region Script host - discovery, metadata, compilation

public sealed class ScriptHost
{
    sealed class CacheEntry
    {
        public DateTime LastWriteUtc;
        public Func<CraftContext, Task<object>> Runner;
        public string CompileError;
    }

    sealed class MetaEntry
    {
        public DateTime LastWriteUtc;
        public ScriptMeta Meta;
    }

    readonly Dictionary<string, CacheEntry> _cache = new();
    readonly Dictionary<string, MetaEntry> _metaCache = new();
    readonly string _scriptsDirectory;

    public const string NewScriptTemplate = "// Name: \r\n// Description: \r\n// Type: Currency\r\n\r\n";

    public ScriptHost(string scriptsDirectory)
    {
        _scriptsDirectory = scriptsDirectory;
        Directory.CreateDirectory(_scriptsDirectory);
    }

    // scripts live in config, but the bundled examples ship next to the dll, so they'd never show up
    // in the add picker. copy each one across once and remember it, so deleting one makes it stay
    // gone while a new example in a later version still arrives.
    public void SeedExamples(HashSet<string> seeded)
    {
        try
        {
            var folder = Path.Combine(ExileCrafting.Main.DirectoryFullName, "examples");
            if (!Directory.Exists(folder)) return;

            foreach (var source in Directory.GetFiles(folder, "*.csx"))
            {
                var name = Path.GetFileName(source);
                if (!seeded.Add(name)) continue;

                var destination = Path.Combine(_scriptsDirectory, name);
                if (!File.Exists(destination)) File.Copy(source, destination);
            }
        }
        catch
        {
            // no examples folder, or it's locked - the plugin just starts with an empty script list
        }
    }

    public IReadOnlyList<string> DiscoverScripts() =>
        Directory.GetFiles(_scriptsDirectory, "*.csx")
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToList();

    public string ReadScript(string fileName) => File.ReadAllText(Path.Combine(_scriptsDirectory, fileName));

    public void WriteScript(string fileName, string text) => File.WriteAllText(Path.Combine(_scriptsDirectory, fileName), text);

    // header the crafter reads when a script is first added to the list. only the leading comment
    // block counts - a "// Name:" further down the file is just a comment.
    public ScriptMeta ReadMetadata(string fileName)
    {
        var path = Path.Combine(_scriptsDirectory, fileName);
        if (!File.Exists(path)) return ScriptMeta.None;

        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (!_metaCache.TryGetValue(path, out var entry) || entry.LastWriteUtc != lastWrite)
        {
            entry = new MetaEntry { LastWriteUtc = lastWrite, Meta = ParseMetadata(File.ReadLines(path)) };
            _metaCache[path] = entry;
        }

        return entry.Meta;
    }

    // the file is the only source of truth for the type, so this is safe to call every frame
    public CraftType ReadType(string fileName) => ReadMetadata(fileName).Type;

    internal static ScriptMeta ParseMetadata(IEnumerable<string> lines)
    {
        string name = "", description = "", type = "";

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("//")) break;

            var body = line.Substring(2).Trim();
            if (TryTag(body, "Name:", ref name)) continue;
            if (TryTag(body, "Description:", ref description)) continue;
            TryTag(body, "Type:", ref type);
        }

        return new ScriptMeta(name, description, ParseType(type));
    }

    static CraftType ParseType(string value) =>
        (value ?? "").Trim().Equals("harvest", StringComparison.OrdinalIgnoreCase)
            ? CraftType.Harvest
            : CraftType.Currency;

    static bool TryTag(string body, string tag, ref string target)
    {
        if (!body.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) return false;
        target = body.Substring(tag.Length).Trim();
        return true;
    }

    public void OpenScriptsFolder() => Process.Start("explorer.exe", _scriptsDirectory);

    public bool TryGetOrCompile(string fileName, out Func<CraftContext, Task<object>> runner, out string error)
    {
        var path = Path.Combine(_scriptsDirectory, fileName);
        var lastWrite = File.GetLastWriteTimeUtc(path);

        if (!_cache.TryGetValue(path, out var entry) || entry.LastWriteUtc != lastWrite)
        {
            entry = Compile(path);
            entry.LastWriteUtc = lastWrite;
            _cache[path] = entry;
        }

        runner = entry.Runner;
        error = entry.CompileError;
        return entry.CompileError == null;
    }

    // one source of truth: scripts compile against exactly these, and the editor's autocomplete
    // indexes the same set so it can't offer something that won't compile.
    public static readonly Assembly[] ScriptAssemblies =
    {
        typeof(CraftContext).Assembly,
        typeof(ExileCore.GameController).Assembly,
        typeof(ItemFilterLibrary.ItemData).Assembly,
        typeof(Enumerable).Assembly,
    };

    public static readonly string[] ScriptImports =
    {
        "System", "System.Linq", "System.Threading.Tasks", "ExileCrafting",
        "ExileCore.PoEMemory.Components", "ExileCore.Shared.Enums",
        "ExileCore.PoEMemory.MemoryObjects", "ExileCore.PoEMemory.Models",
        "ItemFilterLibrary",
    };

    static ScriptOptions BuildOptions() =>
        ScriptOptions.Default
            .WithReferences(ScriptAssemblies.Select(ToMetadataReference))
            .WithImports(ScriptImports);

    // diagnostics only - no emit, no assembly load, so it's safe to run against unsaved editor text
    public string CheckCompile(string code)
    {
        try
        {
            var errors = CSharpScript.Create<object>(code, BuildOptions(), typeof(CraftContext))
                .GetCompilation()
                .GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            return errors.Count > 0 ? string.Join("\n", errors.Select(d => d.ToString())) : null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    CacheEntry Compile(string path)
    {
        var code = File.ReadAllText(path);

        try
        {
            var compilation = CSharpScript.Create<object>(code, BuildOptions(), typeof(CraftContext)).GetCompilation();

            using var pe = new MemoryStream();
            var emit = compilation.Emit(pe);
            var errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                return new CacheEntry { CompileError = string.Join("\n", errors.Select(d => d.ToString())) };

            pe.Position = 0;
            return new CacheEntry { Runner = BuildRunner(pe) };
        }
        catch (Exception e)
        {
            return new CacheEntry { CompileError = e.Message };
        }
    }

    // we emit and load the script ourselves rather than using Script.RunAsync, because Roslyn's
    // scripting loader makes the script assembly non-collectible - and ExileCore loads plugins
    // into a collectible context, which a non-collectible assembly is not allowed to reference.
    // ponytail: each recompile leaves its old context loaded. they're collectible now, so add an
    // Unload() if editing scripts all session actually grows memory enough to notice.
    static Func<CraftContext, Task<object>> BuildRunner(Stream peStream)
    {
        var scriptAssembly = new ScriptLoadContext(typeof(CraftContext).Assembly).LoadFromStream(peStream);
        var factory = scriptAssembly.GetType("Submission#0")
            ?.GetMethod("<Factory>", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("compiled script has no Submission#0.<Factory> entry point.");

        return ctx =>
        {
            // slot 0 is the globals instance, the rest is per-submission state
            var state = new object[2];
            state[0] = ctx;
            try
            {
                return (Task<object>)factory.Invoke(null, new object[] { state });
            }
            catch (TargetInvocationException e)
            {
                return Task.FromException<object>(e.InnerException ?? e);
            }
        };
    }

    // our own plugin assembly is loaded from a stream so it has no file location, and
    // CreateFromFile blows up on it. TryGetRawMetadata reads the in-memory PE image instead.
    static unsafe MetadataReference ToMetadataReference(Assembly assembly)
    {
        if (!string.IsNullOrEmpty(assembly.Location))
            return MetadataReference.CreateFromFile(assembly.Location);

        System.Reflection.Metadata.AssemblyExtensions.TryGetRawMetadata(assembly, out byte* blob, out var length);
        return AssemblyMetadata.Create(ModuleMetadata.CreateFromMetadata((IntPtr)blob, length)).GetReference();
    }

    // resolving our plugin here in Load() matters: if it falls through to the default context
    // instead, that one is non-collectible and refuses to reference our collectible assembly.
    sealed class ScriptLoadContext : AssemblyLoadContext
    {
        readonly Assembly _plugin;

        public ScriptLoadContext(Assembly plugin) : base("ExileCraftingScript", isCollectible: true) => _plugin = plugin;

        protected override Assembly Load(AssemblyName name) =>
            name.Name == _plugin.GetName().Name ? _plugin : null;
    }
}

// which panel a script drives. Currency is the default, so every pre-existing script keeps working.
public enum CraftType
{
    Currency,
    Harvest,
}

// what the "// Name:" / "// Description:" / "// Type:" header at the top of a .csx says, if anything
public readonly struct ScriptMeta
{
    public readonly string Name;
    public readonly string Description;
    public readonly CraftType Type;

    public ScriptMeta(string name, string description, CraftType type)
    {
        Name = name;
        Description = description;
        Type = type;
    }

    public static readonly ScriptMeta None = new("", "", CraftType.Currency);
}

// a script the user has added to their list, with the limits that run gets. plain fields so the
// settings serializer round-trips it without any attributes.
public sealed class ScriptEntry
{
    public string Name = "";
    public string Description = "";
    public string FileName = "";
    public int MaxApplications = 200;

    // 0 means no budget. needs the Ninja Price plugin loaded to do anything.
    public float MaxChaos;

    public string Title => string.IsNullOrWhiteSpace(Name) ? FileName : Name;

    // the file's own header wins as the starting values, filename as the fallback. editable after.
    public static ScriptEntry FromFile(ScriptHost host, string fileName)
    {
        var meta = host.ReadMetadata(fileName);

        return new ScriptEntry
        {
            FileName = fileName,
            Name = string.IsNullOrWhiteSpace(meta.Name) ? Path.GetFileNameWithoutExtension(fileName) : meta.Name,
            Description = meta.Description ?? "",
        };
    }
}

// one queued item: which inventory cell it sits in, and the script and limits it gets. plain
// fields so the settings serializer round-trips it without any attributes.
public sealed class QueueJob
{
    public int Cell;
    public string FileName = "";
    public int MaxApplications = 200;
    public float MaxChaos;
}

#endregion

#region Autocomplete for the editor

// Autocomplete for the script editor, driven by reflection over the same assemblies and imports
// ScriptHost compiles with. Roslyn's real CompletionService lives in CodeAnalysis.Features, which
// ExileCore doesn't ship, and pulling it in risks colliding with its own Roslyn - reflection covers
// the dotted chains people actually write in these scripts.
public static class ScriptCompletion
{
    const int MaxRows = 200;

    static readonly string[] Keywords =
    {
        "var", "if", "else", "while", "for", "foreach", "in", "return", "break", "continue",
        "await", "true", "false", "null", "new", "int", "float", "bool", "string", "double",
    };

    static Dictionary<string, Type> _typesByName;

    static Dictionary<string, Type> TypeIndex
    {
        get
        {
            if (_typesByName != null) return _typesByName;

            var imports = new HashSet<string>(ScriptHost.ScriptImports, StringComparer.Ordinal);
            var index = new Dictionary<string, Type>(StringComparer.Ordinal);

            foreach (var assembly in ScriptHost.ScriptAssemblies)
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (!type.IsPublic || type.Namespace == null || !imports.Contains(type.Namespace)) continue;
                    // first one wins - later namespaces don't silently shadow an earlier type
                    index.TryAdd(StripArity(type.Name), type);
                }
            }

            return _typesByName = index;
        }
    }

    public static void Complete(in CompletionContext ctx, List<Completion> into)
    {
        try
        {
            var chain = ReadChain(ctx.Text, ctx.WordStart);

            if (chain.Count == 0)
            {
                AddRootCandidates(into);
                return;
            }

            var (type, isStatic) = ResolveChain(chain);
            if (type != null) AddMembers(type, isStatic, into);
        }
        catch
        {
            // a completer that throws gets switched off for the editor's lifetime - offering
            // nothing for one keystroke is much better than losing autocomplete entirely.
        }
    }

    // the bare-identifier case: what you can reach with nothing typed before the caret
    static void AddRootCandidates(List<Completion> into)
    {
        // the globals type's members are in scope unqualified, same as in the compiled script
        AddMembers(typeof(CraftContext), staticOnly: false, into);

        foreach (var kw in Keywords)
            into.Add(new Completion(kw, null, TokenKind.Keyword));

        foreach (var pair in TypeIndex)
        {
            if (into.Count >= MaxRows) return;
            into.Add(new Completion(pair.Key, pair.Value.Namespace, TokenKind.Type));
        }
    }

    // walks "ctx.Item.Mods" one segment at a time, so each hop is a real reflected type
    static (Type type, bool isStatic) ResolveChain(List<string> chain)
    {
        Type current = null;
        var isStatic = false;

        var root = chain[0];
        var globals = typeof(CraftContext);

        var rootMember = MemberType(globals, root, staticOnly: false);
        if (rootMember != null)
        {
            current = rootMember;
        }
        else if (TypeIndex.TryGetValue(root, out var rootType))
        {
            current = rootType;
            isStatic = true;
        }
        else
        {
            return (null, false);
        }

        for (var i = 1; i < chain.Count; i++)
        {
            current = MemberType(current, chain[i], isStatic);
            if (current == null) return (null, false);
            isStatic = false;
        }

        return (current, isStatic);
    }

    static Type MemberType(Type owner, string name, bool staticOnly)
    {
        var flags = BindingFlags.Public | (staticOnly ? BindingFlags.Static : BindingFlags.Instance | BindingFlags.Static);

        var property = owner.GetProperty(name, flags);
        if (property != null) return property.PropertyType;

        var field = owner.GetField(name, flags);
        if (field != null) return field.FieldType;

        var method = owner.GetMethods(flags).FirstOrDefault(m => m.Name == name && !m.IsSpecialName);
        return method != null ? UnwrapTask(method.ReturnType) : null;
    }

    // scripts await these, so completing on the awaited value is what people mean
    static Type UnwrapTask(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.Task<>)
            ? type.GetGenericArguments()[0]
            : type;

    static void AddMembers(Type type, bool staticOnly, List<Completion> into)
    {
        if (type.IsEnum)
        {
            foreach (var name in Enum.GetNames(type))
                into.Add(new Completion(name, type.Name, TokenKind.Number));
            return;
        }

        var flags = BindingFlags.Public | (staticOnly ? BindingFlags.Static : BindingFlags.Instance);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in type.GetProperties(flags))
        {
            if (into.Count >= MaxRows) return;
            if (seen.Add(property.Name))
                into.Add(new Completion(property.Name, FriendlyName(property.PropertyType), TokenKind.Identifier));
        }

        foreach (var field in type.GetFields(flags))
        {
            if (into.Count >= MaxRows) return;
            if (seen.Add(field.Name))
                into.Add(new Completion(field.Name, FriendlyName(field.FieldType), TokenKind.Identifier));
        }

        foreach (var method in type.GetMethods(flags))
        {
            if (into.Count >= MaxRows) return;
            if (method.IsSpecialName || !seen.Add(method.Name)) continue;
            // inserts the open paren so the caret lands where the arguments go
            into.Add(new Completion(method.Name + "(", FriendlyName(method.ReturnType), TokenKind.Identifier, method.Name + "()"));
        }
    }

    // reads the dotted chain immediately before the word being typed, right to left
    static List<string> ReadChain(string text, int wordStart)
    {
        var chain = new List<string>();
        var i = Math.Min(wordStart, text?.Length ?? 0) - 1;

        while (i >= 0 && text[i] == '.')
        {
            var end = i;
            i--;
            while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i--;

            var segment = text.Substring(i + 1, end - i - 1);
            if (segment.Length == 0) break;

            chain.Insert(0, segment);
        }

        return chain;
    }

    static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            // a plugin assembly can reference something that isn't loadable here - keep what did load
            return e.Types.Where(t => t != null);
        }
    }

    static string StripArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name.Substring(0, tick);
    }

    // generic args spelled out, so the detail column doesn't read "Task" for a Task<bool>
    static string FriendlyName(Type type)
    {
        if (!type.IsGenericType) return type.Name;

        var args = string.Join(", ", type.GetGenericArguments().Select(FriendlyName));
        return $"{StripArity(type.Name)}<{args}>";
    }
}

#endregion


#region Script editor dialog

public sealed class ScriptEditorDialog
{
    public bool IsOpen;

    // Size is set per frame in Draw - zero height means fill whatever's left in the window
    readonly EditorOpts _editorOpts = new() { Tokenize = CSharp.Tokenize, Complete = ScriptCompletion.Complete };
    const string SaveAsPopupId = "Save Script As";
    const string EditorId = "crafteditor";

    const string InsertMenuId = "insertmenu";

    enum Picker
    {
        None,
        Mods,
        Currency,
        Harvest,
        Snippets,
    }

    enum AffixFilter
    {
        All,
        Prefix,
        Suffix,
    }

    // one picker panel is open at a time, so its whole filter state lives here
    Picker _picker;
    string _pickFilter = "";
    string _pickTag = AnyTag;
    AffixFilter _pickAffix;
    bool _openHarvestPicker;

    // mods only: picked but not inserted yet
    readonly List<string> _picked = new();

    const string AnyTag = "All";

    string _fileName;
    string _text = "";
    string _saveAsName = "";
    string _status;
    uint _statusColor;
    double _statusUntil;

    const uint Green = 0xFF00CC00u;
    const uint Red = 0xFF0000FFu;

    // errors stick around until something replaces them, the short confirmations fade
    void SetStatus(string message, uint color, bool fade) =>
        (_status, _statusColor, _statusUntil) = (message, color, fade ? ImGui.GetTime() + 1.5 : double.MaxValue);

    public void OpenForEdit(string fileName, string text)
    {
        _fileName = fileName;
        _text = text;
        Editor.SetText(EditorId,text);
        _status = null;
        IsOpen = true;
    }

    public void OpenForNew()
    {
        _fileName = null;
        _text = ScriptHost.NewScriptTemplate;
        Editor.SetText(EditorId,_text);
        _status = null;
        IsOpen = true;
    }

    public void Draw(ExileCrafting plugin)
    {
        if (!IsOpen) return;

        var open = IsOpen;
        // windows sdk pulls in a global Windows namespace here, has to be fully qualified or it wont resolve
        if (!ExileImGui.Windows.Begin("Script Editor", "crafteditor", ref open, 0, new Vector2(1040, 720)))
        {
            ExileImGui.Windows.End();
            IsOpen = open;
            return;
        }

        ImGui.TextUnformatted(_fileName ?? "(unsaved)");
        ImGui.SameLine();
        if (ImGui.SmallButton("Save"))
        {
            if (_fileName != null)
            {
                plugin.ScriptHost.WriteScript(_fileName, _text);
                SetStatus("Saved", Green, fade: true);
            }
            else
            {
                _saveAsName = SuggestedName();
                ImGui.OpenPopup(SaveAsPopupId);
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Compile Check"))
        {
            var error = plugin.ScriptHost.CheckCompile(_text);
            SetStatus(error ?? "compiles ok", error == null ? Green : Red, fade: error == null);
        }
        ImGui.SameLine();
        if (_fileName != null && ImGui.SmallButton("Run"))
        {
            plugin.ScriptHost.WriteScript(_fileName, _text);
            plugin.StartScript(_fileName);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Close")) open = false;

        if (_status != null && ImGui.GetTime() < _statusUntil)
        {
            ImGui.SameLine();
            using (new EColor.StyleColorScope((ImGuiCol.Text, _statusColor)))
                ImGui.TextWrapped(_status);
        }

        DrawInsertToolbar();

        ImGui.Separator();

        _editorOpts.Size = new Vector2(0, 0);
        Editor.Code(EditorId, ref _text, _editorOpts);

        DrawSaveAsPopup(plugin);

        ExileImGui.Windows.End();
        IsOpen = open;
    }

    // pickers for the three strings scripts get wrong most often: mod names, currency base names and
    // harvest craft text. currency is a split button because harvest crafting is the rarer of the two.
    void DrawInsertToolbar()
    {
        if (Toggle("Insert Mod", Picker.Mods)) TogglePicker(Picker.Mods);
        Controls.Tip("Search every item mod by name, id or what it does, tick as many as you want, " +
                     "then insert. Those names are what ctx.CountMods and ctx.CheckMods match on.");

        ImGui.SameLine();
        if (Toggle("Insert Currency", Picker.Currency)) TogglePicker(Picker.Currency);
        Controls.Tip("Search currency by base name, then drop it in. That's the name " +
                     "ctx.ApplyCurrency looks for in the stash tab and your inventory.");

        // split button: no spacing between the two halves, so they read as one control
        ImGui.SameLine(0f, 1f);
        if (ImGui.ArrowButton("##insertmore", ImGuiDir.Down)) ImGui.OpenPopup(InsertMenuId);

        if (ImGui.BeginPopup(InsertMenuId))
        {
            // opening the panel from in here would fight the popup closing, so flag it and act below
            if (ImGui.MenuItem("Insert Harvest Craft")) _openHarvestPicker = true;
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (Toggle("Insert Snippet", Picker.Snippets)) TogglePicker(Picker.Snippets);
        Controls.Tip("Drop a whole working block in at the caret - roll until a mod, bring an item " +
                     "to rare, alch and scour a map. Add your own as .csx files in data/snippets.");

        if (_openHarvestPicker)
        {
            _openHarvestPicker = false;
            TogglePicker(Picker.Harvest);
        }

        DrawPickerPanel();
    }

    // the open picker's own button stays depressed, so it reads as the thing you're looking at
    bool Toggle(string label, Picker picker)
    {
        if (_picker != picker) return ImGui.Button(label);

        using (new EColor.StyleColorScope((ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive))))
            return ImGui.Button(label);
    }

    void TogglePicker(Picker picker)
    {
        _picker = _picker == picker ? Picker.None : picker;
        _pickFilter = "";
        _pickTag = AnyTag;
        _pickAffix = AffixFilter.All;
        _picked.Clear();
    }

    const int MaxPickerRows = 300;

    void DrawPickerPanel()
    {
        if (_picker == Picker.None) return;

        var mods = _picker == Picker.Mods;

        var (rows, hint, empty) = _picker switch
        {
            Picker.Mods => (ScriptSnippets.Mods(), "search mods by name, id or effect",
                "no mods yet - the game files load a moment after you're in a zone."),
            Picker.Currency => (ScriptSnippets.Currency(), "search currency",
                "data/currency.txt is missing or empty."),
            Picker.Snippets => (ScriptSnippets.Snippets(), "search snippets",
                "data/snippets is missing or has no .csx files in it."),
            _ => (ScriptSnippets.Harvest(), "search harvest crafts",
                "data/harvest-crafts.txt is missing or empty."),
        };

        ImGui.BeginChild("##pickerpanel", new Vector2(0f, 340f), ImGuiChildFlags.Border);

        if (rows.Count == 0)
        {
            ImGui.TextDisabled(empty);
            ImGui.EndChild();
            return;
        }

        var matches = Filter(rows, mods);

        ImGui.SetNextItemWidth(-220f);
        ImGui.InputTextWithHint("##pickfilter", hint, ref _pickFilter, 128);
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        Controls.Tip("Each space-separated word has to match somewhere in the row, in any order. " +
                     "A word is treated as a regex when it is one.");
        ImGui.SameLine();
        ImGui.TextDisabled($"{matches.Count} of {rows.Count}");

        DrawPickerFilters(rows, mods);

        ImGui.Separator();

        // the footer has to keep its room whatever the list does, so the rows get what's left
        var footer = mods ? ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y : 0f;
        ImGui.BeginChild("##pickerrows", new Vector2(0f, -footer), ImGuiChildFlags.None);

        // the child above owns the scrolling, the table just does the banding and the row rules
        if (ImGui.BeginTable("##pickertable", 1, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            var shown = 0;

            foreach (var row in matches)
            {
                if (shown++ >= MaxPickerRows) break;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.PushID(row.Insert);
                DrawPickerRow(row, mods);
                ImGui.PopID();
            }

            if (matches.Count > MaxPickerRows)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled($"{matches.Count - MaxPickerRows} more - narrow the search.");
            }

            if (matches.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled("nothing matches that.");
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();

        if (mods) DrawPickerFooter();

        ImGui.EndChild();
    }

    void DrawPickerFilters(IReadOnlyList<ScriptSnippets.Row> rows, bool mods)
    {
        if (mods)
        {
            Controls.Segmented("##pickaffix", ref _pickAffix,
                ("All", AffixFilter.All), ("Prefix", AffixFilter.Prefix), ("Suffix", AffixFilter.Suffix));
            return;
        }

        var tags = rows.Select(r => r.Tag).Where(t => t.Length > 0).Distinct().ToList();
        if (tags.Count < 2) return;

        tags.Insert(0, AnyTag);

        ImGui.SetNextItemWidth(220f);
        if (!ImGui.BeginCombo("##picktag", _pickTag)) return;

        foreach (var tag in tags)
            if (ImGui.Selectable(tag, tag == _pickTag)) _pickTag = tag;

        ImGui.EndCombo();
    }

    // two lines per row: what it does, then the muted line naming what actually gets inserted. both
    // wrap, because a harvest craft is a full sentence and truncating it makes the list unreadable.
    void DrawPickerRow(ScriptSnippets.Row row, bool mods)
    {
        var picked = mods && _picked.Contains(row.Insert);
        var wrap = Math.Max(120f, ImGui.GetContentRegionAvail().X - 8f);

        var height = ImGui.CalcTextSize(row.Primary, false, wrap).Y;
        if (row.Secondary.Length > 0) height += ImGui.CalcTextSize(row.Secondary, false, wrap).Y;

        var start = ImGui.GetCursorPos();

        if (ImGui.Selectable("##sel", picked, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0f, height)))
            Pick(row, mods);

        // the selectable owns the row's hit box, so the text goes back over the top of it
        ImGui.SetCursorPos(start);
        ImGui.PushTextWrapPos(start.X + wrap);
        ImGui.TextWrapped(Controls.EscPct(row.Primary));

        if (row.Secondary.Length > 0)
            using (new EColor.StyleColorScope((ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled))))
                ImGui.TextWrapped(Controls.EscPct(row.Secondary));

        ImGui.PopTextWrapPos();
    }

    void Pick(ScriptSnippets.Row row, bool mods)
    {
        // mods gather up, since a script usually wants "did it roll any of these". everything else
        // is the whole answer on its own, so it goes straight in.
        if (mods)
        {
            if (!_picked.Remove(row.Insert)) _picked.Add(row.Insert);
            return;
        }

        // a snippet is code and lands as-is; a currency or craft name is a string literal
        Editor.Insert(EditorId, ref _text,
            _picker == Picker.Snippets ? row.Insert : Literal(row.Insert));

        _picker = Picker.None;
    }

    void DrawPickerFooter()
    {
        ImGui.BeginDisabled(_picked.Count == 0);
        if (ImGui.Button($"Insert ({_picked.Count})"))
        {
            Editor.Insert(EditorId, ref _text, ModsLiteral());
            _picker = Picker.None;
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Clear")) _picked.Clear();

        ImGui.SameLine();
        if (ImGui.Button("Close")) _picker = Picker.None;

        if (_picked.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(string.Join(", ", _picked));
        }
    }

    // one pick is a plain string, several become the array the mod calls all take
    string ModsLiteral() =>
        _picked.Count == 1
            ? Literal(_picked[0])
            : "new[] { " + string.Join(", ", _picked.Select(Literal)) + " }";

    static string Literal(string value) =>
        "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // space-separated words, all of which have to match, in any order - a word that parses as a
    // regex is used as one, which is how you get at "fire.*resist" without an exact phrase
    List<ScriptSnippets.Row> Filter(IReadOnlyList<ScriptSnippets.Row> rows, bool mods)
    {
        var terms = (_pickFilter ?? "")
            .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
            .Select(TermMatcher)
            .ToList();

        return rows
            .Where(r => (!mods || _pickAffix == AffixFilter.All || r.Tag == _pickAffix.ToString())
                        && (mods || _pickTag == AnyTag || r.Tag == _pickTag)
                        && terms.All(m => m(r.Search)))
            .ToList();
    }

    static Func<string, bool> TermMatcher(string term)
    {
        try
        {
            var rx = new Regex(term, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return s => rx.IsMatch(s);
        }
        catch (ArgumentException)
        {
            // half-typed pattern - fall back to a plain contains so the list doesn't blank out
            return s => s.Contains(term, StringComparison.OrdinalIgnoreCase);
        }
    }

    void DrawSaveAsPopup(ExileCrafting plugin)
    {
        // imgui.net 1.90 has no BeginPopupModal(id, flags) overload, so we pass a throwaway open flag -
        // same workaround ExileImGui.Windows.ConfirmModal uses for the same reason.
        var open = true;
        if (!ImGui.BeginPopupModal(SaveAsPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.TextUnformatted("Script name:");
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##saveasname", "my-craft.csx", ref _saveAsName, 64);

        if (ImGui.Button("Save"))
        {
            var name = Sanitize(_saveAsName);
            if (name.Length == 0)
            {
                SetStatus("that name has nothing usable in it", Red, fade: false);
            }
            else
            {
                if (!name.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)) name += ".csx";
                plugin.ScriptHost.WriteScript(name, _text);
                _fileName = name;

                if (!plugin.Settings.Scripts.Any(s => s.FileName == name))
                    plugin.Settings.Scripts.Add(ScriptEntry.FromFile(plugin.ScriptHost, name));

                SetStatus("Saved", Green, fade: true);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    // "// Name:" in the header is what the user already called it, so start the box there
    string SuggestedName() => Sanitize(ScriptHost.ParseMetadata(_text.Split('\n')).Name);

    static string Sanitize(string name) =>
        string.Concat((name ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
}

#endregion
