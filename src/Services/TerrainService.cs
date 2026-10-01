using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace FateLimit.Services;

/// <summary>
/// Finds the ground height under every point of a FATE's ring, so borders lie on hills and
/// slopes instead of cutting through them. Raycasts are spread over several frames and cached
/// per FATE, since terrain never moves.
/// </summary>
public sealed class TerrainService
{
    /// <summary>Raycasts allowed per framework tick across all FATEs.</summary>
    private const int Budget = 96;
    /// <summary>Unused samples are dropped after this many seconds.</summary>
    private const double Expiry = 60;
    /// <summary>Points the collision mesh didn't answer for are tried again after this many seconds.</summary>
    private const double RetryDelay = 2;

    private sealed class Entry
    {
        public required Vector3 Center;
        public required float Radius;
        public required float[] Heights;
        public required byte[] State; // 0 = pending, 1 = sampled, 2 = missed
        public int Cursor;
        public int Pending;
        public bool Smoothed;
        public double LastUsed;
        public double RetryAt;
    }

    private readonly Dictionary<(ushort Id, int Segments), Entry> entries = new();

    /// <summary>Raycasts made since load, for the Diagnostics page.</summary>
    public long Raycasts { get; private set; }
    public int CachedRings => entries.Count;

    /// <summary>Number of points for a ring of this radius: about one every 0.6 yalms.</summary>
    public static int SegmentsFor(float radius)
        => Math.Clamp((int)MathF.Ceiling(radius * MathF.PI * 2f / 0.6f), 96, 720);

    /// <summary>
    /// Ground heights for a FATE's ring, one per segment. Points not sampled yet hold the FATE's
    /// own height, so a ring is drawable straight away and settles onto the ground as samples land.
    /// </summary>
    public float[] HeightsFor(ushort id, Vector3 center, float radius, int segments, double now)
    {
        var key = (id, segments);
        if (!entries.TryGetValue(key, out var entry)
            || Vector3.DistanceSquared(entry.Center, center) > 0.25f
            || Math.Abs(entry.Radius - radius) > 0.05f)
        {
            entry = new Entry
            {
                Center = center,
                Radius = radius,
                Heights = new float[segments],
                State = new byte[segments],
                Pending = segments,
            };
            Array.Fill(entry.Heights, center.Y);
            entries[key] = entry;
        }

        entry.LastUsed = now;
        return entry.Heights;
    }

    public void Clear() => entries.Clear();

    /// <summary>Spends this tick's raycast budget on whichever rings still have unsampled points.</summary>
    public void Update(double now)
    {
        var budget = Budget;
        List<(ushort, int)>? expired = null;

        foreach (var (key, entry) in entries)
        {
            if (now - entry.LastUsed > Expiry)
            {
                (expired ??= []).Add(key);
                continue;
            }

            if (budget <= 0) continue;

            if (entry.Pending == 0)
            {
                if (!entry.Smoothed)
                {
                    Smooth(entry);
                    entry.Smoothed = true;
                }

                // Retry misses now and then: collision streams in as you move closer.
                if (now < entry.RetryAt) continue;
                for (var i = 0; i < entry.State.Length; i++)
                {
                    if (entry.State[i] != 2) continue;
                    entry.State[i] = 0;
                    entry.Pending++;
                }

                entry.RetryAt = now + RetryDelay;
                if (entry.Pending == 0) continue;
                entry.Smoothed = false;
            }

            var n = entry.Heights.Length;
            for (var scanned = 0; scanned < n && budget > 0 && entry.Pending > 0; scanned++)
            {
                var i = entry.Cursor;
                entry.Cursor = (entry.Cursor + 1) % n;
                if (entry.State[i] != 0) continue;

                budget--;
                entry.Pending--;

                var theta = MathF.PI * 2f * i / n;
                var x = entry.Center.X + MathF.Cos(theta) * entry.Radius;
                var z = entry.Center.Z + MathF.Sin(theta) * entry.Radius;

                if (TrySample(x, z, entry.Center.Y, out var y))
                {
                    entry.Heights[i] = y;
                    entry.State[i] = 1;
                }
                else
                {
                    entry.State[i] = 2;
                }
            }

            if (entry.Pending == 0)
                entry.RetryAt = now + RetryDelay;
        }

        if (expired is not null)
            foreach (var key in expired)
                entries.Remove(key);
    }

    /// <summary>
    /// Casts down from a little above the FATE's height first, so roofs and tree canopies above
    /// the ring are skipped; falls back to a tall cast for ground that climbs above that.
    /// </summary>
    private bool TrySample(float x, float z, float baseY, out float y)
    {
        y = baseY;
        try
        {
            Raycasts++;
            if (BGCollisionModule.RaycastMaterialFilter(new Vector3(x, baseY + 6f, z), -Vector3.UnitY, out var near, 40f))
            {
                y = near.Point.Y;
                return true;
            }

            Raycasts++;
            if (BGCollisionModule.RaycastMaterialFilter(new Vector3(x, baseY + 45f, z), -Vector3.UnitY, out var far, 120f))
            {
                y = far.Point.Y;
                return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Terrain raycast failed");
        }

        return false;
    }

    /// <summary>
    /// Replaces lone spikes - a fence post, a crate, a branch - with the median of their
    /// neighbours, so the ring doesn't jump over small props.
    /// </summary>
    private static void Smooth(Entry entry)
    {
        var h = entry.Heights;
        var n = h.Length;
        var copy = (float[])h.Clone();
        Span<float> window = stackalloc float[5];

        for (var i = 0; i < n; i++)
        {
            for (var k = -2; k <= 2; k++)
                window[k + 2] = copy[((i + k) % n + n) % n];
            window.Sort();
            var median = window[2];
            if (Math.Abs(copy[i] - median) > 1.2f)
                h[i] = median;
        }
    }
}
