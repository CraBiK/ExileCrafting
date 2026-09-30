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

public sealed partial class ClickExecutor
{
    const int HoverConfirmTimeoutMs = 2000;
    const int CursorConfirmTimeoutMs = 2000;
    const int ApplyResultTimeoutMs = 3000;
    const int MoveResultTimeoutMs = 3000;
    const int PollIntervalMs = 10;
    const int MaxCarryMs = 20;
    const int OffClickDwellMs = 25;
    const int HarvestClickAttempts = 3;
    const int HarvestFilterTimeoutMs = 2000;

    // the station's search box drops input that arrives too fast: ctrl+F has to land and focus the
    // box before ctrl+A, and the clipboard write has to settle before ctrl+V. 500 was proven in game,
    // these are the trimmed values - raise them again if a paste ever comes back empty.
    const int HarvestStepDelayMs = 250;
    const int HarvestKeyDelayMs = 120;
    const int AltSwapTimeoutMs = 2000;

    // the generic alt, matching how ControlKey and ShiftKey are used above. if the swap never
    // lands in game, Keys.LMenu is the other one to try.
    const Keys AltKey = Keys.Menu;

    const float AimSpread = 0.5f;
    const float JiggleRadius = 2f;

    readonly Random _rng = new();

    // where the last MoveMouseTo aimed - jiggle nudges around this instead of the live cursor,
    // so a hundred shift-clicks don't random-walk off the slot
    Vector2 _aimPoint;

    IEnumerator _routine;
    TaskCompletionSource<bool> _tcs;
    CancellationToken _token;
    int _waitRemainingMs;
    bool _result;
    bool _pollResult;

    // pause holds off the next action instead of freezing this one - stopping mid-routine could
    // leave the mouse button or shift held down
    bool _routineStarted;

    // set by PrepareAndPickUp, read by whichever routine called it
    NormalInventoryItem _slot;
    long _startAddress;
    bool _prepared;

    // set for the length of one pair run, so the alt toggle knows which name means "press alt"
    string _alternate;
    bool _altHeld;

    // which craft is selected in the station right now, so repeat calls are one click and no search
    string _selectedCraft;

    // how many applications actually landed in the run that just finished
    public int LastRunApplied { get; private set; }

    public Task<bool> ApplyCurrency(string currencyName, CancellationToken token) =>
        Start(token, ApplyCurrencyRoutine(currencyName));

    public Task<bool> ApplyCurrencyUntil(string currencyName, Func<bool> done, Action charge, CancellationToken token) =>
        Start(token, ApplyCurrencyUntilRoutine(currencyName, done, charge));

    public Task<bool> ApplyPairUntil(string primary, string alternate, Func<bool> useAlternate,
        Func<bool> done, Action<string> charge, Action<string> applied, CancellationToken token) =>
        Start(token, ApplyPairUntilRoutine(primary, alternate, useAlternate, done, charge, applied));

    public Task<bool> ApplyHarvest(string craftName, CancellationToken token) =>
        Start(token, ApplyHarvestRoutine(craftName));

    // the station keeps its selection between items, but a fresh item re-verifies from scratch
    public void ResetHarvestSelection() => _selectedCraft = null;

    // the rect the last taken item occupied - the queue keeps it so the return lands on the exact
    // block the item came out of, which is what makes 2x3 and 2x4 items go back straight
    public RectangleF LastOriginRect { get; private set; }

    // true while a click routine is mid-flight, which is the one time ctx.Ask cannot be served
    public bool Busy => _routine != null;

    // a plain pause, pumped through Tick like everything else - the queue can't await Task.Delay,
    // that resumes on a threadpool thread where game-state reads aren't safe
    public Task<bool> Wait(int milliseconds, CancellationToken token) =>
        Start(token, WaitRoutine(milliseconds));

    public Task<bool> TakeIntoCraftingSlot(int cellKey, CancellationToken token) =>
        Start(token, TakeRoutine(cellKey));

    public Task<bool> ReturnToInventory(int cellKey, RectangleF originRect, CancellationToken token) =>
        Start(token, ReturnRoutine(cellKey, originRect));

    Task<bool> Start(CancellationToken token, IEnumerator routine)
    {
        if (Environment.CurrentManagedThreadId != ExileCrafting.Main.MainThreadId)
            throw new InvalidOperationException(
                "A craft action was called off the main thread. This usually means the script awaited " +
                "something other than a ctx call (e.g. Task.Delay) - that resumes on a threadpool " +
                "thread, and ExileCore's game state isn't safe to read from there. Only await " +
                "ctx.ApplyCurrency / ctx.ApplyHarvest / ctx.CheckMods in crafting scripts, not other " +
                "Task-returning calls.");

        if (_routine != null)
            throw new InvalidOperationException("ClickExecutor already has an operation in flight.");

        // deliberately not RunContinuationsAsynchronously: Tick() below completes this TCS from the
        // main thread, and we want the script's next line to resume synchronously right there, not
        // hop to a threadpool thread where ExileCore's game-state reads aren't safe to call from.
        _tcs = new TaskCompletionSource<bool>();
        _token = token;
        _waitRemainingMs = 0;
        _routineStarted = false;
        _routine = routine;
        return _tcs.Task;
    }

    public void Tick(int deltaMs)
    {
        if (_routine == null) return;

        if (_token.IsCancellationRequested)
        {
            var cancelledTcs = _tcs;
            var cancelled = _routine;
            _routine = null;
            _tcs = null;
            // Dispose runs the routine's finally blocks - without it an aborted shift-click run
            // leaves the shift key held down.
            (cancelled as IDisposable)?.Dispose();
            cancelledTcs.TrySetCanceled(_token);
            return;
        }

        if (!_routineStarted && ExileCrafting.Main.IsPaused) return;
        _routineStarted = true;

        _waitRemainingMs -= deltaMs;
        if (_waitRemainingMs > 0) return;
        // a frame hitch must not bank credit against the next wait, only one frame of it carries
        if (_waitRemainingMs < -MaxCarryMs) _waitRemainingMs = -MaxCarryMs;

        bool hasNext;
        try
        {
            hasNext = _routine.MoveNext();
        }
        catch (Exception e)
        {
            var faultedTcs = _tcs;
            _routine = null;
            _tcs = null;
            faultedTcs.TrySetException(e);
            return;
        }

        if (!hasNext)
        {
            var doneTcs = _tcs;
            var result = _result;
            _routine = null;
            _tcs = null;
            doneTcs.TrySetResult(result);
            return;
        }

        // carry the negative leftover - without it every wait rounds up to the next frame
        _waitRemainingMs += _routine.Current is WaitTime wait ? wait.Milliseconds : PollIntervalMs;
    }

    // finally, not a plain KeyUp - a cancel disposes the routine mid-flight and must not leave ctrl held
    IEnumerable PressWithControl(Keys key)
    {
        try
        {
            Input.KeyDown(Keys.ControlKey);
            yield return new WaitTime(HarvestKeyDelayMs);

            // held, not KeyPressRelease - a press and release in the same instant is one the game can miss
            Input.KeyDown(key);
            yield return new WaitTime(HarvestKeyDelayMs);
            Input.KeyUp(key);
            // ctrl stays down for this last gap, so the game never sees the key release as unmodified
            yield return new WaitTime(HarvestKeyDelayMs);
        }
        finally
        {
            Input.KeyUp(Keys.ControlKey);
        }

        yield return new WaitTime(OffClickDwellMs);
    }

    IEnumerator WaitRoutine(int milliseconds)
    {
        _result = true;
        yield return new WaitTime(milliseconds);
    }

    // wall clock, not a count of intervals - a poll step really costs a frame, not PollIntervalMs
    IEnumerable Poll(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (condition())
            {
                _pollResult = true;
                yield break;
            }
            if (Environment.TickCount64 >= deadline) break;
            yield return new WaitTime(PollIntervalMs);
        }
        _pollResult = false;
    }

    IEnumerable MoveMouseTo(Vector2 target)
    {
        _aimPoint = target;

        if (!ExileCrafting.Main.Settings.HumanizerEnabled)
        {
            Input.SetCursorPos(target);
            yield break;
        }

        foreach (var step in Drive(new HumanInput(HumanizerConfigFromSettings(), _rng).MoveTo(target)))
            yield return step;
    }

    IEnumerable ClickButton(MouseButtons button)
    {
        if (!ExileCrafting.Main.Settings.HumanizerEnabled)
        {
            // one frame of hold even with the humanizer off - a press and release in the same
            // frame is one the game can miss entirely
            if (button == MouseButtons.Right) Input.RightDown(); else Input.LeftDown();
            yield return new WaitTime(OffClickDwellMs);
            if (button == MouseButtons.Right) Input.RightUp(); else Input.LeftUp();
            yield break;
        }

        foreach (var step in Drive(new HumanInput(HumanizerConfigFromSettings(), _rng).Click(button)))
            yield return step;
    }

    // read fresh each move so tweaking the sliders takes effect without a reload
    static HumanizerConfig HumanizerConfigFromSettings()
    {
        var s = ExileCrafting.Main.Settings;
        return new HumanizerConfig
        {
            Gravity = s.Gravity.Value,
            Wind = s.Wind.Value,
            MaxStep = s.MaxStep.Value,
            SlowDistance = s.SlowDistance.Value,
            StepDelayMin = s.StepDelayMin.Value,
            StepDelayMax = s.StepDelayMax.Value,
            PreClickSettleMin = s.PreClickSettleMin.Value,
            PreClickSettleMax = s.PreClickSettleMax.Value,
            ClickDwellMin = s.ClickDwellMin.Value,
            ClickDwellMax = s.ClickDwellMax.Value,
            PostClickSettleMin = s.PostClickSettleMin.Value,
            PostClickSettleMax = s.PostClickSettleMax.Value,
        };
    }

    // an iterator handed back as IEnumerator gets no dispose from a while loop, so route it through
    // this - without it a cancelled click never runs its finally and the mouse button stays down
    static IEnumerable Drive(IEnumerator inner)
    {
        try
        {
            while (inner.MoveNext()) yield return inner.Current;
        }
        finally
        {
            (inner as IDisposable)?.Dispose();
        }
    }

    // anywhere in the middle half of the rect, not dead center every single time
    Vector2 AimAt(RectangleF rect)
    {
        var x = rect.X + rect.Width / 2f + (float)(_rng.NextDouble() - 0.5) * rect.Width * AimSpread;
        var y = rect.Y + rect.Height / 2f + (float)(_rng.NextDouble() - 0.5) * rect.Height * AimSpread;
        return ToScreen(new Vector2(x, y));
    }

    // a couple pixels off the aim point between repeat clicks - a hand doesn't hold perfectly still
    void Jiggle()
    {
        if (_aimPoint == default) return;
        Input.SetCursorPos(new Vector2(
            _aimPoint.X + (float)(_rng.NextDouble() - 0.5) * 2f * JiggleRadius,
            _aimPoint.Y + (float)(_rng.NextDouble() - 0.5) * 2f * JiggleRadius));
    }

    static Vector2 Center(RectangleF rect) =>
        ToScreen(new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f));

    // GetClientRectCache is game-window-relative but Input.SetCursorPos is absolute screen coords -
    // add the window's top-left so every caller gets a point that's actually clickable in windowed
    // mode or on a secondary monitor.
    static Vector2 ToScreen(Vector2 windowRelative)
    {
        var offset = ExileCrafting.Main.GameController.Window.GetWindowRectangle().TopLeft;
        return new Vector2(windowRelative.X + offset.X, windowRelative.Y + offset.Y);
    }

    // the slot reads as empty for a frame or two while the game swaps the item, so "the address
    // changed" on its own is true mid-refresh - wait for a real item to be sitting there.
    static bool SlotSettledOn(long previousAddress)
    {
        var now = CraftingSlot.ReadItem();
        return !now.IsEmpty && now.Address != previousAddress;
    }

    static bool IsHovering(long entityAddress) =>
        ExileCrafting.Main.GameController.IngameState.UIHoverElement?.Entity?.Address == entityAddress;

    static bool CursorHoldingAnything()
    {
        var cursor = ExileCrafting.Main.GameController.IngameState.IngameUi.Cursor;
        return cursor != null && (cursor.Action == MouseActionType.HoldItem || cursor.Action == MouseActionType.UseItem);
    }

    static bool IsCursorHolding(string baseName)
    {
        var cursor = ExileCrafting.Main.GameController.IngameState.IngameUi.Cursor;
        if (cursor == null) return false;
        if (cursor.Action != MouseActionType.HoldItem && cursor.Action != MouseActionType.UseItem) return false;
        return cursor.ItemType?.BaseName == baseName;
    }
}
