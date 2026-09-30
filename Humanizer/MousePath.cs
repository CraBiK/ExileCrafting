using System;
using System.Collections.Generic;
using System.Numerics;

namespace Humanizer;

public readonly record struct Waypoint(Vector2 Point, int DelayMs);

public sealed class HumanizerConfig
{
    public float Gravity = 9f;   // floored against MaxStep, see WindMouse
    public float Wind = 3f;
    public float MaxStep = 15f;
    public float SlowDistance = 8f;

    public int StepDelayMin = 4;
    public int StepDelayMax = 9;

    public int PreClickSettleMin = 20, PreClickSettleMax = 60;
    public int ClickDwellMin = 25, ClickDwellMax = 45;
    public int PostClickSettleMin = 30, PostClickSettleMax = 90;
}

public static class MousePath
{
    static readonly float Sqrt3 = MathF.Sqrt(3f);
    static readonly float Sqrt5 = MathF.Sqrt(5f);

    // minimum gravity per unit of MaxStep, below which the path stops converging (see WindMouse)
    const float GravityPerMaxStep = 0.6f;

    // no overshoot - precision matters more than looking human here, so we walk straight to the target.
    public static IReadOnlyList<Waypoint> Generate(Vector2 from, Vector2 to, HumanizerConfig cfg, Random rng)
    {
        var pts = new List<Waypoint>();
        WindMouse(from, to, cfg, rng, pts);
        pts.Add(new Waypoint(to, StepDelay(cfg, rng)));
        return pts;
    }

    static void WindMouse(Vector2 start, Vector2 end, HumanizerConfig cfg, Random rng, List<Waypoint> outPts)
    {
        float windX = 0f, windY = 0f, veloX = 0f, veloY = 0f;
        float maxStep = cfg.MaxStep;
        float curX = start.X, curY = start.Y;
        float dist;

        // gravity is the only thing steering back at the target and momentum gets a whole maxStep per
        // step, so letting maxStep outrun gravity makes the cursor orbit the target for laps instead of
        // closing on it. 0.6 is where the stock 9/15 pairing sits, so a default config is unchanged.
        float gravity = MathF.Max(cfg.Gravity, cfg.MaxStep * GravityPerMaxStep);

        int budget = 10_000;
        while ((dist = Dist(curX, curY, end.X, end.Y)) >= 1f && budget-- > 0)
        {
            float w = MathF.Min(cfg.Wind, dist);
            if (dist >= cfg.SlowDistance)
            {
                windX = windX / Sqrt3 + (2f * (float)rng.NextDouble() - 1f) * w / Sqrt5;
                windY = windY / Sqrt3 + (2f * (float)rng.NextDouble() - 1f) * w / Sqrt5;
            }
            else
            {
                windX /= Sqrt3;
                windY /= Sqrt3;
                if (maxStep < 3f) maxStep = (float)rng.NextDouble() * 3f + 3f;
                else maxStep /= Sqrt5;
            }

            veloX += windX + gravity * (end.X - curX) / dist;
            veloY += windY + gravity * (end.Y - curY) / dist;

            float cap = MathF.Min(maxStep, dist);
            float veloMag = MathF.Sqrt(veloX * veloX + veloY * veloY);
            if (veloMag > cap)
            {
                float randStep = cap / 2f + (float)rng.NextDouble() * cap / 2f;
                veloX = veloX / veloMag * randStep;
                veloY = veloY / veloMag * randStep;
            }

            curX += veloX;
            curY += veloY;
            outPts.Add(new Waypoint(new Vector2(MathF.Round(curX), MathF.Round(curY)), StepDelay(cfg, rng)));
        }
    }

    static int StepDelay(HumanizerConfig cfg, Random rng) => rng.Next(cfg.StepDelayMin, cfg.StepDelayMax + 1);
    static float Dist(float ax, float ay, float bx, float by) => MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
}
