using ExileCore.PoEMemory.FilesInMemory;
using ExileCore.Shared.Enums;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System;

namespace ExileCrafting;

#region Editor toolbar pickers - mods, currency, harvest crafts

// the three lists behind the editor's Insert buttons. mods and currency come out of the game's own
// files so they can never disagree with what a script will match at run time; harvest crafts have
// no such file, so they're read off disk from data/harvest-crafts.txt.
public static class ScriptSnippets
{
    // one line in a picker. Insert is what lands in the script; Primary is the readable line you
    // click, Secondary the muted line under it, Tag the group the filter dropdown offers.
    public sealed class Row
    {
        public string Insert = "";
        public string Primary = "";
        public string Secondary = "";
        public string Tag = "";

        // everything searchable in one string, built once rather than concatenated per keystroke
        public string Search = "";

        public void Index() => Search = $"{Primary}\n{Secondary}\n{Insert}\n{Tag}";
    }

    // the only domains a crafted item's mods can come from - everything else is monster, map,
    // atlas and heist noise that would bury the list
    static readonly HashSet<ModDomain> ItemDomains = new()
    {
        ModDomain.Item, ModDomain.Flask, ModDomain.Crafted,
        ModDomain.BaseJewel, ModDomain.AbyssJewel, ModDomain.ClusterJewel,
    };

    static List<Row> _mods;
    static List<Row> _currency;
    static List<Row> _harvest;
    static List<Row> _snippets;

    public static IReadOnlyList<Row> Mods() => Get(ref _mods, BuildMods);

    public static IReadOnlyList<Row> Currency() => Get(ref _currency, BuildCurrency);

    public static IReadOnlyList<Row> Harvest() => Get(ref _harvest, BuildHarvest);

    public static IReadOnlyList<Row> Snippets() => Get(ref _snippets, BuildSnippets);

    // an empty list means the files weren't up yet, so it doesn't get remembered - opening the
    // picker again rebuilds it
    static IReadOnlyList<Row> Get(ref List<Row> cache, Func<List<Row>> build)
    {
        if (cache != null) return cache;

        var rows = build();
        if (rows.Count > 0) cache = rows;
        return rows;
    }

    // ctx.CheckMods matches loosely against ItemMod.Name, which is the mod key with its tier digits
    // dropped, so that's what gets inserted - one entry covers every tier of the same mod.
    static List<Row> BuildMods()
    {
        var rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var mod in ExileCrafting.Main.GameController.Files.Mods.records.Values)
            {
                if (mod == null) continue;
                if (mod.AffixType != ModType.Prefix && mod.AffixType != ModType.Suffix) continue;
                if (!ItemDomains.Contains(mod.Domain)) continue;

                var key = (mod.Key ?? "").TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (key.Length == 0 || rows.ContainsKey(key)) continue;

                var affix = mod.AffixType.ToString();
                var effect = DescribeMod(mod);
                var name = mod.UserFriendlyName ?? "";

                rows[key] = new Row
                {
                    Insert = key,
                    // what the mod does reads best as the click target - the id it inserts sits under it
                    Primary = effect.Length > 0 ? effect : (name.Length > 0 ? name : key),
                    Secondary = name.Length > 0 ? $"{affix} - {key} - {name}" : $"{affix} - {key}",
                    Tag = affix,
                };
            }
        }
        catch
        {
            // game files not loaded yet, or a record that won't read - show whatever did come back
        }

        return Indexed(rows.Values.OrderBy(r => r.Primary, StringComparer.OrdinalIgnoreCase));
    }

    // what the mod does, worded the way the game words it on an item. the rolls get blanked to '#'
    // so one line covers every tier of the mod - same trick ExileMaps uses on map mods.
    static string DescribeMod(ModsDat.ModRecord mod)
    {
        var stats = mod.StatNames;
        if (stats == null || stats.Length == 0) return FriendlyStats(mod);

        var ranges = mod.StatRange;
        var values = new Dictionary<GameStat, int>();

        for (var i = 0; i < stats.Length; i++)
        {
            if (stats[i] == null) continue;

            // a top roll, so the wording lands on the increased/more side rather than reduced
            var value = 1;
            if (ranges != null && i < ranges.Length && ranges[i] != null)
                value = ranges[i].Max != 0 ? ranges[i].Max : (ranges[i].Min != 0 ? ranges[i].Min : 1);

            values[stats[i].MatchingStat] = value;
        }

        if (values.Count == 0) return FriendlyStats(mod);

        // the roll varies by tier and one picked name covers every tier, so blank the numbers
        var text = Regex.Replace(Flatten(Translate(values)), @"\d+(\.\d+)?", "#");
        return text.Length > 0 ? text : FriendlyStats(mod);
    }

    // the whole set first, so combined lines like "Adds # to # Fire Damage" stay one line. per-stat
    // after that, so a single untranslatable stat doesn't sink the rest of the mod.
    static string Translate(Dictionary<GameStat, int> values)
    {
        var whole = TranslateOne(values);
        if (whole.Length > 0) return whole;

        var parts = values
            .Select(v => TranslateOne(new Dictionary<GameStat, int> { [v.Key] = v.Value }))
            .Where(s => s.Length > 0);

        return string.Join("\n", parts);
    }

    // the game hands back a "<unknown ...>" placeholder rather than failing, so that gets rejected
    // here and the caller falls back to the plain stat names
    static string TranslateOne(IReadOnlyDictionary<GameStat, int> values)
    {
        try
        {
            var text = ExileCrafting.Main.GameController.Files.StatDescriptions?.TranslateMod(values);
            return string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith("<unknown") ? "" : text;
        }
        catch
        {
            return "";
        }
    }

    // one row per entry in the picker, so a multi-line description gets joined rather than wrapped
    static string Flatten(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var lines = text
            .Split('\n', '\r')
            .Select(l => Regex.Replace(l, @"\s+", " ").Trim())
            .Where(l => l.Length > 0);

        return string.Join(", ", lines);
    }

    static string FriendlyStats(ModsDat.ModRecord mod) =>
        mod.StatNames == null
            ? ""
            : string.Join(", ", mod.StatNames
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.UserFriendlyName))
                .Select(s => s.UserFriendlyName));

    // the whole currency file is splinters, shards, scarabs and incubators as well as orbs, so the
    // list to show comes off disk. the game file is still what says a name is real and what it does.
    static List<Row> BuildCurrency()
    {
        var rows = new List<Row>();
        var descriptions = CurrencyDescriptions();

        foreach (var (name, group) in ReadDataLines("currency.txt"))
        {
            var known = descriptions.TryGetValue(name, out var description);

            // a name the game doesn't have is a stale line in the file - unless nothing loaded at
            // all, in which case the curated list is all we've got and it goes up without help text
            if (!known && descriptions.Count > 0) continue;

            rows.Add(new Row { Insert = name, Primary = name, Secondary = description ?? "", Tag = group });
        }

        return Indexed(rows);
    }

    // base name -> help text. ApplyCurrency compares against BaseItemTypes.BaseName exactly and the
    // currency file's Type is that same record, so this doubles as the "is that a real name" check.
    static Dictionary<string, string> CurrencyDescriptions()
    {
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var currency in ExileCrafting.Main.GameController.Files.CurrencyItems.EntriesList)
            {
                var name = currency?.Type?.BaseName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                descriptions[name] = Flatten(currency.Description ?? "");
            }
        }
        catch
        {
            // game files not up yet - an empty list isn't cached, so opening the picker again retries
        }

        return descriptions;
    }

    // one file per snippet under data/snippets. the leading '//' run is the same header a script
    // uses; it stops at the first blank line so a comment belonging to the code stays with it.
    static List<Row> BuildSnippets()
    {
        var rows = new List<Row>();

        try
        {
            var folder = Path.Combine(ExileCrafting.Main.DirectoryFullName, "data", "snippets");
            if (!Directory.Exists(folder)) return rows;

            foreach (var path in Directory.GetFiles(folder, "*.csx")
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var lines = File.ReadAllLines(path);

                var end = 0;
                while (end < lines.Length && lines[end].TrimStart().StartsWith("//")) end++;

                var meta = ScriptHost.ParseMetadata(lines.Take(end));
                var body = string.Join("\r\n", lines.Skip(end).SkipWhile(l => l.Trim().Length == 0)).TrimEnd();
                if (body.Length == 0) continue;

                rows.Add(new Row
                {
                    Insert = body,
                    Primary = meta.Name.Length > 0 ? meta.Name : Path.GetFileNameWithoutExtension(path),
                    Secondary = meta.Description ?? "",
                });
            }
        }
        catch
        {
            // missing or locked - the picker says the list is empty and names the folder
        }

        return Indexed(rows);
    }

    static List<Row> BuildHarvest() =>
        Indexed(ReadDataLines("harvest-crafts.txt")
            .Select(entry => new Row { Insert = entry.line, Primary = entry.line, Tag = entry.group }));

    static List<Row> Indexed(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        foreach (var row in list) row.Index();
        return list;
    }

    // one curated list per file in data/. blank lines and '#' comments are skipped; a '##' line
    // names the group every entry under it belongs to, which is what the filter dropdown offers.
    static List<(string line, string group)> ReadDataLines(string fileName)
    {
        var lines = new List<(string, string)>();

        try
        {
            var path = Path.Combine(ExileCrafting.Main.DirectoryFullName, "data", fileName);
            if (!File.Exists(path)) return lines;

            var group = "";

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("##"))
                {
                    group = line.Substring(2).Trim();
                    continue;
                }

                if (line.StartsWith("#")) continue;

                lines.Add((line, group));
            }
        }
        catch
        {
            // missing or locked - the picker says the list is empty and names the file
        }

        return lines;
    }
}

#endregion
