using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JustDance.Services;

public sealed record ResolvedModAssets(
    string? StartPapPath,
    string? LoopPapPath,
    bool IsLoopHint,
    Dictionary<string, List<string>> EffectiveOptions);

public sealed record ModMusicGroupInfo(
    string GroupName,
    bool IsMulti,
    List<string> OnOptions,
    string? OffOption,
    List<string> DefaultSelectedOptions);

public sealed record ModGroupMetadata(
    string GroupName,
    bool IsMulti,
    int GroupType,
    string Description,
    List<string> Options,
    List<string> DefaultSelectedOptions);

/// <summary>
/// Resolves a Penumbra mod's active .pap (Start + Loop) and music toggle group using
/// the mod's Penumbra JSON metadata, the user's current Penumbra settings, and current race.
/// </summary>
public static class PenumbraModResolver
{
    private static readonly Regex FaceAnimRegex = new(@"/animation/f\d{4}/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RaceCodeRegex = new(@"chara/human/(c\d{4})/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] AllRaceIds =
    {
        "c0101", "c0201", "c0301", "c0401", "c0501", "c0601",
        "c0701", "c0801", "c0901", "c1001", "c1101", "c1201",
        "c1301", "c1401", "c1501", "c1601", "c1701", "c1801",
    };

    private static readonly Dictionary<string, string[]> RaceAffinity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c0101"] = new[] { "c0701", "c1301", "c1701", "c0301", "c0501" },
        ["c0301"] = new[] { "c0101", "c0901", "c0501", "c0701", "c1301", "c1701" },
        ["c0501"] = new[] { "c0101", "c0701", "c1301", "c1701", "c0301" },
        ["c0701"] = new[] { "c0101", "c1301", "c1701", "c0301", "c0501" },
        ["c0901"] = new[] { "c0301", "c0101", "c1501", "c0501" },
        ["c1101"] = new[] { "c1201" },
        ["c1301"] = new[] { "c0101", "c0701", "c1701", "c0301", "c0501" },
        ["c1501"] = new[] { "c0901", "c0301", "c0101" },
        ["c1701"] = new[] { "c0101", "c0701", "c1301", "c0301", "c0501" },

        ["c0201"] = new[] { "c0801", "c1401", "c1801", "c0401", "c0601", "c0101" },
        ["c0401"] = new[] { "c0201", "c0801", "c1401", "c1801", "c0601", "c0101" },
        ["c0601"] = new[] { "c1001", "c0401", "c0201", "c0801", "c0101" },
        ["c0801"] = new[] { "c0201", "c1401", "c1801", "c0401", "c0601", "c0101" },
        ["c1001"] = new[] { "c0601", "c0401", "c0201", "c0801", "c0101" },
        ["c1201"] = new[] { "c1101" },
        ["c1401"] = new[] { "c0801", "c0201", "c1801", "c0401", "c0601", "c0101" },
        ["c1601"] = new[] { "c1001", "c0601", "c0401", "c0201", "c0801", "c0101" },
        ["c1801"] = new[] { "c0801", "c0201", "c1401", "c0401", "c0601", "c0101" },
    };

    public static ResolvedModAssets Resolve(
        string modRoot,
        IReadOnlyDictionary<string, List<string>>? penumbraOptions,
        string? playerRaceCode)
    {
        var activeFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var effectiveOptions = penumbraOptions != null
            ? penumbraOptions.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.Ordinal)
            : new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(modRoot) && Directory.Exists(modRoot))
            CollectActiveFileMappings(modRoot, effectiveOptions, activeFiles);

        var bodyPaps = new List<(string gamePath, string diskPath)>();

        foreach (var (gamePath, diskRel) in activeFiles)
        {
            if (!gamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsFacialOrNonBodyPath(gamePath))
                continue;

            var fullDisk = Path.Combine(modRoot, diskRel.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullDisk))
                bodyPaps.Add((gamePath, fullDisk));
        }

        // Fallback for unmapped / raw .pap mods (no JSON Files mapping for body .paps).
        if (bodyPaps.Count == 0 && Directory.Exists(modRoot))
        {
            foreach (var file in EnumerateModBodyPapsFallback(modRoot))
            {
                var rel = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
                bodyPaps.Add((rel, file));
            }
        }

        var startCandidates = new List<(string gamePath, string diskPath)>();
        var loopCandidates = new List<(string gamePath, string diskPath)>();

        foreach (var item in bodyPaps)
        {
            if (IsStartPap(item.gamePath, item.diskPath))
                startCandidates.Add(item);
            else
                loopCandidates.Add(item);
        }

        string? loopPath = PickBestForRace(loopCandidates, playerRaceCode)?.diskPath;
        string? startPath = PickBestForRace(startCandidates, playerRaceCode)?.diskPath;

        bool isLoopHint = true;
        if (loopPath == null && startPath != null)
        {
            loopPath = startPath;
            startPath = null;
            isLoopHint = false;
        }
        else if (startPath != null && string.Equals(startPath, loopPath, StringComparison.OrdinalIgnoreCase))
        {
            // Mod maps _start to the exact same file as _loop (e.g., "w/ ears" option).
            startPath = null;
        }

        return new ResolvedModAssets(startPath, loopPath, isLoopHint, effectiveOptions);
    }

    /// <summary>
    /// Detects if a mod exposes a Penumbra option group that enables/disables .scd music.
    /// Returns null when no music toggle setting exists in the mod.
    /// </summary>
    public static ModMusicGroupInfo? DetectMusicGroup(string modRoot)
    {
        if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot))
            return null;

        foreach (var group in EnumerateAllGroups(modRoot))
        {
            if (!group.TryGetProperty("Name", out var nameProp))
                continue;
            var groupName = nameProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(groupName))
                continue;

            var groupType = group.TryGetProperty("Type", out var t) ? (t.GetString() ?? "Single") : "Single";
            bool isMulti = string.Equals(groupType, "Multi", StringComparison.OrdinalIgnoreCase);

            if (!group.TryGetProperty("Options", out var optsProp) || optsProp.ValueKind != JsonValueKind.Array)
                continue;

            var onOptions = new List<string>();
            var offCandidates = new List<string>();
            var allOptionNames = new List<string>();

            foreach (var opt in optsProp.EnumerateArray())
            {
                if (opt.ValueKind != JsonValueKind.Object)
                    continue;
                var optName = opt.TryGetProperty("Name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(optName))
                    continue;

                allOptionNames.Add(optName);

                if (OptionContainsScd(opt))
                    onOptions.Add(optName);
                else
                    offCandidates.Add(optName);
            }

            if (onOptions.Count == 0)
                continue;

            // Only treat Single groups as toggleable if there is an option without .scd (e.g. "Off").
            // Multi groups can always be toggled by checking/unchecking the .scd option(s).
            string? offOption = null;
            if (!isMulti)
            {
                if (offCandidates.Count == 0)
                    continue;

                offOption = offCandidates.FirstOrDefault(o =>
                    o.Equals("Off", StringComparison.OrdinalIgnoreCase) ||
                    o.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                    o.Equals("Mute", StringComparison.OrdinalIgnoreCase) ||
                    o.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) ?? offCandidates[0];
            }

            var defaultIndices = GetDefaultIndices(group, isMulti);
            var defaultSelected = new List<string>();
            foreach (var idx in defaultIndices)
            {
                if (idx >= 0 && idx < allOptionNames.Count)
                    defaultSelected.Add(allOptionNames[idx]);
            }

            return new ModMusicGroupInfo(groupName, isMulti, onOptions, offOption, defaultSelected);
        }

        return null;
    }

    /// <summary>
    /// Returns all option groups declared in the mod's Penumbra JSON metadata in display order.
    /// </summary>
    public static List<ModGroupMetadata> GetModGroupsMetadata(string modRoot)
    {
        var results = new List<ModGroupMetadata>();
        if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot))
            return results;

        foreach (var group in EnumerateAllGroups(modRoot))
        {
            if (!group.TryGetProperty("Name", out var nameProp))
                continue;
            var groupName = nameProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(groupName))
                continue;

            var groupTypeStr = group.TryGetProperty("Type", out var t) ? (t.GetString() ?? "Single") : "Single";
            int groupType = groupTypeStr.ToLowerInvariant() switch
            {
                "multi" => 1,
                "imc" => 2,
                "combining" => 3,
                _ => 0,
            };
            bool isMulti = groupType == 1;
            var description = group.TryGetProperty("Description", out var d) ? (d.GetString() ?? "") : "";

            var optionNames = new List<string>();
            if (group.TryGetProperty("Options", out var optsProp) && optsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var opt in optsProp.EnumerateArray())
                {
                    if (opt.ValueKind != JsonValueKind.Object)
                        continue;
                    var optName = opt.TryGetProperty("Name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(optName))
                        optionNames.Add(optName);
                }
            }

            var defaultIndices = GetDefaultIndices(group, isMulti);
            var defaults = new List<string>();
            foreach (var idx in defaultIndices)
            {
                if (idx >= 0 && idx < optionNames.Count)
                    defaults.Add(optionNames[idx]);
            }
            if (!isMulti && defaults.Count == 0 && optionNames.Count > 0)
                defaults.Add(optionNames[0]);

            results.Add(new ModGroupMetadata(groupName, isMulti, groupType, description, optionNames, defaults));
        }

        return results;
    }

    public static List<string> GetEffectiveGroupSelection(
        string modRoot,
        string groupName,
        IReadOnlyDictionary<string, List<string>>? penumbraOptions)
    {
        if (penumbraOptions != null &&
            penumbraOptions.TryGetValue(groupName, out var selected) &&
            selected != null)
        {
            return new List<string>(selected);
        }

        if (!string.IsNullOrEmpty(modRoot) && Directory.Exists(modRoot))
        {
            foreach (var group in EnumerateAllGroups(modRoot))
            {
                if (!group.TryGetProperty("Name", out var n) ||
                    !string.Equals(n.GetString(), groupName, StringComparison.Ordinal))
                    continue;

                var groupType = group.TryGetProperty("Type", out var t) ? (t.GetString() ?? "Single") : "Single";
                bool isMulti = string.Equals(groupType, "Multi", StringComparison.OrdinalIgnoreCase);

                var names = new List<string>();
                if (group.TryGetProperty("Options", out var optsProp) && optsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var opt in optsProp.EnumerateArray())
                    {
                        var optName = opt.TryGetProperty("Name", out var on) ? on.GetString() : null;
                        names.Add(optName ?? "");
                    }
                }

                var result = new List<string>();
                foreach (var idx in GetDefaultIndices(group, isMulti))
                {
                    if (idx >= 0 && idx < names.Count && !string.IsNullOrEmpty(names[idx]))
                        result.Add(names[idx]);
                }
                return result;
            }
        }

        return new List<string>();
    }

    public static bool HasAnyBodyPap(string modRoot)
    {
        if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot))
            return false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "*.pap", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
                if (IsFacialOrNonBodyPath(rel))
                    continue;

                var header = PapInspector.ReadHeader(file);
                if (header.IsValid && header.AnimationNames.Count > 0)
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool OptionContainsScd(JsonElement option)
    {
        if (option.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in files.EnumerateObject())
            {
                if (prop.Name.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) ||
                    (prop.Value.GetString()?.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) == true))
                {
                    return true;
                }
            }
        }

        if (option.TryGetProperty("FileSwaps", out var swaps) && swaps.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in swaps.EnumerateObject())
            {
                if (prop.Name.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) ||
                    (prop.Value.GetString()?.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) == true))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateModBodyPapsFallback(string modRoot)
    {
        var list = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "*.pap", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
                if (IsFacialOrNonBodyPath(rel))
                    continue;
                list.Add(file);
            }
        }
        catch
        {
        }
        return list;
    }

    private static bool IsFacialOrNonBodyPath(string path)
    {
        var lower = path.ToLowerInvariant().Replace('\\', '/');
        return lower.Contains("/nonresident/")
               || lower.Contains("nonresident/")
               || FaceAnimRegex.IsMatch(lower)
               || lower.Contains("/face/");
    }

    private static bool IsStartPap(string gamePath, string diskPath)
    {
        var gameName = Path.GetFileNameWithoutExtension(gamePath).ToLowerInvariant();
        var diskName = Path.GetFileNameWithoutExtension(diskPath).ToLowerInvariant();

        static bool MatchesStart(string n) =>
            n.EndsWith("_start", StringComparison.Ordinal) ||
            n.EndsWith("_st", StringComparison.Ordinal) ||
            n.EndsWith("_intro", StringComparison.Ordinal) ||
            n.Equals("start", StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(gameName) && gamePath.Contains('/'))
            return MatchesStart(gameName);

        return MatchesStart(diskName);
    }

    private static (string gamePath, string diskPath)? PickBestForRace(
        List<(string gamePath, string diskPath)> candidates,
        string? raceCode)
    {
        if (candidates.Count == 0)
            return null;

        if (!string.IsNullOrEmpty(raceCode))
        {
            var exact = candidates.FirstOrDefault(c => MatchesRace(c.gamePath, raceCode));
            if (exact.diskPath != null)
                return exact;

            if (RaceAffinity.TryGetValue(raceCode, out var chain))
            {
                foreach (var alt in chain)
                {
                    var match = candidates.FirstOrDefault(c => MatchesRace(c.gamePath, alt));
                    if (match.diskPath != null)
                        return match;
                }
            }

            var c0101 = candidates.FirstOrDefault(c => MatchesRace(c.gamePath, "c0101"));
            if (c0101.diskPath != null)
                return c0101;

            if (raceCode.Length >= 3 && int.TryParse(raceCode.AsSpan(1, 2), out int num))
            {
                bool isMale = num % 2 != 0;
                foreach (var r in AllRaceIds)
                {
                    if (int.TryParse(r.AsSpan(1, 2), out int rNum) && (rNum % 2 != 0) == isMale)
                    {
                        var match = candidates.FirstOrDefault(c => MatchesRace(c.gamePath, r));
                        if (match.diskPath != null)
                            return match;
                    }
                }
            }
        }

        var baseFallback = candidates.FirstOrDefault(c => MatchesRace(c.gamePath, "c0101"));
        if (baseFallback.diskPath != null)
            return baseFallback;

        var agnosticFallback = candidates.FirstOrDefault(c => ExtractRaceCode(c.gamePath) == null);
        return agnosticFallback.diskPath != null ? agnosticFallback : candidates[0];
    }

    private static bool MatchesRace(string path, string raceCode)
        => path.Contains($"/{raceCode}/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith($"{raceCode}/", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractRaceCode(string path)
    {
        var m = RaceCodeRegex.Match(path.Replace('\\', '/'));
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static List<JsonElement> EnumerateAllGroups(string modRoot)
    {
        var results = new List<JsonElement>();
        var metaPath = Path.Combine(modRoot, "meta.json");

        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("FileVersion", out var ver) &&
                    ver.ValueKind == JsonValueKind.Number &&
                    ver.GetInt32() >= 4 &&
                    root.TryGetProperty("Groups", out var groupsProp) &&
                    groupsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in groupsProp.EnumerateArray())
                    {
                        if (g.ValueKind == JsonValueKind.Object)
                            results.Add(g.Clone());
                    }
                    return results.OrderBy(GetGroupPriority).ToList();
                }
            }
            catch
            {
            }
        }

        foreach (var groupFile in Directory.EnumerateFiles(modRoot, "group_*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(groupFile));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    results.Add(doc.RootElement.Clone());
            }
            catch
            {
            }
        }

        return results.OrderBy(GetGroupPriority).ToList();
    }

    private static void CollectActiveFileMappings(
        string modRoot,
        Dictionary<string, List<string>> effectiveOptions,
        Dictionary<string, string> activeFiles)
    {
        var metaPath = Path.Combine(modRoot, "meta.json");
        JsonElement? v4Meta = null;

        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("FileVersion", out var ver) &&
                    ver.ValueKind == JsonValueKind.Number &&
                    ver.GetInt32() >= 4)
                {
                    v4Meta = root.Clone();
                }
            }
            catch
            {
            }
        }

        if (v4Meta is { } meta)
        {
            if (meta.TryGetProperty("DefaultData", out var defaultData) && defaultData.ValueKind == JsonValueKind.Object)
                MergeFilesObject(defaultData, activeFiles);

            foreach (var group in EnumerateAllGroups(modRoot))
                ApplyGroup(group, effectiveOptions, activeFiles);
            return;
        }

        var defaultPath = Path.Combine(modRoot, "default_mod.json");
        if (File.Exists(defaultPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(defaultPath));
                MergeFilesObject(doc.RootElement, activeFiles);
            }
            catch
            {
            }
        }

        foreach (var group in EnumerateAllGroups(modRoot))
            ApplyGroup(group, effectiveOptions, activeFiles);
    }

    private static int GetGroupPriority(JsonElement group)
        => group.TryGetProperty("Priority", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32()
            : 0;

    private static List<int> GetDefaultIndices(JsonElement group, bool isMulti)
    {
        uint defaults = 0;
        if (group.TryGetProperty("DefaultSettings", out var d))
        {
            if (!d.TryGetUInt32(out defaults) && d.TryGetInt64(out long signedDefaults))
                defaults = unchecked((uint)signedDefaults);
        }

        var indices = new List<int>();
        if (!isMulti)
        {
            indices.Add((int)defaults);
        }
        else
        {
            for (int i = 0; i < 32; i++)
            {
                if ((defaults & (1u << i)) != 0)
                    indices.Add(i);
            }
        }
        return indices;
    }

    private static void ApplyGroup(
        JsonElement group,
        Dictionary<string, List<string>> effectiveOptions,
        Dictionary<string, string> activeFiles)
    {
        if (!group.TryGetProperty("Name", out var nameProp))
            return;
        var groupName = nameProp.GetString() ?? "";
        var groupType = group.TryGetProperty("Type", out var t) ? (t.GetString() ?? "Single") : "Single";
        bool isMulti = string.Equals(groupType, "Multi", StringComparison.OrdinalIgnoreCase);

        if (!group.TryGetProperty("Options", out var optsProp) || optsProp.ValueKind != JsonValueKind.Array)
            return;

        List<int>? selectedIndices = null;
        if (effectiveOptions.TryGetValue(groupName, out var selectedNames) && selectedNames.Count > 0)
        {
            selectedIndices = new List<int>();
            int idx = 0;
            foreach (var opt in optsProp.EnumerateArray())
            {
                var optName = opt.TryGetProperty("Name", out var n) ? n.GetString() : null;
                if (optName != null && selectedNames.Any(s => string.Equals(s, optName, StringComparison.OrdinalIgnoreCase)))
                    selectedIndices.Add(idx);
                idx++;
            }
            if (selectedIndices.Count == 0 && !isMulti)
                selectedIndices = null;
        }

        selectedIndices ??= GetDefaultIndices(group, isMulti);

        int oi = 0;
        foreach (var opt in optsProp.EnumerateArray())
        {
            if (selectedIndices.Contains(oi) && opt.ValueKind == JsonValueKind.Object)
                MergeFilesObject(opt, activeFiles);
            oi++;
        }
    }

    private static void MergeFilesObject(JsonElement container, Dictionary<string, string> activeFiles)
    {
        if (!container.TryGetProperty("Files", out var filesProp) || filesProp.ValueKind != JsonValueKind.Object)
            return;

        foreach (var f in filesProp.EnumerateObject())
        {
            var gamePath = f.Name.Replace('\\', '/');
            var diskRel = f.Value.GetString();
            if (!string.IsNullOrEmpty(gamePath) && !string.IsNullOrEmpty(diskRel))
                activeFiles[gamePath] = diskRel;
        }
    }
}
