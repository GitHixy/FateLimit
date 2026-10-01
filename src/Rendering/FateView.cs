using System;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using FateLimit.Models;
using FateLimit.Services;

namespace FateLimit.Rendering;

/// <summary>Where the player stands relative to one FATE, and how visible its border should be.</summary>
public readonly record struct FateView(
    FateInfo Fate,
    /// <summary>Yalms to the edge: positive inside the FATE, negative outside.</summary>
    float EdgeDistance,
    /// <summary>0..1, how close to the edge you are, within the warning distance.</summary>
    float Closeness,
    /// <summary>0..1 target opacity of the border before smoothing.</summary>
    float Visibility)
{
    public bool Inside => EdgeDistance >= 0f;
}

/// <summary>The rules for when a border shows, shared by the world overlay and the settings preview.</summary>
public static class FateLogic
{
    /// <summary>After stepping out, the border of the FATE you left stays up for at least this far.</summary>
    public const float StepBackDistance = 6f;

    public static bool IsDrawable(Configuration config, FateInfo fate) => fate.State switch
    {
        FateState.Running => true,
        FateState.Preparing => config.ShowPreparing,
        _ => fate.IsPractice,
    };

    /// <param name="lastInsideId">The FATE the player was most recently inside, which keeps its border while they step back in.</param>
    public static FateView Evaluate(Configuration config, FateInfo fate, Vector3 player, ushort syncedId, ushort lastInsideId)
    {
        var dx = player.X - fate.Center.X;
        var dz = player.Z - fate.Center.Z;
        var edge = fate.Radius - MathF.Sqrt(dx * dx + dz * dz);

        if (!IsDrawable(config, fate))
            return new FateView(fate, edge, 0f, 0f);

        var warn = Math.Max(config.WarnDistance, 0.5f);
        var synced = !config.OnlyWhenSynced || fate.IsPractice || fate.Id == syncedId;

        float closeness;
        if (edge >= 0f)
        {
            closeness = synced ? 1f - BorderRenderer.SmoothStep(0f, warn, edge) : 0f;
        }
        else if (fate.Id == lastInsideId && synced)
        {
            // Just stepped out: keep the way back in visible.
            closeness = 1f - BorderRenderer.SmoothStep(0f, Math.Max(warn, StepBackDistance), -edge);
        }
        else if (config.WarnFromOutside)
        {
            closeness = 1f - BorderRenderer.SmoothStep(0f, warn, -edge);
        }
        else
        {
            closeness = 0f;
        }

        float visibility;
        if (config.Visibility == VisibilityMode.Always)
        {
            visibility = config.OnlyCurrentFate
                ? (edge >= 0f || (fate.Id == lastInsideId && edge >= -StepBackDistance) ? 1f : 0f)
                : (Math.Abs(edge) <= config.MaxDrawDistance ? 1f : 0f);
        }
        else
        {
            // Rises a little ahead of the colour shift so the ring is readable before it turns red.
            visibility = closeness > 0f ? MathF.Pow(closeness, 0.55f) : 0f;
        }

        return new FateView(fate, edge, closeness, visibility);
    }
}
