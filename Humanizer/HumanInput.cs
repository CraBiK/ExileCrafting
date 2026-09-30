using System;
using System.Collections;
using System.Numerics;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared;

namespace Humanizer;

public sealed class HumanInput
{
    readonly HumanizerConfig _cfg;
    readonly Random _rng;

    public HumanInput(HumanizerConfig cfg, Random rng)
    {
        _cfg = cfg ?? new HumanizerConfig();
        _rng = rng ?? new Random();
    }

    // absolute screen coords, read from the OS cursor so a move started anywhere lands correctly.
    public IEnumerator MoveTo(Vector2 target)
    {
        var cur = Cursor.Position;
        var from = new Vector2(cur.X, cur.Y);
        foreach (var wp in MousePath.Generate(from, target, _cfg, _rng))
        {
            Input.SetCursorPos(wp.Point);
            yield return new WaitTime(wp.DelayMs);
        }
    }

    public IEnumerator Click(MouseButtons button)
    {
        yield return new WaitTime(Range(_cfg.PreClickSettleMin, _cfg.PreClickSettleMax));

        // finally, not a plain Up - a cancel disposes this mid-dwell and must not leave the button held
        try
        {
            Down(button);
            yield return new WaitTime(Range(_cfg.ClickDwellMin, _cfg.ClickDwellMax));
        }
        finally
        {
            Up(button);
        }

        yield return new WaitTime(Range(_cfg.PostClickSettleMin, _cfg.PostClickSettleMax));
    }

    public IEnumerator ClickAt(Vector2 target, MouseButtons button)
    {
        var move = MoveTo(target);
        while (move.MoveNext()) yield return move.Current;
        var click = Click(button);
        while (click.MoveNext()) yield return click.Current;
    }

    int Range(int min, int max) => _rng.Next(min, max + 1);

    static void Down(MouseButtons b) { if (b == MouseButtons.Right) Input.RightDown(); else Input.LeftDown(); }
    static void Up(MouseButtons b) { if (b == MouseButtons.Right) Input.RightUp(); else Input.LeftUp(); }
}
