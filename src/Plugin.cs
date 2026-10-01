using System;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FateLimit.Models;
using FateLimit.Rendering;
using FateLimit.Services;
using FateLimit.UI;
using FateLimit.Windows;

namespace FateLimit;

/// <summary>
/// Main plugin class for FateLimit
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => "FateLimit";

    private const string CommandName = "/fatelimit";
    private const string CommandAlt = "/fl";

    public const string RepoUrl = "https://github.com/GitHixy/FateLimit";
    public const string PatreonUrl = "https://www.patreon.com/GitHixy";
    public const string DiscordUrl = "https://discord.gg/9kjUjWDBkJ";

    // Dalamud Services
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IPluginLog DalamudLog { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IFateTable FateTable { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;

    /// <summary>Plugin-wide logger: writes to Dalamud's log and keeps a copy for the Diagnostics page.</summary>
    public static DiagnosticsLog Log { get; } = new(() => DalamudLog);

    // Plugin Services
    public ConfigurationService ConfigService { get; }
    public FateTracker Fates { get; }
    public TerrainService Terrain { get; }
    public Overlay Overlay { get; }

    // Windows
    public ConfigWindow ConfigWindow { get; }
    public ChangelogWindow ChangelogWindow { get; }

    public Configuration Configuration => ConfigService.Configuration;

    private readonly Stopwatch clock = Stopwatch.StartNew();

    public Plugin()
    {
        try
        {
            Log.Info("FateLimit plugin initializing...");

            ConfigService = new ConfigurationService();
            Fates = new FateTracker(ConfigService);
            Terrain = new TerrainService();
            Overlay = new Overlay(this);

            ConfigWindow = new ConfigWindow(this);
            ChangelogWindow = new ChangelogWindow(this);

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open FateLimit settings\n→ /fatelimit test - Place or remove a practice FATE around you\n→ /fatelimit toggle - Turn borders on or off"
            });
            CommandManager.AddHandler(CommandAlt, new CommandInfo(OnCommand) { ShowInHelp = false });

            Framework.Update += OnFrameworkUpdate;
            ClientState.TerritoryChanged += OnTerritoryChanged;

            PluginInterface.UiBuilder.Draw += Draw;
            PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;

            ShowChangelogIfUpdated();

            Log.Info("FateLimit plugin initialized successfully!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize FateLimit plugin");
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            Log.Info("FateLimit plugin disposing...");

            PluginInterface.UiBuilder.Draw -= Draw;
            PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;

            Framework.Update -= OnFrameworkUpdate;
            ClientState.TerritoryChanged -= OnTerritoryChanged;

            ConfigWindow?.Dispose();
            ChangelogWindow?.Dispose();
            ConfigService?.Dispose();

            CommandManager.RemoveHandler(CommandName);
            CommandManager.RemoveHandler(CommandAlt);

            Log.Info("FateLimit plugin disposed successfully!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error disposing FateLimit plugin");
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "test":
            case "practice":
                TogglePractice();
                break;
            case "toggle":
                Configuration.Enabled = !Configuration.Enabled;
                ConfigService.Save();
                Log.Info("Borders {State}", Configuration.Enabled ? "on" : "off");
                break;
            default:
                ConfigWindow.IsOpen = !ConfigWindow.IsOpen;
                break;
        }
    }

    public void TogglePractice()
    {
        var placed = Fates.TogglePractice();
        Log.Info(placed ? "Practice FATE placed around you" : "Practice FATE removed");
    }

    private void OpenConfigUi() => ConfigWindow.IsOpen = true;

    private void ShowChangelogIfUpdated()
    {
        var current = Changelog.CurrentVersion;
        if (ConfigService.IsNewConfiguration)
        {
            Configuration.LastSeenVersion = current;
            ConfigService.Save();
            return;
        }

        if (Configuration.LastSeenVersion != current)
            ChangelogWindow.ShowSince(Configuration.LastSeenVersion);
    }

    private void OnTerritoryChanged(uint territory)
    {
        Fates.ClearPractice();
        Terrain.Clear();
        Overlay.Reset();
    }

    // ------------------------------------------------------------------
    // Frame
    // ------------------------------------------------------------------

    public double Now => clock.Elapsed.TotalSeconds;

    public bool ReducedMotion => Configuration.ReducedMotion switch
    {
        ReducedMotionMode.On => true,
        ReducedMotionMode.Off => false,
        _ => PluginInterface.UiBuilder.ShouldUseReducedMotion,
    };

    /// <summary>Why borders are currently not drawn, or null when they are.</summary>
    public static string? HiddenReason
    {
        get
        {
            if (!ClientState.IsLoggedIn) return "not logged in";
            if (GameGui.GameUiHidden) return "game UI hidden";
            if (ClientState.IsGPosing) return "GPose";
            if (Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51]) return "loading";
            if (Condition[ConditionFlag.OccupiedInCutSceneEvent]) return "cutscene event";
            if (Condition[ConditionFlag.WatchingCutscene]) return "watching cutscene";
            if (Condition[ConditionFlag.WatchingCutscene78]) return "watching cutscene (78)";
            return null;
        }
    }

    /// <summary>Frames whose drawing threw, for the Diagnostics report.</summary>
    public int FrameErrors { get; private set; }
    private int loggedFrameErrors;

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            Fates.Update();
            if (Configuration.Enabled && Configuration.FollowTerrain && ClientState.IsLoggedIn)
                Terrain.Update(Now);
        }
        catch (Exception ex)
        {
            FrameErrors++;
            if (loggedFrameErrors++ < 5)
                Log.Error(ex, "Error during FateLimit update");
        }
    }

    private void Draw()
    {
        try
        {
            Overlay.Draw(Now, ImGui.GetIO().DeltaTime, HiddenReason is null);

            ConfigWindow.Draw();
            ChangelogWindow.Draw();
        }
        catch (Exception ex)
        {
            // A per-frame error would flood the log; the first few are enough to diagnose it.
            FrameErrors++;
            if (loggedFrameErrors++ < 5)
                Log.Error(ex, "Error during FateLimit frame");
        }
    }
}
