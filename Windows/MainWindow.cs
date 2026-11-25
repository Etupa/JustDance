using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using JustDance.Services;

namespace JustDance.Windows;

public sealed class MainWindow : Window
{
    private const string PayloadEntry = "JD_ENTRY";
    private const string PayloadFolder = "JD_FOLDER";

    private static readonly Vector4 AccentTeal = new(0.28f, 0.82f, 0.78f, 1f);
    private static readonly Vector4 FolderGold = new(0.92f, 0.76f, 0.34f, 1f);
    private static readonly Vector4 DangerRed = new(0.82f, 0.25f, 0.25f, 1f);
    private static readonly Vector4 MutedText = new(0.62f, 0.65f, 0.70f, 1f);

    private readonly Configuration configuration;
    private readonly PenumbraService penumbraService;
    private readonly FolderScannerService scannerService;
    private readonly AnimationPlaybackService playbackService;
    private readonly PenumbraFolderPickerWindow folderPickerWindow;
    private readonly ModSettingsWindow modSettingsWindow;

    private string searchFilter = "";
    private bool favoritesOnly;
    private string? statusMessage;

    private string? dragEntryId;
    private string? dragFolderId;

    // Modal / popup editing state
    private string? pendingNewFolderParentId;
    private string newFolderName = "New Folder";
    private bool openNewFolderPopup;

    private string? renamingId;
    private bool renamingIsFolder;
    private string renameBuffer = "";
    private bool openRenamePopup;

    public MainWindow(
        Configuration configuration,
        PenumbraService penumbraService,
        FolderScannerService scannerService,
        AnimationPlaybackService playbackService,
        PenumbraFolderPickerWindow folderPickerWindow,
        ModSettingsWindow modSettingsWindow)
        : base("JustDance###JustDanceMain")
    {
        this.configuration = configuration;
        this.penumbraService = penumbraService;
        this.scannerService = scannerService;
        this.playbackService = playbackService;
        this.folderPickerWindow = folderPickerWindow;
        this.modSettingsWindow = modSettingsWindow;

        Size = new Vector2(490f, 590f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380f, 380f),
            MaximumSize = new Vector2(1100f, 1200f),
        };
    }

    public override void OnOpen()
    {
        base.OnOpen();
        playbackService.RefreshModOptionStates();
    }

    public override void Draw()
    {
        DrawFolderSelectorBar();
        DrawToolbar();
        DrawStatusBanner();

        ImGui.Separator();

        DrawEntriesArea();
        DrawPopups();

        if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            dragEntryId = null;
            dragFolderId = null;
        }
    }

    private void DrawFolderSelectorBar()
    {
        bool scanning = scannerService.IsScanning;
        bool hasSelection = configuration.SelectedPenumbraFolder != null;

        float rightButtonsWidth = 152f;
        float pathWidth = Math.Max(100f, ImGui.GetContentRegionAvail().X - rightButtonsWidth);

        ImGui.AlignTextToFramePadding();
        if (hasSelection)
        {
            var rawLabel = string.IsNullOrEmpty(configuration.SelectedPenumbraFolder)
                ? "Penumbra: <All Mods>"
                : $"Penumbra: {configuration.SelectedPenumbraFolder}";
            ImGui.PushItemWidth(pathWidth);
            ImGui.TextUnformatted(TruncateMiddle(rawLabel, 42));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(rawLabel);
            ImGui.PopItemWidth();
        }
        else
        {
            ImGui.TextColored(MutedText, "No Penumbra folder selected");
        }

        ImGui.SameLine(Math.Max(10f, ImGui.GetWindowContentRegionMax().X - rightButtonsWidth));

        if (ImGui.Button("Select...", new Vector2(62f, 0f)))
            folderPickerWindow.Open(configuration.SelectedPenumbraFolder);

        ImGui.SameLine();

        if (scanning || !hasSelection)
            ImGui.BeginDisabled();

        if (ImGui.Button(scanning ? "..." : "Scan", new Vector2(48f, 0f)))
            TriggerScan();

        if (scanning || !hasSelection)
            ImGui.EndDisabled();

        ImGui.SameLine();

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{FontAwesomeIcon.Cog.ToIconString()}##jdSettings", new Vector2(28f, 0f)))
            ImGui.OpenPopup("JustDanceSettingsPopup");
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Settings");

        DrawSettingsPopup();
    }

    private void DrawSettingsPopup()
    {
        if (!ImGui.BeginPopup("JustDanceSettingsPopup"))
            return;

        bool penumbraOk = penumbraService.EnsureAvailable();
        ImGui.TextColored(
            penumbraOk ? AccentTeal : DangerRed,
            penumbraOk ? $"Penumbra IPC: Connected (v{penumbraService.ApiVersion})" : "Penumbra IPC: Not available");

        ImGui.Separator();

        var mirror = configuration.MirrorPenumbraSubfolders;
        if (ImGui.Checkbox("Mirror Penumbra subfolders on scan", ref mirror))
        {
            configuration.MirrorPenumbraSubfolders = mirror;
            configuration.Save();
        }

        var prune = configuration.PruneMissingOnScan;
        if (ImGui.Checkbox("Remove missing mods on rescan", ref prune))
        {
            configuration.PruneMissingOnScan = prune;
            configuration.Save();
        }

        var alwaysJump = configuration.AlwaysJumpBeforeStart;
        if (ImGui.Checkbox("Always jump to stop before starting", ref alwaysJump))
        {
            configuration.AlwaysJumpBeforeStart = alwaysJump;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When unchecked, JustDance switches emotes seamlessly without jumping unless sitting.");

        var chat = configuration.ShowChatMessages;
        if (ImGui.Checkbox("Show chat messages", ref chat))
        {
            configuration.ShowChatMessages = chat;
            configuration.Save();
        }

        ImGui.Separator();
        if (ImGui.Button("Clear All Entries & Folders"))
        {
            configuration.Entries.Clear();
            configuration.Folders.Clear();
            configuration.Save();
            statusMessage = "Cleared library.";
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawToolbar()
    {
        float controlsWidth = 158f;
        float searchWidth = Math.Max(90f, ImGui.GetContentRegionAvail().X - controlsWidth);

        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##SearchDances", "Search...", ref searchFilter, 128);

        ImGui.SameLine();

        ImGui.PushFont(UiBuilder.IconFont);
        if (favoritesOnly)
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.84f, 0.25f, 1f));
        if (ImGui.Button($"{FontAwesomeIcon.Star.ToIconString()}##FavFilter", new Vector2(28f, 0f)))
            favoritesOnly = !favoritesOnly;
        if (favoritesOnly)
            ImGui.PopStyleColor();
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(favoritesOnly ? "Showing favorites only" : "Filter favorites");

        ImGui.SameLine();

        if (ImGui.Button("+ Folder", new Vector2(64f, 0f)))
        {
            pendingNewFolderParentId = null;
            newFolderName = "New Folder";
            openNewFolderPopup = true;
        }

        ImGui.SameLine();

        bool isPlaying = !string.IsNullOrEmpty(playbackService.ActiveEntryId);
        if (isPlaying)
            ImGui.PushStyleColor(ImGuiCol.Button, DangerRed);

        if (ImGui.Button("Stop", new Vector2(48f, 0f)))
        {
            playbackService.Stop();
            statusMessage = null;
        }

        if (isPlaying)
            ImGui.PopStyleColor();
    }

    private void DrawStatusBanner()
    {
        if (scannerService.IsScanning)
        {
            ImGui.TextColored(AccentTeal, $"Scanning Penumbra folder... {scannerService.DiscoveredCount} dance mod(s) found");
            return;
        }

        if (!string.IsNullOrEmpty(playbackService.ActiveEntryName))
        {
            ImGui.TextColored(AccentTeal, $"Playing: {playbackService.ActiveEntryName}");
            return;
        }

        if (!string.IsNullOrEmpty(playbackService.LastError))
        {
            ImGui.TextColored(DangerRed, playbackService.LastError);
            return;
        }

        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.TextColored(MutedText, statusMessage);
        }
        else
        {
            ImGui.TextColored(MutedText, $"{configuration.Entries.Count} dance(s) in {configuration.Folders.Count} folder(s)  |  Right-click: Mod Settings");
        }
    }

    private void DrawEntriesArea()
    {
        if (!ImGui.BeginChild("JustDanceListArea", Vector2.Zero, false))
        {
            ImGui.EndChild();
            return;
        }

        if (configuration.Entries.Count == 0 && configuration.Folders.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped("Click Select... to choose one of your Penumbra folders (for example 03_Dance) and scan the dance mods inside it.");
            ImGui.EndChild();
            return;
        }

        bool isFiltering = !string.IsNullOrWhiteSpace(searchFilter) || favoritesOnly;
        if (isFiltering)
        {
            DrawFilteredList();
        }
        else
        {
            DrawFolderContents(parentFolderId: null, depth: 0);
            DrawRootDropZone();
        }

        ImGui.EndChild();
    }

    private void DrawFilteredList()
    {
        var query = searchFilter.Trim();
        var folderMap = configuration.Folders.ToDictionary(f => f.Id, f => f);

        var matches = configuration.Entries
            .Where(e =>
            {
                if (favoritesOnly && !e.IsFavorite)
                    return false;
                if (string.IsNullOrEmpty(query))
                    return true;
                if (e.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (e.ModName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (e.PenumbraFolder.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            })
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            ImGui.TextDisabled("No matching entries.");
            return;
        }

        foreach (var entry in matches)
        {
            var breadcrumb = BuildFolderBreadcrumb(entry.FolderId, folderMap);
            DrawEntryRow(entry, depth: 0, breadcrumbHint: breadcrumb);
        }
    }

    private void DrawFolderContents(string? parentFolderId, int depth)
    {
        var childFolders = configuration.Folders
            .Where(f => string.Equals(f.ParentFolderId, parentFolderId, StringComparison.Ordinal))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var folder in childFolders)
            DrawFolderNode(folder, depth);

        var childEntries = configuration.Entries
            .Where(e => string.Equals(e.FolderId, parentFolderId, StringComparison.Ordinal))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var entry in childEntries)
            DrawEntryRow(entry, depth);
    }

    private void DrawFolderNode(DanceFolder folder, int depth)
    {
        ImGui.PushID(folder.Id);

        if (depth > 0)
            ImGui.Indent(depth * 14f);

        int itemCount = CountEntriesRecursive(folder.Id);
        var chevron = folder.IsCollapsed ? FontAwesomeIcon.ChevronRight : FontAwesomeIcon.ChevronDown;
        var folderIcon = folder.IsCollapsed ? FontAwesomeIcon.Folder : FontAwesomeIcon.FolderOpen;

        var rowPos = ImGui.GetCursorScreenPos();
        float availW = ImGui.GetContentRegionAvail().X;
        float rowH = ImGui.GetFrameHeight();

        if (ImGui.InvisibleButton($"##folderHit_{folder.Id}", new Vector2(Math.Max(20f, availW), rowH)))
        {
            folder.IsCollapsed = !folder.IsCollapsed;
            configuration.Save();
        }

        bool hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.GetWindowDrawList().AddRectFilled(
                rowPos,
                new Vector2(rowPos.X + availW, rowPos.Y + rowH),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.06f)),
                4f);
        }

        HandleFolderDragAndDrop(folder);
        DrawFolderContextMenu(folder);

        ImGui.SetCursorScreenPos(new Vector2(rowPos.X + 4f, rowPos.Y + 3f));
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(MutedText, chevron.ToIconString());
        ImGui.SameLine(0f, 6f);
        ImGui.TextColored(FolderGold, folderIcon.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine(0f, 6f);
        ImGui.TextUnformatted(folder.Name);
        ImGui.SameLine(0f, 6f);
        ImGui.TextColored(MutedText, $"({itemCount})");

        ImGui.SetCursorScreenPos(new Vector2(rowPos.X, rowPos.Y + rowH + 1f));

        if (depth > 0)
            ImGui.Unindent(depth * 14f);

        if (!folder.IsCollapsed)
            DrawFolderContents(folder.Id, depth + 1);

        ImGui.PopID();
    }

    private void DrawEntryRow(DanceEntry entry, int depth, string? breadcrumbHint = null)
    {
        ImGui.PushID(entry.Id);

        if (depth > 0)
            ImGui.Indent(depth * 14f);

        bool isActive = string.Equals(playbackService.ActiveEntryId, entry.Id, StringComparison.Ordinal);
        bool hasMusicToggle = entry.HasMusicToggle;

        const float comboControlW = 86f;
        float speakerBtnW = hasMusicToggle ? 26f : 0f;
        float starBtnW = 24f;
        float menuBtnW = 22f;

        AnimationPlaybackService.ModSettingsSnapshot? snapshot = null;
        var pinnedWidths = entry.PinnedControls.Count > 0 ? new float[entry.PinnedControls.Count] : Array.Empty<float>();
        var pinnedLabels = entry.PinnedControls.Count > 0 ? new string[entry.PinnedControls.Count] : Array.Empty<string>();
        float pinnedClusterW = 0f;

        if (entry.PinnedControls.Count > 0)
        {
            snapshot = playbackService.GetModSettingsSnapshot(entry);
            float checkBoxChromeW = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + 4f;
            for (int pi = 0; pi < entry.PinnedControls.Count; pi++)
            {
                var p = entry.PinnedControls[pi];
                if (p.IsMulti)
                {
                    var rawLabel = !string.IsNullOrEmpty(p.OptionName) ? p.OptionName : p.GroupName;
                    var shortLabel = rawLabel.Length <= 14 ? rawLabel : rawLabel[..13] + "…";
                    pinnedLabels[pi] = shortLabel;
                    pinnedWidths[pi] = ImGui.CalcTextSize(shortLabel).X + checkBoxChromeW;
                }
                else
                {
                    pinnedWidths[pi] = comboControlW;
                }
                pinnedClusterW += pinnedWidths[pi] + 4f;
            }
        }

        float rightClusterW = starBtnW + menuBtnW + 4f
                            + pinnedClusterW
                            + (hasMusicToggle ? speakerBtnW + 2f : 0f);

        float availW = ImGui.GetContentRegionAvail().X;
        float mainBtnWidth = Math.Max(60f, availW - rightClusterW);
        float rowH = ImGui.GetFrameHeight();
        var rowPos = ImGui.GetCursorScreenPos();

        if (isActive)
        {
            ImGui.GetWindowDrawList().AddRectFilled(
                rowPos,
                new Vector2(rowPos.X + mainBtnWidth, rowPos.Y + rowH),
                ImGui.GetColorU32(new Vector4(AccentTeal.X, AccentTeal.Y, AccentTeal.Z, 0.18f)),
                4f);
        }

        // Left-click: play using Penumbra's current settings for this mod.
        if (ImGui.InvisibleButton($"##play_{entry.Id}", new Vector2(mainBtnWidth, rowH)))
        {
            statusMessage = null;
            playbackService.PlayEntry(entry, err => statusMessage = err);
        }

        // Right-click: open JustDance Mod Settings sub-window for this entry.
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            statusMessage = null;
            modSettingsWindow.Open(entry);
        }

        bool hovered = ImGui.IsItemHovered();
        if (hovered && !isActive)
        {
            ImGui.GetWindowDrawList().AddRectFilled(
                rowPos,
                new Vector2(rowPos.X + mainBtnWidth, rowPos.Y + rowH),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.06f)),
                4f);
        }

        if (hovered)
        {
            var penFolder = string.IsNullOrEmpty(entry.PenumbraFolder) ? "<Root>" : entry.PenumbraFolder;
            var musicInfo = hasMusicToggle
                ? $"\nPenumbra Music ({entry.MusicGroupName}): {(entry.IsMusicEnabled ? "On" : "Off")}"
                : "";
            ImGui.SetTooltip($"Mod: {entry.ModName}\nPenumbra Folder: {penFolder}{musicInfo}\nLeft-click: Play  |  Right-click: Mod Settings");
        }

        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullId))
        {
            dragEntryId = entry.Id;
            dragFolderId = null;
            ImGui.SetDragDropPayload(PayloadEntry, ReadOnlySpan<byte>.Empty, ImGuiCond.None);
            ImGui.TextUnformatted(entry.Name);
            ImGui.EndDragDropSource();
        }

        float cursorX = rowPos.X + mainBtnWidth + 4f;

        if (snapshot != null)
        {
            for (int pi = 0; pi < entry.PinnedControls.Count; pi++)
            {
                var pinned = entry.PinnedControls[pi];
                var groupView = snapshot.Groups.FirstOrDefault(g =>
                    string.Equals(g.GroupName, pinned.GroupName, StringComparison.Ordinal));

                ImGui.SetCursorScreenPos(new Vector2(cursorX, rowPos.Y));
                if (pinned.IsMulti)
                {
                    var optName = pinned.OptionName ?? "";
                    bool checkedVal = groupView != null &&
                                      groupView.SelectedOptions.Contains(optName, StringComparer.OrdinalIgnoreCase);

                    if (ImGui.Checkbox($"{pinnedLabels[pi]}##pin_{entry.Id}_{pi}", ref checkedVal))
                    {
                        statusMessage = null;
                        playbackService.SetMultiGroupOption(
                            entry, pinned.GroupName, optName, checkedVal, err => statusMessage = err);
                    }
                    else if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip($"{pinned.GroupName}: {optName}");
                    }
                }
                else
                {
                    var options = groupView?.Options ?? new List<string>();
                    var currentOpt = groupView?.SelectedOptions.FirstOrDefault(s =>
                                         options.Contains(s, StringComparer.OrdinalIgnoreCase))
                                     ?? options.FirstOrDefault()
                                     ?? "-";

                    ImGui.SetNextItemWidth(pinnedWidths[pi]);
                    if (ImGui.BeginCombo($"##pin_{entry.Id}_{pi}", currentOpt))
                    {
                        foreach (var opt in options)
                        {
                            bool selected = string.Equals(currentOpt, opt, StringComparison.OrdinalIgnoreCase);
                            if (ImGui.Selectable($"{opt}##opt_{opt}", selected))
                            {
                                statusMessage = null;
                                playbackService.SetSingleGroupOption(
                                    entry, pinned.GroupName, opt, err => statusMessage = err);
                            }
                        }
                        ImGui.EndCombo();
                    }
                    else if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip($"{pinned.GroupName}: {currentOpt}");
                    }
                }

                cursorX += pinnedWidths[pi] + 4f;
            }
        }

        if (hasMusicToggle)
        {
            ImGui.SetCursorScreenPos(new Vector2(cursorX, rowPos.Y));
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.Text, entry.IsMusicEnabled ? AccentTeal : DangerRed);
            var spkIcon = entry.IsMusicEnabled ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute;
            if (ImGui.Button($"{spkIcon.ToIconString()}##spk_{entry.Id}", new Vector2(speakerBtnW, rowH)))
            {
                if (!playbackService.ToggleEntryMusic(entry))
                    statusMessage = "Failed to toggle Penumbra music setting.";
            }
            ImGui.PopStyleColor(2);
            ImGui.PopFont();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(entry.IsMusicEnabled
                    ? $"Music On in Penumbra ({entry.MusicGroupName}) - click to disable"
                    : $"Music Off in Penumbra ({entry.MusicGroupName}) - click to enable");
            }

            cursorX += speakerBtnW + 2f;
        }

        // Favorite star
        ImGui.SetCursorScreenPos(new Vector2(cursorX, rowPos.Y));
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, entry.IsFavorite ? new Vector4(1f, 0.84f, 0.25f, 1f) : MutedText);
        if (ImGui.Button($"{FontAwesomeIcon.Star.ToIconString()}##fav_{entry.Id}", new Vector2(starBtnW, rowH)))
        {
            entry.IsFavorite = !entry.IsFavorite;
            configuration.Save();
        }
        ImGui.PopStyleColor(2);
        ImGui.PopFont();
        cursorX += starBtnW;

        // Organization options menu ("...")
        ImGui.SetCursorScreenPos(new Vector2(cursorX, rowPos.Y));
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, MutedText);
        if (ImGui.Button($"{FontAwesomeIcon.EllipsisV.ToIconString()}##menuBtn_{entry.Id}", new Vector2(menuBtnW, rowH)))
            ImGui.OpenPopup($"entryMenu_{entry.Id}");
        ImGui.PopStyleColor(2);
        ImGui.PopFont();

        DrawEntryMenuPopup(entry);

        // Draw icon + entry name inside the clickable area (clipped cleanly so it never overlaps right buttons)
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(rowPos, new Vector2(rowPos.X + mainBtnWidth, rowPos.Y + rowH), true);

        ImGui.SetCursorScreenPos(new Vector2(rowPos.X + 6f, rowPos.Y + 3f));
        ImGui.PushFont(UiBuilder.IconFont);
        var playIcon = isActive ? FontAwesomeIcon.Music : FontAwesomeIcon.Play;
        ImGui.TextColored(isActive ? AccentTeal : MutedText, playIcon.ToIconString());
        ImGui.PopFont();

        ImGui.SameLine(0f, 7f);
        if (isActive)
            ImGui.TextColored(AccentTeal, entry.Name);
        else
            ImGui.TextUnformatted(entry.Name);

        if (entry.LoopMode != PlaybackLoopMode.Auto)
        {
            ImGui.SameLine(0f, 6f);
            ImGui.TextColored(MutedText, entry.LoopMode == PlaybackLoopMode.Loop ? "[loop]" : "[1-shot]");
        }

        if (!string.IsNullOrEmpty(breadcrumbHint))
        {
            ImGui.SameLine(0f, 6f);
            ImGui.TextColored(MutedText, $"[{breadcrumbHint}]");
        }

        dl.PopClipRect();

        ImGui.SetCursorScreenPos(new Vector2(rowPos.X, rowPos.Y + rowH + 1f));

        if (depth > 0)
            ImGui.Unindent(depth * 14f);

        ImGui.PopID();
    }

    private void DrawRootDropZone()
    {
        if (dragEntryId == null && dragFolderId == null)
            return;

        ImGui.Spacing();
        float availH = Math.Max(36f, ImGui.GetContentRegionAvail().Y);
        ImGui.Button("Drop here to move to Root", new Vector2(ImGui.GetContentRegionAvail().X, availH));

        if (!ImGui.BeginDragDropTarget())
            return;

        unsafe
        {
            var entryPayload = ImGui.AcceptDragDropPayload(PayloadEntry);
            if (entryPayload.Handle != null && entryPayload.IsDelivery() && dragEntryId != null)
            {
                MoveEntryToFolder(dragEntryId, null);
                dragEntryId = null;
            }

            var folderPayload = ImGui.AcceptDragDropPayload(PayloadFolder);
            if (folderPayload.Handle != null && folderPayload.IsDelivery() && dragFolderId != null)
            {
                MoveFolderToParent(dragFolderId, null);
                dragFolderId = null;
            }
        }

        ImGui.EndDragDropTarget();
    }

    private void HandleFolderDragAndDrop(DanceFolder folder)
    {
        if (ImGui.BeginDragDropSource())
        {
            dragFolderId = folder.Id;
            dragEntryId = null;
            ImGui.SetDragDropPayload(PayloadFolder, ReadOnlySpan<byte>.Empty, ImGuiCond.None);
            ImGui.TextUnformatted(folder.Name);
            ImGui.EndDragDropSource();
        }

        if (!ImGui.BeginDragDropTarget())
            return;

        unsafe
        {
            var entryPayload = ImGui.AcceptDragDropPayload(PayloadEntry);
            if (entryPayload.Handle != null && entryPayload.IsDelivery() && dragEntryId != null)
            {
                MoveEntryToFolder(dragEntryId, folder.Id);
                folder.IsCollapsed = false;
                dragEntryId = null;
            }

            var folderPayload = ImGui.AcceptDragDropPayload(PayloadFolder);
            if (folderPayload.Handle != null && folderPayload.IsDelivery() && dragFolderId != null)
            {
                MoveFolderToParent(dragFolderId, folder.Id);
                folder.IsCollapsed = false;
                dragFolderId = null;
            }
        }

        ImGui.EndDragDropTarget();
    }

    private void DrawFolderContextMenu(DanceFolder folder)
    {
        if (!ImGui.BeginPopupContextItem($"folderCtx_{folder.Id}"))
            return;

        if (ImGui.MenuItem("New Subfolder"))
        {
            pendingNewFolderParentId = folder.Id;
            newFolderName = "New Subfolder";
            openNewFolderPopup = true;
            folder.IsCollapsed = false;
        }

        if (ImGui.MenuItem("Rename"))
        {
            renamingId = folder.Id;
            renamingIsFolder = true;
            renameBuffer = folder.Name;
            openRenamePopup = true;
        }

        if (folder.ParentFolderId != null && ImGui.MenuItem("Move to Root"))
            MoveFolderToParent(folder.Id, null);

        if (ImGui.BeginMenu("Move to Folder"))
        {
            DrawFolderMoveTargetsMenu(
                parentFolderId: null,
                excludeFolderId: folder.Id,
                currentTargetId: folder.ParentFolderId,
                onPick: targetId => MoveFolderToParent(folder.Id, targetId));
            ImGui.EndMenu();
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Delete Folder (Keep Entries)"))
            DeleteFolder(folder.Id, deleteContents: false);

        if (ImGui.MenuItem("Delete Folder & Contents"))
            DeleteFolder(folder.Id, deleteContents: true);

        ImGui.EndPopup();
    }

    private void DrawEntryMenuPopup(DanceEntry entry)
    {
        if (!ImGui.BeginPopup($"entryMenu_{entry.Id}"))
            return;

        if (ImGui.MenuItem("Play"))
        {
            statusMessage = null;
            playbackService.PlayEntry(entry, err => statusMessage = err);
        }

        if (ImGui.MenuItem("Mod Settings..."))
            modSettingsWindow.Open(entry);

        if (ImGui.MenuItem("Open in Penumbra"))
        {
            if (!penumbraService.OpenModInPenumbra(entry.ModDirectory, entry.ModName))
                statusMessage = "Could not open mod in Penumbra.";
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Rename"))
        {
            renamingId = entry.Id;
            renamingIsFolder = false;
            renameBuffer = entry.Name;
            openRenamePopup = true;
        }

        if (ImGui.BeginMenu("Playback Mode"))
        {
            if (ImGui.MenuItem("Auto", "", entry.LoopMode == PlaybackLoopMode.Auto))
            {
                entry.LoopMode = PlaybackLoopMode.Auto;
                configuration.Save();
            }
            if (ImGui.MenuItem("Loop", "", entry.LoopMode == PlaybackLoopMode.Loop))
            {
                entry.LoopMode = PlaybackLoopMode.Loop;
                configuration.Save();
            }
            if (ImGui.MenuItem("One-Shot", "", entry.LoopMode == PlaybackLoopMode.OneShot))
            {
                entry.LoopMode = PlaybackLoopMode.OneShot;
                configuration.Save();
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Move to Folder"))
        {
            if (ImGui.MenuItem("Root (Top Level)", "", entry.FolderId == null))
                MoveEntryToFolder(entry.Id, null);

            ImGui.Separator();
            DrawFolderMoveTargetsMenu(
                parentFolderId: null,
                excludeFolderId: null,
                currentTargetId: entry.FolderId,
                onPick: targetId => MoveEntryToFolder(entry.Id, targetId));
            ImGui.EndMenu();
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Remove from JustDance"))
        {
            configuration.Entries.RemoveAll(e => e.Id == entry.Id);
            configuration.Save();
        }

        ImGui.EndPopup();
    }

    private void DrawFolderMoveTargetsMenu(
        string? parentFolderId,
        string? excludeFolderId,
        string? currentTargetId,
        Action<string?> onPick)
    {
        var children = configuration.Folders
            .Where(f => string.Equals(f.ParentFolderId, parentFolderId, StringComparison.Ordinal))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var candidate in children)
        {
            if (excludeFolderId != null &&
                (candidate.Id == excludeFolderId || IsDescendantOf(candidate.Id, excludeFolderId)))
            {
                continue;
            }

            bool hasChildren = configuration.Folders.Any(f =>
                string.Equals(f.ParentFolderId, candidate.Id, StringComparison.Ordinal) &&
                (excludeFolderId == null || (f.Id != excludeFolderId && !IsDescendantOf(f.Id, excludeFolderId))));

            bool isCurrent = string.Equals(currentTargetId, candidate.Id, StringComparison.Ordinal);

            if (hasChildren)
            {
                if (ImGui.BeginMenu($"{candidate.Name}##moveMenu_{candidate.Id}"))
                {
                    if (ImGui.MenuItem($"Place in \"{candidate.Name}\"", "", isCurrent))
                        onPick(candidate.Id);
                    ImGui.Separator();
                    DrawFolderMoveTargetsMenu(candidate.Id, excludeFolderId, currentTargetId, onPick);
                    ImGui.EndMenu();
                }
            }
            else
            {
                if (ImGui.MenuItem($"{candidate.Name}##moveItem_{candidate.Id}", "", isCurrent))
                    onPick(candidate.Id);
            }
        }
    }

    private void DrawPopups()
    {
        if (openNewFolderPopup)
        {
            ImGui.OpenPopup("NewFolderModal");
            openNewFolderPopup = false;
        }

        if (ImGui.BeginPopupModal("NewFolderModal", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(pendingNewFolderParentId == null ? "Create Folder" : "Create Subfolder");
            ImGui.SetNextItemWidth(240f);
            bool submit = ImGui.InputText("##NewFolderName", ref newFolderName, 128, ImGuiInputTextFlags.EnterReturnsTrue);

            if (ImGui.Button("Create", new Vector2(115f, 0f)) || submit)
            {
                var trimmed = newFolderName.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    configuration.Folders.Add(new DanceFolder
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = trimmed,
                        ParentFolderId = pendingNewFolderParentId,
                        IsCollapsed = false,
                        CreatedAt = DateTime.UtcNow,
                    });
                    configuration.Save();
                }
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(115f, 0f)))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        if (openRenamePopup)
        {
            ImGui.OpenPopup("RenameItemModal");
            openRenamePopup = false;
        }

        if (ImGui.BeginPopupModal("RenameItemModal", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(renamingIsFolder ? "Rename Folder" : "Rename Entry");
            ImGui.SetNextItemWidth(240f);
            bool submit = ImGui.InputText("##RenameInput", ref renameBuffer, 128, ImGuiInputTextFlags.EnterReturnsTrue);

            if (ImGui.Button("Save", new Vector2(115f, 0f)) || submit)
            {
                var trimmed = renameBuffer.Trim();
                if (!string.IsNullOrEmpty(trimmed) && renamingId != null)
                {
                    if (renamingIsFolder)
                    {
                        var folder = configuration.Folders.FirstOrDefault(f => f.Id == renamingId);
                        if (folder != null)
                            folder.Name = trimmed;
                    }
                    else
                    {
                        var entry = configuration.Entries.FirstOrDefault(e => e.Id == renamingId);
                        if (entry != null)
                            entry.Name = trimmed;
                    }
                    configuration.Save();
                }
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(115f, 0f)))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
    }

    private void TriggerScan()
    {
        if (configuration.SelectedPenumbraFolder == null)
            return;

        statusMessage = "Scanning...";
        scannerService.ScanAsync(configuration.SelectedPenumbraFolder, summary =>
        {
            statusMessage = summary;
        });
    }

    private void MoveEntryToFolder(string entryId, string? targetFolderId)
    {
        var entry = configuration.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry == null)
            return;

        entry.FolderId = targetFolderId;
        configuration.Save();
    }

    private void MoveFolderToParent(string folderId, string? newParentId)
    {
        if (folderId == newParentId)
            return;
        if (newParentId != null && IsDescendantOf(newParentId, folderId))
            return;

        var folder = configuration.Folders.FirstOrDefault(f => f.Id == folderId);
        if (folder == null)
            return;

        folder.ParentFolderId = newParentId;
        configuration.Save();
    }

    private void DeleteFolder(string folderId, bool deleteContents)
    {
        var folder = configuration.Folders.FirstOrDefault(f => f.Id == folderId);
        if (folder == null)
            return;

        if (!deleteContents)
        {
            foreach (var entry in configuration.Entries.Where(e => e.FolderId == folderId))
                entry.FolderId = folder.ParentFolderId;

            foreach (var child in configuration.Folders.Where(f => f.ParentFolderId == folderId))
                child.ParentFolderId = folder.ParentFolderId;

            configuration.Folders.Remove(folder);
        }
        else
        {
            var toDelete = new HashSet<string> { folderId };
            bool added;
            do
            {
                added = false;
                foreach (var f in configuration.Folders)
                {
                    if (f.ParentFolderId != null && toDelete.Contains(f.ParentFolderId) && toDelete.Add(f.Id))
                        added = true;
                }
            } while (added);

            configuration.Entries.RemoveAll(e => e.FolderId != null && toDelete.Contains(e.FolderId));
            configuration.Folders.RemoveAll(f => toDelete.Contains(f.Id));
        }

        configuration.Save();
    }

    private bool IsDescendantOf(string candidateId, string ancestorId)
    {
        var visited = new HashSet<string>();
        string? cur = candidateId;

        while (!string.IsNullOrEmpty(cur) && visited.Add(cur))
        {
            var folder = configuration.Folders.FirstOrDefault(f => f.Id == cur);
            if (folder == null)
                return false;
            if (folder.ParentFolderId == ancestorId)
                return true;
            cur = folder.ParentFolderId;
        }

        return false;
    }

    private int CountEntriesRecursive(string folderId)
    {
        int count = configuration.Entries.Count(e => e.FolderId == folderId);
        foreach (var child in configuration.Folders.Where(f => f.ParentFolderId == folderId))
            count += CountEntriesRecursive(child.Id);
        return count;
    }

    private static string? BuildFolderBreadcrumb(string? folderId, Dictionary<string, DanceFolder> folderMap)
    {
        if (string.IsNullOrEmpty(folderId))
            return null;

        var parts = new Stack<string>();
        var visited = new HashSet<string>();
        string? cur = folderId;

        while (!string.IsNullOrEmpty(cur) && visited.Add(cur) && folderMap.TryGetValue(cur, out var folder))
        {
            parts.Push(folder.Name);
            cur = folder.ParentFolderId;
        }

        return parts.Count > 0 ? string.Join(" / ", parts) : null;
    }

    private static string TruncateMiddle(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        int side = (maxLength - 3) / 2;
        return $"{text[..side]}...{text[^side..]}";
    }
}
