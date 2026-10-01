using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using FateManager = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateManager;

namespace FateLimit.Services;

/// <summary>One FATE as FateLimit sees it: where it is, how big, and what it's doing.</summary>
public sealed record FateInfo(
    ushort Id,
    string Name,
    Vector3 Center,
    float Radius,
    FateState State,
    byte Progress,
    long TimeRemaining,
    byte Level,
    bool IsPractice);

/// <summary>
/// Reads the game's FATE table once per framework tick and keeps an immutable snapshot that
/// the overlay and the settings window draw from.
/// </summary>
public sealed class FateTracker
{
    /// <summary>Id given to the practice FATE, far above any real FATE id.</summary>
    public const ushort PracticeId = ushort.MaxValue;
    public const float PracticeRadius = 20f;

    public IReadOnlyList<FateInfo> Fates { get; private set; } = Array.Empty<FateInfo>();

    /// <summary>The player's feet, or null when there is no player (title screen, loading).</summary>
    public Vector3? PlayerPosition { get; private set; }

    /// <summary>Id of the FATE the player is level-synced to, or 0.</summary>
    public ushort SyncedFateId { get; private set; }

    /// <summary>The practice FATE placed with /fatelimit test, if any. Not saved.</summary>
    public FateInfo? Practice { get; private set; }

    private readonly HashSet<ushort> seen = [];
    private readonly ConfigurationService configService;

    public FateTracker(ConfigurationService configService)
    {
        this.configService = configService;
    }

    /// <summary>Places a practice FATE centred on the player, or removes it.</summary>
    public bool TogglePractice()
    {
        if (Practice is not null)
        {
            Practice = null;
            return false;
        }

        var self = Plugin.ObjectTable.LocalPlayer;
        if (self is null) return false;

        Practice = new FateInfo(PracticeId, "Practice FATE", self.Position, PracticeRadius,
            FateState.Running, 0, 0, 0, true);
        return true;
    }

    /// <summary>Drops the practice FATE, e.g. when leaving the zone it was placed in.</summary>
    public void ClearPractice() => Practice = null;

    public void Update()
    {
        var self = Plugin.ObjectTable.LocalPlayer;
        PlayerPosition = self?.Position;

        if (self is null || !Plugin.ClientState.IsLoggedIn)
        {
            Fates = Array.Empty<FateInfo>();
            SyncedFateId = 0;
            seen.Clear();
            return;
        }

        var list = new List<FateInfo>(Plugin.FateTable.Length + 1);
        var present = new HashSet<ushort>();

        foreach (var fate in Plugin.FateTable)
        {
            if (fate is null || fate.Radius <= 0.5f) continue;

            var info = new FateInfo(fate.FateId, fate.Name.TextValue, fate.Position, fate.Radius,
                fate.State, fate.Progress, fate.TimeRemaining, fate.Level, false);
            list.Add(info);
            present.Add(info.Id);

            if (seen.Add(info.Id) && configService.Configuration.LogEvents)
                Plugin.Log.Debug("FATE {Id} \"{Name}\" lv{Level}: radius {Radius:F1} at {Pos} ({State})",
                    info.Id, info.Name, info.Level, info.Radius, Fmt(info.Center), info.State);
        }

        seen.IntersectWith(present);

        if (Practice is not null)
            list.Add(Practice);

        Fates = list;
        SyncedFateId = ReadSyncedFateId();
    }

    private static unsafe ushort ReadSyncedFateId()
    {
        try
        {
            var manager = FateManager.Instance();
            return manager is null ? (ushort)0 : manager->SyncedFateId;
        }
        catch
        {
            return 0;
        }
    }

    public static string Fmt(Vector3 v) => $"({v.X:F1}, {v.Y:F1}, {v.Z:F1})";
}
