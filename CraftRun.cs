using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Models;
using ExileCore.Shared.Enums;
using ExileCore;
using ExileImGui;
using ImGuiNET;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace ExileCrafting;

#region Craft context - the API scripts call

// which list on the Mods component a name check reads. Any is every mod the item has.
public enum ModPool
{
    Explicit,
    Implicit,
    Enchanted,
    Any,
}

public sealed class CraftContext
{
    readonly ClickExecutor _executor;
    readonly CancellationToken _token;
    readonly DateTime _startedAt;
    readonly int _maxApplications;
    readonly int _maxSeconds;
    readonly double _maxChaos;
    readonly CraftLog _log;
    readonly Dictionary<string, int> _used = new();
    readonly Dictionary<string, double> _priceCache = new();
    int _applications;
    double _spentChaos;
    TimeSpan _paused;
    DateTime? _clockStoppedAt;
    int _clockHolds;

    public CraftContext(ClickExecutor executor, CancellationToken token, int maxApplications, int maxSeconds,
        double maxChaos, CraftLog log)
    {
        _executor = executor;
        _token = token;
        _maxApplications = maxApplications;
        _maxSeconds = maxSeconds;
        _maxChaos = maxChaos;
        _log = log;
        _startedAt = DateTime.UtcNow;
    }

    public double SpentChaos => _spentChaos;

    public int Applications => _applications;

    public int MaxApplications => _maxApplications;

    public double MaxChaos => _maxChaos;

    public int MaxSeconds => _maxSeconds;

    public TimeSpan Elapsed => (_clockStoppedAt ?? DateTime.UtcNow) - _startedAt - _paused;

    // a prompt and the pause hotkey can both hold the run clock, so it's a count, not a flag
    public void StopClock()
    {
        if (_clockHolds++ == 0) _clockStoppedAt = DateTime.UtcNow;
    }

    public void StartClock()
    {
        if (_clockHolds == 0 || --_clockHolds > 0) return;
        if (_clockStoppedAt is { } at) _paused += DateTime.UtcNow - at;
        _clockStoppedAt = null;
    }

    public IReadOnlyDictionary<string, int> Used => _used;

    // globals type puts our members directly in script scope, not a `ctx` variable - this lets
    // scripts still write ctx.Item / ctx.ApplyCurrency(...) like the docs and spec show.
    public CraftContext ctx => this;

    public CraftItem Item => CraftingSlot.ReadItem();

    // drops case and everything that isn't a letter or digit, so "Master of Fire" still matches
    // "MasterOfFire" and "master-of-fire".
    public string Squash(string s) =>
        new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // substring match against all three names a mod can go by - the display name is the one you
    // read in game, Name and RawName are what the game data calls it.
    public bool IsMod(ItemMod mod, string wanted)
    {
        if (mod == null) return false;

        var target = Squash(wanted);
        if (target.Length == 0) return false;

        return Squash(mod.Name).Contains(target)
            || Squash(mod.RawName).Contains(target)
            || Squash(mod.DisplayName).Contains(target);
    }

    // how many of these the item has right now. no delay - use it inside an ApplyCurrencyUntil
    // condition, which already pauses for the read before it asks.
    public int CountMods(params string[] names) => CountMods(ModPool.Explicit, names);

    public int CountMods(ModPool pool, params string[] names)
    {
        if (names == null || names.Length == 0) return 0;

        var mods = Item.Mods;
        var list = mods == null ? null : PoolOf(mods, pool);
        if (list == null) return 0;

        return names.Count(n => list.Any(m => IsMod(m, n)));
    }

    static List<ItemMod> PoolOf(Mods mods, ModPool pool) => pool switch
    {
        ModPool.Explicit => mods.ExplicitMods,
        ModPool.Implicit => mods.ImplicitMods,
        ModPool.Enchanted => mods.EnchantedMods,
        _ => mods.ItemMods,
    };

    // same count, plus the pause a person needs to actually read the mods off the item. every
    // "does it have x yet" check should go through this.
    public Task<int> CheckMods(params string[] names) => CheckMods(ModPool.Explicit, names);

    public async Task<int> CheckMods(ModPool pool, params string[] names)
    {
        _token.ThrowIfCancellationRequested();

        var count = CountMods(pool, names);

        var s = ExileCrafting.Main.Settings;
        await _executor.Wait(s.RollDelay(s.CheckModsDelayMin, s.CheckModsDelayMax), _token);

        return count;
    }

    public string StatusText { get; private set; } = "";

    // overlay line on the craft panel - scripts can set their own instead of the default
    public void SetStatus(string message) => ExileCrafting.Main.StatusMessage = message;

    public void PlaySound(CraftSound sound) => Sounds.Request(sound);

    // the player's thinking time isn't the script's, so the run clock stops while a prompt is up
    public async Task<bool> Ask(string message, PromptButtons buttons = PromptButtons.YesNo)
    {
        _token.ThrowIfCancellationRequested();

        if (_executor.Busy)
            throw new InvalidOperationException(
                "ctx.Ask was called from inside a running craft action, most likely the condition of " +
                "an ApplyCurrencyUntil. The prompt is answered on the render thread, which cannot run " +
                "while a click routine is in flight, so waiting for it there would hang the game. Ask " +
                "before or after the run instead.");

        var labels = CraftPrompt.Labels(buttons);
        SetStatus("waiting for you");
        _log.Add($"asking: {message}");

        StopClock();

        try
        {
            var answer = await CraftPrompt.Ask(message, buttons, _token);
            _log.Add($"answered {(answer ? labels.Yes : labels.No).ToLowerInvariant()}.");
            return answer;
        }
        finally
        {
            StartClock();
        }
    }

    public void Log(string message)
    {
        StatusText = message;
        _log.Add(message);
    }

    public async Task<bool> ApplyCurrency(string currencyName)
    {
        _token.ThrowIfCancellationRequested();
        ChargeApplication(currencyName);

        SetStatus($"using {currencyName}");
        _log.Add($"applying {currencyName}...");
        var result = await _executor.ApplyCurrency(currencyName, _token);
        Count(currencyName, _executor.LastRunApplied);
        _log.Add(result ? $"{currencyName} applied." : $"{currencyName} failed - not found in stash or click didn't land.");
        return result;
    }

    // one pickup, then shift-click the same spot until done() is true. much faster than a loop of
    // ApplyCurrency calls, which walks the mouse back to the stack every single time.
    public async Task<bool> ApplyCurrencyUntil(string currencyName, Func<bool> done)
    {
        if (done == null) throw new ArgumentNullException(nameof(done));
        _token.ThrowIfCancellationRequested();

        SetStatus($"using {currencyName}");
        _log.Add($"applying {currencyName} until the condition holds...");
        var result = await _executor.ApplyCurrencyUntil(currencyName, done, () => ChargeApplication(currencyName), _token);
        Count(currencyName, _executor.LastRunApplied);
        _log.Add(result ? $"{currencyName} run hit the condition." : $"{currencyName} run stopped short.");
        return result;
    }

    // one pickup, then alt swaps which orb is on the cursor between clicks. much faster than
    // alternating ApplyCurrency calls, which walks back to the stash for every single orb.
    public async Task<bool> ApplyPairUntil(string primary, string alternate, Func<bool> useAlternate, Func<bool> done)
    {
        if (useAlternate == null) throw new ArgumentNullException(nameof(useAlternate));
        if (done == null) throw new ArgumentNullException(nameof(done));
        _token.ThrowIfCancellationRequested();

        SetStatus($"using {primary} and {alternate}");
        _log.Add($"applying {primary} and {alternate} until the condition holds...");

        var result = await _executor.ApplyPairUntil(primary, alternate, useAlternate, done,
            ChargeApplication, name => Count(name, 1), _token);

        _log.Add(result
            ? $"{primary} and {alternate} run hit the condition."
            : $"{primary} and {alternate} run stopped short.");

        return result;
    }

    // alchemy only lands on a white item, so anything else gets scoured first
    public Task<bool> AlchScourUntil(Func<bool> done) =>
        ApplyPairUntil("Orb of Alchemy", "Orb of Scouring", () => Item.Rarity != ItemRarity.Normal, done);

    // one reroll, leaving the item rare. two pickups rather than the shift dance, which only pays
    // for itself when it repeats.
    public async Task<bool> AlchScour()
    {
        if (Item.Rarity != ItemRarity.Normal && !await ApplyCurrency("Orb of Scouring")) return false;
        return await ApplyCurrency("Orb of Alchemy");
    }

    // explicits only - the pool every reroll actually touches
    public int ModCount => Item.Mods?.ExplicitMods?.Count ?? 0;

    // alt spam until addWhen() likes the roll, one alt-swapped orb for the extra mod, then back to
    // spamming if done() still isn't happy. the mod cap is what stops it adding to a full item.
    public Task<bool> RollThenAddUntil(string primary, string alternate, int maxMods,
        Func<bool> addWhen, Func<bool> done)
    {
        if (addWhen == null) throw new ArgumentNullException(nameof(addWhen));
        return ApplyPairUntil(primary, alternate, () => ModCount < maxMods && addWhen(), done);
    }

    // magic items cap at two explicits, so the aug only ever lands on a single-mod roll
    public Task<bool> AltAugUntil(Func<bool> augWhen, Func<bool> done) =>
        RollThenAddUntil("Orb of Alteration", "Orb of Augmentation", 2, augWhen, done);

    // same shape on a rare. maxMods is 6 for gear, drop it to 4 for jewels
    public Task<bool> ChaosExaltUntil(Func<bool> exaltWhen, Func<bool> done, int maxMods = 6) =>
        RollThenAddUntil("Chaos Orb", "Exalted Orb", maxMods, exaltWhen, done);

    // select the craft in the horticrafting station and press Craft once. the search text has to
    // match exactly one craft in the list - an ambiguous one aborts and logs what it matched.
    public async Task<bool> ApplyHarvest(string craftName)
    {
        _token.ThrowIfCancellationRequested();
        ChargeApplication(craftName);

        SetStatus($"harvest: {craftName}");
        _log.Add($"harvest craft '{craftName}'...");
        var result = await _executor.ApplyHarvest(craftName, _token);
        Count(craftName, _executor.LastRunApplied);
        _log.Add(result ? $"{craftName} applied." : $"{craftName} didn't land.");
        return result;
    }

    void Count(string currencyName, int applied)
    {
        if (applied <= 0) return;
        _used[currencyName] = _used.TryGetValue(currencyName, out var n) ? n + applied : applied;
    }

    // one line for the log when the run ends. the chaos total only appears when Ninja Price is
    // loaded - it's the plugin that owns the poe.ninja data, we just borrow its lookup.
    public string UsageSummary()
    {
        if (_used.Count == 0) return "used no currency.";

        var line = "used " + string.Join(", ", _used.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"));
        return _spentChaos > 0 ? $"{line} (~{_spentChaos:0.##}c)." : line + ".";
    }

    // chaos each of these costs, asked once per name per run. null price method means Ninja Price
    // isn't loaded, and everything is free as far as we can tell.
    double PriceOf(string currencyName)
    {
        if (_priceCache.TryGetValue(currencyName, out var cached)) return cached;

        var price = PriceLookup();
        var baseType = price == null ? null : FindBaseItemType(currencyName);
        var value = baseType == null ? 0d : price(baseType);

        _priceCache[currencyName] = value;
        return value;
    }

    public static Func<BaseItemType, double> PriceLookup() =>
        ExileCrafting.Main.GameController.PluginBridge
            .GetMethod<Func<BaseItemType, double>>("NinjaPrice.GetBaseItemTypeValue");

    // BaseItemTypes is keyed by metadata path, and all we have is the display name the script asked
    // for, so this walks it once and remembers the answer.
    static readonly Dictionary<string, BaseItemType> BaseTypeByName = new();

    static BaseItemType FindBaseItemType(string baseName)
    {
        if (BaseTypeByName.TryGetValue(baseName, out var cached)) return cached;

        var found = ExileCrafting.Main.GameController.Files.BaseItemTypes.Contents.Values
            .FirstOrDefault(b => b.BaseName == baseName);

        BaseTypeByName[baseName] = found;
        return found;
    }

    // both entry points share the run guards - ApplyCurrencyUntil calls this once per click
    void ChargeApplication(string currencyName)
    {
        _applications++;
        if (_maxApplications > 0 && _applications > _maxApplications)
            throw new CraftGuardException(
                $"Stopped at the {_maxApplications} application limit. Raise 'Max currency uses' on this script to go further.");

        if (_maxSeconds > 0 && Elapsed.TotalSeconds > _maxSeconds)
            throw new CraftGuardException(
                $"Stopped at the {_maxSeconds}s time limit. Raise 'Max Run Seconds' in settings to go further.");

        // charged before the click, so the budget is a ceiling the run never crosses
        var cost = PriceOf(currencyName);
        if (_maxChaos > 0 && _spentChaos + cost > _maxChaos)
            throw new CraftGuardException(
                $"Stopped at the {_maxChaos:0.##}c budget - {_spentChaos:0.##}c spent, and the next " +
                $"{currencyName} costs {cost:0.##}c. Raise 'Max cost' on this script to go further.");

        _spentChaos += cost;
    }
}

// a run that ended on purpose rather than breaking. shown as a plain sentence, no stack trace -
// the limit doing its job isn't a bug report.
public sealed class CraftGuardException : Exception
{
    public CraftGuardException(string message) : base(message) { }
}

#endregion

#region Craft queue - many items, one after another

// runs a list of jobs, each with its own script and limits. the whole batch is a single async
// method - safe because ClickExecutor completes its TCS inside Tick, so every resume stays on the
// main thread.
public sealed class CraftQueue
{
    readonly ExileCrafting _plugin;
    readonly IReadOnlyList<QueueJob> _jobs;
    readonly CancellationToken _token;
    readonly List<CraftSummaryRow> _rows = new();
    int _index;

    public CraftQueue(ExileCrafting plugin, IReadOnlyList<QueueJob> jobs, CancellationToken token)
    {
        _plugin = plugin;
        _jobs = jobs;
        _token = token;
    }

    public string Progress => $"item {Math.Min(_index + 1, _jobs.Count)}/{_jobs.Count}";

    public IReadOnlyList<CraftSummaryRow> Rows => _rows;

    public int JobCount => _jobs.Count;

    public async Task<object> RunAsync()
    {
        var log = _plugin.CraftLog;

        try
        {
            for (_index = 0; _index < _jobs.Count; _index++)
            {
                var job = _jobs[_index];
                var row = NewRow(job);
                log.Add($"--- {Progress} ({InventoryGrid.Describe(job.Cell)}) - {row.Script} ---");
                _plugin.Executor.ResetHarvestSelection();

                if (InventoryGrid.IsCellEmpty(job.Cell))
                {
                    row.Result = "skipped";
                    log.Add("nothing there any more - skipped.");
                    continue;
                }

                // compile before the item moves, so a broken script never strands one in the slot
                if (!_plugin.ScriptHost.TryGetOrCompile(job.FileName, out var runner, out var compileError))
                {
                    row.Result = "failed";
                    Ping(CraftSound.Failed);
                    throw new CraftGuardException($"Queue stopped: {job.FileName} didn't compile.\n{compileError}");
                }

                if (!await _plugin.Executor.TakeIntoCraftingSlot(job.Cell, _token))
                {
                    row.Result = "failed";
                    Ping(CraftSound.Failed);
                    throw new CraftGuardException(
                        $"Queue stopped: couldn't move the item from {InventoryGrid.Describe(job.Cell)} into the {CraftingSlot.SlotLabel}.");
                }

                var originRect = _plugin.Executor.LastOriginRect;
                var ctx = _plugin.NewContext(job.MaxApplications, job.MaxChaos);
                _plugin.RunningContext = ctx;

                var result = "done";

                try
                {
                    await runner(ctx);
                }
                catch (OperationCanceledException)
                {
                    Record(row, ctx, "cancelled");
                    throw;
                }
                catch (CraftGuardException e)
                {
                    // a limit is this item's budget running out, not the queue's - put it back and
                    // carry on. the guards the queue itself throws are outside this try.
                    log.Add(e.Message);
                    result = "limit";
                }
                catch
                {
                    Record(row, ctx, "failed");
                    log.Add(ctx.UsageSummary());
                    await TryReturn(job.Cell, originRect);
                    throw;
                }

                Record(row, ctx, result);
                log.Add(ctx.UsageSummary());

                if (!await _plugin.Executor.ReturnToInventory(job.Cell, originRect, _token))
                {
                    row.Result = "stuck";
                    Ping(CraftSound.Failed);
                    throw new CraftGuardException(
                        $"Queue stopped: the item couldn't be put back in {InventoryGrid.Describe(job.Cell)}.");
                }

                if (_index < _jobs.Count - 1) await Pause();
            }

            log.Add($"queue finished - {_jobs.Count} item(s).");
            return null;
        }
        catch (OperationCanceledException)
        {
            log.Add(StrandedNote());
            // cancelling a move throws straight past Record, so nothing has pinged for this row yet
            if (_rows.Count > 0 && _rows[^1].Result == "pending") Ping(CraftSound.Failed);
            throw;
        }
        finally
        {
            foreach (var pending in _rows.Where(r => r.Result == "pending")) pending.Result = "cancelled";
            log.SetSummary(CraftSummary.Render(_rows));
            if (LastOfQueue) Sounds.Request(CraftSound.QueueComplete);
            ClearFinishedJobs(log);
        }
    }

    // an item that got crafted comes off the queue. anything that failed, stuck, was cancelled or was
    // never reached stays, so Run Queue again is a retry of exactly what's left.
    void ClearFinishedJobs(CraftLog log)
    {
        var queue = _plugin.Settings.Queue;
        var cleared = 0;

        for (var i = 0; i < _rows.Count && i < _jobs.Count; i++)
        {
            if (_rows[i].Result is not ("done" or "limit" or "skipped")) continue;
            if (queue.Remove(_jobs[i])) cleared++;
        }

        if (cleared == 0) return;

        log.Add(queue.Count == 0
            ? $"cleared {cleared} finished item(s) - the queue is empty."
            : $"cleared {cleared} finished item(s), {queue.Count} left in the queue.");
    }

    CraftSummaryRow NewRow(QueueJob job)
    {
        var row = new CraftSummaryRow
        {
            Item = InventoryGrid.CellLabel(job.Cell),
            Script = TitleOf(job),
            Result = "pending",
        };

        _rows.Add(row);
        return row;
    }

    // ctx.Elapsed keeps counting after the script stops, so it has to be read the moment it does
    void Record(CraftSummaryRow row, CraftContext ctx, string result)
    {
        row.Result = result;
        row.Time = ctx.Elapsed;
        row.Chaos = ctx.SpentChaos;

        foreach (var pair in ctx.Used) row.Used[pair.Key] = pair.Value;

        Ping(result == "done" ? CraftSound.Succeeded : CraftSound.Failed);
    }

    // once the queue reaches its last job, queue-complete speaks for it - never both
    void Ping(CraftSound sound)
    {
        if (LastOfQueue) return;
        Sounds.Request(sound);
    }

    bool LastOfQueue => _jobs.Count >= 2 && _index >= _jobs.Count - 1;

    async Task Pause()
    {
        var settings = _plugin.Settings;
        var ms = settings.RollDelay(settings.QueueDelayMin, settings.QueueDelayMax);
        if (ms <= 0) return;

        _plugin.RunningContext?.Log($"waiting {ms / 1000f:0.0}s before the next item.");
        await _plugin.Executor.Wait(ms, _token);
    }

    string TitleOf(QueueJob job) =>
        _plugin.Settings.Scripts.FirstOrDefault(s => s.FileName == job.FileName)?.Title ?? job.FileName;

    // best effort on the way out of a failure - the original exception is what the user needs to
    // see, so a failed return only adds a line rather than replacing it
    async Task TryReturn(int cell, SharpDX.RectangleF originRect)
    {
        try
        {
            if (!await _plugin.Executor.ReturnToInventory(cell, originRect, _token))
                _plugin.CraftLog.Add(
                    $"the item is still in the {CraftingSlot.SlotLabel} - put it back in {InventoryGrid.Describe(cell)} by hand.");
        }
        catch (OperationCanceledException)
        {
            _plugin.CraftLog.Add(StrandedNote());
        }
    }

    static string StrandedNote()
    {
        if (InventoryGrid.CursorHeldAddress() != 0) return "cancelled - an item is still on the cursor, drop it.";
        if (!CraftingSlot.ReadItem().IsEmpty) return $"cancelled - an item is still in the {CraftingSlot.SlotLabel}.";
        return "cancelled.";
    }
}

#endregion

#region Prompt - ctx.Ask and its modal

public enum PromptButtons
{
    YesNo,
    OkCancel,
}

// one question at a time, answered by a button in Render. the script awaits the TCS and suspends,
// so the frame loop keeps running while it waits.
public static class CraftPrompt
{
    static TaskCompletionSource<bool> _tcs;
    static string _message;
    static string _title;
    static PromptButtons _buttons;
    static CancellationToken _token;
    static bool _opened;

    public static (string Yes, string No) Labels(PromptButtons buttons) =>
        buttons == PromptButtons.OkCancel ? ("OK", "Cancel") : ("Yes", "No");

    public static Task<bool> Ask(string message, PromptButtons buttons, CancellationToken token)
    {
        if (Environment.CurrentManagedThreadId != ExileCrafting.Main.MainThreadId)
            throw new InvalidOperationException(
                "ctx.Ask was called off the main thread. This usually means the script awaited " +
                "something other than a ctx call (e.g. Task.Delay) - that resumes on a threadpool " +
                "thread, and ExileCore's game state isn't safe to read from there. Only await " +
                "ctx.ApplyCurrency / ctx.ApplyHarvest / ctx.CheckMods / ctx.Ask in crafting scripts, " +
                "not other Task-returning calls.");

        if (_tcs != null)
            throw new InvalidOperationException("CraftPrompt already has a question in flight.");

        // same reason as ClickExecutor: Draw completes this from the main thread, and the script's
        // next line has to resume right there rather than hop to a threadpool thread
        _tcs = new TaskCompletionSource<bool>();
        _message = message ?? "";
        // ### keeps the popup id fixed while the visible title follows the script
        _title = Text.Ascii(ExileCrafting.Main.RunningScriptName ?? "Craft Script") + "###craftprompt";
        _buttons = buttons;
        _token = token;
        _opened = false;

        Sounds.Request(CraftSound.Notification);
        return _tcs.Task;
    }

    public static void Draw()
    {
        if (_tcs == null) return;

        if (_token.IsCancellationRequested)
        {
            var cancelled = _tcs;
            var cancelledToken = _token;
            Close();
            cancelled.TrySetCanceled(cancelledToken);
            return;
        }

        if (!_opened)
        {
            ImGui.OpenPopup(_title);
            _opened = true;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(320f, 0f), new Vector2(520f, float.MaxValue));

        // unlike Windows.ConfirmModal, a false return here is the answer: the X and Escape both
        // reach it, and dismissing means no
        var open = true;
        if (!ImGui.BeginPopupModal(_title, ref open,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse))
        {
            Answer(false);
            return;
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 480f);
        // TextWrapped is printf-style and a craft question is full of % signs
        ImGui.TextUnformatted(Text.Ascii(_message));
        ImGui.PopTextWrapPos();

        ImGui.Separator();

        var labels = Labels(_buttons);
        var answered = false;
        var answer = false;

        if (ImGui.Button(labels.Yes, new Vector2(120f, 0f)))
        {
            answered = true;
            answer = true;
        }

        ImGui.SameLine();

        if (ImGui.Button(labels.No, new Vector2(120f, 0f)))
        {
            answered = true;
            answer = false;
        }

        if (answered) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();

        // after EndPopup: completing the task resumes the script inline, and it may ask again
        if (answered) Answer(answer);
    }

    static void Answer(bool value)
    {
        var tcs = _tcs;
        Close();
        tcs.TrySetResult(value);
    }

    // cleared before the task completes, because the continuation runs inline and can call Ask again
    static void Close()
    {
        _tcs = null;
        _message = null;
        _title = null;
        _token = default;
        _opened = false;
    }
}

#endregion

#region Sounds - slots, throttle queue, playback

// append-only: the saved slot list is indexed by this
public enum CraftSound
{
    Succeeded,
    Failed,
    Error,
    Notification,
    QueueComplete,
}

// plain fields so the settings serializer round-trips it without any attributes
public sealed class SoundSlot
{
    public bool Enabled;
    public string File = "";
    public float Volume = 1f;
}

public static class Sounds
{
    public static readonly CraftSound[] All = (CraftSound[])Enum.GetValues(typeof(CraftSound));

    static SoundController _controller;
    static bool _controllerFailed;
    static readonly List<CraftSound> _pending = new();
    static int _cooldownMs;

    public static string Folder => Path.Combine(ExileCrafting.Main.DirectoryFullName, "sounds");

    public static IReadOnlyList<string> Files()
    {
        try
        {
            if (!Directory.Exists(Folder)) return Array.Empty<string>();
            return Directory.GetFiles(Folder, "*.wav").Select(Path.GetFileName).OrderBy(n => n).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static SoundSlot SlotFor(CraftSound sound)
    {
        var slots = ExileCrafting.Main.Settings.SoundSlots;
        while (slots.Count < All.Length) slots.Add(new SoundSlot());
        return slots[(int)sound];
    }

    public static void Request(CraftSound sound)
    {
        // a script that awaits off the main thread would race Tick on these lists
        if (Environment.CurrentManagedThreadId != ExileCrafting.Main.MainThreadId) return;

        var slot = SlotFor(sound);
        if (!slot.Enabled || string.IsNullOrEmpty(slot.File)) return;
        if (_pending.Contains(sound)) return;

        _pending.Add(sound);
    }

    public static void Preview(CraftSound sound) => Play(SlotFor(sound));

    internal static void Clear()
    {
        _pending.Clear();
        _cooldownMs = 0;
    }

    internal static void Tick(int deltaMs)
    {
        if (_cooldownMs > 0) _cooldownMs -= deltaMs;
        if (_pending.Count == 0 || _cooldownMs > 0) return;

        var sound = _pending[0];
        _pending.RemoveAt(0);
        Play(SlotFor(sound));
        _cooldownMs = ExileCrafting.Main.Settings.SoundThrottleMs.Value;
    }

    internal static void Dispose()
    {
        try
        {
            _controller?.Dispose();
        }
        catch
        {
        }

        _controller = null;
        _controllerFailed = false;
        _pending.Clear();
        _cooldownMs = 0;
    }

    static void Play(SoundSlot slot)
    {
        if (slot == null || string.IsNullOrEmpty(slot.File)) return;

        try
        {
            // SoundController appends .wav itself, so a name with one on already misses
            Controller()?.PlaySound(Path.GetFileNameWithoutExtension(slot.File), Math.Clamp(slot.Volume, 0f, 1f));
        }
        catch
        {
            // a missing or unreadable wav is a settings mistake, not a reason to break a craft
        }
    }

    static SoundController Controller()
    {
        if (_controller != null || _controllerFailed) return _controller;

        try
        {
            if (!Directory.Exists(Folder)) return null;
            _controller = new SoundController(Folder);
        }
        catch
        {
            _controllerFailed = true;
        }

        return _controller;
    }
}

#endregion

#region Run log and the end-of-run summary table

// line buffer for the running craft - drawn inside the Craft Scripts window, not its own dialog
public sealed class CraftLog
{
    public readonly List<string> Lines = new();

    string _summary;
    string[] _summaryLines = Array.Empty<string>();

    public void Clear()
    {
        Lines.Clear();
        SetSummary(null);
    }

    public void Add(string line) => Lines.Add(line);

    // the end-of-run table. kept apart from Lines because it must not wrap, and because it always
    // belongs at the bottom no matter what gets logged after the run ends.
    public void SetSummary(string text)
    {
        _summary = text;
        _summaryLines = string.IsNullOrEmpty(text) ? Array.Empty<string>() : text.Split('\n');
    }

    public void Draw()
    {
        ImGui.PushTextWrapPos(0f);
        foreach (var line in Lines)
            ImGui.TextUnformatted(line);
        ImGui.PopTextWrapPos();

        // sits at the bottom because the view sticks there, so it's the one spot always on screen
        if (Lines.Count > 0)
        {
            ImGui.Separator();
            if (ImGui.SmallButton("Copy log")) ImGui.SetClipboardText(string.Join("\n", Lines));
        }

        if (_summary != null)
        {
            ImGui.Separator();
            if (ImGui.SmallButton("Copy summary")) ImGui.SetClipboardText(_summary);

            foreach (var line in _summaryLines)
                ImGui.TextUnformatted(line);
        }

        // stick to the bottom only while the user hasn't scrolled up to read something
        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            ImGui.SetScrollHereY(1f);
    }
}

public sealed class CraftSummaryRow
{
    public string Item = "";
    public string Script = "";
    public string Result = "";
    public TimeSpan Time;
    public double Chaos;
    public readonly Dictionary<string, int> Used = new();
}

// box-drawn with ascii so it survives a copy into discord or a text file. the in-game font isn't
// guaranteed monospace, which is what the Copy summary button is there for.
public static class CraftSummary
{
    static readonly string[] Headers = { "Item", "Script", "Result", "Time", "Cost", "Currency" };

    public static string Render(IReadOnlyList<CraftSummaryRow> rows)
    {
        if (rows == null || rows.Count == 0) return null;

        var cells = rows.Select(RowCells).ToList();
        cells.Add(TotalCells(rows));

        var widths = Headers
            .Select((h, i) => Math.Max(h.Length, cells.Max(c => c[i].Length)))
            .ToArray();

        var rule = Rule(widths);
        var lines = new List<string> { rule, Line(Headers, widths), rule };

        for (var i = 0; i < rows.Count; i++) lines.Add(Line(cells[i], widths));

        lines.Add(rule);
        lines.Add(Line(cells[^1], widths));
        lines.Add(rule);

        return string.Join("\n", lines);
    }

    static string[] RowCells(CraftSummaryRow row) => new[]
    {
        row.Item,
        row.Script,
        row.Result,
        Duration(row.Time),
        Cost(row.Chaos),
        Currency(row.Used),
    };

    static string[] TotalCells(IReadOnlyList<CraftSummaryRow> rows)
    {
        var used = new Dictionary<string, int>();
        foreach (var pair in rows.SelectMany(r => r.Used))
            used[pair.Key] = used.TryGetValue(pair.Key, out var n) ? n + pair.Value : pair.Value;

        var crafted = rows.Count(r => r.Result == "done");

        return new[]
        {
            "Total",
            crafted == 1 ? "1 crafted" : $"{crafted} crafted",
            $"of {rows.Count}",
            Duration(TimeSpan.FromTicks(rows.Sum(r => r.Time.Ticks))),
            Cost(rows.Sum(r => r.Chaos)),
            Currency(used),
        };
    }

    // the numeric columns read right-aligned, the wordy ones left
    static string Line(IReadOnlyList<string> cells, IReadOnlyList<int> widths)
    {
        var text = new StringBuilder("|");

        for (var i = 0; i < cells.Count; i++)
        {
            var pad = i is 3 or 4 ? cells[i].PadLeft(widths[i]) : cells[i].PadRight(widths[i]);
            text.Append(' ').Append(pad).Append(" |");
        }

        return text.ToString();
    }

    static string Rule(IReadOnlyList<int> widths) =>
        "+" + string.Join("+", widths.Select(w => new string('-', w + 2))) + "+";

    static string Duration(TimeSpan time)
    {
        if (time.TotalSeconds < 1) return "-";
        return time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes}m{time.Seconds:00}s" : $"{time.Seconds}s";
    }

    static string Cost(double chaos) => chaos > 0 ? $"{chaos:0.##}c" : "-";

    static string Currency(IReadOnlyDictionary<string, int> used) =>
        used.Count == 0
            ? "-"
            : string.Join(", ", used.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"));
}

#endregion

#region Crafting slot - which item is being crafted

public static class CraftingSlot
{
    // the crafting slot isn't its own inventory - it's a cell of the currency stash, at X 28.
    const int CraftingSlotX = 28;
    const int CraftingSlotY = 0;

    // ponytail: one run at a time, so a static beats threading the type through every signature.
    // set by StartScript / StartQueue before anything reads it.
    public static CraftType Mode;

    public static string SlotLabel => Mode == CraftType.Harvest ? "harvest craft slot" : "crafting slot";

    static IList<ExileCore.PoEMemory.MemoryObjects.ServerInventory.InventSlotItem> CurrencyStashSlots() =>
        ExileCrafting.Main.GameController.IngameState.ServerData
            .GetPlayerInventoryByType(InventoryTypeE.Currency)?.InventorySlotItems;

    public static HarvestWindow HarvestPanel()
    {
        var window = ExileCrafting.Main.GameController.IngameState.IngameUi.HorticraftingStationWindow;
        return window != null && window.IsVisible ? window : null;
    }

    public static CraftItem ReadItem() => Mode == CraftType.Harvest ? HarvestItem() : CurrencyItem();

    public static NormalInventoryItem FindClickableSlot() =>
        Mode == CraftType.Harvest ? HarvestSlotItem() : CurrencySlotItem();

    // the harvest craft slot never registers on UIHoverElement, so its hover comes off the inventory
    public static bool IsHoveringSlot(long address) =>
        Mode == CraftType.Harvest
            ? HarvestPanel()?.CraftInventory?.HoverItem?.Item?.Address == address
            : ExileCrafting.Main.GameController.IngameState.UIHoverElement?.Entity?.Address == address;

    static CraftItem CurrencyItem()
    {
        var slot = CurrencyStashSlots()?
            .FirstOrDefault(s => s?.Item != null && s.PosX == CraftingSlotX && s.PosY == CraftingSlotY);

        return slot == null ? CraftItem.Empty : new CraftItem(slot.Item);
    }

    static CraftItem HarvestItem()
    {
        var slot = HarvestSlotItem();
        return slot?.Item == null ? CraftItem.Empty : new CraftItem(slot.Item);
    }

    static NormalInventoryItem HarvestSlotItem() =>
        HarvestPanel()?.CraftInventory?.VisibleInventoryItems?.FirstOrDefault(i => i?.Item != null);

    // the crafting slot renders as one of the visible items in the currency-tab stash panel -
    // match it by entity address rather than guessing its tile size/position.
    static NormalInventoryItem CurrencySlotItem()
    {
        var target = CurrencyItem();
        if (target.IsEmpty) return null;

        var visible = ExileCrafting.Main.GameController.IngameState.IngameUi.StashElement?.VisibleStash?.VisibleInventoryItems;
        return visible?.FirstOrDefault(i => i.Item != null && i.Item.Address == target.Address);
    }

    // harvest crafting has no stash to pull from, so its currency comes out of the player inventory
    public static NormalInventoryItem FindCurrency(string baseName)
    {
        var log = ExileCrafting.Main.CraftLog;
        var seen = new List<string>();

        // the stash is the bulk supply, so it wins in both modes; the bag is the fallback. the harvest
        // station opens this same stash, so nothing about it is special-cased.
        var stash = ExileCrafting.Main.GameController.IngameState.IngameUi.StashElement?.VisibleStash?.VisibleInventoryItems;
        var stashed = SearchFor(baseName, stash, ExileCrafting.Main.Settings.RestrictedMode.Value, seen);
        if (stashed != null) return stashed;

        var held = SearchFor(baseName, InventoryGrid.Panel()?.VisibleInventoryItems, false, seen);
        if (held != null) return held;

        log.Add($"'{baseName}' not found in the open stash tab or your inventory. " +
                $"{seen.Count} visible item(s) checked.");
        log.Add("saw: " + (seen.Count > 0 ? string.Join(", ", seen.Distinct().OrderBy(n => n)) : "<no readable base names>"));
        return null;
    }

    // restricted mode is an allowlist of currency stash cells, so it only ever gates a stash search
    static NormalInventoryItem SearchFor(string baseName, IList<NormalInventoryItem> visible, bool restricted,
        List<string> seen)
    {
        if (visible == null || visible.Count == 0) return null;

        var slotKeys = restricted ? SlotKeysByAddress() : null;

        foreach (var item in visible)
        {
            var itemBaseName = ExileCrafting.Main.GameController.Files.BaseItemTypes.Translate(item.Item?.Path)?.BaseName;
            if (itemBaseName != null) seen.Add(itemBaseName);

            if (restricted && !IsAllowed(slotKeys, item)) continue;

            if (itemBaseName == baseName) return item;
        }

        return null;
    }

    // grid position is the only stable slot identity, addresses churn as items are consumed.
    // visible currency-tab items report InventPos 0/0, so the position has to come from server data.
    public static Dictionary<long, int> SlotKeysByAddress()
    {
        var map = new Dictionary<long, int>();
        var slots = CurrencyStashSlots();
        if (slots == null) return map;

        foreach (var s in slots)
            if (s?.Item != null) map[s.Item.Address] = s.PosX * 100 + s.PosY;

        return map;
    }

    public static bool IsAllowed(Dictionary<long, int> slotKeys, NormalInventoryItem item) =>
        item?.Item != null && slotKeys.TryGetValue(item.Item.Address, out var key) &&
        ExileCrafting.Main.Settings.AllowedSlots.Contains(key);

    public static void ToggleAllowed(Dictionary<long, int> slotKeys, NormalInventoryItem item)
    {
        if (item?.Item == null || !slotKeys.TryGetValue(item.Item.Address, out var key)) return;

        var allowed = ExileCrafting.Main.Settings.AllowedSlots;
        if (!allowed.Add(key)) allowed.Remove(key);
    }
}

public readonly struct CraftItem
{
    readonly Entity _entity;

    public CraftItem(Entity entity) => _entity = entity;

    public static readonly CraftItem Empty = new(null);

    public bool IsEmpty => _entity is not { IsValid: true };
    public long Address => _entity?.Address ?? 0;

    public string BaseName => _entity == null
        ? ""
        : ExileCrafting.Main.GameController.Files.BaseItemTypes.Translate(_entity.Path)?.BaseName ?? "";

    public Mods Mods => _entity != null && _entity.TryGetComponent<Mods>(out var mods) ? mods : null;
    public ItemRarity Rarity => Mods?.ItemRarity ?? ItemRarity.Normal;
}

#endregion

#region Inventory grid - cell keys for the queue

public static class InventoryGrid
{
    static IList<ExileCore.PoEMemory.MemoryObjects.ServerInventory.InventSlotItem> Slots() =>
        ExileCrafting.Main.GameController.IngameState.ServerData
            .GetPlayerInventoryByType(InventoryTypeE.MainInventory)?.InventorySlotItems;

    // a closed inventory still hands back its last items, which drew queue marks over a shut panel
    // and let a currency search match something that can't be clicked
    public static Inventory Panel()
    {
        var panel = ExileCrafting.Main.GameController.IngameState.IngameUi.InventoryPanel;
        return panel is { IsVisible: true } ? panel[InventoryIndex.PlayerInventory] : null;
    }

    public static int Key(int x, int y) => x * 100 + y;

    public static string CellLabel(int key) => $"{key / 100},{key % 100}";

    public static string Describe(int key) => $"cell ({CellLabel(key)})";

    public static Dictionary<long, int> KeysByAddress()
    {
        var map = new Dictionary<long, int>();
        var slots = Slots();
        if (slots == null) return map;

        foreach (var s in slots)
            if (s?.Item != null) map[s.Item.Address] = Key(s.PosX, s.PosY);

        return map;
    }

    public static int KeyOf(Dictionary<long, int> keys, NormalInventoryItem item) =>
        item?.Item != null && keys.TryGetValue(item.Item.Address, out var key) ? key : -1;

    public static long AddressAt(int key)
    {
        var slot = Slots()?.FirstOrDefault(s => s?.Item != null && Key(s.PosX, s.PosY) == key);
        return slot?.Item.Address ?? 0;
    }

    public static bool IsCellEmpty(int key) => AddressAt(key) == 0;

    public static NormalInventoryItem VisibleItemAt(int key)
    {
        var address = AddressAt(key);
        if (address == 0) return null;

        return Panel()?.VisibleInventoryItems?.FirstOrDefault(i => i.Item != null && i.Item.Address == address);
    }

    public static long CursorHeldAddress()
    {
        var held = ExileCrafting.Main.GameController.IngameState.ServerData
            .GetPlayerInventoryByType(InventoryTypeE.Cursor)?.InventorySlotItems;
        return held?.FirstOrDefault(s => s?.Item != null)?.Item.Address ?? 0;
    }

    public static QueueJob JobFor(int key) =>
        ExileCrafting.Main.Settings.Queue.FirstOrDefault(j => j.Cell == key);

    public static bool IsQueued(int key) => JobFor(key) != null;
}

#endregion
