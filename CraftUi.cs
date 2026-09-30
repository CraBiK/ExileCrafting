using ExileCore.PoEMemory.Elements;
using ExileImGui;
using ImGuiNET;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System;
using SColor = SharpDX.Color;

namespace ExileCrafting;

#region Craft dialog - the Craft Scripts window

// three states, one shape: action bar on top, a body that fills and scrolls, status pinned to the
// bottom. the body is the script list until a craft starts, then it's the log.
public sealed class CraftDialog
{
    public bool IsOpen;

    string _filter = "";
    int _remove = -1;
    int _removeJob = -1;

    // refreshed once per frame in DrawList - the per-row missing-file check would otherwise hit the
    // disk for every row, every frame
    HashSet<string> _filesOnDisk = new();

    // built once per frame - the sort comparator calls SortKey O(n log n) times and each read stats the file
    Dictionary<string, string> _typeByFile = new();

    public void Draw(ExileCrafting plugin)
    {
        if (!IsOpen) return;

        var open = IsOpen;
        // windows sdk pulls in a global Windows namespace here, has to be fully qualified or it wont resolve
        if (!ExileImGui.Windows.Begin("Craft Scripts", "craftdialog", ref open, 0, new Vector2(640, 420)))
        {
            ExileImGui.Windows.End();
            IsOpen = open;
            return;
        }

        DrawActionBar(plugin);
        ImGui.Separator();

        // negative height means "everything except this much", which is what keeps the status bar
        // glued to the bottom no matter how the window is resized
        var reserved = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y * 2f
                       + CraftProgress.Height(plugin);
        ImGui.BeginChild("craftbody", new Vector2(0f, -reserved), ImGuiChildFlags.None);
        if (plugin.RunningScriptName != null) DrawLog(plugin);
        else DrawList(plugin);
        ImGui.EndChild();

        CraftProgress.Draw(plugin);

        ImGui.Separator();
        DrawStatusBar(plugin);

        ExileImGui.Windows.End();
        IsOpen = open;
    }

    void DrawActionBar(ExileCrafting plugin)
    {
        if (plugin.IsScriptRunning)
        {
            if (ImGui.Button($"Cancel (Press {plugin.Settings.AbortHotkey.Value})")) plugin.CancelRunningScript();
            ImGui.SameLine();
            var pause = plugin.IsPaused ? "Resume" : "Pause";
            if (ImGui.Button($"{pause} (Press {plugin.Settings.PauseHotkey.Value})")) plugin.TogglePause();
            return;
        }

        if (plugin.RunningScriptName != null)
        {
            if (ImGui.Button("Rerun"))
            {
                if (plugin.RunningEntry != null) plugin.StartScript(plugin.RunningEntry);
                else plugin.StartQueue();
            }
            ImGui.SameLine();
            if (ImGui.Button("Done")) plugin.ResetRunState();
            return;
        }

        DrawAddPicker(plugin);
        ImGui.SameLine();
        if (ImGui.Button("New Script")) plugin.EditorDialog.OpenForNew();
        ImGui.SameLine();
        if (ImGui.Button("Open Folder")) plugin.ScriptHost.OpenScriptsFolder();

        var queued = plugin.Settings.Queue.Count;
        ImGui.SameLine();
        ImGui.BeginDisabled(queued == 0);
        if (ImGui.Button($"Run Queue ({queued})")) plugin.StartQueue();
        ImGui.SameLine();
        if (ImGui.Button("Clear queue")) plugin.Settings.Queue.Clear();
        ImGui.EndDisabled();

        if (queued > 0) return;

        ImGui.SameLine();
        ImGui.TextDisabled($"queue an item: hover an inventory cell, press {plugin.Settings.ToggleSlotHotkey.Value}, " +
                           "press again to confirm");
    }

    void DrawLog(ExileCrafting plugin)
    {
        if (plugin.RunningError != null)
        {
            // amber for a guard stop, red for an actual fault. detail is only set for the latter.
            var crashed = plugin.RunningErrorDetail != null;
            using (new EColor.StyleColorScope((ImGuiCol.Text, crashed ? 0xFF4444FFu : 0xFF33CCFFu)))
            {
                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted(plugin.RunningError);
                ImGui.PopTextWrapPos();
            }

            if (crashed)
            {
                if (ImGui.SmallButton("Copy error")) ImGui.SetClipboardText(plugin.RunningErrorDetail);

                if (ImGui.TreeNode("Details"))
                {
                    ImGui.PushTextWrapPos(0f);
                    ImGui.TextUnformatted(plugin.RunningErrorDetail);
                    ImGui.PopTextWrapPos();
                    ImGui.TreePop();
                }
            }

            ImGui.Separator();
        }

        plugin.CraftLog.Draw();
    }

    void DrawStatusBar(ExileCrafting plugin)
    {
        if (plugin.RunningScriptName == null)
        {
            var n = plugin.Settings.Scripts.Count;
            ImGui.TextDisabled(n == 1 ? "1 script" : $"{n} scripts");
            return;
        }

        var state = plugin.RunningStatusText;
        ImGui.TextUnformatted(Text.Ascii($"{plugin.RunningScriptName} - {state}"));
    }

    void DrawList(ExileCrafting plugin)
    {
        DrawQueue(plugin);

        _filesOnDisk = plugin.ScriptHost.DiscoverScripts().ToHashSet();

        var scripts = plugin.Settings.Scripts;
        if (scripts.Count == 0)
        {
            ImGui.TextWrapped("No scripts added yet. Use 'Add Script' to pick one from the scripts folder.");
            return;
        }

        _typeByFile = scripts.Select(s => s.FileName ?? "").Distinct()
            .ToDictionary(f => f, f => plugin.ScriptHost.ReadType(f).ToString());

        SortableTable.Draw("craftscripts", scripts, Columns(plugin), ref _filter,
            e => $"{e.Name} {e.Description} {e.FileName}");

        if (_remove >= 0 && _remove < scripts.Count)
        {
            scripts.RemoveAt(_remove);
            _remove = -1;
        }
    }

    // read-out only: the popover is where a job gets edited
    void DrawQueue(ExileCrafting plugin)
    {
        var jobs = plugin.Settings.Queue;
        if (jobs.Count == 0) return;

        ImGui.SeparatorText($"Queue ({jobs.Count})");

        if (ImGui.BeginTable("craftqueue", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Cell", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Script");
            ImGui.TableSetupColumn("Max uses", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Max cost", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed, 30f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                ImGui.PushID(i);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(InventoryGrid.Describe(job.Cell));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Text.Ascii(TitleOf(plugin, job)));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(job.MaxApplications > 0 ? job.MaxApplications.ToString() : "-");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(job.MaxChaos > 0 ? $"{job.MaxChaos:0}c" : "-");

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("X")) _removeJob = i;

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (_removeJob >= 0 && _removeJob < jobs.Count)
        {
            jobs.RemoveAt(_removeJob);
            _removeJob = -1;
        }

        ImGui.Separator();
    }

    static string TitleOf(ExileCrafting plugin, QueueJob job) =>
        plugin.Settings.Scripts.FirstOrDefault(s => s.FileName == job.FileName)?.Title ?? job.FileName;

    // files in the scripts folder that aren't on the list yet
    void DrawAddPicker(ExileCrafting plugin)
    {
        if (ImGui.Button("Add Script")) ImGui.OpenPopup("addscript");

        if (!ImGui.BeginPopup("addscript")) return;

        var already = plugin.Settings.Scripts.Select(s => s.FileName).ToHashSet();
        var candidates = plugin.ScriptHost.DiscoverScripts().Where(f => !already.Contains(f)).ToList();

        if (candidates.Count == 0)
        {
            ImGui.TextDisabled("every script in the folder is already on the list");
        }
        else
        {
            foreach (var file in candidates)
                if (ImGui.Selectable(file))
                    plugin.Settings.Scripts.Add(ScriptEntry.FromFile(plugin.ScriptHost, file));
        }

        ImGui.EndPopup();
    }

    // everything fixed except Description, which takes whatever's left
    TableColumn<ScriptEntry>[] Columns(ExileCrafting plugin) => new[]
    {
        new TableColumn<ScriptEntry> { Header = "Name", Width = 150f, SortKey = e => e.Title, Draw = (e, _) => Field("##name", ref e.Name, 64) },
        new TableColumn<ScriptEntry> { Header = "Type", Width = 70f, SortKey = TypeOf, Draw = (e, _) => DrawType(e) },
        new TableColumn<ScriptEntry> { Header = "Description", SortKey = e => e.Description, Draw = (e, _) => Field("##desc", ref e.Description, 256) },
        new TableColumn<ScriptEntry> { Header = "Max uses", Width = 84f, SortKey = e => e.MaxApplications, Draw = (e, _) => Uses(e) },
        new TableColumn<ScriptEntry> { Header = "Max cost", Width = 84f, SortKey = e => e.MaxChaos, Draw = (e, _) => Cost(e) },
        new TableColumn<ScriptEntry> { Header = "", Width = 132f, Draw = (e, i) => Actions(plugin, e, i) },
    };

    string TypeOf(ScriptEntry e) => _typeByFile.TryGetValue(e.FileName ?? "", out var type) ? type : "";

    void DrawType(ScriptEntry e)
    {
        ImGui.TextUnformatted(TypeOf(e));
        Controls.Tip("Set by the '// Type:' line in the script header. Currency drives the stash " +
                     "crafting slot, Harvest drives the horticrafting station window.");
    }

    // id per column, not per row - the row PushID only separates rows from each other
    static void Field(string id, ref string value, uint max)
    {
        var v = value ?? "";
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText(id, ref v, max)) value = v;
    }

    static void Uses(ScriptEntry e)
    {
        var v = e.MaxApplications;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputInt("##uses", ref v, 0)) e.MaxApplications = Math.Max(0, v);
        Controls.Tip("Hard stop after this many applications. 0 means no limit.");
    }

    static void Cost(ScriptEntry e)
    {
        var v = e.MaxChaos;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputFloat("##cost", ref v, 0f, 0f, "%.0fc")) e.MaxChaos = Math.Max(0f, v);
        Controls.Tip("Chaos budget for one run. 0 means no limit. Needs the Ninja Price plugin " +
                     "loaded, otherwise currency reads as free and this never trips.");
    }

    void Actions(ExileCrafting plugin, ScriptEntry e, int index)
    {
        var missing = !_filesOnDisk.Contains(e.FileName);

        ImGui.BeginDisabled(missing);
        if (ImGui.SmallButton("Run")) plugin.StartScript(e);
        ImGui.SameLine();
        if (ImGui.SmallButton("Edit"))
            plugin.EditorDialog.OpenForEdit(e.FileName, plugin.ScriptHost.ReadScript(e.FileName));
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.SmallButton("X")) _remove = index;
        Controls.Tip($"Remove {e.Title} from the list. The .csx file stays on disk.");

        if (missing) Controls.Tip($"{e.FileName} isn't in the scripts folder any more.");
    }
}

#endregion

#region Queue popover - one job config, at the mouse

// opens where the mouse is when you mark an inventory cell: one job's whole config, then it closes.
public sealed class QueuePopover
{
    // remembered across opens so marking a run of similar items is hover, hotkey, hotkey
    static string _lastFileName = "";

    int _cell = -1;
    bool _editing;
    string _fileName = "";
    int _maxApplications = 200;
    float _maxChaos;
    string _filter = "";
    Vector2 _position;

    public void OpenFor(ExileCrafting plugin, int cell, Vector2 screenPos)
    {
        _cell = cell;
        _position = screenPos;
        _filter = "";

        var existing = InventoryGrid.JobFor(cell);
        _editing = existing != null;

        if (existing != null)
        {
            _fileName = existing.FileName;
            _maxApplications = existing.MaxApplications;
            _maxChaos = existing.MaxChaos;
            return;
        }

        _fileName = plugin.Settings.Scripts.Any(s => s.FileName == _lastFileName)
            ? _lastFileName
            : plugin.Settings.Scripts.FirstOrDefault()?.FileName ?? "";
        SeedLimits(plugin);
    }

    public void Close() => _cell = -1;

    // second press of the queue hotkey is the Queue button. eats the press either way, otherwise it
    // reopens the popover on the cell the mouse is still sitting on.
    public bool AcceptHotkey(ExileCrafting plugin)
    {
        if (_cell < 0) return false;

        if (!string.IsNullOrEmpty(_fileName)) Commit(plugin);
        return true;
    }

    public void Draw(ExileCrafting plugin)
    {
        if (_cell < 0) return;

        ImGui.SetNextWindowPos(_position, ImGuiCond.Appearing);

        var open = true;
        // windows sdk pulls in a global Windows namespace here, has to be fully qualified or it wont resolve
        if (ExileImGui.Windows.Begin($"Queue {InventoryGrid.Describe(_cell)}", "queuepopover", ref open,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse))
            DrawBody(plugin);

        ExileImGui.Windows.End();

        if (!open) Close();
    }

    void SeedLimits(ExileCrafting plugin)
    {
        var entry = plugin.Settings.Scripts.FirstOrDefault(s => s.FileName == _fileName);
        _maxApplications = entry?.MaxApplications ?? 200;
        _maxChaos = entry?.MaxChaos ?? 0f;
    }

    void DrawBody(ExileCrafting plugin)
    {
        var scripts = plugin.Settings.Scripts;
        if (scripts.Count == 0)
        {
            ImGui.TextDisabled("no scripts on the list yet - add one first.");
            if (ImGui.Button("Close")) Close();
            return;
        }

        var current = scripts.FirstOrDefault(s => s.FileName == _fileName);

        if (Combo.SearchCombo("queuescript", current?.Title ?? "pick a script",
                scripts.Select(s => (s.FileName, s.Title)), ref _filter, out var picked, 220f))
        {
            _fileName = picked;
            SeedLimits(plugin);
        }

        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputInt("Max uses", ref _maxApplications, 0))
            _maxApplications = Math.Max(0, _maxApplications);
        Controls.Tip("Hard stop after this many applications. 0 means no limit.");

        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputFloat("Max cost", ref _maxChaos, 0f, 0f, "%.0fc"))
            _maxChaos = Math.Max(0f, _maxChaos);

        ImGui.BeginDisabled(string.IsNullOrEmpty(_fileName));
        if (ImGui.Button(_editing ? "Save" : "Queue")) Commit(plugin);
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) Close();

        if (!_editing) return;

        ImGui.SameLine();
        if (ImGui.Button("Remove"))
        {
            plugin.Settings.Queue.RemoveAll(j => j.Cell == _cell);
            Close();
        }
    }

    void Commit(ExileCrafting plugin)
    {
        var job = InventoryGrid.JobFor(_cell);
        if (job == null)
        {
            job = new QueueJob { Cell = _cell };
            plugin.Settings.Queue.Add(job);
        }

        job.FileName = _fileName;
        job.MaxApplications = _maxApplications;
        job.MaxChaos = _maxChaos;
        _lastFileName = _fileName;
        Close();
    }
}

#endregion

#region Progress bars - current craft and the queue

// the two bars pinned above the craft dialog's status line: this item's budget, and the queue's items
public static class CraftProgress
{
    static readonly uint Pending = ImGui.ColorConvertFloat4ToU32(new Vector4(0.38f, 0.38f, 0.42f, 1f));
    static readonly uint Done = ImGui.ColorConvertFloat4ToU32(new Vector4(0.30f, 0.75f, 0.35f, 1f));
    static readonly uint Bad = ImGui.ColorConvertFloat4ToU32(new Vector4(0.80f, 0.25f, 0.25f, 1f));

    static readonly List<uint> Segments = new();

    // the dialog reserves this much before it sizes the scrolling body
    public static float Height(ExileCrafting plugin)
    {
        var bars = 0;
        if (ShowCraft(plugin)) bars++;
        if (ShowQueue(plugin)) bars++;
        if (bars == 0) return 0f;

        return bars * (ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.Y);
    }

    public static void Draw(ExileCrafting plugin)
    {
        if (ShowCraft(plugin)) DrawCraft(plugin.RunningContext);
        if (ShowQueue(plugin)) DrawQueue(plugin);
    }

    static bool ShowCraft(ExileCrafting plugin) => plugin.IsScriptRunning && plugin.RunningContext != null;

    static bool ShowQueue(ExileCrafting plugin) => plugin.IsScriptRunning && plugin.RunningQueue != null;

    // first limit that's switched on wins - a bar that changes what it means mid-run is worse than
    // one that only ever tracks the thing you set
    static void DrawCraft(CraftContext ctx)
    {
        if (ctx.MaxApplications > 0)
        {
            Bars.Progress(ctx.Applications / (float)ctx.MaxApplications,
                $"{ctx.Applications} / {ctx.MaxApplications} uses");
            return;
        }

        if (ctx.MaxChaos > 0)
        {
            Bars.Progress((float)(ctx.SpentChaos / ctx.MaxChaos),
                $"{ctx.SpentChaos:0.##}c / {ctx.MaxChaos:0.##}c");
            return;
        }

        if (ctx.MaxSeconds > 0)
        {
            var elapsed = ctx.Elapsed.TotalSeconds;
            Bars.Progress((float)(elapsed / ctx.MaxSeconds), $"{elapsed:0}s / {ctx.MaxSeconds}s");
            return;
        }

        Bars.Progress(-1f, "running");
    }

    static void DrawQueue(ExileCrafting plugin)
    {
        var queue = plugin.RunningQueue;
        var rows = queue.Rows;

        Segments.Clear();
        for (var i = 0; i < queue.JobCount; i++)
            Segments.Add(i < rows.Count ? ColorOf(rows[i].Result) : Pending);

        Bars.Segmented(CollectionsMarshal.AsSpan(Segments));
    }

    static uint ColorOf(string result) => result switch
    {
        "pending" => Pending,
        "done" => Done,
        _ => Bad,
    };
}

#endregion

#region Overlay - drawn over the stash or the station

// drawn over the game, anchored to whichever craft panel is open - the stash for currency crafting,
// the horticrafting station for harvest. the button hangs off the bottom right corner and the status
// line sits along the bottom of the panel itself.
public static class CraftOverlay
{
    static readonly uint AllowedColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.30f, 0.85f, 0.35f, 0.95f));
    static readonly uint BlockedColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.90f, 0.25f, 0.25f, 0.95f));
    static readonly Vector4 AllowedTint = new(0.18f, 0.45f, 0.20f, 1f);

    public static void Draw(ExileCrafting plugin)
    {
        var anchor = Anchor(plugin);
        if (anchor == null) return;

        var (rect, harvest) = anchor.Value;
        // both the element rects and the draw list start at the game window's top left, so no
        // window offset here - adding one only looked right because fullscreen puts it at 0,0
        var min = new Vector2(rect.X, rect.Y);
        var max = new Vector2(min.X + rect.Width, min.Y + rect.Height);

        // PressedOnce consumes the press, so both pickers share one read
        var toggling = plugin.IsCraftDialogOpen && plugin.Settings.ToggleSlotHotkey.PressedOnce();
        if (toggling && plugin.QueuePopover.AcceptHotkey(plugin)) toggling = false;

        // the station's bottom right corner sits way off to the side of the craft slot, so in harvest
        // the button hangs under the Craft button instead
        var underCraft = CraftButtonAnchor(harvest);
        DrawOpenButton(plugin, underCraft ?? new Vector2(max.X, max.Y + 4f),
            underCraft.HasValue ? new Vector2(0.5f, 0f) : new Vector2(1f, 0f));
        DrawStatus(plugin, min, max);
        DrawSlotPicker(plugin, toggling);
        DrawQueuePicker(plugin, toggling);
    }

    // the harvest window comes back with the rect so the button and the status line can't disagree
    // about which panel won when both are open
    static (SharpDX.RectangleF Rect, HarvestWindow Harvest)? Anchor(ExileCrafting plugin)
    {
        var stash = plugin.GameController.IngameState.IngameUi.StashElement?.StashInventoryPanel;
        if (stash != null && stash.IsVisible) return (stash.GetClientRectCache, null);

        var harvest = CraftingSlot.HarvestPanel();
        if (harvest != null) return (harvest.GetClientRectCache, harvest);

        return null;
    }

    static void DrawSlotPicker(ExileCrafting plugin, bool toggling)
    {
        if (!plugin.IsCraftDialogOpen || !plugin.Settings.RestrictedMode.Value) return;

        var visible = plugin.GameController.IngameState.IngameUi.StashElement?.VisibleStash?.VisibleInventoryItems;
        if (visible == null) return;

        var slotKeys = CraftingSlot.SlotKeysByAddress();
        var mouse = ImGui.GetMousePos();
        var draw = ImGui.GetForegroundDrawList();

        foreach (var item in visible)
        {
            if (item?.Item == null) continue;

            var r = item.GetClientRectCache;
            var min = new Vector2(r.X, r.Y);
            var max = new Vector2(min.X + r.Width, min.Y + r.Height);

            var hovered = mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
            if (toggling && hovered) CraftingSlot.ToggleAllowed(slotKeys, item);

            var allowed = CraftingSlot.IsAllowed(slotKeys, item);
            draw.AddRect(min, max, allowed ? AllowedColor : BlockedColor, 0f, ImDrawFlags.None, hovered ? 3f : 2f);
        }
    }

    static void DrawQueuePicker(ExileCrafting plugin, bool toggling)
    {
        if (!plugin.IsCraftDialogOpen) return;

        var visible = InventoryGrid.Panel()?.VisibleInventoryItems;
        if (visible == null) return;

        var keys = InventoryGrid.KeysByAddress();
        var mouse = ImGui.GetMousePos();
        var draw = ImGui.GetForegroundDrawList();

        foreach (var item in visible)
        {
            var key = InventoryGrid.KeyOf(keys, item);
            if (key < 0) continue;

            var r = item.GetClientRectCache;
            var min = new Vector2(r.X, r.Y);
            var max = new Vector2(min.X + r.Width, min.Y + r.Height);

            var hovered = mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
            if (toggling && hovered) plugin.QueuePopover.OpenFor(plugin, key, mouse);

            if (InventoryGrid.IsQueued(key))
                draw.AddRect(min, max, AllowedColor, 0f, ImDrawFlags.None, hovered ? 3f : 2f);
            else if (hovered)
                draw.AddRect(min, max, BlockedColor, 0f, ImDrawFlags.None, 2f);
        }
    }

    static Vector2? CraftButtonAnchor(HarvestWindow harvest)
    {
        var rect = harvest?.CraftButton?.GetClientRectCache;
        if (rect == null) return null;

        var r = rect.Value;
        return new Vector2(r.X + r.Width * 0.5f, r.Y + r.Height + 40f);
    }

    static void DrawOpenButton(ExileCrafting plugin, Vector2 pos, Vector2 pivot)
    {
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always, pivot);

        var open = true;
        // windows sdk pulls in a global Windows namespace here, has to be fully qualified or it wont resolve
        if (ExileImGui.Windows.Begin("Open Crafter", "craftoverlaybutton", ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            if (ImGui.Button("Open Crafter")) plugin.OpenCraftDialog();

            if (plugin.IsCraftDialogOpen)
            {
                ImGui.SameLine();
                var restricted = plugin.Settings.RestrictedMode;
                var tinted = restricted.Value;
                if (tinted) ImGui.PushStyleColor(ImGuiCol.Button, AllowedTint);
                if (ImGui.Button("Restricted Mode")) restricted.Value = !restricted.Value;
                if (tinted) ImGui.PopStyleColor();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"hover a slot, press {plugin.Settings.ToggleSlotHotkey.Value} to allow or block it");
            }
        }

        ExileImGui.Windows.End();
    }

    static void DrawStatus(ExileCrafting plugin, Vector2 panelMin, Vector2 panelMax)
    {
        var message = plugin.StatusMessage;
        if (string.IsNullOrEmpty(message)) return;

        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize();
        var textSize = font.CalcTextSizeA(size, float.MaxValue, 0f, message);
        var pos = new Vector2((panelMin.X + panelMax.X - textSize.X) * 0.5f, panelMax.Y - textSize.Y - 6f);

        RichText.Outlined(ImGui.GetForegroundDrawList(), font, size, pos, message, SColor.White);
    }
}

#endregion
