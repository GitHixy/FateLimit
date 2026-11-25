using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace FateLimit.Services;

/// <summary>
/// Works out which parts of a FATE's ring the camera can actually see, so borders don't show
/// through hills, walls and rocks. A ray is cast from the camera to sample points along the ring
/// every tick (within a budget); the renderer fades the ring out where those rays hit terrain.
/// </summary>
public sealed class OcclusionService
{
    /// <summary>Raycasts allowed per framework tick across all FATEs.</summary>
    private const int Budget = 160;
    /// <summary>Yalms between line-of-sight samples along the ring.</summary>
    private const float SampleSpacing = 1.5f;
    /// <summary>Samples farther than this from the camera aren't tested and count as visible.</summary>
    private const float MaxDistance = 110f;
    /// <summary>Rings not drawn for this many seconds are forgotten.</summary>
    private const double Expiry = 10;
    /// <summary>How fast, per second, a part of the ring fades out once hidden and back in once seen.</summary>
    private const float FadeOut = 7f;
    private const float FadeIn = 9f;

    private sealed class Entry
    {
        public required Vector3 Center;
        public required float Radius;
        public required int Segments;
        public required int Samples;
        /// <summary>Ground heights shared with <see cref="TerrainService"/>, or null for a flat ring.</summary>
        public float[]? Heights;
        /// <summary>Height above the ground the rays aim at, so a raised style counts as seen when its top is.</summary>
        public float Lift;
        /// <summary>Per sample: true when the last ray to it hit something first.</summary>
        public required bool[] Blocked;
        /// <summary>Per ring point: smoothed 0..1 visibility handed to the renderer.</summary>
        public required float[] Visibility;
        public int Cursor;
        public double LastUsed;
    }

    private readonly Dictionary<ushort, Entry> entries = new();

    /// <summary>Raycasts made since load, for the Diagnostics page.</summary>
    public long Raycasts { get; private set; }
    /// <summary>Samples currently blocked across all rings, for the Diagnostics page.</summary>
    public int BlockedSamples { get; private set; }

    /// <summary>
    /// Per-point visibility of a FATE's ring, eased toward the latest line-of-sight results.
    /// A ring seen for the first time starts fully visible.
    /// </summary>
    public float[] VisibilityFor(ushort id, Vector3 center, float radius, int segments, float[]? heights, float lift, double now, float dt)
    {
        if (!entries.TryGetValue(id, out var entry)
            || entry.Segments != segments
            || Vector3.DistanceSquared(entry.Center, center) > 0.25f
            || Math.Abs(entry.Radius - radius) > 0.05f)
        {
            var samples = Math.Clamp((int)MathF.Ceiling(radius * MathF.PI * 2f / SampleSpacing), 32, segments);
            entry = new Entry
            {
                Center = center,
                Radius = radius,
                Segments = segments,
                Samples = samples,
                Blocked = new bool[samples],
                Visibility = new float[segments],
            };
            Array.Fill(entry.Visibility, 1f);
            entries[id] = entry;
        }

        entry.Heights = heights is { } h && h.Length == segments ? h : null;
        entry.Lift = lift;
        entry.LastUsed = now;

        // Ease each point toward the visibility of the samples either side of it.
        var outRate = Math.Clamp(dt * FadeOut, 0f, 1f);
        var inRate = Math.Clamp(dt * FadeIn, 0f, 1f);
        var vis = entry.Visibility;
        for (var i = 0; i < segments; i++)
        {
            var t = (float)i * entry.Samples / segments;
            var s0 = (int)t;
            var frac = t - s0;
            var s1 = (s0 + 1) % entry.Samples;
            var target = (entry.Blocked[s0 % entry.Samples] ? 0f : 1f) * (1f - frac) + (entry.Blocked[s1] ? 0f : 1f) * frac;

            var current = vis[i];
            current += (target - current) * (target < current ? outRate : inRate);
            vis[i] = Math.Abs(target - current) < 0.002f ? target : current;
        }

        return vis;
    }

    public void Clear() => entries.Clear();

    /// <summary>
    /// Spends this tick's raycast budget re-testing the samples that matter: those near enough to
    /// the player to be drawn (within <paramref name="focusRadius"/>, or all when it's 0).
    /// </summary>
    public unsafe void Update(double now, Vector3? player, float focusRadius)
    {
        List<ushort>? expired = null;
        foreach (var (id, entry) in entries)
            if (now - entry.LastUsed > Expiry)
                (expired ??= []).Add(id);
        if (expired is not null)
            foreach (var id in expired)
                entries.Remove(id);

        if (entries.Count == 0 || player is not { } viewer) return;

        var manager = SceneCameraManager.Instance();
        if (manager == null || manager->CurrentCamera == null) return;
        Vector3 camera = manager->CurrentCamera->Position;

        var budget = Budget;
        var blocked = 0;
        var relevant = focusRadius > 0f ? focusRadius : float.MaxValue;

        foreach (var entry in entries.Values)
        {
            var n = entry.Samples;
            for (var scanned = 0; scanned < n; scanned++)
            {
                var s = entry.Cursor;
                if (budget <= 0) break;
                entry.Cursor = (entry.Cursor + 1) % n;

                var target = SamplePoint(entry, s);
                if (Vector3.Distance(target, viewer) > relevant)
                {
                    entry.Blocked[s] = false;
                    continue;
                }

                budget--;
                entry.Blocked[s] = IsHidden(camera, target);
            }

            foreach (var b in entry.Blocked)
                if (b) blocked++;
        }

        BlockedSamples = blocked;
    }

    /// <summary>World position of sample <paramref name="s"/>, raised to the height the rays aim at.</summary>
    private static Vector3 SamplePoint(Entry entry, int s)
    {
        var theta = MathF.PI * 2f * s / entry.Samples;
        float y;
        if (entry.Heights is { } h)
        {
            var t = (float)s * entry.Segments / entry.Samples;
            var i0 = (int)t % entry.Segments;
            var i1 = (i0 + 1) % entry.Segments;
            y = h[i0] + (h[i1] - h[i0]) * (t - (int)t);
        }
        else
        {
            y = entry.Center.Y;
        }

        return new Vector3(
            entry.Center.X + MathF.Cos(theta) * entry.Radius,
            y + entry.Lift,
            entry.Center.Z + MathF.Sin(theta) * entry.Radius);
    }

    /// <summary>
    /// True when the ray from the camera hits collision well before reaching the point. The last
    /// stretch is left out so the ground the ring lies on never counts as hiding it.
    /// </summary>
    private bool IsHidden(Vector3 camera, Vector3 target)
    {
        var delta = target - camera;
        var distance = delta.Length();
        if (distance < 2f || distance > MaxDistance) return false;

        try
        {
            Raycasts++;
            return BGCollisionModule.RaycastMaterialFilter(camera, delta / distance, out _, distance - 0.8f);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Line of sight raycast failed");
            return false;
        }
    }
}
