using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ExileImGui;
using ImGuiNET;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System;

namespace ExileCrafting;

#region Settings nodes

// Off sits last so the saved index of the others doesn't shift under existing configs
public enum HumanizerPreset
{
    Slow,
    Normal,
    Fast,
    Custom,
    Off
}

public class ExileCraftingSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public HotkeyNodeV2 OpenPickerHotkey { get; set; } = new(Keys.NumPad7);
    public HotkeyNodeV2 AbortHotkey { get; set; } = new(Keys.Escape);
    public HotkeyNodeV2 PauseHotkey { get; set; } = new(Keys.Space);
    public ToggleNode RestrictedMode { get; set; } = new ToggleNode(false);
    public HotkeyNodeV2 ToggleSlotHotkey { get; set; } = new(Keys.Add);

    // slot keys from CraftingSlot.SlotKey - hand-picked in game, not in the menu
    [IgnoreMenu] public HashSet<int> AllowedSlots { get; set; } = new();

    // queued crafts, in run order. cell keys come from InventoryGrid.Key.
    [IgnoreMenu] public List<QueueJob> Queue { get; set; } = new();
    public ToggleNode MaxRunSecondsEnabled { get; set; } = new ToggleNode(true);
    public RangeNode<int> MaxRunSeconds { get; set; } = new(120, 5, 900);

    // the user's curated script list - drawn in the crafter dialog, not the settings menu
    [IgnoreMenu] public List<ScriptEntry> Scripts { get; set; } = new();

    // bundled examples already copied into the scripts folder, so a deleted one doesn't come back
    [IgnoreMenu] public HashSet<string> SeededExamples { get; set; } = new();

    // drawn by hand in the Sounds tab. one entry per CraftSound, padded on read.
    [IgnoreMenu] public List<SoundSlot> SoundSlots { get; set; } = new();
    [IgnoreMenu] public RangeNode<int> SoundThrottleMs { get; set; } = new(2000, 0, 10000);

    // everything below is drawn by hand in the Humanizer tab - IgnoreMenu keeps the auto-built
    // menu from listing them twice, the nodes still save and load normally.
    [IgnoreMenu] public RangeNode<int> Preset { get; set; } = new((int)HumanizerPreset.Normal, 0, 4);
    [IgnoreMenu] public RangeNode<float> Gravity { get; set; } = new(11f, 1f, 30f);
    [IgnoreMenu] public RangeNode<float> Wind { get; set; } = new(3f, 0f, 15f);
    [IgnoreMenu] public RangeNode<float> MaxStep { get; set; } = new(40f, 1f, 120f);
    [IgnoreMenu] public RangeNode<float> SlowDistance { get; set; } = new(8f, 1f, 40f);
    [IgnoreMenu] public RangeNode<int> StepDelayMin { get; set; } = new(2, 0, 60);
    [IgnoreMenu] public RangeNode<int> StepDelayMax { get; set; } = new(6, 0, 60);
    [IgnoreMenu] public RangeNode<int> PreClickSettleMin { get; set; } = new(20, 0, 400);
    [IgnoreMenu] public RangeNode<int> PreClickSettleMax { get; set; } = new(60, 0, 400);
    [IgnoreMenu] public RangeNode<int> ClickDwellMin { get; set; } = new(25, 0, 400);
    [IgnoreMenu] public RangeNode<int> ClickDwellMax { get; set; } = new(45, 0, 400);
    [IgnoreMenu] public RangeNode<int> PostClickSettleMin { get; set; } = new(30, 0, 400);
    [IgnoreMenu] public RangeNode<int> PostClickSettleMax { get; set; } = new(90, 0, 400);
    [IgnoreMenu] public RangeNode<int> ActionDelayMin { get; set; } = new(150, 0, 5000);
    [IgnoreMenu] public RangeNode<int> ActionDelayMax { get; set; } = new(380, 0, 5000);
    [IgnoreMenu] public RangeNode<int> CheckModsDelayMin { get; set; } = new(200, 0, 5000);
    [IgnoreMenu] public RangeNode<int> CheckModsDelayMax { get; set; } = new(500, 0, 5000);
    [IgnoreMenu] public RangeNode<int> QueueDelayMin { get; set; } = new(675, 0, 15000);
    [IgnoreMenu] public RangeNode<int> QueueDelayMax { get; set; } = new(1950, 0, 15000);

    static readonly System.Random Rng = new();

    [Newtonsoft.Json.JsonIgnore]
    public bool HumanizerEnabled => Preset.Value != (int)HumanizerPreset.Off;

    // every delay in the plugin comes through here, so Off kills all of them in one place
    public int RollDelay(RangeNode<int> min, RangeNode<int> max)
    {
        if (!HumanizerEnabled) return 0;
        return max.Value <= min.Value ? System.Math.Max(0, min.Value) : Rng.Next(min.Value, max.Value + 1);
    }

    public void ApplyHumanizerPreset(HumanizerPreset preset)
    {
        Preset.Value = (int)preset;
        // both keep the slider values they had - Custom because they're the point, Off because
        // it's a switch, not a wipe
        if (preset == HumanizerPreset.Custom || preset == HumanizerPreset.Off) return;

        // Max step is what actually sets move duration: Tick advances the path one waypoint per
        // frame, so a 600px move costs roughly 600 / (0.75 * MaxStep) frames regardless of delay.
        (Gravity.Value, Wind.Value, MaxStep.Value, SlowDistance.Value) = preset switch
        {
            HumanizerPreset.Slow => (10f, 3f, 25f, 12f),
            HumanizerPreset.Fast => (13f, 2f, 70f, 5f),
            _ => (11f, 3f, 40f, 8f),
        };

        // anything under one frame is free, so these stay small - Max step above does the work
        (StepDelayMin.Value, StepDelayMax.Value) = preset switch
        {
            HumanizerPreset.Slow => (4, 10),
            HumanizerPreset.Fast => (0, 2),
            _ => (2, 6),
        };

        (PreClickSettleMin.Value, PreClickSettleMax.Value) = preset switch
        {
            HumanizerPreset.Slow => (30, 80),
            HumanizerPreset.Fast => (5, 15),
            _ => (20, 60),
        };

        // the game registers a press in one frame, so this is about looking real, not landing
        (ClickDwellMin.Value, ClickDwellMax.Value) = preset switch
        {
            HumanizerPreset.Slow => (35, 70),
            HumanizerPreset.Fast => (12, 25),
            _ => (25, 45),
        };

        (PostClickSettleMin.Value, PostClickSettleMax.Value) = preset switch
        {
            HumanizerPreset.Slow => (45, 110),
            HumanizerPreset.Fast => (10, 25),
            _ => (30, 90),
        };

        // reading time, not motor time - but spamming alts is a glance, not a study, so it's
        // nowhere near as long as it used to be. this is the only pause a reroll loop pays.
        (ActionDelayMin.Value, ActionDelayMax.Value) = preset switch
        {
            HumanizerPreset.Slow => (250, 600),
            HumanizerPreset.Fast => (55, 130),
            _ => (150, 380),
        };

        // reading time too, but a deliberate one - you stop and look the mods over instead of
        // glancing at a roll, so it sits just above Action delay
        (CheckModsDelayMin.Value, CheckModsDelayMax.Value) = preset switch
        {
            HumanizerPreset.Slow => (320, 750),
            HumanizerPreset.Fast => (70, 170),
            _ => (200, 500),
        };

        // longest gap of the lot: finishing one item and starting the next is where a person puts
        // the mouse down and looks at what they just made
        (QueueDelayMin.Value, QueueDelayMax.Value) = preset switch
        {
            HumanizerPreset.Slow => (1350, 3750),
            HumanizerPreset.Fast => (225, 675),
            _ => (675, 1950),
        };
    }
}

#endregion

#region Automation tab (humanizer timings)

public static class HumanizerSettingsTab
{
    static readonly Controls.GroupButton[] PresetButtons =
    {
        new() { Text = "Slow" },
        new() { Text = "Normal" },
        new() { Text = "Fast" },
        new() { Text = "Custom" },
        new() { Text = "Off" },
    };

    public static bool Draw(ExileCraftingSettings settings)
    {
        ImGui.TextWrapped("Timings. Off moves the cursor straight to the target and runs with no " +
                          "delays at all - fastest, and the most obvious.");
        ImGui.Separator();

        var preset = settings.Preset.Value;
        if (Controls.ButtonGroup("humanizerpreset", ref preset, PresetButtons))
        {
            settings.ApplyHumanizerPreset((HumanizerPreset)preset);
            return true;
        }
        Controls.Tip("Normal is calibrated to human timing.\n" +
                     "Slow is a bit slower than a person, Fast is deliberately faster.\n" +
                     "Off skips the curved path and every delay below.\n" +
                     "Editing any value below switches this to Custom.");

        if (!settings.HumanizerEnabled)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Humanizer is off - the values below are kept, but nothing reads them.");
            return false;
        }

        ImGui.Separator();
        ImGui.TextDisabled("Path shape (humanized mouse only)");

        // any hand edit means the values no longer match the named preset
        var changed = false;

        changed |= Controls.SliderFloat("Gravity", () => settings.Gravity.Value, v => settings.Gravity.Value = v, 1f, 30f);
        Controls.Tip("How hard the cursor is pulled toward the target.\n" +
                     "Higher goes straighter and arrives sooner, lower wanders.");

        changed |= Controls.SliderFloat("Wind", () => settings.Wind.Value, v => settings.Wind.Value = v, 0f, 15f);
        Controls.Tip("Sideways jitter along the path.\n" +
                     "0 draws a dead-straight line - the least human thing there is.");

        changed |= Controls.SliderFloat("Max step (px)", () => settings.MaxStep.Value, v => settings.MaxStep.Value = v, 1f, 120f);
        Controls.Tip("Biggest jump in pixels per step, and the real control over move speed.\n" +
                     "One step per frame, so a 600px move costs about 600 / (0.75 x this) frames.\n" +
                     "Raise it to speed moves up. Step delay under one frame does nothing.");

        changed |= Controls.SliderFloat("Slow distance (px)", () => settings.SlowDistance.Value, v => settings.SlowDistance.Value = v, 1f, 40f);
        Controls.Tip("Pixels from the target where the cursor starts easing off.\n" +
                     "The deceleration a real hand does at the end of a reach.");

        ImGui.Separator();
        ImGui.TextDisabled("Delays - each one picks a random value in its min/max range");

        changed |= DrawRange(settings.StepDelayMin, settings.StepDelayMax, "Step delay", 0, 60,
            "Extra pause between movement steps, on top of the frame each one costs.\n" +
            "Under ~16ms is free at 60fps - use Max step to change move speed.\n" +
            "Humanized mouse only.");

        changed |= DrawRange(settings.PreClickSettleMin, settings.PreClickSettleMax, "Pre-click settle", 0, 400,
            "Pause after the cursor arrives, before the button goes down.\n" +
            "Without it the click lands the same instant the move ends.");

        changed |= DrawRange(settings.ClickDwellMin, settings.ClickDwellMax, "Click dwell", 0, 400,
            "How long the button is held down.\n" +
            "The press itself, not the gap around it.");

        changed |= DrawRange(settings.PostClickSettleMin, settings.PostClickSettleMax, "Post-click settle", 0, 400,
            "Pause after the button comes back up, before anything else happens.");

        changed |= DrawRange(settings.ActionDelayMin, settings.ActionDelayMax, "Action delay", 0, 5000,
            "Pause between applications in an ApplyCurrencyUntil run - glancing at the\n" +
            "roll and clicking again.\n" +
            "The only delay a reroll loop pays, so this is the dial for run speed.");

        changed |= DrawRange(settings.CheckModsDelayMin, settings.CheckModsDelayMax, "Check mods delay", 0, 5000,
            "Pause on a ctx.CheckMods call.\n" +
            "Time to read the mods over and decide. Instant checks are free\n" +
            "information no person has.");

        changed |= DrawRange(settings.QueueDelayMin, settings.QueueDelayMax, "Queue delay", 0, 15000,
            "Pause between one queued item finishing and the next being picked up.\n" +
            "Longer than Action delay on purpose - this is where a person looks over\n" +
            "what they just made.");

        if (changed) settings.Preset.Value = (int)HumanizerPreset.Custom;
        return changed;
    }

    // min/max pair kept ordered, so dragging one past the other doesn't invert the range
    static bool DrawRange(ExileCore.Shared.Nodes.RangeNode<int> min, ExileCore.Shared.Nodes.RangeNode<int> max,
        string label, int lo, int hi, string tip)
    {
        var changed = Controls.SliderInt($"{label} min (ms)", () => min.Value, v => min.Value = v, lo, hi);
        Controls.Tip(tip);

        changed |= Controls.SliderInt($"{label} max (ms)", () => max.Value, v => max.Value = v, lo, hi);
        Controls.Tip(tip);

        if (min.Value > max.Value)
        {
            max.Value = min.Value;
            changed = true;
        }

        return changed;
    }
}

#endregion

#region Sounds tab

public static class SoundSettingsTab
{
    static readonly (CraftSound Sound, string Label, string Tip)[] Rows =
    {
        (CraftSound.Succeeded, "Craft succeeded",
            "Plays when a craft finishes clean.\nOn the last item of a 2+ item queue, Queue complete plays instead."),
        (CraftSound.Failed, "Craft failed",
            "Plays when a craft errors, hits a limit, or is aborted."),
        (CraftSound.Error, "Craft error",
            "Never fires on its own.\nScripts call ctx.PlaySound(CraftSound.Error)."),
        (CraftSound.Notification, "Craft notification",
            "Never fires on its own.\nScripts call ctx.PlaySound(CraftSound.Notification)."),
        (CraftSound.QueueComplete, "Queue complete",
            "Plays when a queue of 2 or more items runs all the way to the end."),
    };

    static IReadOnlyList<string> _files;

    // one per row - SearchCombo owns its filter, and sharing one leaks typed text between rows
    static readonly string[] _filters = Sounds.All.Select(_ => "").ToArray();

    public static bool Draw(ExileCraftingSettings settings)
    {
        _files ??= Sounds.Files();

        ImGui.TextWrapped(Text.Ascii($"wav files are read from {Sounds.Folder}"));
        if (ImGui.SmallButton("Rescan")) _files = Sounds.Files();

        ImGui.Separator();

        var changed = Controls.SliderInt("Gap between sounds (ms)",
            () => settings.SoundThrottleMs.Value, v => settings.SoundThrottleMs.Value = v, 0, 10000);
        Controls.Tip("Two sounds starting on the same frame screech.\n" +
                     "Anything asked for during the gap waits it out, and a repeat of something\n" +
                     "already waiting is dropped. 0 turns the gap off entirely.");

        ImGui.Separator();

        if (_files.Count == 0)
        {
            ImGui.TextWrapped("No .wav files found. Drop some in that folder and press Rescan.");
            return changed;
        }

        if (!ImGui.BeginTable("craftsounds", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return changed;

        // a real label, not "##on" - a column whose label matches a widget id inside it collides
        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 34f);
        ImGui.TableSetupColumn("Sound", ImGuiTableColumnFlags.WidthFixed, 150f);
        // a wav name is short, a volume slider is easier to hit wide - so Volume takes the slack
        ImGui.TableSetupColumn("File", ImGuiTableColumnFlags.WidthFixed, 220f);
        ImGui.TableSetupColumn("Volume");
        ImGui.TableSetupColumn("##test", ImGuiTableColumnFlags.WidthFixed, 46f);
        ImGui.TableHeadersRow();

        foreach (var (sound, label, tip) in Rows)
        {
            var slot = Sounds.SlotFor(sound);
            ImGui.PushID((int)sound);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            changed |= ImGui.Checkbox("##enable", ref slot.Enabled);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(label);
            Controls.Tip(tip);

            ImGui.TableNextColumn();
            var candidates = new[] { (key: "", label: "(none)") }
                .Concat(_files.Select(f => (key: f, label: f)));

            if (Combo.SearchCombo("file", string.IsNullOrEmpty(slot.File) ? "(none)" : slot.File,
                    candidates, ref _filters[(int)sound], out var picked, -1f))
            {
                slot.File = picked;
                changed = true;
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            var volume = slot.Volume;
            if (ImGui.SliderFloat("##vol", ref volume, 0f, 1f, "%.2f"))
            {
                slot.Volume = volume;
                changed = true;
            }

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(string.IsNullOrEmpty(slot.File));
            if (ImGui.SmallButton("Test")) Sounds.Preview(sound);
            ImGui.EndDisabled();

            ImGui.PopID();
        }

        ImGui.EndTable();
        return changed;
    }
}

#endregion
