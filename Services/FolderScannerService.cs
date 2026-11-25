using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace JustDance.Services;

/// <summary>
/// Scans Penumbra mods inside a user-selected Penumbra virtual folder (e.g. "03_Dance"),
/// creating one JustDance entry per dance/animation mod and mirroring its Penumbra dance & music options.
/// </summary>
public sealed class FolderScannerService
{
    private readonly Configuration configuration;
    private readonly PenumbraService penumbraService;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private int scanGeneration;

    public bool IsScanning { get; private set; }
    public int DiscoveredCount { get; private set; }
    public string LastStatus { get; private set; } = "";

    public FolderScannerService(
        Configuration configuration,
        PenumbraService penumbraService,
        IFramework framework,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.penumbraService = penumbraService;
        this.framework = framework;
        this.log = log;
    }

    public void ScanAsync(string penumbraFolder, Action<string>? onCompleted = null)
    {
        if (!penumbraService.EnsureAvailable())
        {
            LastStatus = "Penumbra is not available.";
            onCompleted?.Invoke(LastStatus);
            return;
        }

        var normalizedFolder = NormalizePenumbraFolder(penumbraFolder);
        var generation = Interlocked.Increment(ref scanGeneration);
        IsScanning = true;
        DiscoveredCount = 0;
        LastStatus = "Scanning Penumbra folder...";

        Task.Run(async () =>
        {
            try
            {
                var (modRootDir, hierarchy, collection) = await framework.RunOnFrameworkThread(() =>
                    (penumbraService.GetModDirectory(),
                     penumbraService.GetPenumbraHierarchy(),
                     penumbraService.GetCurrentCollection()));

                if (scanGeneration != generation)
                    return;

                if (string.IsNullOrWhiteSpace(modRootDir) || !Directory.Exists(modRootDir))
                {
                    LastStatus = "Penumbra mod root directory not found.";
                    onCompleted?.Invoke(LastStatus);
                    return;
                }

                var matchingMods = hierarchy.mods
                    .Where(m => IsModInsidePenumbraFolder(m.PenumbraFolder, normalizedFolder))
                    .OrderBy(m => m.PenumbraFolder, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(m => m.ModName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var scannedMods = DiscoverDanceMods(modRootDir, normalizedFolder, matchingMods, generation);
                if (scanGeneration != generation)
                    return;

                var summary = await framework.RunOnFrameworkThread(() =>
                    ApplyScanResults(normalizedFolder, modRootDir, collection.success ? collection.collectionId : Guid.Empty, matchingMods.Count, scannedMods));

                if (scanGeneration == generation)
                {
                    LastStatus = summary;
                    onCompleted?.Invoke(summary);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[Scanner] Scan failed: {ex}");
                if (scanGeneration == generation)
                {
                    LastStatus = $"Scan failed: {ex.Message}";
                    onCompleted?.Invoke(LastStatus);
                }
            }
            finally
            {
                if (scanGeneration == generation)
                    IsScanning = false;
            }
        });
    }

    private sealed record ScannedDanceMod(
        PenumbraModInfo Mod,
        List<string> PenumbraSubfolders,
        ModMusicGroupInfo? MusicGroup);

    private List<ScannedDanceMod> DiscoverDanceMods(
        string modRootDir,
        string selectedPenumbraFolder,
        List<PenumbraModInfo> matchingMods,
        int generation)
    {
        var results = new List<ScannedDanceMod>();

        foreach (var mod in matchingMods)
        {
            if (scanGeneration != generation)
                return results;

            var modPhysicalDir = Path.Combine(modRootDir, mod.ModDirectory);
            if (!PenumbraModResolver.HasAnyBodyPap(modPhysicalDir))
                continue;

            var musicGroup = PenumbraModResolver.DetectMusicGroup(modPhysicalDir);
            var relSegments = GetRelativePenumbraSegments(mod.PenumbraFolder, selectedPenumbraFolder);

            results.Add(new ScannedDanceMod(mod, relSegments, musicGroup));
            DiscoveredCount = results.Count;
        }

        return results;
    }

    private string ApplyScanResults(
        string selectedPenumbraFolder,
        string modRootDir,
        Guid collectionId,
        int matchedModCount,
        List<ScannedDanceMod> scannedMods)
    {
        configuration.SelectedPenumbraFolder = selectedPenumbraFolder;

        var existingByModDir = new Dictionary<string, DanceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in configuration.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ModDirectory))
                continue;
            existingByModDir.TryAdd(entry.ModDirectory, entry);
        }

        var seenMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int addedCount = 0;
        int updatedCount = 0;

        foreach (var item in scannedMods)
        {
            seenMods.Add(item.Mod.ModDirectory);

            var modDirPath = Path.Combine(modRootDir, item.Mod.ModDirectory);
            IReadOnlyDictionary<string, List<string>>? penOpts = null;
            if (collectionId != Guid.Empty && item.MusicGroup != null)
            {
                var (ok, _, _, opts) = penumbraService.GetCurrentModSettings(collectionId, item.Mod.ModDirectory, item.Mod.ModName);
                if (ok)
                    penOpts = opts;
            }

            bool musicEnabled = false;
            string? activeOnOpt = null;
            if (item.MusicGroup != null)
            {
                var effectiveSel = PenumbraModResolver.GetEffectiveGroupSelection(modDirPath, item.MusicGroup.GroupName, penOpts);
                activeOnOpt = effectiveSel.FirstOrDefault(s =>
                    item.MusicGroup.OnOptions.Contains(s, StringComparer.OrdinalIgnoreCase));
                musicEnabled = activeOnOpt != null;
            }

            if (existingByModDir.TryGetValue(item.Mod.ModDirectory, out var existing))
            {
                existing.ModName = item.Mod.ModName;
                existing.PenumbraFolder = item.Mod.PenumbraFolder;
                PruneInvalidPinnedControls(existing, modDirPath);
                ApplyMusicInfo(existing, item.MusicGroup, musicEnabled, activeOnOpt);
                updatedCount++;
                continue;
            }

            string? folderId = null;
            if (configuration.MirrorPenumbraSubfolders && item.PenumbraSubfolders.Count > 0)
                folderId = EnsureFolderChain(item.PenumbraSubfolders);

            var displayName = !string.IsNullOrWhiteSpace(item.Mod.SortName)
                ? item.Mod.SortName
                : (!string.IsNullOrWhiteSpace(item.Mod.ModName) ? item.Mod.ModName : item.Mod.ModDirectory);

            var newEntry = new DanceEntry
            {
                Id = Guid.NewGuid().ToString(),
                Name = displayName,
                ModDirectory = item.Mod.ModDirectory,
                ModName = item.Mod.ModName,
                PenumbraFolder = item.Mod.PenumbraFolder,
                PinnedControls = new List<PinnedModControl>(),
                FolderId = folderId,
                LoopMode = PlaybackLoopMode.Auto,
                AddedAt = DateTime.UtcNow,
            };
            ApplyMusicInfo(newEntry, item.MusicGroup, musicEnabled, activeOnOpt);
            configuration.Entries.Add(newEntry);
            addedCount++;
        }

        int removedCount = 0;
        if (configuration.PruneMissingOnScan)
        {
            removedCount = configuration.Entries.RemoveAll(e => !seenMods.Contains(e.ModDirectory));
        }

        configuration.Save();

        var folderLabel = string.IsNullOrEmpty(selectedPenumbraFolder) ? "<All Mods>" : selectedPenumbraFolder;
        var summary = $"Scanned '{folderLabel}' ({matchedModCount} mods): {scannedMods.Count} dance mod(s) (+{addedCount} added, {updatedCount} kept" +
                      (removedCount > 0 ? $", -{removedCount} removed" : "") + ")";
        log.Information($"[Scanner] {summary}");
        return summary;
    }

    private static void PruneInvalidPinnedControls(DanceEntry entry, string modDirPath)
    {
        if (entry.PinnedControls.Count == 0)
            return;

        var meta = PenumbraModResolver.GetModGroupsMetadata(modDirPath);
        if (meta.Count == 0)
            return;

        entry.PinnedControls.RemoveAll(p =>
        {
            var g = meta.FirstOrDefault(m => string.Equals(m.GroupName, p.GroupName, StringComparison.Ordinal));
            if (g == null)
                return true;
            if (p.IsMulti && !string.IsNullOrEmpty(p.OptionName))
                return !g.Options.Contains(p.OptionName, StringComparer.Ordinal);
            return false;
        });
    }

    private static void ApplyMusicInfo(
        DanceEntry entry,
        ModMusicGroupInfo? musicGroup,
        bool musicEnabled,
        string? activeOnOption)
    {
        if (musicGroup == null)
        {
            entry.HasMusicToggle = false;
            entry.MusicGroupName = "";
            entry.MusicIsMulti = false;
            entry.MusicOnOptions = new List<string>();
            entry.MusicOffOption = null;
            entry.PreferredMusicOption = null;
            entry.IsMusicEnabled = false;
            return;
        }

        entry.HasMusicToggle = true;
        entry.MusicGroupName = musicGroup.GroupName;
        entry.MusicIsMulti = musicGroup.IsMulti;
        entry.MusicOnOptions = new List<string>(musicGroup.OnOptions);
        entry.MusicOffOption = musicGroup.OffOption;
        entry.IsMusicEnabled = musicEnabled;

        if (!string.IsNullOrEmpty(activeOnOption))
            entry.PreferredMusicOption = activeOnOption;
        else if (string.IsNullOrEmpty(entry.PreferredMusicOption) ||
                 !musicGroup.OnOptions.Contains(entry.PreferredMusicOption, StringComparer.OrdinalIgnoreCase))
            entry.PreferredMusicOption = musicGroup.OnOptions.FirstOrDefault();
    }

    private string EnsureFolderChain(IReadOnlyList<string> segments)
    {
        string? parentId = null;

        foreach (var seg in segments)
        {
            var existing = configuration.Folders.FirstOrDefault(f =>
                string.Equals(f.ParentFolderId, parentId, StringComparison.Ordinal) &&
                string.Equals(f.Name, seg, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new DanceFolder
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = seg,
                    ParentFolderId = parentId,
                    IsCollapsed = false,
                    CreatedAt = DateTime.UtcNow,
                };
                configuration.Folders.Add(existing);
            }

            parentId = existing.Id;
        }

        return parentId!;
    }

    private static string NormalizePenumbraFolder(string folder)
        => string.IsNullOrWhiteSpace(folder) ? "" : folder.Trim().Trim('/');

    private static bool IsModInsidePenumbraFolder(string modPenumbraFolder, string selectedFolder)
    {
        if (string.IsNullOrEmpty(selectedFolder))
            return true;

        if (string.Equals(modPenumbraFolder, selectedFolder, StringComparison.OrdinalIgnoreCase))
            return true;

        return modPenumbraFolder.StartsWith(selectedFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> GetRelativePenumbraSegments(string modPenumbraFolder, string selectedFolder)
    {
        string remainder;
        if (string.IsNullOrEmpty(selectedFolder))
        {
            remainder = modPenumbraFolder;
        }
        else if (modPenumbraFolder.Length <= selectedFolder.Length)
        {
            remainder = "";
        }
        else
        {
            remainder = modPenumbraFolder[(selectedFolder.Length + 1)..];
        }

        return remainder
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }
}
