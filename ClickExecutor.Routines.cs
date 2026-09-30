using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using Humanizer;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace ExileCrafting;

// the craft actions themselves: pick up currency and apply it, drive the harvest station, and move
// items in and out of the craft slot. the engine that pumps these lives in ClickExecutor.cs.
public sealed partial class ClickExecutor
{
    // everything up to and including "currency is on the cursor, mouse is over the craft slot".
    // sets _prepared, _slot and _startAddress for the caller.
    IEnumerable PrepareAndPickUp(string currencyName)
    {
        _prepared = false;
        _slot = null;

        var log = ExileCrafting.Main.CraftLog;

        // block before touching anything if the cursor's already holding an item - either a
        // stranded currency from a cancelled/faulted previous run, or the player's own pickup.
        if (CursorHoldingAnything())
        {
            log.Add("aborted: cursor is already holding an item - drop it first.");
            yield break;
        }

        _startAddress = CraftingSlot.ReadItem().Address;

        // resolve the slot before we touch the currency - both checks below used to happen after
        // the pickup right-click, which could strand the currency on the cursor if they failed.
        var slot = CraftingSlot.FindClickableSlot();
        if (slot == null)
        {
            log.Add(_startAddress == 0
                ? $"aborted: {CraftingSlot.SlotLabel} is empty - put an item in it."
                : $"aborted: the item in the {CraftingSlot.SlotLabel} isn't visible on screen.");
            yield break;
        }

        var currencyItem = CraftingSlot.FindCurrency(currencyName);
        if (currencyItem == null) yield break;

        foreach (var step in MoveMouseTo(AimAt(currencyItem.GetClientRectCache))) yield return step;

        foreach (var step in Poll(() => IsHovering(currencyItem.Item.Address), HoverConfirmTimeoutMs)) yield return step;
        if (!_pollResult)
        {
            log.Add($"aborted: mouse never landed on the {currencyName} stack.");
            yield break;
        }

        foreach (var step in ClickButton(MouseButtons.Right)) yield return step;

        foreach (var step in Poll(() => IsCursorHolding(currencyName), CursorConfirmTimeoutMs)) yield return step;
        if (!_pollResult)
        {
            log.Add($"aborted: right-click didn't put {currencyName} on the cursor.");
            yield break;
        }

        if (CraftingSlot.ReadItem().Address != _startAddress)
        {
            log.Add($"aborted: the {CraftingSlot.SlotLabel} item changed while picking up currency.");
            yield break;
        }

        // slot was resolved before the pickup - re-check it still points at the same item.
        if (slot.Item?.Address != _startAddress)
        {
            log.Add($"aborted: the {CraftingSlot.SlotLabel} no longer points at the target item.");
            yield break;
        }

        foreach (var step in MoveMouseTo(AimAt(slot.GetClientRectCache))) yield return step;

        foreach (var step in Poll(() => CraftingSlot.IsHoveringSlot(slot.Item.Address), HoverConfirmTimeoutMs)) yield return step;
        if (!_pollResult)
        {
            log.Add($"aborted: mouse never landed on the {CraftingSlot.SlotLabel}.");
            yield break;
        }

        _slot = slot;
        _prepared = true;
    }

    IEnumerator ApplyCurrencyRoutine(string currencyName)
    {
        _result = false;
        LastRunApplied = 0;

        foreach (var step in PrepareAndPickUp(currencyName)) yield return step;
        if (!_prepared) yield break;

        foreach (var step in ClickButton(MouseButtons.Left)) yield return step;

        foreach (var step in Poll(() => SlotSettledOn(_startAddress), ApplyResultTimeoutMs)) yield return step;
        _result = _pollResult;
        if (_result) LastRunApplied = 1;
    }

    // shift-click keeps the currency on the cursor between applications, so the mouse never has to
    // travel back to the stack. one pickup, then click the same pixel until done() says stop.
    IEnumerator ApplyCurrencyUntilRoutine(string currencyName, Func<bool> done, Action charge)
    {
        _result = false;
        LastRunApplied = 0;

        var log = ExileCrafting.Main.CraftLog;

        // check before the pickup so a already-satisfied condition never touches the currency
        if (done())
        {
            _result = true;
            yield break;
        }

        foreach (var step in PrepareAndPickUp(currencyName)) yield return step;
        if (!_prepared) yield break;

        var applied = 0;

        // finally, not a plain KeyUp at the end - a guard throw or an abort must not leave shift held.
        // Tick disposes the routine on cancel so this still runs there.
        try
        {
            Input.KeyDown(Keys.ShiftKey);

            while (true)
            {
                charge();

                var before = CraftingSlot.ReadItem().Address;

                if (applied > 0) Jiggle();

                foreach (var step in ClickButton(MouseButtons.Left)) yield return step;

                foreach (var step in Poll(() => SlotSettledOn(before), ApplyResultTimeoutMs)) yield return step;
                if (!_pollResult)
                {
                    log.Add($"stopped after {applied}: the {currencyName} click didn't change the item.");
                    break;
                }

                applied++;
                LastRunApplied = applied;

                if (done())
                {
                    _result = true;
                    break;
                }

                if (!IsCursorHolding(currencyName))
                {
                    log.Add($"stopped after {applied}: ran out of {currencyName}.");
                    break;
                }

                // the one pause per roll: looking at what came up before clicking again
                var s = ExileCrafting.Main.Settings;
                yield return new WaitTime(s.RollDelay(s.ActionDelayMin, s.ActionDelayMax));
            }
        }
        finally
        {
            // letting shift go is also what drops the stack off the cursor, so this is the release
            Input.KeyUp(Keys.ShiftKey);
        }

        log.Add($"{currencyName} x{applied}.");
    }

    // alch and scour without a second pickup. shift holds the orb on the cursor, alt swaps which
    // orb it is. letting shift go is what ends the whole use, so it goes down once and up once.
    IEnumerator ApplyPairUntilRoutine(string primary, string alternate, Func<bool> useAlternate,
        Func<bool> done, Action<string> charge, Action<string> applied)
    {
        _result = false;
        _alternate = alternate;
        _altHeld = false;

        var log = ExileCrafting.Main.CraftLog;

        if (done())
        {
            _result = true;
            yield break;
        }

        foreach (var step in PrepareAndPickUp(primary)) yield return step;
        if (!_prepared) yield break;

        var applications = 0;

        try
        {
            Input.KeyDown(Keys.ShiftKey);

            while (true)
            {
                var want = useAlternate() ? alternate : primary;

                foreach (var step in SetCursorTo(want)) yield return step;
                if (!_pollResult)
                {
                    log.Add($"stopped after {applications}: the alt swap didn't put {want} on the " +
                            $"cursor - {primary} and {alternate} may not be a pair, or you're out of {want}.");
                    break;
                }

                charge(want);

                var before = CraftingSlot.ReadItem().Address;

                if (applications > 0) Jiggle();

                foreach (var step in ClickButton(MouseButtons.Left)) yield return step;

                foreach (var step in Poll(() => SlotSettledOn(before), ApplyResultTimeoutMs)) yield return step;
                if (!_pollResult)
                {
                    log.Add($"stopped after {applications}: the {want} click didn't change the item.");
                    break;
                }

                applications++;
                applied(want);

                if (done())
                {
                    _result = true;
                    break;
                }

                if (!CursorHoldingAnything())
                {
                    log.Add($"stopped after {applications}: ran out of currency.");
                    break;
                }

                var s = ExileCrafting.Main.Settings;
                yield return new WaitTime(s.RollDelay(s.ActionDelayMin, s.ActionDelayMax));
            }
        }
        finally
        {
            // alt first: letting shift go is what drops the orb, and that must never happen mid-swap
            if (_altHeld)
            {
                Input.KeyUp(AltKey);
                _altHeld = false;
            }

            Input.KeyUp(Keys.ShiftKey);
        }

        log.Add($"{primary} and {alternate} x{applications}.");
    }

    // every modifier change is confirmed on the cursor before anything gets clicked
    IEnumerable SetCursorTo(string want)
    {
        if (IsCursorHolding(want))
        {
            _pollResult = true;
            yield break;
        }

        if (want == _alternate)
        {
            Input.KeyDown(AltKey);
            _altHeld = true;
        }
        else
        {
            Input.KeyUp(AltKey);
            _altHeld = false;
        }

        yield return new WaitTime(OffClickDwellMs);

        foreach (var step in Poll(() => IsCursorHolding(want), AltSwapTimeoutMs)) yield return step;
    }

    IEnumerator ApplyHarvestRoutine(string craftName)
    {
        _result = false;
        LastRunApplied = 0;

        var log = ExileCrafting.Main.CraftLog;

        if (CraftingSlot.Mode != CraftType.Harvest)
        {
            log.Add("aborted: ApplyHarvest needs a script with '// Type: Harvest' in its header.");
            yield break;
        }

        var window = CraftingSlot.HarvestPanel();
        if (window?.CraftButton == null)
        {
            log.Add("aborted: the horticrafting station window isn't open.");
            yield break;
        }

        if (CursorHoldingAnything())
        {
            log.Add("aborted: cursor is already holding an item - drop it first.");
            yield break;
        }

        if (CraftingSlot.ReadItem().IsEmpty)
        {
            log.Add($"aborted: the {CraftingSlot.SlotLabel} is empty - put an item in it.");
            yield break;
        }

        // a spent craft shows up as the filtered row vanishing, so a stale selection re-searches
        if (_selectedCraft != craftName || !window.CraftButton.IsActive || VisibleCraft(window, craftName) == null)
        {
            foreach (var step in SelectHarvestCraft(window, craftName)) yield return step;
            if (_selectedCraft != craftName) yield break;
        }

        foreach (var step in MoveMouseTo(AimAt(window.CraftButton.GetClientRectCache))) yield return step;

        for (var attempt = 1; attempt <= HarvestClickAttempts; attempt++)
        {
            if (VisibleCraft(window, craftName) == null || !window.CraftButton.IsActive)
            {
                log.Add($"aborted: '{craftName}' isn't selected in the station any more.");
                _selectedCraft = null;
                yield break;
            }

            var before = CraftingSlot.ReadItem().Address;

            if (attempt > 1) Jiggle();

            foreach (var step in ClickButton(MouseButtons.Left)) yield return step;

            foreach (var step in Poll(() => SlotSettledOn(before), ApplyResultTimeoutMs)) yield return step;
            if (_pollResult)
            {
                _result = true;
                LastRunApplied = 1;
                yield break;
            }

            log.Add($"the Craft click didn't change the item ({attempt}/{HarvestClickAttempts}).");

            if (attempt < HarvestClickAttempts)
            {
                var s = ExileCrafting.Main.Settings;
                yield return new WaitTime(s.RollDelay(s.ActionDelayMin, s.ActionDelayMax));
            }
        }

        log.Add($"stopped: the item didn't change after {HarvestClickAttempts} clicks - out of " +
                "lifeforce, or the craft came unselected.");
        _selectedCraft = null;
    }

    // ctrl+F filters the craft list, and everything that doesn't match stops reporting IsVisible.
    // without a filter every craft reads visible, including ones scrolled far off screen.
    IEnumerable SelectHarvestCraft(HarvestWindow window, string craftName)
    {
        var log = ExileCrafting.Main.CraftLog;
        _selectedCraft = null;

        // right-click the item in the craft slot: it does nothing but hands the window focus. a blind
        // click on empty UI reads as a move click, and that closes the station.
        var slot = CraftingSlot.FindClickableSlot();
        if (slot == null)
        {
            log.Add($"aborted: nothing in the {CraftingSlot.SlotLabel} to focus on.");
            yield break;
        }

        foreach (var step in MoveMouseTo(AimAt(slot.GetClientRect()))) yield return step;
        foreach (var step in ClickButton(MouseButtons.Right)) yield return step;

        // a frame for the game to register a pickup - with the humanizer off the click has no settle
        yield return new WaitTime(OffClickDwellMs);

        if (CursorHoldingAnything())
        {
            // put it straight back rather than stranding it mid-run
            foreach (var step in ClickButton(MouseButtons.Left)) yield return step;
            foreach (var step in Poll(() => !CursorHoldingAnything(), CursorConfirmTimeoutMs)) yield return step;

            log.Add(_pollResult
                ? "aborted: the focus right-click picked the item up and put it back."
                : "aborted: the focus right-click picked the item up and it's still on your cursor - drop it.");
            yield break;
        }

        yield return new WaitTime(HarvestStepDelayMs);

        // ctrl+F has to land and focus the box before ctrl+A selects what's in it
        foreach (var step in PressWithControl(Keys.F)) yield return step;
        yield return new WaitTime(HarvestStepDelayMs);

        // the box keeps the last run's text, and a stale filter can satisfy the one-match check below
        foreach (var step in PressWithControl(Keys.A)) yield return step;
        yield return new WaitTime(HarvestStepDelayMs);

        // the craft name is left on the clipboard. reading the old one back to restore it is what
        // used to crash here: imgui returns NULL for an empty clipboard and ImGui.NET derefs it.
        ImGui.SetClipboardText(craftName);
        yield return new WaitTime(HarvestStepDelayMs);

        foreach (var step in PressWithControl(Keys.V)) yield return step;
        foreach (var step in Poll(() => VisibleCrafts(window).Count == 1, HarvestFilterTimeoutMs)) yield return step;

        var visible = VisibleCrafts(window);

        if (visible.Count == 0)
        {
            log.Add($"aborted: no harvest craft matched '{craftName}'.");
            yield break;
        }

        if (visible.Count > 1)
        {
            log.Add($"aborted: '{craftName}' matched {visible.Count} crafts - narrow the search text.");
            foreach (var c in visible) log.Add($"  {c.CraftDisplayName}");
            yield break;
        }

        if (!NameMatches(visible[0], craftName))
        {
            log.Add($"aborted: the only craft showing is '{visible[0].CraftDisplayName}', " +
                    $"which doesn't contain '{craftName}'.");
            yield break;
        }

        // fresh rect, not the cached one - the filter re-lays out the list right before this
        var rowRect = visible[0].GetClientRect();
        foreach (var step in MoveMouseTo(AimAt(rowRect))) yield return step;

        // tolerance, not exact equality - parent-chain float math jitters by a fraction of a pixel
        if (Vector2.Distance(Center(visible[0].GetClientRect()), Center(rowRect)) > 2f)
        {
            log.Add("aborted: the craft row moved while the mouse was travelling to it.");
            yield break;
        }

        foreach (var step in ClickButton(MouseButtons.Left)) yield return step;

        foreach (var step in Poll(() => window.CraftButton.IsActive, CursorConfirmTimeoutMs)) yield return step;
        if (!_pollResult)
        {
            log.Add("aborted: clicking the craft didn't activate the Craft button.");
            yield break;
        }

        log.Add($"selected '{visible[0].CraftDisplayName}'.");
        _selectedCraft = craftName;
    }

    static List<HarvestCraftElement> VisibleCrafts(HarvestWindow window) =>
        window.Crafts?.Where(c => c != null && c.IsVisible).ToList() ?? new List<HarvestCraftElement>();

    static HarvestCraftElement VisibleCraft(HarvestWindow window, string craftName)
    {
        var visible = VisibleCrafts(window);
        return visible.Count == 1 && NameMatches(visible[0], craftName) ? visible[0] : null;
    }

    static bool NameMatches(HarvestCraftElement craft, string craftName) =>
        (craft.CraftDisplayName ?? "").IndexOf(craftName ?? "", StringComparison.OrdinalIgnoreCase) >= 0;

    IEnumerator TakeRoutine(int cellKey)
    {
        _result = false;

        var log = ExileCrafting.Main.CraftLog;

        if (CursorHoldingAnything())
        {
            log.Add("aborted: cursor is already holding an item - drop it first.");
            yield break;
        }

        // without this the ctrl+click still fires and the game puts the item in whatever panel is open
        if (CraftingSlot.Mode == CraftType.Harvest && CraftingSlot.HarvestPanel() == null)
        {
            log.Add("aborted: the horticrafting station window isn't open.");
            yield break;
        }

        if (!CraftingSlot.ReadItem().IsEmpty)
        {
            log.Add($"aborted: the {CraftingSlot.SlotLabel} isn't empty.");
            yield break;
        }

        var item = InventoryGrid.VisibleItemAt(cellKey);
        if (item?.Item == null)
        {
            log.Add($"aborted: nothing visible at {InventoryGrid.Describe(cellKey)}.");
            yield break;
        }

        var address = item.Item.Address;
        LastOriginRect = item.GetClientRectCache;

        foreach (var step in MoveMouseTo(AimAt(LastOriginRect))) yield return step;

        foreach (var step in Poll(() => IsHovering(address), HoverConfirmTimeoutMs)) yield return step;
        if (!_pollResult)
        {
            log.Add($"aborted: mouse never landed on the item at {InventoryGrid.Describe(cellKey)}.");
            yield break;
        }

        // an empty slot has no rect to aim at, so ctrl+click lets the game do the placing
        try
        {
            Input.KeyDown(Keys.ControlKey);
            foreach (var step in ClickButton(MouseButtons.Left)) yield return step;
        }
        finally
        {
            Input.KeyUp(Keys.ControlKey);
        }

        foreach (var step in Poll(() => !CraftingSlot.ReadItem().IsEmpty && InventoryGrid.IsCellEmpty(cellKey),
                     MoveResultTimeoutMs)) yield return step;
        if (!_pollResult)
        {
            log.Add($"aborted: the item from {InventoryGrid.Describe(cellKey)} didn't move into the {CraftingSlot.SlotLabel}.");
            yield break;
        }

        _result = true;
    }

    IEnumerator ReturnRoutine(int cellKey, RectangleF originRect)
    {
        _result = false;

        var log = ExileCrafting.Main.CraftLog;

        if (CursorHoldingAnything())
        {
            log.Add("aborted: cursor is already holding an item - drop it first.");
            yield break;
        }

        var slot = CraftingSlot.FindClickableSlot();
        if (slot == null)
        {
            log.Add($"aborted: the item in the {CraftingSlot.SlotLabel} isn't visible on screen.");
            yield break;
        }

        var address = slot.Item.Address;

        foreach (var step in MoveMouseTo(AimAt(slot.GetClientRectCache))) yield return step;

        foreach (var step in Poll(() => CraftingSlot.IsHoveringSlot(address), HoverConfirmTimeoutMs)) yield return step;
        if (!_pollResult)
        {
            log.Add($"aborted: mouse never landed on the {CraftingSlot.SlotLabel}.");
            yield break;
        }

        foreach (var step in ClickButton(MouseButtons.Left)) yield return step;

        foreach (var step in Poll(() => InventoryGrid.CursorHeldAddress() != 0 && CraftingSlot.ReadItem().IsEmpty,
                     CursorConfirmTimeoutMs)) yield return step;
        if (!_pollResult)
        {
            log.Add("aborted: the crafted item didn't land on the cursor.");
            yield break;
        }

        if (!InventoryGrid.IsCellEmpty(cellKey))
        {
            log.Add($"aborted: {InventoryGrid.Describe(cellKey)} isn't free any more - the item is on the cursor.");
            yield break;
        }

        foreach (var step in MoveMouseTo(AimAt(originRect))) yield return step;

        foreach (var step in ClickButton(MouseButtons.Left)) yield return step;

        foreach (var step in Poll(() => !InventoryGrid.IsCellEmpty(cellKey) && InventoryGrid.CursorHeldAddress() == 0,
                     MoveResultTimeoutMs)) yield return step;
        if (!_pollResult)
        {
            log.Add($"aborted: the item didn't land back in {InventoryGrid.Describe(cellKey)}.");
            yield break;
        }

        _result = true;
    }
}
