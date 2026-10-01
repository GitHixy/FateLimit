using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FateLimit.Models;
using FateLimit.Rendering;
using FateLimit.Services;
using FateLimit.UI;

namespace FateLimit.Windows;

public class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private readonly PreviewCanvas preview;

    private int section;

    private const int ReportLineLimit = 200;
    private const float LabelWidth = 170f;
    private const float ControlWidth = 230f;

    private bool changed;
    private double saveAt = double.MaxValue;

    // Diagnostics view state. The filtered list is rebuilt only when the log or the filter changes.
    private int logLevelFilter;
    private string logSearch = string.Empty;
    private bool logAutoScroll = true;
    private readonly HashSet<LogEntry> selectedLogLines = [];
    private List<LogEntry> visibleLogLines = [];
    private (int Revision, int Level, string Search) logViewKey = (-1, -1, string.Empty);
    private int scrolledRevision = -1;

    private static readonly (FontAwesomeIcon Icon, string Label, string Blurb)[] Sections =
    [
        (FontAwesomeIcon.PaintBrush, "Style", "Pick how FATE borders look, and tune it"),
        (FontAwesomeIcon.Eye, "Visibility", "When borders appear, and for which FATEs"),
        (FontAwesomeIcon.ExclamationTriangle, "Warning", "What happens as you near the edge"),
        (FontAwesomeIcon.SlidersH, "General", "Master switch, opacity, terrain and motion"),
        (FontAwesomeIcon.Bug, "Diagnostics", "Nearby FATEs, log and bug reports"),
        (FontAwesomeIcon.InfoCircle, "About", "Version, links and support"),
    ];

    public ConfigWindow(Plugin plugin)
        : base("FateLimit Settings###FateLimitConfig")
    {
        this.plugin = plugin;
        preview = new PreviewCanvas(plugin);

        Size = new Vector2(820, 640);
        SizeConstraintMin = new Vector2(700, 500);
        SizeConstraintMax = new Vector2(1400, 1200);
        WindowFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    private ConfigurationService ConfigService => plugin.ConfigService;
    private Configuration Config => plugin.Configuration;

    protected override void DrawContents()
    {
        try
        {
            BgAlpha = 0.94f;
            changed = false;

            Theme.WindowHeader(FontAwesomeIcon.DrawPolygon, "FateLimit", Sections[section].Blurb, DrawHeaderActions);

            var avail = ImGui.GetContentRegionAvail();
            const float railWidth = 158f;

            DrawNavRail(new Vector2(railWidth, avail.Y));

            ImGui.SameLine(0, 12);

            using (var pane = Theme.Region("##ConfigPane", new Vector2(0, avail.Y)))
            {
                if (pane)
                {
                    switch (section)
                    {
                        case 0: DrawStyle(); break;
                        case 1: DrawVisibility(); break;
                        case 2: DrawWarning(); break;
                        case 3: DrawGeneral(); break;
                        case 4: DrawDiagnostics(); break;
                        case 5: DrawAbout(); break;
                    }

                    ImGui.Dummy(new Vector2(0, 8));
                }
            }

            if (changed)
                saveAt = plugin.Now + 1.0;

            // Sliders fire every frame while dragged; save once things settle.
            if (plugin.Now >= saveAt)
            {
                saveAt = double.MaxValue;
                ConfigService.Save();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing config window");
            ImGui.TextColored(Theme.Bad, "Error drawing settings.");
        }
    }

    protected override void OnClosed()
    {
        if (saveAt != double.MaxValue)
        {
            saveAt = double.MaxValue;
            ConfigService.Save();
        }
    }

    private void DrawHeaderActions()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var active = plugin.Fates.Practice is not null;
        var label = active ? "Remove practice FATE" : "Practice FATE";
        var buttonWidth = 170f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + width - buttonWidth);
        if (active ? Theme.PrimaryButton(label, new Vector2(buttonWidth, 0)) : Theme.GhostButton(label, new Vector2(buttonWidth, 0), Theme.Gold))
            plugin.TogglePractice();
        if (ImGui.IsItemHovered())
            Theme.Tooltip("Places a practice FATE centred on you, so you can try styles and walk to its edge without waiting for a real one. "
                          + "It goes away when you change zone. Also /fatelimit test.");
    }

    private void DrawNavRail(Vector2 size)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();

        dl.AddRectFilled(origin, origin + size, Theme.U32(Theme.Panel, 0.55f), Theme.Radius);
        dl.AddRect(origin, origin + size, Theme.U32(Theme.Line, 0.8f), Theme.Radius, ImDrawFlags.None, 1f);

        using var child = Theme.Region("##ConfigNav", size);
        if (!child) return;

        ImGui.Dummy(new Vector2(0, 4));

        for (var i = 0; i < Sections.Length; i++)
        {
            var (icon, label, _) = Sections[i];
            var selected = section == i;

            var p = ImGui.GetCursorScreenPos();
            var w = ImGui.GetContentRegionAvail().X - 8;
            const float h = 34f;

            ImGui.SetCursorScreenPos(new Vector2(p.X + 4, p.Y));
            if (ImGui.InvisibleButton($"##nav{i}", new Vector2(w, h)))
                section = i;

            var hovered = ImGui.IsItemHovered();
            var min = new Vector2(p.X + 4, p.Y);
            var max = new Vector2(p.X + 4 + w, p.Y + h);

            if (selected)
            {
                dl.AddRectFilled(min, max, Theme.U32(Theme.Gold, 0.13f), Theme.Radius);
                dl.AddRectFilled(min, new Vector2(min.X + 2.5f, max.Y), Theme.U32(Theme.Gold, 0.95f), 1.5f);
            }
            else if (hovered)
            {
                dl.AddRectFilled(min, max, Theme.U32(Theme.Crystal, 0.12f), Theme.Radius);
            }

            var tint = selected ? Theme.GoldBright : hovered ? Theme.Text : Theme.TextMuted;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = icon.ToIconString();
                var gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(min.X + 15 - gs.X * 0.5f, min.Y + (h - gs.Y) * 0.5f), Theme.U32(tint), glyph);
            }

            var ts = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(min.X + 30, min.Y + (h - ts.Y) * 0.5f), Theme.U32(tint), label);

            ImGui.Dummy(new Vector2(0, 2));
        }

        // Status pill at the bottom of the rail: are borders being drawn right now?
        var (statusText, statusColor) = StatusSummary();
        var pillY = origin.Y + size.Y - 34f;
        var pillMin = new Vector2(origin.X + 8, pillY);
        var pillMax = new Vector2(origin.X + size.X - 8, pillY + 24f);
        dl.AddRectFilled(pillMin, pillMax, Theme.U32(statusColor, 0.12f), 12f);
        dl.AddCircleFilled(new Vector2(pillMin.X + 12, pillMin.Y + 12), 4f, Theme.U32(statusColor), 12);
        dl.AddText(new Vector2(pillMin.X + 22, pillMin.Y + (24f - ImGui.GetTextLineHeight()) * 0.5f), Theme.U32(statusColor), statusText);
    }

    private (string Text, Vector4 Color) StatusSummary()
    {
        if (!Config.Enabled) return ("Turned off", Theme.Warn);
        if (Plugin.HiddenReason is not null) return ("Hidden", Theme.TextMuted);
        if (plugin.Overlay.Warning is not null) return ("Near the edge", Theme.Bad);
        if (plugin.Fates.Practice is not null) return ("Practice FATE", Theme.Crystal);
        return ("Active", Theme.Good);
    }

    // ------------------------------------------------------------------
    // Style
    // ------------------------------------------------------------------

    private void DrawStyle()
    {
        preview.Draw("##StylePreview", new Vector2(0, 250f));

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.TextColored(Theme.TextMuted, "The preview follows your Visibility and Warning settings too.");

        ImGui.Dummy(new Vector2(0, 6));
        Theme.SectionHeader("Border style", FontAwesomeIcon.LayerGroup);
        DrawStyleTiles();

        var style = Config.Style;
        var s = Config.ActiveStyle;

        ImGui.Dummy(new Vector2(0, 10));
        Theme.SectionHeader($"Tune {BorderStyleInfo.Name(style)}", FontAwesomeIcon.SlidersH);

        ColorRow("Colour", s.Color, v => s.Color = v);
        FloatRow("Thickness", s.Thickness, v => s.Thickness = v, 0.5f, 10f, "%.1f px");

        if (BorderStyleInfo.HeightLabel(style) is { } heightLabel)
            FloatRow(heightLabel, s.Height, v => s.Height = v, 0.3f, 8f, "%.1f y");

        if (BorderStyleInfo.SpacingLabel(style) is { } spacingLabel)
            FloatRow(spacingLabel, s.Spacing, v => s.Spacing = v, style == BorderStyle.Dashes ? 0.4f : 1f, 15f, "%.1f y");

        FloatRow("Glow", s.Glow, v => s.Glow = v, 0f, 1f, "%.2f");
        ToggleRow("Animate", s.Animate, v => s.Animate = v, "Shimmer, sweeps, flicker and bobbing. Turned off for everyone when reduced motion is on.");
        if (s.Animate)
            FloatRow("Speed", s.Speed, v => s.Speed = v, 0.1f, 3f, "%.2fx");

        ImGui.Dummy(new Vector2(0, 6));
        if (Theme.GhostButton($"Reset {BorderStyleInfo.Name(style)}", new Vector2(190, 30)))
        {
            Config.Styles[style] = StyleSettings.DefaultsFor(style);
            changed = true;
        }
    }

    /// <summary>Every style as a selectable tile: crest, name and one line about it.</summary>
    private void DrawStyleTiles()
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var columns = avail > 560f ? 3 : 2;
        const float gap = 8f;
        const float h = 74f;
        var w = (avail - gap * (columns - 1)) / columns;
        var dl = ImGui.GetWindowDrawList();

        for (var i = 0; i < BorderStyleInfo.All.Length; i++)
        {
            var style = BorderStyleInfo.All[i];
            if (i % columns != 0) ImGui.SameLine(0, gap);

            var p = ImGui.GetCursorScreenPos();
            var max = p + new Vector2(w, h);

            if (ImGui.InvisibleButton($"##style{i}", new Vector2(w, h)) && Config.Style != style)
            {
                Config.Style = style;
                changed = true;
            }

            var hovered = ImGui.IsItemHovered();
            var selected = Config.Style == style;
            var accent = Config.StyleFor(style).Color;

            dl.AddRectFilled(p, max, Theme.U32(selected ? Theme.Alpha(Theme.Gold, 0.10f) : hovered ? Theme.Alpha(Theme.Crystal, 0.08f) : Theme.Alpha(Theme.Surface, 0.6f)), Theme.Radius);
            dl.AddRectFilledMultiColor(p, new Vector2(max.X, p.Y + h * 0.6f),
                Theme.U32(accent, 0.08f), Theme.U32(accent, 0.02f), Theme.U32(accent, 0f), Theme.U32(accent, 0f));
            dl.AddRect(p, max, Theme.U32(selected ? Theme.Gold : hovered ? Theme.Alpha(Theme.Crystal, 0.5f) : Theme.Line, selected ? 0.8f : 1f),
                Theme.Radius, ImDrawFlags.None, selected ? 1.5f : 1f);

            // Crest diamond tinted with the style's own colour.
            var crest = new Vector2(p.X + 22, p.Y + 24);
            Theme.DrawDiamond(dl, crest, 13f, Theme.U32(accent, 0.18f), Theme.U32(accent, selected ? 0.95f : 0.6f), 1.3f);
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = BorderStyleInfo.Icon(style).ToIconString();
                var gs = ImGui.CalcTextSize(glyph);
                dl.AddText(crest - gs * 0.5f, Theme.U32(Theme.Lighten(accent, 0.3f)), glyph);
            }

            dl.AddText(new Vector2(p.X + 44, p.Y + 14), Theme.U32(selected ? Theme.GoldBright : Theme.Text), BorderStyleInfo.Name(style));

            // Blurb wrapped by hand into the tile.
            var blurbPos = new Vector2(p.X + 12, p.Y + 40);
            using (new Theme.FontScale(0.86f))
            {
                dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(), blurbPos, Theme.U32(Theme.TextMuted), BorderStyleInfo.Blurb(style), w - 22f);
            }

            if (selected)
                dl.AddRectFilled(new Vector2(p.X + 10, max.Y - 2.5f), new Vector2(max.X - 10, max.Y - 1f), Theme.U32(Theme.Gold, 0.95f), 1f);

            if (i % columns == columns - 1 || i == BorderStyleInfo.All.Length - 1)
                ImGui.Dummy(new Vector2(0, gap - ImGui.GetStyle().ItemSpacing.Y));
        }
    }

    // ------------------------------------------------------------------
    // Visibility
    // ------------------------------------------------------------------

    private void DrawVisibility()
    {
        Theme.SectionHeader("When to show borders", FontAwesomeIcon.Eye);

        var mode = Config.Visibility == VisibilityMode.Always ? 0 : 1;
        if (Theme.SegmentedControl("##Visibility", ref mode, "Always", "Only near the edge"))
        {
            Config.Visibility = mode == 0 ? VisibilityMode.Always : VisibilityMode.NearEdge;
            changed = true;
        }

        ImGui.Dummy(new Vector2(0, 4));
        Wrapped(Config.Visibility == VisibilityMode.Always
            ? "Every running FATE around you is outlined all the time. Good for seeing where FATEs are and how big they are."
            : "Borders stay hidden until you get close to the edge of the FATE you're in, then fade in. Nothing on screen the rest of the time.");

        ImGui.Dummy(new Vector2(0, 8));
        FloatRow("Warning distance", Config.WarnDistance, v => Config.WarnDistance = v, 1f, 30f, "%.0f yalms");
        HelpAfter("How far from the edge the warning starts. Borders fade in from here in \"Only near the edge\", and the colour shift, banner and marker start here in both modes.");

        ToggleRow("Only when level-synced", Config.OnlyWhenSynced, v => Config.OnlyWhenSynced = v,
            "Warn only about the FATE you're level-synced to. Leaving it is what costs you the sync, so this skips FATEs you're just passing through.");

        ToggleRow("Warn from outside too", Config.WarnFromOutside, v => Config.WarnFromOutside = v,
            "Also show a FATE's edge as you walk up to it from outside. Handy for not stepping into one by accident.");

        if (Config.Visibility == VisibilityMode.Always)
        {
            ImGui.Dummy(new Vector2(0, 8));
            Theme.SectionHeader("Always mode", FontAwesomeIcon.Globe);

            ToggleRow("Only the FATE I'm in", Config.OnlyCurrentFate, v => Config.OnlyCurrentFate = v,
                "Outline just the FATE you're standing in, instead of every FATE nearby.");

            if (!Config.OnlyCurrentFate)
                FloatRow("Draw distance", Config.MaxDrawDistance, v => Config.MaxDrawDistance = v, 20f, 400f, "%.0f yalms");
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Which FATEs", FontAwesomeIcon.Filter);

        ToggleRow("FATEs not started yet", Config.ShowPreparing, v => Config.ShowPreparing = v,
            "Some FATEs wait for someone to talk to an NPC before they begin. Turn this on to outline those too.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Focus", FontAwesomeIcon.Bullseye);

        var focusOn = Config.FocusRadius > 0f;
        ToggleRow("Fade far parts of the ring", focusOn, v => Config.FocusRadius = v ? 30f : 0f,
            "Keeps the part of the border near you and fades the rest, so a big FATE doesn't draw a ring across the whole screen.");
        if (Config.FocusRadius > 0f)
            FloatRow("Focus radius", Config.FocusRadius, v => Config.FocusRadius = v, 8f, 120f, "%.0f yalms");
    }

    // ------------------------------------------------------------------
    // Warning
    // ------------------------------------------------------------------

    private void DrawWarning()
    {
        Theme.SectionHeader("Colour", FontAwesomeIcon.Palette);

        ToggleRow("Shift colour near the edge", Config.WarnColorShift, v => Config.WarnColorShift = v,
            "The part of the border close to you blends into the warning colour as you approach it.");
        ColorRow("Warning colour", Config.WarnColor, v => Config.WarnColor = v);

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Banner", FontAwesomeIcon.Flag);

        ToggleRow("Show banner", Config.ShowBanner, v => Config.ShowBanner = v,
            "A banner across the top of the screen with the distance left to the border, and how far back it is once you've stepped out.");
        if (Config.ShowBanner)
        {
            FloatRow("Position", Config.BannerHeight, v => Config.BannerHeight = v, 0.05f, 0.9f, "%.2f");
            HelpAfter("How far down the screen the banner sits: 0 is the top, 1 the bottom.");
            FloatRow("Size", Config.BannerScale, v => Config.BannerScale = v, 0.5f, 2f, "%.2fx");
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Edge marker", FontAwesomeIcon.MapMarkerAlt);

        ToggleRow("Mark the closest point", Config.ShowEdgeMarker, v => Config.ShowEdgeMarker = v,
            "A warning gem hovers over the point of the border nearest to you, with a ring pulsing on the ground beneath it.");

        ImGui.Dummy(new Vector2(0, 10));
        Theme.Callout(FontAwesomeIcon.Lightbulb, "Try it",
            "Press Practice FATE at the top of this window, then walk towards the edge to see the warning in the world.",
            Theme.Crystal);
    }

    // ------------------------------------------------------------------
    // General
    // ------------------------------------------------------------------

    private void DrawGeneral()
    {
        Theme.SectionHeader("FateLimit", FontAwesomeIcon.PowerOff);

        ToggleRow("Draw FATE borders", Config.Enabled, v => Config.Enabled = v, "The master switch. Also /fatelimit toggle.");
        FloatRow("Opacity", Config.Opacity, v => Config.Opacity = v, 0.1f, 1f, "%.2f");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("World", FontAwesomeIcon.Mountain);

        ToggleRow("Follow the terrain", Config.FollowTerrain, v =>
        {
            Config.FollowTerrain = v;
            plugin.Terrain.Clear();
        }, "Lays the border over hills and slopes. Off draws a flat ring at the FATE's own height, which can sink into slopes or float over dips.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Motion", FontAwesomeIcon.Running);

        Row("Reduced motion");
        var motion = (int)Config.ReducedMotion;
        if (Theme.SegmentedControl("##Motion", ref motion, "Follow Dalamud", "On", "Off"))
        {
            Config.ReducedMotion = (ReducedMotionMode)motion;
            changed = true;
        }
        HelpAfter("When on, borders stop shimmering, sweeping and bobbing, and the banner stops pulsing.");

        ImGui.Dummy(new Vector2(0, 10));
        Theme.Callout(FontAwesomeIcon.ShieldAlt, "Purely visual",
            "FateLimit only reads where FATEs are, which your map already shows, and draws over the screen. "
            + "It never moves your character or sends anything to the game, and it hides itself in cutscenes and GPose.",
            Theme.Good);
    }

    // ------------------------------------------------------------------
    // Diagnostics
    // ------------------------------------------------------------------

    private void DrawDiagnostics()
    {
        Theme.SectionHeader("Status", FontAwesomeIcon.Heartbeat);
        DrawStatus();

        ImGui.Dummy(new Vector2(0, 8));
        var logEvents = Config.LogEvents;
        if (Theme.ToggleRow("Log FATEs and warnings", ref logEvents,
                "Writes each FATE that appears (id, name, size, position) and each edge warning to the log below. "
                + "Useful when a border shows up in the wrong place. Leave it off otherwise."))
        {
            Config.LogEvents = logEvents;
            ConfigService.Save();
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Feedback", FontAwesomeIcon.CommentDots);

        Theme.Callout(FontAwesomeIcon.Bug, "Something not working?",
            "1. Turn on Log FATEs and warnings, make the problem happen again, then come back to this page.\n" +
            "2. Press Copy report. It copies your FateLimit version, a few settings, the FATEs around you and the log below. " +
            "Click individual lines first if you only want to share those.\n" +
            "3. Press Report a bug, tell me what you did, what you expected and what happened instead, " +
            "then paste the report into the issue.",
            Theme.Warn);

        ImGui.Dummy(new Vector2(0, 6));
        Theme.Callout(FontAwesomeIcon.Lightbulb, "Have an idea?",
            "Issues are also the place for feature requests. Press Suggest a feature and describe what you'd " +
            "like FateLimit to do and how it would help you. No log needed.\n" +
            "For quick questions, or to talk ideas through first, join the Discord.",
            Theme.Crystal);

        ImGui.Dummy(new Vector2(0, 8));
        if (Theme.PrimaryButton("Report a bug", new Vector2(140, 32)))
            OpenUrl(NewIssueUrl("bug", "[Bug] ",
                "**What happened?**\n\n\n**What did you expect?**\n\n\n**Steps to reproduce**\n1. \n\n" +
                "**Diagnostics report**\n<!-- Settings → Diagnostics → Copy report, then paste here -->\n"));

        ImGui.SameLine(0, 8);
        if (Theme.GhostButton("Suggest a feature", new Vector2(150, 32), Theme.Crystal))
            OpenUrl(NewIssueUrl("enhancement", "[Feature] ",
                "**What would you like FateLimit to do?**\n\n\n**How would it help you?**\n\n"));

        ImGui.SameLine(0, 8);
        if (Theme.GhostButton("Open issues", new Vector2(120, 32)))
            OpenUrl($"{Plugin.RepoUrl}/issues");

        ImGui.SameLine(0, 8);
        if (Theme.GhostButton("Discord", new Vector2(100, 32), Theme.DiscordBlurple))
            OpenUrl(Plugin.DiscordUrl);

        ImGui.Dummy(new Vector2(0, 12));
        Theme.SectionHeader("Log", FontAwesomeIcon.Terminal);

        RefreshLogView();

        if (Theme.SegmentedControl("##LogLevel", ref logLevelFilter, "All", "Info", "Warnings", "Errors"))
            RefreshLogView();

        ImGui.SameLine(0, 10);
        ImGui.SetNextItemWidth(Math.Max(ImGui.GetContentRegionAvail().X, 120f));
        if (ImGui.InputTextWithHint("##LogSearch", "Filter log lines", ref logSearch, 100))
            RefreshLogView();

        ImGui.Dummy(new Vector2(0, 4));
        var copyLabel = selectedLogLines.Count > 0 ? $"Copy report ({selectedLogLines.Count})" : "Copy report";
        if (Theme.PrimaryButton(copyLabel, new Vector2(150, 30)))
            ImGui.SetClipboardText(BuildReport());
        if (ImGui.IsItemHovered())
            Theme.Tooltip(selectedLogLines.Count > 0
                ? "Copies your setup and the selected lines, ready to paste into an issue."
                : $"Copies your setup and the last {ReportLineLimit} lines shown, ready to paste into an issue.");

        ImGui.SameLine(0, 8);
        if (Theme.GhostButton("Copy lines only", new Vector2(130, 30)))
            ImGui.SetClipboardText(string.Join("\n", ReportLines().Select(e => e.Format())));

        if (selectedLogLines.Count > 0)
        {
            ImGui.SameLine(0, 8);
            if (Theme.GhostButton("Deselect", new Vector2(90, 30)))
                selectedLogLines.Clear();
        }

        ImGui.SameLine(0, 8);
        if (Theme.DangerButton("Clear", new Vector2(70, 30)))
        {
            Plugin.Log.Clear();
            selectedLogLines.Clear();
            RefreshLogView();
        }

        ImGui.SameLine(0, 12);
        ImGui.AlignTextToFramePadding();
        ImGui.Checkbox("Auto-scroll", ref logAutoScroll);

        ImGui.Dummy(new Vector2(0, 4));
        DrawLogLines();
    }

    private void DrawStatus()
    {
        var hidden = Plugin.HiddenReason;
        var width = (ImGui.GetContentRegionAvail().X - 16f) / 3f;

        Theme.StatCard(FontAwesomeIcon.MapMarkedAlt, "FATEs nearby", plugin.Fates.Fates.Count(f => !f.IsPractice).ToString(), Theme.Gold, width);
        ImGui.SameLine(0, 8);
        Theme.StatCard(FontAwesomeIcon.Link, "Synced to", plugin.Fates.SyncedFateId == 0 ? "-" : $"#{plugin.Fates.SyncedFateId}", Theme.Crystal, width,
            "The FATE you're level-synced to, by id.");
        ImGui.SameLine(0, 8);
        Theme.StatCard(hidden is null ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.EyeSlash, "Drawing",
            !Config.Enabled ? "Off" : hidden is null ? "Yes" : "Hidden", hidden is null && Config.Enabled ? Theme.Good : Theme.Warn, width,
            hidden is null ? null : $"Hidden: {hidden}.");

        ImGui.Dummy(new Vector2(0, 6));

        var views = plugin.Overlay.Views;
        if (views.Count == 0)
        {
            ImGui.TextColored(Theme.TextFaint, "No FATEs around you right now. Use Practice FATE to try FateLimit anywhere.");
            return;
        }

        using var table = ImRaii.Table("##Fates", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        ImGui.TableSetupColumn("FATE", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Radius", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("To edge", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Shown", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableHeadersRow();

        foreach (var view in views.OrderBy(v => Math.Abs(v.EdgeDistance)))
        {
            var fate = view.Fate;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(fate.Id == plugin.Fates.SyncedFateId ? Theme.GoldBright : Theme.Text,
                fate.IsPractice ? fate.Name : $"{fate.Name} (#{fate.Id}, lv{fate.Level})");
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextMuted, fate.State.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextMuted, $"{fate.Radius:0.0} y");
            ImGui.TableNextColumn();
            ImGui.TextColored(view.Closeness > 0 ? Theme.Bad : Theme.TextMuted,
                view.Inside ? $"{view.EdgeDistance:0.0} y in" : $"{-view.EdgeDistance:0.0} y out");
            ImGui.TableNextColumn();
            ImGui.TextColored(view.Visibility > 0 ? Theme.Good : Theme.TextFaint, view.Visibility > 0 ? "Yes" : "No");
        }
    }

    private void DrawLogLines()
    {
        var height = Math.Max(ImGui.GetContentRegionAvail().Y - 10f, 220f);
        using var card = Theme.Card("##LogCard", new Vector2(0, height));
        if (!card) return;

        using var rows = Theme.Region("##LogRows", Vector2.Zero, ImGuiWindowFlags.HorizontalScrollbar);
        if (!rows) return;

        if (visibleLogLines.Count == 0)
        {
            ImGui.TextColored(Theme.TextFaint, logViewKey.Level == 0 && logSearch.Length == 0
                ? "Nothing logged yet."
                : "No lines match the current filter.");
            return;
        }

        for (var i = 0; i < visibleLogLines.Count; i++)
        {
            var entry = visibleLogLines[i];
            var selected = selectedLogLines.Contains(entry);

            using (ImRaii.PushColor(ImGuiCol.Text, LevelColor(entry.Level)))
            {
                if (ImGui.Selectable($"{entry.Format()}##log{i}", selected))
                {
                    if (!selectedLogLines.Remove(entry))
                        selectedLogLines.Add(entry);
                }
            }

            using var popup = ImRaii.ContextPopupItem($"##logctx{i}");
            if (popup.Success && ImGui.Selectable("Copy this line"))
                ImGui.SetClipboardText(entry.Format());
        }

        if (logAutoScroll && scrolledRevision != Plugin.Log.Revision)
        {
            ImGui.SetScrollHereY(1f);
            scrolledRevision = Plugin.Log.Revision;
        }
    }

    private void RefreshLogView()
    {
        var key = (Plugin.Log.Revision, logLevelFilter, logSearch);
        if (key == logViewKey) return;
        logViewKey = key;

        var minLevel = (LogLevel)logLevelFilter;
        visibleLogLines = Plugin.Log.Snapshot()
            .Where(e => e.Level >= minLevel)
            .Where(e => logSearch.Length == 0
                        || e.Message.Contains(logSearch, StringComparison.OrdinalIgnoreCase)
                        || (e.Exception?.Contains(logSearch, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        // Lines that scrolled out of the buffer can't be copied any more.
        var all = Plugin.Log.Snapshot().ToHashSet();
        selectedLogLines.RemoveWhere(e => !all.Contains(e));
    }

    private IEnumerable<LogEntry> ReportLines()
    {
        return selectedLogLines.Count > 0
            ? selectedLogLines.OrderBy(e => e.Time)
            : visibleLogLines.Skip(Math.Max(0, visibleLogLines.Count - ReportLineLimit));
    }

    private string BuildReport()
    {
        var sb = new StringBuilder();

        sb.AppendLine("### FateLimit diagnostics");
        sb.AppendLine($"- FateLimit: {Changelog.CurrentVersion}");
        sb.AppendLine($"- Dalamud: {typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetName().Version}");
        sb.AppendLine($"- Game language: {Plugin.ClientState.ClientLanguage}, territory {Plugin.ClientState.TerritoryType}");
        sb.AppendLine($"- Settings: enabled {OnOff(Config.Enabled)}, style {Config.Style}, visibility {Config.Visibility}, "
                      + $"warn {Config.WarnDistance:0}y, synced only {OnOff(Config.OnlyWhenSynced)}, from outside {OnOff(Config.WarnFromOutside)}, "
                      + $"terrain {OnOff(Config.FollowTerrain)}, focus {Config.FocusRadius:0}y");
        sb.AppendLine($"- State: hidden {Plugin.HiddenReason ?? "no"}, synced FATE {plugin.Fates.SyncedFateId}, "
                      + $"frame errors {plugin.FrameErrors}, terrain rings {plugin.Terrain.CachedRings}, raycasts {plugin.Terrain.Raycasts}");
        foreach (var view in plugin.Overlay.Views)
            sb.AppendLine($"- FATE {view.Fate.Id} \"{view.Fate.Name}\" {view.Fate.State}: radius {view.Fate.Radius:0.0}, "
                          + $"edge {view.EdgeDistance:0.0}, closeness {view.Closeness:0.00}, visible {view.Visibility:0.00}");
        sb.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("```text");
        foreach (var entry in ReportLines())
            sb.AppendLine(entry.Format());
        sb.AppendLine("```");

        return sb.ToString();

        static string OnOff(bool value) => value ? "on" : "off";
    }

    private static Vector4 LevelColor(LogLevel level) => level switch
    {
        LogLevel.Error => Theme.Bad,
        LogLevel.Warning => Theme.Warn,
        LogLevel.Info => Theme.Text,
        _ => Theme.TextMuted,
    };

    private static string NewIssueUrl(string label, string title, string body)
        => $"{Plugin.RepoUrl}/issues/new?labels={label}&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";

    // ------------------------------------------------------------------
    // About
    // ------------------------------------------------------------------

    private void DrawAbout()
    {
        Theme.SectionHeader("FateLimit", FontAwesomeIcon.DrawPolygon);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, $"Version {Changelog.CurrentVersion}  ·  by GitHixy");
        ImGui.SameLine(0, 12);
        if (Theme.GhostButton("What's new", new Vector2(120, 0), Theme.Gold))
            plugin.ChangelogWindow.ShowAll();

        ImGui.Dummy(new Vector2(0, 6));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Min(ImGui.GetContentRegionAvail().X, 440f));
        ImGui.TextColored(Theme.Text,
            "Draws the border of FATE areas in the world, and warns you before you step out of one " +
            "and lose your level sync.");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(new Vector2(0, 14));
        Theme.SectionHeader("Support", FontAwesomeIcon.Heart);

        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Alpha(Theme.PatreonCoral, 0.85f))
                   .Push(ImGuiCol.ButtonHovered, Theme.Lighten(Theme.PatreonCoral, 0.12f))
                   .Push(ImGuiCol.ButtonActive, new Vector4(0.85f, 0.20f, 0.24f, 1.0f))
                   .Push(ImGuiCol.Text, Theme.Text))
        {
            if (ImGui.Button("Support me on Patreon", new Vector2(230, 34)))
                OpenUrl(Plugin.PatreonUrl);
        }

        ImGui.SameLine(0, 10);
        if (Theme.GhostButton("Join the Discord", new Vector2(150, 34), Theme.DiscordBlurple))
            OpenUrl(Plugin.DiscordUrl);

        ImGui.SameLine(0, 10);
        if (Theme.GhostButton("GitHub", new Vector2(110, 34)))
            OpenUrl(Plugin.RepoUrl);

        ImGui.Dummy(new Vector2(0, 2));
        if (Theme.GhostButton("Report a problem", new Vector2(150, 30)))
            section = Array.FindIndex(Sections, s => s.Label == "Diagnostics");

        ImGui.Dummy(new Vector2(0, 14));
        Theme.SectionHeader("Shortcuts", FontAwesomeIcon.Keyboard);

        ShortcutRow("/fatelimit", "Open these settings (also /fl)");
        ShortcutRow("/fatelimit test", "Place or remove a practice FATE around you");
        ShortcutRow("/fatelimit toggle", "Turn borders on or off");

        ImGui.Dummy(new Vector2(0, 14));
        Theme.SectionHeader("Also by GitHixy", FontAwesomeIcon.Cubes);
        ShortcutRow("LootView", "Live loot tracker with history and statistics");
        ShortcutRow("HitSpark", "Animated, styleable combat text");
    }

    private static void ShortcutRow(string command, string description)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var ts = ImGui.CalcTextSize(command);

        dl.AddRectFilled(p, new Vector2(p.X + ts.X + 14, p.Y + ts.Y + 5), Theme.U32(Theme.Panel, 0.95f), 4f);
        dl.AddRect(p, new Vector2(p.X + ts.X + 14, p.Y + ts.Y + 5), Theme.U32(Theme.Line), 4f, ImDrawFlags.None, 1f);
        dl.AddText(new Vector2(p.X + 7, p.Y + 2.5f), Theme.U32(Theme.GoldBright), command);

        ImGui.Dummy(new Vector2(Math.Max(ts.X + 14, 150f), ts.Y + 5));
        ImGui.SameLine(0, 12);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, description);
        ImGui.Dummy(new Vector2(0, 2));
    }

    // ------------------------------------------------------------------
    // Setting rows
    // ------------------------------------------------------------------

    private static void Row(string label)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, label);
        ImGui.SameLine(LabelWidth);
    }

    private static void Wrapped(string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Min(ImGui.GetContentRegionAvail().X, 520f));
        ImGui.TextColored(Theme.TextMuted, text);
        ImGui.PopTextWrapPos();
    }

    private static void HelpAfter(string help)
    {
        ImGui.SameLine(0, 8);
        Theme.HelpMarker(help);
    }

    private void FloatRow(string label, float value, Action<float> set, float min, float max, string format)
    {
        Row(label);
        var v = value;
        if (Theme.Slider($"##{label}", ref v, min, max, format, ControlWidth))
        {
            set(Math.Clamp(v, min, max));
            changed = true;
        }
    }

    private void ToggleRow(string label, bool value, Action<bool> set, string? help = null)
    {
        Row(label);
        var v = value;
        if (Theme.Toggle($"##tgl_{label}", ref v))
        {
            set(v);
            changed = true;
        }

        if (help is not null)
            HelpAfter(help);
    }

    private void ColorRow(string label, Vector4 value, Action<Vector4> set)
    {
        Row(label);
        var v = value;
        if (ImGui.ColorEdit4($"##{label}", ref v, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
        {
            set(v);
            changed = true;
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to open {Url}", url);
        }
    }
}
