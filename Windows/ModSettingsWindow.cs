using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using JustDance.Services;

namespace JustDance.Windows;

public sealed class ModSettingsWindow : Window
{
    private static readonly Vector4 AccentTeal = new(0.28f, 0.82f, 0.78f, 1f);
    private static readonly Vector4 DangerRed = new(0.82f, 0.25f, 0.25f, 1f);
    private static readonly Vector4 MutedText = new(0.62f, 0.65f, 0.70f, 1f);

    private readonly Configuration configuration;
    private readonly PenumbraService penumbraService;
    private readonly AnimationPlaybackService playbackService;

    private string? targetEntryId;
    private int priorityDraft;
    private string? errorMessage;

    public ModSettingsWindow(
        Configuration configuration,
        PenumbraService penumbraService,
        AnimationPlaybackService playbackService)
        : base("Mod Settings###JustDanceModSettings")
    {
        this.configuration = configuration;
        this.penumbraService = penumbraService;
        this.playbackService = playbackService;

        Size = new Vector2(430f, 520f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340f, 300f),
            MaximumSize = new Vector2(900f, 1000f),
        };
    }

    public void Open(DanceEntry entry)
    {
        targetEntryId = entry.Id;
        WindowName = $"Mod Settings - {entry.Name}###JustDanceModSettings";
        errorMessage = null;
        playbackService.InvalidateModSettingsCache(entry.ModDirectory);
        var snapshot = playbackService.GetModSettingsSnapshot(entry, forceRefresh: true);
        priorityDraft = snapshot.Priority;
        IsOpen = true;
        BringToFront();
    }

    public override void Draw()
    {
        var entry = string.IsNullOrEmpty(targetEntryId)
            ? null
            : configuration.Entries.FirstOrDefault(e => string.Equals(e.Id, targetEntryId, StringComparison.Ordinal));

        if (entry == null)
        {
            IsOpen = false;
            return;
        }

        var snapshot = playbackService.GetModSettingsSnapshot(entry);

        // Header bar: mod identity + Refresh + Open in Penumbra
        float rightBtnsW = 148f;
        float leftW = Math.Max(100f, ImGui.GetContentRegionAvail().X - rightBtnsW);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + leftW);
        ImGui.TextColored(AccentTeal, entry.ModName);
        ImGui.PopTextWrapPos();
        if (snapshot.IsValid && !string.IsNullOrEmpty(snapshot.CollectionName))
            ImGui.TextColored(MutedText, $"Collection: {snapshot.CollectionName}");
        else
            ImGui.TextColored(DangerRed, "Penumbra settings unavailable");
        ImGui.EndGroup();

        ImGui.SameLine(Math.Max(10f, ImGui.GetWindowContentRegionMax().X - rightBtnsW));
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{FontAwesomeIcon.SyncAlt.ToIconString()}##refreshMod", new Vector2(28f, 0f)))
        {
            errorMessage = null;
            playbackService.InvalidateModSettingsCache(entry.ModDirectory);
            snapshot = playbackService.GetModSettingsSnapshot(entry, forceRefresh: true);
            priorityDraft = snapshot.Priority;
        }
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Refresh settings from Penumbra");

        ImGui.SameLine();
        if (ImGui.Button("Open in Penumbra", new Vector2(114f, 0f)))
        {
            if (!penumbraService.OpenModInPenumbra(entry.ModDirectory, entry.ModName))
                errorMessage = "Could not open mod in Penumbra.";
        }

        if (!string.IsNullOrEmpty(errorMessage))
        {
            ImGui.Spacing();
            ImGui.TextColored(DangerRed, errorMessage);
        }

        ImGui.Separator();

        if (!snapshot.IsValid)
        {
            ImGui.TextColored(MutedText, "Make sure Penumbra is loaded and an active collection is available.");
            return;
        }

        // Enabled + Priority controls mirroring Penumbra
        bool enabled = snapshot.IsEnabled;
        if (ImGui.Checkbox("Enabled##penEnabled", ref enabled))
        {
            errorMessage = null;
            playbackService.SetModEnabled(entry, enabled, err => errorMessage = err);
            snapshot = playbackService.GetModSettingsSnapshot(entry, forceRefresh: true);
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX() + 12f, ImGui.GetWindowContentRegionMax().X - 180f));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Priority");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        if (!ImGui.IsAnyItemActive())
            priorityDraft = snapshot.Priority;
        ImGui.InputInt("##penPrio", ref priorityDraft, 1, 10);
        if (ImGui.IsItemDeactivatedAfterEdit() && priorityDraft != snapshot.Priority)
        {
            errorMessage = null;
            playbackService.SetModPriority(entry, priorityDraft, err => errorMessage = err);
            snapshot = playbackService.GetModSettingsSnapshot(entry, forceRefresh: true);
            priorityDraft = snapshot.Priority;
        }

        ImGui.Separator();
        ImGui.TextColored(MutedText, "Click a pin icon to show or hide a setting next to the dance name.");
        ImGui.Spacing();

        if (!ImGui.BeginChild("##modSettingsGroups", Vector2.Zero, true))
        {
            ImGui.EndChild();
            return;
        }

        if (snapshot.Groups.Count == 0)
        {
            ImGui.TextColored(MutedText, "This mod has no option groups.");
        }
        else
        {
            for (int gi = 0; gi < snapshot.Groups.Count; gi++)
            {
                DrawOptionGroup(entry, snapshot.Groups[gi], gi);
                if (gi < snapshot.Groups.Count - 1)
                    ImGui.Separator();
            }
        }

        ImGui.EndChild();
    }

    private void DrawOptionGroup(DanceEntry entry, AnimationPlaybackService.ModOptionGroupView group, int groupIndex)
    {
        ImGui.PushID($"grp_{groupIndex}");

        if (group.GroupType == 0)
        {
            DrawSingleGroup(entry, group);
        }
        else if (group.GroupType == 1)
        {
            DrawMultiGroup(entry, group);
        }
        else
        {
            var typeLabel = group.GroupType == 2 ? "IMC" : "Combining";
            ImGui.TextUnformatted(group.GroupName);
            ImGui.SameLine();
            ImGui.TextColored(MutedText, $"[{typeLabel} - edit in Penumbra]");
        }

        ImGui.PopID();
    }

    private void DrawSingleGroup(DanceEntry entry, AnimationPlaybackService.ModOptionGroupView group)
    {
        bool isPinned = IsSettingPinned(entry, group.GroupName, isMulti: false, optionName: null);
        DrawPinButton("##pinSingle", isPinned, () => TogglePin(entry, group.GroupName, isMulti: false, optionName: null));

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(group.GroupName);
        if (!string.IsNullOrWhiteSpace(group.Description) && ImGui.IsItemHovered())
            ImGui.SetTooltip(group.Description);

        var current = group.SelectedOptions.FirstOrDefault(o =>
                          group.Options.Contains(o, StringComparer.OrdinalIgnoreCase))
                      ?? group.Options.FirstOrDefault()
                      ?? "";

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo($"##combo", current))
        {
            foreach (var opt in group.Options)
            {
                bool selected = string.Equals(current, opt, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(opt, selected))
                {
                    errorMessage = null;
                    playbackService.SetSingleGroupOption(entry, group.GroupName, opt, err => errorMessage = err);
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
    }

    private void DrawMultiGroup(DanceEntry entry, AnimationPlaybackService.ModOptionGroupView group)
    {
        ImGui.TextUnformatted(group.GroupName);
        if (!string.IsNullOrWhiteSpace(group.Description) && ImGui.IsItemHovered())
            ImGui.SetTooltip(group.Description);

        if (group.Options.Count == 0)
        {
            ImGui.TextColored(MutedText, "  (No options)");
            return;
        }

        foreach (var opt in group.Options)
        {
            ImGui.PushID(opt);

            bool isPinned = IsSettingPinned(entry, group.GroupName, isMulti: true, optionName: opt);
            DrawPinButton("##pinOpt", isPinned, () => TogglePin(entry, group.GroupName, isMulti: true, optionName: opt));

            ImGui.SameLine();
            bool isChecked = group.SelectedOptions.Contains(opt, StringComparer.OrdinalIgnoreCase);
            if (ImGui.Checkbox(opt, ref isChecked))
            {
                errorMessage = null;
                playbackService.SetMultiGroupOption(entry, group.GroupName, opt, isChecked, err => errorMessage = err);
            }

            ImGui.PopID();
        }
    }

    private static void DrawPinButton(string id, bool isPinned, Action onClick)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, isPinned ? AccentTeal : MutedText);
        if (ImGui.Button($"{FontAwesomeIcon.Thumbtack.ToIconString()}{id}", new Vector2(24f, ImGui.GetFrameHeight())))
            onClick();
        ImGui.PopStyleColor(2);
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isPinned ? "Pinned to main window (click to unpin)" : "Pin to main window");
    }

    private static bool IsSettingPinned(DanceEntry entry, string groupName, bool isMulti, string? optionName)
    {
        return entry.PinnedControls.Any(p =>
            p.IsMulti == isMulti &&
            string.Equals(p.GroupName, groupName, StringComparison.Ordinal) &&
            (!isMulti || string.Equals(p.OptionName, optionName, StringComparison.Ordinal)));
    }

    private void TogglePin(DanceEntry entry, string groupName, bool isMulti, string? optionName)
    {
        int existingIdx = entry.PinnedControls.FindIndex(p =>
            p.IsMulti == isMulti &&
            string.Equals(p.GroupName, groupName, StringComparison.Ordinal) &&
            (!isMulti || string.Equals(p.OptionName, optionName, StringComparison.Ordinal)));

        if (existingIdx >= 0)
        {
            entry.PinnedControls.RemoveAt(existingIdx);
        }
        else
        {
            entry.PinnedControls.Add(new PinnedModControl
            {
                GroupName = groupName,
                IsMulti = isMulti,
                OptionName = optionName,
            });
        }

        configuration.Save();
    }
}
