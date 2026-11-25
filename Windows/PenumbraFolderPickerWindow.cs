using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using JustDance.Services;

namespace JustDance.Windows;

/// <summary>
/// Displays the virtual mod folders created inside Penumbra so the user can pick one to scan.
/// </summary>
public sealed class PenumbraFolderPickerWindow : Window
{
    private static readonly Vector4 FolderGold = new(0.92f, 0.76f, 0.34f, 1f);
    private static readonly Vector4 AccentTeal = new(0.28f, 0.82f, 0.78f, 1f);
    private static readonly Vector4 MutedText = new(0.62f, 0.65f, 0.70f, 1f);

    private sealed class TreeNode
    {
        public string FullPath { get; init; } = "";
        public string Name { get; init; } = "";
        public int DirectModCount { get; set; }
        public int TotalModCount { get; set; }
        public bool IsExpanded { get; set; } = true;
        public List<TreeNode> Children { get; } = new();
    }

    private readonly PenumbraService penumbraService;
    private readonly Action<string> onFolderSelected;

    private readonly List<TreeNode> rootNodes = new();
    private int totalModCount;
    private string selectedPath = "";
    private string filterText = "";
    private string? errorMessage;

    public PenumbraFolderPickerWindow(PenumbraService penumbraService, Action<string> onFolderSelected)
        : base("Select Penumbra Folder###JustDancePenumbraFolderPicker")
    {
        this.penumbraService = penumbraService;
        this.onFolderSelected = onFolderSelected;

        Size = new Vector2(460f, 500f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340f, 320f),
            MaximumSize = new Vector2(900f, 1000f),
        };
    }

    public void Open(string? currentSelection)
    {
        selectedPath = currentSelection ?? "";
        RefreshFolders();
        IsOpen = true;
    }

    private void RefreshFolders()
    {
        rootNodes.Clear();
        totalModCount = 0;
        errorMessage = null;

        if (!penumbraService.EnsureAvailable())
        {
            errorMessage = "Penumbra IPC is not available.";
            return;
        }

        var (mods, folders) = penumbraService.GetPenumbraHierarchy();
        totalModCount = mods.Count;

        var directCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            if (string.IsNullOrEmpty(mod.PenumbraFolder))
                continue;
            directCounts[mod.PenumbraFolder] = directCounts.GetValueOrDefault(mod.PenumbraFolder) + 1;
        }

        var nodeMap = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var folderPath in folders.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var parts = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string current = "";
            TreeNode? parent = null;

            foreach (var part in parts)
            {
                current = string.IsNullOrEmpty(current) ? part : $"{current}/{part}";
                if (!nodeMap.TryGetValue(current, out var node))
                {
                    node = new TreeNode
                    {
                        FullPath = current,
                        Name = part,
                        DirectModCount = directCounts.GetValueOrDefault(current),
                        IsExpanded = !current.Contains('/'), // Expand top-level Penumbra folders by default
                    };
                    nodeMap[current] = node;

                    if (parent == null)
                        rootNodes.Add(node);
                    else
                        parent.Children.Add(node);
                }

                parent = node;
            }
        }

        foreach (var root in rootNodes)
            ComputeTotalsAndSort(root);

        rootNodes.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
    }

    private static int ComputeTotalsAndSort(TreeNode node)
    {
        node.Children.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        int sum = node.DirectModCount;
        foreach (var child in node.Children)
            sum += ComputeTotalsAndSort(child);
        node.TotalModCount = sum;
        return sum;
    }

    public override void Draw()
    {
        float searchWidth = Math.Max(100f, ImGui.GetContentRegionAvail().X - 76f);
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##PenumbraFolderFilter", "Filter Penumbra folders...", ref filterText, 128);

        ImGui.SameLine();
        if (ImGui.Button("Refresh", new Vector2(68f, 0f)))
            RefreshFolders();

        ImGui.Spacing();

        float footerH = 42f;
        float listH = Math.Max(140f, ImGui.GetContentRegionAvail().Y - footerH);

        if (ImGui.BeginChild("PenumbraFolderTree", new Vector2(0f, listH), true))
        {
            if (!string.IsNullOrEmpty(errorMessage))
            {
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), errorMessage);
            }
            else
            {
                bool rootSelected = string.IsNullOrEmpty(selectedPath);
                if (ImGui.Selectable($"<All Penumbra Mods> ({totalModCount} mods)##root", rootSelected, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    selectedPath = "";
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        ConfirmSelection();
                }

                ImGui.Separator();

                if (rootNodes.Count == 0)
                {
                    ImGui.TextDisabled("No Penumbra folders found.");
                }
                else
                {
                    var query = filterText.Trim();
                    foreach (var node in rootNodes)
                        DrawNode(node, 0, query);
                }
            }
        }
        ImGui.EndChild();

        ImGui.Spacing();
        DrawFooter();
    }

    private void DrawNode(TreeNode node, int depth, string query)
    {
        bool filtering = !string.IsNullOrEmpty(query);
        if (filtering && !MatchesSubtree(node, query))
            return;

        ImGui.PushID(node.FullPath);
        if (depth > 0)
            ImGui.Indent(depth * 14f);

        bool hasChildren = node.Children.Count > 0;
        bool expanded = filtering || node.IsExpanded;

        if (hasChildren)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            var chev = expanded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight;
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            if (ImGui.Button($"{chev.ToIconString()}##exp", new Vector2(20f, 0f)))
                node.IsExpanded = !node.IsExpanded;
            ImGui.PopStyleColor();
            ImGui.PopFont();
            ImGui.SameLine(0f, 2f);
        }
        else
        {
            ImGui.Dummy(new Vector2(22f, 0f));
            ImGui.SameLine(0f, 0f);
        }

        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(FolderGold, FontAwesomeIcon.Folder.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine(0f, 6f);

        bool isSelected = string.Equals(selectedPath, node.FullPath, StringComparison.OrdinalIgnoreCase);
        if (isSelected)
            ImGui.PushStyleColor(ImGuiCol.Text, AccentTeal);

        if (ImGui.Selectable($"{node.Name} ({node.TotalModCount})##sel", isSelected, ImGuiSelectableFlags.AllowDoubleClick))
        {
            selectedPath = node.FullPath;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                if (isSelected)
                    ImGui.PopStyleColor();
                if (depth > 0)
                    ImGui.Unindent(depth * 14f);
                ImGui.PopID();
                ConfirmSelection();
                return;
            }
        }

        if (isSelected)
            ImGui.PopStyleColor();

        if (depth > 0)
            ImGui.Unindent(depth * 14f);

        if (expanded)
        {
            foreach (var child in node.Children)
                DrawNode(child, depth + 1, query);
        }

        ImGui.PopID();
    }

    private static bool MatchesSubtree(TreeNode node, string query)
    {
        if (node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            node.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return node.Children.Any(c => MatchesSubtree(c, query));
    }

    private void DrawFooter()
    {
        float buttonsWidth = 205f;
        float labelWidth = Math.Max(80f, ImGui.GetContentRegionAvail().X - buttonsWidth);

        var label = string.IsNullOrEmpty(selectedPath) ? "<All Penumbra Mods>" : selectedPath;
        ImGui.PushItemWidth(labelWidth);
        ImGui.TextDisabled(label);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(label);
        ImGui.PopItemWidth();

        ImGui.SameLine(Math.Max(10f, ImGui.GetWindowContentRegionMax().X - buttonsWidth));

        if (!penumbraService.IsAvailable)
            ImGui.BeginDisabled();

        if (ImGui.Button("Select & Scan", new Vector2(120f, 0f)))
            ConfirmSelection();

        if (!penumbraService.IsAvailable)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(75f, 0f)))
            IsOpen = false;
    }

    private void ConfirmSelection()
    {
        onFolderSelected(selectedPath);
        IsOpen = false;
    }
}
