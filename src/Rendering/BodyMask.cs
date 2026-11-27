using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace FateLimit.Rendering;

/// <summary>
/// Keeps borders from painting over your own character when they run behind it. Terrain line of
/// sight can't see characters, so this traces the player's silhouette on screen from the bones of
/// their skeleton (pelvis, spine, head, arms, legs as capsules) and slices it into thin horizontal
/// strips. Inside those strips the ring is drawn a second time with every point farther from the
/// camera than the player turned off, so the cut follows the body to the pixel however far apart
/// the ring's points are.
/// </summary>
public sealed unsafe class BodyMask
{
    /// <summary>Yalms a ring point must be behind the player before it's hidden.</summary>
    private const float DepthMargin = 0.3f;
    /// <summary>Strips the silhouette is sliced into, head to feet.</summary>
    private const int StripCount = 36;
    /// <summary>Extra room around every capsule, as a fraction of its radius, for clothes and hair.</summary>
    private const float Padding = 1.15f;

    /// <summary>Bones that shape the silhouette, with their capsule radius as a fraction of the character's height.</summary>
    private static readonly Dictionary<string, float> BoneRadius = new()
    {
        ["j_kosi"] = 0.095f,
        ["j_sebo_a"] = 0.095f, ["j_sebo_b"] = 0.105f, ["j_sebo_c"] = 0.11f,
        ["j_kubi"] = 0.05f, ["j_kao"] = 0.07f,
        ["j_sako_l"] = 0.05f, ["j_sako_r"] = 0.05f,
        ["j_ude_a_l"] = 0.042f, ["j_ude_a_r"] = 0.042f,
        ["j_ude_b_l"] = 0.036f, ["j_ude_b_r"] = 0.036f,
        ["j_te_l"] = 0.036f, ["j_te_r"] = 0.036f,
        ["j_asi_a_l"] = 0.062f, ["j_asi_a_r"] = 0.062f,
        ["j_asi_b_l"] = 0.05f, ["j_asi_b_r"] = 0.05f,
        ["j_asi_c_l"] = 0.045f, ["j_asi_c_r"] = 0.045f,
        ["j_asi_d_l"] = 0.036f, ["j_asi_d_r"] = 0.036f,
        ["j_asi_e_l"] = 0.03f, ["j_asi_e_r"] = 0.03f,
    };

    private Vector3 camera;
    private float depth;
    private float[] scratch = [];

    // Bone layout of the last skeleton seen, rebuilt only when the skeleton changes.
    private nint layoutFor;
    private readonly List<(int Bone, int Parent, float Radius)> layout = [];
    private int headBone = -1;

    // This frame's silhouette in screen space.
    private readonly List<(Vector2 A, Vector2 B, float R)> capsules = [];
    private readonly List<Vector2> intervals = [];
    private readonly List<Vector4> strips = [];

    /// <summary>Whether the player was found on screen this frame.</summary>
    public bool Active { get; private set; }

    /// <summary>Whether this frame's silhouette came from the skeleton (false: a plain box).</summary>
    public bool FromSkeleton { get; private set; }

    /// <summary>The silhouette as screen rectangles, (minX, minY, maxX, maxY), no two overlapping.</summary>
    public IReadOnlyList<Vector4> Strips => strips;

    public void Clear()
    {
        Active = false;
        strips.Clear();
    }

    public void Update(Projector project)
    {
        Clear();

        var self = Plugin.ObjectTable.LocalPlayer;
        if (self is null) return;
        var manager = SceneCameraManager.Instance();
        if (manager == null || manager->CurrentCamera == null) return;

        camera = manager->CurrentCamera->Position;
        var pos = self.Position;
        depth = Vector3.Distance(camera, pos + new Vector3(0, 0.9f, 0));
        // First person: the character isn't drawn.
        if (depth < 0.8f) return;

        var obj = (GameObject*)self.Address;
        var height = obj->Height;
        if (!(height is > 0.5f and < 5f)) height = 1.9f;

        if (!project(pos, out var feet) || !project(pos + new Vector3(0, height, 0), out var head)) return;
        var tall = Vector2.Distance(feet, head);
        if (tall < 8f) return;

        capsules.Clear();
        FromSkeleton = TraceSkeleton(obj, tall, project);
        if (!FromSkeleton)
        {
            // No skeleton to read: a column from feet to head.
            capsules.Add((feet, head, tall * 0.15f));
        }

        Slice(tall);
        Active = strips.Count > 0;
    }

    /// <summary>Projects the body's bones and adds a capsule from each to its parent.</summary>
    private bool TraceSkeleton(GameObject* obj, float tall, Projector project)
    {
        var draw = obj->DrawObject;
        if (draw == null || draw->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase) return false;
        var skeleton = ((CharacterBase*)draw)->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount < 1 || skeleton->PartialSkeletons == null) return false;
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null) return false;

        var hk = pose->Skeleton;
        var count = Math.Min(hk->Bones.Length, pose->ModelPose.Length);
        if (count <= 0) return false;

        if (layoutFor != (nint)hk)
        {
            layoutFor = (nint)hk;
            layout.Clear();
            headBone = -1;
            var names = new string?[count];
            for (var i = 0; i < count; i++) names[i] = hk->Bones[i].Name.String;
            for (var i = 0; i < count; i++)
            {
                if (names[i] is not { } name || !BoneRadius.TryGetValue(name, out var radius)) continue;
                if (name == "j_kao") headBone = i;
                // Walk up past twist and helper bones to the nearest bone that's part of the shape.
                var parent = hk->ParentIndices[i];
                while (parent >= 0 && parent < count && (names[parent] is not { } pn || !BoneRadius.ContainsKey(pn)))
                    parent = hk->ParentIndices[parent];
                layout.Add((i, parent is >= 0 && parent < count ? parent : -1, radius));
            }
        }
        if (layout.Count == 0) return false;

        var t = skeleton->Transform;
        Vector3 World(int bone)
        {
            var p = pose->ModelPose[bone].Translation;
            return (Vector3)t.Position + Vector3.Transform(new Vector3(p.X, p.Y, p.Z) * (Vector3)t.Scale, (Quaternion)t.Rotation);
        }

        foreach (var (bone, parent, radius) in layout)
        {
            if (!project(World(bone), out var a)) continue;
            var r = radius * tall * Padding;
            var b = a;
            if (parent >= 0 && project(World(parent), out var pb)) b = pb;
            capsules.Add((a, b, r));
        }

        // The head bone sits at the jaw: add the skull above it, up along the neck.
        if (headBone >= 0)
        {
            var neck = layout.Find(l => l.Bone == headBone).Parent;
            var top = World(headBone);
            var up = neck >= 0 ? Vector3.Normalize(top - World(neck)) : Vector3.UnitY;
            if (float.IsNaN(up.X)) up = Vector3.UnitY;
            var size = 0.13f * ((Vector3)t.Scale).Y;
            if (project(top, out var jaw) && project(top + up * size, out var crown))
                capsules.Add((jaw, crown, 0.075f * tall * Padding));
        }

        return capsules.Count > 0;
    }

    /// <summary>Turns this frame's capsules into horizontal strips of screen rectangles.</summary>
    private void Slice(float tall)
    {
        if (capsules.Count == 0) return;

        var minY = float.MaxValue;
        var maxY = float.MinValue;
        foreach (var (a, b, r) in capsules)
        {
            minY = Math.Min(minY, Math.Min(a.Y, b.Y) - r);
            maxY = Math.Max(maxY, Math.Max(a.Y, b.Y) + r);
        }

        var step = Math.Max(2f, MathF.Ceiling(Math.Max(maxY - minY, tall) / StripCount));
        for (var y0 = MathF.Floor(minY); y0 < maxY; y0 += step)
        {
            var y1 = y0 + step;
            intervals.Clear();
            foreach (var (a, b, r) in capsules)
                AddSpan(a, b, r, y0, y1);
            if (intervals.Count == 0) continue;

            intervals.Sort((p, q) => p.X.CompareTo(q.X));
            var cur = intervals[0];
            for (var i = 1; i < intervals.Count; i++)
            {
                if (intervals[i].X <= cur.Y + 1f) cur.Y = Math.Max(cur.Y, intervals[i].Y);
                else
                {
                    strips.Add(new Vector4(MathF.Floor(cur.X), y0, MathF.Ceiling(cur.Y), y1));
                    cur = intervals[i];
                }
            }
            strips.Add(new Vector4(MathF.Floor(cur.X), y0, MathF.Ceiling(cur.Y), y1));
        }
    }

    /// <summary>Adds the horizontal extent of a capsule within the strip y0..y1.</summary>
    private void AddSpan(Vector2 a, Vector2 b, float r, float y0, float y1)
    {
        if (Math.Max(a.Y, b.Y) + r < y0 || Math.Min(a.Y, b.Y) - r > y1) return;

        // Sweep circles along the segment; each one covers cx ± sqrt(r² - dy²) where dy is the
        // distance from its centre to the nearest point of the strip.
        var len = Vector2.Distance(a, b);
        var n = Math.Max(1, (int)MathF.Ceiling(len / Math.Max(r * 0.5f, 1f)));
        var lo = float.MaxValue;
        var hi = float.MinValue;
        for (var k = 0; k <= n; k++)
        {
            var c = Vector2.Lerp(a, b, (float)k / n);
            var dy = c.Y < y0 ? y0 - c.Y : c.Y > y1 ? c.Y - y1 : 0f;
            if (dy >= r) continue;
            var half = MathF.Sqrt(r * r - dy * dy);
            lo = Math.Min(lo, c.X - half);
            hi = Math.Max(hi, c.X + half);
        }
        if (lo < hi) intervals.Add(new Vector2(lo, hi));
    }

    /// <summary>
    /// The ring's per-point visibility with every point behind the player (seen from the camera)
    /// turned off. Only meant for drawing inside <see cref="Strips"/>.
    /// </summary>
    public float[] Behind(float[]? visibility, Vector3 center, float ringRadius, int segments, float[]? heights, float lift)
    {
        if (scratch.Length != segments) scratch = new float[segments];
        if (visibility is { } v && v.Length == segments) Array.Copy(v, scratch, segments);
        else Array.Fill(scratch, 1f);

        // The whole ring is closer to the camera than the player: nothing to hide.
        if (Vector3.Distance(camera, center) + ringRadius < depth) return scratch;

        for (var i = 0; i < segments; i++)
        {
            var theta = MathF.PI * 2f * i / segments;
            var y = heights is { } h && h.Length == segments ? h[i] : center.Y;
            var p = new Vector3(center.X + MathF.Cos(theta) * ringRadius, y + lift, center.Z + MathF.Sin(theta) * ringRadius);
            var behind = SmoothStep(depth + DepthMargin * 0.3f, depth + DepthMargin, Vector3.Distance(camera, p));
            scratch[i] *= 1f - behind;
        }
        return scratch;
    }

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
