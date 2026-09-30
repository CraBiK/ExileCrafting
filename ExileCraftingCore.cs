using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExileCrafting;

public class ExileCrafting : BaseSettingsPlugin<ExileCraftingSettings>
{
    public static ExileCrafting Main;

    public ScriptHost ScriptHost;
    public readonly ScriptEditorDialog EditorDialog = new();
    public readonly QueuePopover QueuePopover = new();
    public readonly CraftLog CraftLog = new();
    readonly ClickExecutor _executor = new();
    readonly CraftDialog _dialog = new();

    CancellationTokenSource _runCts;
    Task<object> _runTask;
    CraftQueue _queue;

    public ClickExecutor Executor => _executor;

    public CraftQueue RunningQueue => _queue;

    // the queue swaps this per item, so the status line and the usage summary follow the item
    // that's actually running
    public CraftContext RunningContext { get; set; }

    // shown as the outlined line along the bottom of whichever craft panel is open
    public string StatusMessage { get; set; }

    public string RunningScriptName { get; private set; }

    // kept so the dialog's Rerun button can start the same entry with the same limits
    public ScriptEntry RunningEntry { get; private set; }
    public string RunningStatusText { get; private set; }
    public string RunningError { get; private set; }

    // full stack, only set for a genuine fault - a guard stop leaves this null and that's what
    // tells the dialog to render it as a normal stop rather than a crash
    public string RunningErrorDetail { get; private set; }
    public bool IsScriptRunning => _runTask != null;

    // holds the run between actions, unlike the abort key which cancels it outright
    public bool IsPaused { get; private set; }

    public bool IsCraftDialogOpen => _dialog.IsOpen;

    // ClickExecutor checks calls against this - scripts must resume on the main thread, since
    // ExileCore's game-state reads aren't safe from a background one.
    public int MainThreadId { get; private set; }

    public override bool Initialise()
    {
        Main = this;
        ScriptHost = new ScriptHost(Path.Combine(ConfigDirectory, "Scripts"));
        ScriptHost.SeedExamples(Settings.SeededExamples);
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
    }

    public override void OnUnload() => Sounds.Dispose();

    public override Job Tick()
    {
        // Tick is the thread we're actually guarding ApplyCurrency against, so capture it here
        // instead of Initialise (which may run on a plugin-loader thread, not this one).
        if (MainThreadId == 0) MainThreadId = Environment.CurrentManagedThreadId;

        // DeltaTime is already milliseconds despite the name - x1000 here made every WaitTime
        // expire on the frame it started
        _executor.Tick((int)GameController.DeltaTime);
        Sounds.Tick((int)GameController.DeltaTime);

        if (Settings.OpenPickerHotkey.PressedOnce())
            _dialog.IsOpen = !_dialog.IsOpen;

        if (Settings.AbortHotkey.PressedOnce())
            CancelRunningScript();

        if (Settings.PauseHotkey.PressedOnce())
            TogglePause();

        if (RunningContext != null && _runTask != null)
        {
            var line = _queue == null
                ? RunningContext.StatusText
                : $"{_queue.Progress} - {RunningContext.StatusText}";
            RunningStatusText = IsPaused ? $"Paused - {line}" : line;
        }

        if (_runTask is { IsCompleted: true })
        {
            IsPaused = false;

            // summary goes in first so it reads above the outcome line, and it runs for a cancelled
            // or failed craft too - that's exactly when you want to know what it burned. the queue
            // logs its own per item as it goes.
            if (_queue == null && RunningContext != null) CraftLog.Add(RunningContext.UsageSummary());

            if (_runTask.IsFaulted)
            {
                var ex = _runTask.Exception?.GetBaseException();

                if (ex is CraftGuardException guard)
                {
                    RunningError = guard.Message;
                    RunningErrorDetail = null;
                    RunningStatusText = "Stopped.";
                    StatusMessage = "craft stopped";
                    CraftLog.Add(guard.Message);
                }
                else
                {
                    RunningError = ex == null ? "Unknown error." : $"{ex.GetType().Name}: {ex.Message}";
                    RunningErrorDetail = ex?.ToString();
                    RunningStatusText = "Failed.";
                    StatusMessage = "craft failed";
                    CraftLog.Add(RunningError);
                    if (ex != null) LogError($"script '{RunningScriptName}' failed: {ex}");
                }
            }
            else if (_runTask.IsCanceled)
            {
                RunningStatusText = "Cancelled.";
                StatusMessage = "craft cancelled";
            }
            else
            {
                RunningStatusText = "Done.";
                StatusMessage = "craft finished";
            }

            // the queue pings per item, so a queue run must not also ping at the end
            if (_queue == null)
                Sounds.Request(_runTask.IsCompletedSuccessfully ? CraftSound.Succeeded : CraftSound.Failed);

            _runTask = null;
            _queue = null;
        }

        return null;
    }

    public override void DrawSettings()
    {
        // windows sdk pulls in a global Windows namespace here, has to be fully qualified or it wont resolve
        ExileImGui.Windows.TabBar("exilecraftingsettings",
            ("General", () => { base.DrawSettings(); return false; }),
            ("Sounds", () => SoundSettingsTab.Draw(Settings)),
            ("Automation", () => HumanizerSettingsTab.Draw(Settings)));
    }

    public override void Render()
    {
        CraftOverlay.Draw(this);
        _dialog.Draw(this);
        EditorDialog.Draw(this);
        QueuePopover.Draw(this);
        CraftPrompt.Draw();
    }

    public override void EntityAdded(Entity entity)
    {
    }

    // the editor's Run button only knows a filename - use the user's entry for it if they have one,
    // so the limits they set still apply, otherwise fall back to a default-limits run.
    public void StartScript(string fileName) =>
        StartScript(Settings.Scripts.FirstOrDefault(s => s.FileName == fileName)
                    ?? new ScriptEntry { FileName = fileName, Name = fileName });

    public void StartScript(ScriptEntry entry)
    {
        if (_runTask != null) return;
        if (entry == null || string.IsNullOrWhiteSpace(entry.FileName)) return;

        CraftingSlot.Mode = ScriptHost.ReadType(entry.FileName);
        _executor.ResetHarvestSelection();

        RunningScriptName = entry.Title;
        RunningEntry = entry;
        RunningError = null;
        RunningErrorDetail = null;
        RunningStatusText = "Compiling...";

        if (!ScriptHost.TryGetOrCompile(entry.FileName, out var runner, out var compileError))
        {
            RunningError = compileError;
            RunningErrorDetail = null;
            RunningStatusText = null;
            return;
        }

        RunningStatusText = "Running...";
        StatusMessage = $"running {entry.Title}";
        CraftLog.Clear();
        Sounds.Clear();

        if (entry.MaxChaos > 0 && CraftContext.PriceLookup() == null)
            CraftLog.Add("note: budget set but Ninja Price isn't loaded, so cost can't be tracked.");

        _runCts = new CancellationTokenSource();
        RunningContext = NewContext(entry.MaxApplications, entry.MaxChaos);
        _runTask = runner(RunningContext);
    }

    public CraftContext NewContext(int maxApplications, float maxChaos) =>
        new(_executor, _runCts.Token, maxApplications,
            Settings.MaxRunSecondsEnabled.Value ? Settings.MaxRunSeconds.Value : 0,
            maxChaos, CraftLog);

    public void StartQueue()
    {
        if (_runTask != null) return;

        var jobs = Settings.Queue.ToList();
        if (jobs.Count == 0) return;

        RunningScriptName = jobs.Count == 1 ? "Queue (1 item)" : $"Queue ({jobs.Count} items)";
        RunningEntry = null;
        RunningError = null;
        RunningErrorDetail = null;
        // the queue only sets this once its first item is in the slot, and Rerun skips ResetRunState
        RunningContext = null;

        var types = jobs.Select(j => ScriptHost.ReadType(j.FileName)).Distinct().ToList();
        if (types.Count > 1)
        {
            RunningError = "This queue mixes Currency and Harvest scripts. They drive different " +
                           "panels, so every job in one queue has to be the same type.";
            RunningStatusText = null;
            return;
        }

        // the emptiness check below reads whichever slot the mode points at, so mode comes first
        CraftingSlot.Mode = types[0];

        if (!CraftingSlot.ReadItem().IsEmpty)
        {
            RunningError = $"Empty the {CraftingSlot.SlotLabel} first - the queue moves its own items in and out.";
            RunningStatusText = null;
            return;
        }

        RunningStatusText = "Running...";
        StatusMessage = "running queue";
        CraftLog.Clear();
        Sounds.Clear();

        if (jobs.Any(j => j.MaxChaos > 0) && CraftContext.PriceLookup() == null)
            CraftLog.Add("note: budget set but Ninja Price isn't loaded, so cost can't be tracked.");

        _runCts = new CancellationTokenSource();
        _queue = new CraftQueue(this, jobs, _runCts.Token);
        _runTask = _queue.RunAsync();
    }

    public void OpenCraftDialog() => _dialog.IsOpen = true;

    public void CancelRunningScript() => _runCts?.Cancel();

    public void TogglePause()
    {
        if (_runTask == null) return;

        IsPaused = !IsPaused;
        if (IsPaused) RunningContext?.StopClock(); else RunningContext?.StartClock();

        StatusMessage = IsPaused ? "craft paused" : "craft resumed";
        CraftLog.Add(IsPaused ? "paused - the current action finishes, then it holds." : "resumed.");
    }

    public void ResetRunState()
    {
        RunningScriptName = null;
        RunningStatusText = null;
        RunningError = null;
        RunningErrorDetail = null;
        RunningContext = null;
    }
}
