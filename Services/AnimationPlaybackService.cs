using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using Newtonsoft.Json.Linq;

namespace JustDance.Services;

/// <summary>
/// Plays a Penumbra mod using the user's current Penumbra settings, automatically
/// pairing Start + Loop animations, overriding Penumbra priorities, and delegating
/// music playback enable/disable to Penumbra's own mod settings.
/// </summary>
public sealed class AnimationPlaybackService : IDisposable
{
    public const string SwapModName = "_JustDanceSwap";
    private const string TempModTag = "JustDanceTempSwap";

    private static readonly string[] RaceIds =
    {
        "c0101", "c0201", "c0301", "c0401", "c0501", "c0601",
        "c0701", "c0801", "c0901", "c1001", "c1101", "c1201",
        "c1301", "c1401", "c1501", "c1601", "c1701", "c1801",
    };

    private static readonly string[] AnimSubfolders =
    {
        "", "bt_common/", "resident/", "nonresident/",
        "bt_swd_sld/", "bt_2ax_emp/", "bt_2sw_emp/", "bt_2gb_emp/",
        "bt_2sp_emp/", "bt_2km_emp/", "bt_clw_clw/", "bt_dgr_dgr/",
        "bt_nin_nin/", "bt_2kt_emp/", "bt_bld_bld/", "bt_2bw_emp/",
        "bt_2gn_emp/", "bt_chk_chk/", "bt_stf_sld/", "bt_jst_sld/",
        "bt_2bk_emp/", "bt_2gl_emp/", "bt_2ff_emp/", "bt_rod_emp/",
        "bt_2rp_emp/", "bt_brs_plt/",
    };

    private static readonly string[] CarrierPathSubfolders =
    {
        "", "bt_common/", "resident/", "nonresident/",
    };

    private static readonly HashSet<string> ExcludedCarrierCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/sit", "/groundsit", "/doze", "/changepose", "/cpose",
        "/hug", "/pet", "/handover", "/embrace", "/dote",
        "/showleft", "/showright", "/savortea", "/hildy", "/songbird",
    };

    private static readonly string[] PreferredLoopingWithIntroCarriers =
    {
        "/stepdance", "/harvestdance", "/golddance", "/balldance",
        "/mandervilledance", "/bombdance", "/mogdance",
        "/beesknees", "/sidestep", "/sundance", "/moonlift",
        "/popotostep", "/flamedance", "/yoldance", "/littleladiesdance",
        "/heeltoe", "/mandervillemambo",
    };

    private static readonly string[] PreferredLoopingCarriers =
    {
        "/dance", "/stepdance", "/harvestdance", "/golddance", "/balldance",
        "/mandervilledance", "/bombdance", "/mogdance", "/beesknees",
        "/sidestep", "/sundance", "/moonlift", "/popotostep", "/flamedance",
        "/yoldance", "/littleladiesdance", "/heeltoe", "/mandervillemambo",
        "/hum",
    };

    private static readonly string[] PreferredOneShotCarriers =
    {
        "/no", "/me", "/yes", "/bow", "/wave", "/clap", "/cheer", "/laugh", "/joy", "/psych",
    };

    public sealed class ModOptionGroupView
    {
        public required string GroupName { get; init; }
        public required bool IsMulti { get; init; }
        public required int GroupType { get; init; }
        public required string Description { get; init; }
        public required List<string> Options { get; init; }
        public required List<string> SelectedOptions { get; init; }
    }

    public sealed class ModSettingsSnapshot
    {
        public bool IsValid { get; init; }
        public Guid CollectionId { get; init; }
        public string CollectionName { get; init; } = "";
        public bool IsEnabled { get; init; }
        public int Priority { get; init; }
        public List<ModOptionGroupView> Groups { get; init; } = new();
        public DateTime FetchedAtUtc { get; init; }
    }

    private sealed record TimelineEntry(int Slot, string Key, bool IsLoop, byte LoadType);

    private sealed record CarrierInfo(
        ushort EmoteId,
        string Command,
        string Slot0Key,
        string Slot0PapName,
        bool IsLoop,
        string? Slot1Key,
        string? Slot1PapName);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly PenumbraService penumbraService;

    private readonly Dictionary<string, ushort> emoteCommandToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ushort, List<TimelineEntry>> emoteIdToTimelines = new();
    private readonly HashSet<ushort> ineligibleCarrierIds = new();
    private readonly Dictionary<string, bool> timelineKeyLoopHints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> carrierPapNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModSettingsSnapshot> modSettingsCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim transitionLock = new(1, 1);
    private CancellationTokenSource? activeTransitionCts;

    private bool swapModRegistered;
    private bool usingTempModFallback;
    private int swapPriority;
    private int priorityNonce;
    private Guid swapCollectionId = Guid.Empty;
    private string? swapProbePath;
    private readonly List<(Guid collectionId, string modDir, string modName)> activeTempMods = new();

    private bool activeEmoteSeen;
    private int endCandidateFrames;

    public string? ActiveEntryId { get; private set; }
    public string? ActiveEntryName { get; private set; }
    public ushort ActiveCarrierId { get; private set; }
    public bool IsTransitioning { get; private set; }
    public string? LastError { get; private set; }

    public AnimationPlaybackService(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IObjectTable objectTable,
        IDataManager dataManager,
        IPluginLog log,
        Configuration configuration,
        PenumbraService penumbraService)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.log = log;
        this.configuration = configuration;
        this.penumbraService = penumbraService;

        BuildEmoteLookup();
        CleanupStaleSwapOnStartup();
    }

    private void BuildEmoteLookup()
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                var emoteId = (ushort)row.RowId;
                var entries = new List<TimelineEntry>();
                bool carrierUnsafe = row.DrawsWeapon;

                for (int i = 0; i < 7; i++)
                {
                    var tlRef = row.ActionTimeline[i];
                    if (tlRef.RowId == 0 || !tlRef.IsValid)
                        continue;

                    var tl = tlRef.Value;
                    var key = tl.Key.ToString();
                    if (string.IsNullOrEmpty(key))
                        continue;

                    entries.Add(new TimelineEntry(i, key, tl.Pause, tl.LoadType));

                    var fileName = Path.GetFileNameWithoutExtension(key);
                    if (!string.IsNullOrEmpty(fileName))
                        timelineKeyLoopHints.TryAdd(fileName, tl.Pause);

                    if (tl.LoadType == 1 || tl.ActionTimelineIDMode == 2)
                        carrierUnsafe = true;
                }

                if (entries.Count > 0)
                {
                    emoteIdToTimelines[emoteId] = entries;
                    if (carrierUnsafe)
                        ineligibleCarrierIds.Add(emoteId);
                }

                var textCmd = row.TextCommand.ValueNullable;
                if (textCmd == null)
                    continue;

                TryRegisterCommand(textCmd.Value.Command.ToString(), emoteId);
                TryRegisterCommand(textCmd.Value.Alias.ToString(), emoteId);
                TryRegisterCommand(textCmd.Value.ShortCommand.ToString(), emoteId);
                TryRegisterCommand(textCmd.Value.ShortAlias.ToString(), emoteId);
            }
        }
        catch (Exception ex)
        {
            log.Error($"[Playback] Failed to build Lumina emote index: {ex.Message}");
        }
    }

    private void TryRegisterCommand(string cmd, ushort emoteId)
    {
        if (string.IsNullOrWhiteSpace(cmd))
            return;
        emoteCommandToId.TryAdd(cmd.ToLowerInvariant(), emoteId);
    }

    private void CleanupStaleSwapOnStartup()
    {
        if (!penumbraService.EnsureAvailable())
            return;

        try
        {
            if (configuration.LastTempModKeys.Count > 0)
            {
                foreach (var key in configuration.LastTempModKeys)
                {
                    var parts = key.Split('|');
                    if (parts.Length >= 2 && Guid.TryParse(parts[0], out var collId))
                        penumbraService.RemoveTemporaryModSettings(collId, parts[1], parts.Length >= 3 ? parts[2] : "");
                }
                configuration.LastTempModKeys.Clear();
                configuration.Save();
            }

            var (ok, currentColl, _) = penumbraService.GetCurrentCollection();
            if (ok && currentColl != Guid.Empty)
                penumbraService.RemoveTemporaryModSettings(currentColl, SwapModName, SwapModName);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Refreshes cached speaker states from Penumbra and clears the mod-settings snapshot cache.
    /// </summary>
    public void RefreshModOptionStates()
    {
        modSettingsCache.Clear();

        if (!penumbraService.EnsureAvailable())
            return;

        var modRootDir = penumbraService.GetModDirectory();
        if (string.IsNullOrWhiteSpace(modRootDir))
            return;

        var (ok, collectionId, _) = penumbraService.GetCurrentCollection();
        if (!ok || collectionId == Guid.Empty)
            return;

        bool changed = false;
        foreach (var entry in configuration.Entries)
        {
            if (!entry.HasMusicToggle || string.IsNullOrEmpty(entry.MusicGroupName))
                continue;

            var modDirPath = Path.Combine(modRootDir, entry.ModDirectory);
            var (got, _, _, opts) = penumbraService.GetCurrentModSettings(collectionId, entry.ModDirectory, entry.ModName);
            var selected = PenumbraModResolver.GetEffectiveGroupSelection(modDirPath, entry.MusicGroupName, got ? opts : null);
            var activeOn = selected.FirstOrDefault(s => entry.MusicOnOptions.Contains(s, StringComparer.OrdinalIgnoreCase));
            bool enabled = activeOn != null;

            if (entry.IsMusicEnabled != enabled)
            {
                entry.IsMusicEnabled = enabled;
                changed = true;
            }
            if (!string.IsNullOrEmpty(activeOn) && entry.PreferredMusicOption != activeOn)
            {
                entry.PreferredMusicOption = activeOn;
                changed = true;
            }
        }

        if (changed)
            configuration.Save();
    }

    public ModSettingsSnapshot GetModSettingsSnapshot(DanceEntry entry, bool forceRefresh = false)
    {
        if (!forceRefresh &&
            modSettingsCache.TryGetValue(entry.ModDirectory, out var cached) &&
            (DateTime.UtcNow - cached.FetchedAtUtc).TotalSeconds < 1.5)
        {
            return cached;
        }

        var (ok, collectionId, collectionName) = penumbraService.GetCurrentCollection();
        var modRootDir = penumbraService.GetModDirectory();
        if (!ok || collectionId == Guid.Empty || string.IsNullOrWhiteSpace(modRootDir))
            return new ModSettingsSnapshot { IsValid = false };

        var modDirPath = Path.Combine(modRootDir, entry.ModDirectory);
        var (gotCurrent, isEnabled, priority, currentOpts) =
            penumbraService.GetCurrentModSettings(collectionId, entry.ModDirectory, entry.ModName);
        var effectiveOpts = gotCurrent ? currentOpts : null;

        var available = penumbraService.GetAvailableModSettings(entry.ModDirectory, entry.ModName);
        var metaGroups = PenumbraModResolver.GetModGroupsMetadata(modDirPath);

        var groups = new List<ModOptionGroupView>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);

        foreach (var meta in metaGroups)
        {
            seenGroups.Add(meta.GroupName);

            List<string> options = meta.Options;
            int groupType = meta.GroupType;
            bool isMulti = meta.IsMulti;

            if (available.TryGetValue(meta.GroupName, out var ipcGroup))
            {
                if (ipcGroup.Options.Length > 0)
                    options = ipcGroup.Options.ToList();
                groupType = ipcGroup.GroupType;
                isMulti = groupType == 1;
            }

            var selected = PenumbraModResolver.GetEffectiveGroupSelection(modDirPath, meta.GroupName, effectiveOpts);
            if (selected.Count == 0 && meta.DefaultSelectedOptions.Count > 0 && effectiveOpts == null)
                selected = new List<string>(meta.DefaultSelectedOptions);

            groups.Add(new ModOptionGroupView
            {
                GroupName = meta.GroupName,
                IsMulti = isMulti,
                GroupType = groupType,
                Description = meta.Description,
                Options = options,
                SelectedOptions = selected,
            });
        }

        // Include any group Penumbra knows about via IPC that wasn't matched from local JSON parsing.
        foreach (var (grpName, (opts, gType)) in available)
        {
            if (!seenGroups.Add(grpName))
                continue;

            bool isMulti = gType == 1;
            var optList = opts?.ToList() ?? new List<string>();
            var selected = effectiveOpts != null && effectiveOpts.TryGetValue(grpName, out var sel) && sel != null
                ? new List<string>(sel)
                : (!isMulti && optList.Count > 0 ? new List<string> { optList[0] } : new List<string>());

            groups.Add(new ModOptionGroupView
            {
                GroupName = grpName,
                IsMulti = isMulti,
                GroupType = gType,
                Description = "",
                Options = optList,
                SelectedOptions = selected,
            });
        }

        var snapshot = new ModSettingsSnapshot
        {
            IsValid = true,
            CollectionId = collectionId,
            CollectionName = collectionName,
            IsEnabled = gotCurrent && isEnabled,
            Priority = gotCurrent ? priority : 0,
            Groups = groups,
            FetchedAtUtc = DateTime.UtcNow,
        };
        modSettingsCache[entry.ModDirectory] = snapshot;
        return snapshot;
    }

    public void InvalidateModSettingsCache(string? modDirectory = null)
    {
        if (string.IsNullOrEmpty(modDirectory))
            modSettingsCache.Clear();
        else
            modSettingsCache.Remove(modDirectory);
    }

    public bool SetModEnabled(DanceEntry entry, bool enabled, Action<string>? onError = null)
    {
        var (ok, collectionId, _) = penumbraService.GetCurrentCollection();
        if (!ok || collectionId == Guid.Empty)
        {
            onError?.Invoke("No active Penumbra collection.");
            return false;
        }

        if (!penumbraService.TrySetModEnabled(collectionId, entry.ModDirectory, enabled, entry.ModName))
        {
            onError?.Invoke("Failed to update mod Enabled state in Penumbra.");
            return false;
        }

        InvalidateModSettingsCache(entry.ModDirectory);
        return true;
    }

    public bool SetModPriority(DanceEntry entry, int priority, Action<string>? onError = null)
    {
        var (ok, collectionId, _) = penumbraService.GetCurrentCollection();
        if (!ok || collectionId == Guid.Empty)
        {
            onError?.Invoke("No active Penumbra collection.");
            return false;
        }

        if (!penumbraService.TrySetModPriority(collectionId, entry.ModDirectory, priority, entry.ModName))
        {
            onError?.Invoke("Failed to update mod Priority in Penumbra.");
            return false;
        }

        InvalidateModSettingsCache(entry.ModDirectory);
        return true;
    }

    public bool SetSingleGroupOption(DanceEntry entry, string groupName, string optionName, Action<string>? onError = null)
    {
        var (ok, collectionId, _) = penumbraService.GetCurrentCollection();
        if (!ok || collectionId == Guid.Empty)
        {
            onError?.Invoke("No active Penumbra collection.");
            return false;
        }

        if (!penumbraService.TrySetModSettings(collectionId, entry.ModDirectory, groupName, new[] { optionName }, entry.ModName))
        {
            onError?.Invoke($"Failed to set '{groupName}' in Penumbra.");
            return false;
        }

        InvalidateModSettingsCache(entry.ModDirectory);
        bool isMusicGroup = SyncMusicStateAfterGroupChange(entry, groupName, new[] { optionName });
        ApplyLiveSettingChangeIfActive(entry, collectionId, groupName, new[] { optionName }, isMusicGroup, onError);
        return true;
    }

    public bool SetMultiGroupOption(
        DanceEntry entry,
        string groupName,
        string optionName,
        bool isChecked,
        Action<string>? onError = null)
    {
        var (ok, collectionId, _) = penumbraService.GetCurrentCollection();
        if (!ok || collectionId == Guid.Empty)
        {
            onError?.Invoke("No active Penumbra collection.");
            return false;
        }

        var snapshot = GetModSettingsSnapshot(entry, forceRefresh: true);
        var group = snapshot.Groups.FirstOrDefault(g => string.Equals(g.GroupName, groupName, StringComparison.Ordinal));
        var selected = group != null ? new List<string>(group.SelectedOptions) : new List<string>();

        selected.RemoveAll(s => string.Equals(s, optionName, StringComparison.OrdinalIgnoreCase));
        if (isChecked)
            selected.Add(optionName);

        if (!penumbraService.TrySetModSettings(collectionId, entry.ModDirectory, groupName, selected, entry.ModName))
        {
            onError?.Invoke($"Failed to update '{groupName}' in Penumbra.");
            return false;
        }

        InvalidateModSettingsCache(entry.ModDirectory);
        bool isMusicGroup = SyncMusicStateAfterGroupChange(entry, groupName, selected);
        ApplyLiveSettingChangeIfActive(entry, collectionId, groupName, selected, isMusicGroup, onError);
        return true;
    }

    private bool SyncMusicStateAfterGroupChange(DanceEntry entry, string groupName, IReadOnlyList<string> newSelection)
    {
        if (!entry.HasMusicToggle || !string.Equals(entry.MusicGroupName, groupName, StringComparison.Ordinal))
            return false;

        var activeOn = newSelection.FirstOrDefault(s => entry.MusicOnOptions.Contains(s, StringComparer.OrdinalIgnoreCase));
        entry.IsMusicEnabled = activeOn != null;
        if (!string.IsNullOrEmpty(activeOn))
            entry.PreferredMusicOption = activeOn;
        configuration.Save();
        return true;
    }

    private void ApplyLiveSettingChangeIfActive(
        DanceEntry entry,
        Guid collectionId,
        string groupName,
        IReadOnlyList<string> newSelection,
        bool isMusicGroup,
        Action<string>? onError)
    {
        if (!string.Equals(ActiveEntryId, entry.Id, StringComparison.Ordinal))
            return;

        if (isMusicGroup)
        {
            var (_, _, _, currentOpts) = penumbraService.GetCurrentModSettings(collectionId, entry.ModDirectory, entry.ModName);
            currentOpts[groupName] = new List<string>(newSelection);
            penumbraService.SetTemporaryModSettings(
                collectionId,
                entry.ModDirectory,
                enabled: true,
                priority: Math.Max(100_000, swapPriority - 100),
                options: currentOpts,
                modName: entry.ModName);
            return;
        }

        PlayEntry(entry, onError);
    }

    /// <summary>
    /// Toggles the mod's Penumbra music option group on/off via TrySetModSettings.V5.
    /// </summary>
    public bool ToggleEntryMusic(DanceEntry entry)
    {
        if (!entry.HasMusicToggle || string.IsNullOrEmpty(entry.MusicGroupName))
            return false;

        var (ok, collectionId, _) = penumbraService.GetCurrentCollection();
        if (!ok || collectionId == Guid.Empty)
            return false;

        var modRootDir = penumbraService.GetModDirectory();
        if (string.IsNullOrWhiteSpace(modRootDir))
            return false;

        var modDirPath = Path.Combine(modRootDir, entry.ModDirectory);
        var (got, _, _, currentOpts) = penumbraService.GetCurrentModSettings(collectionId, entry.ModDirectory, entry.ModName);
        var currentSelected = PenumbraModResolver.GetEffectiveGroupSelection(modDirPath, entry.MusicGroupName, got ? currentOpts : null);

        bool currentlyEnabled = currentSelected.Any(s => entry.MusicOnOptions.Contains(s, StringComparer.OrdinalIgnoreCase));
        List<string> newSelection;

        if (entry.MusicIsMulti)
        {
            newSelection = new List<string>(currentSelected);
            if (currentlyEnabled)
            {
                newSelection.RemoveAll(s => entry.MusicOnOptions.Contains(s, StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                foreach (var onOpt in entry.MusicOnOptions)
                {
                    if (!newSelection.Contains(onOpt, StringComparer.OrdinalIgnoreCase))
                        newSelection.Add(onOpt);
                }
            }
        }
        else
        {
            if (currentlyEnabled)
            {
                if (string.IsNullOrEmpty(entry.MusicOffOption))
                    return false;
                newSelection = new List<string> { entry.MusicOffOption };
            }
            else
            {
                var targetOn = !string.IsNullOrEmpty(entry.PreferredMusicOption) &&
                               entry.MusicOnOptions.Contains(entry.PreferredMusicOption, StringComparer.OrdinalIgnoreCase)
                    ? entry.PreferredMusicOption
                    : entry.MusicOnOptions.FirstOrDefault();
                if (string.IsNullOrEmpty(targetOn))
                    return false;
                newSelection = new List<string> { targetOn };
            }
        }

        if (!penumbraService.TrySetModSettings(collectionId, entry.ModDirectory, entry.MusicGroupName, newSelection, entry.ModName))
            return false;

        InvalidateModSettingsCache(entry.ModDirectory);
        entry.IsMusicEnabled = !currentlyEnabled;
        configuration.Save();

        if (string.Equals(ActiveEntryId, entry.Id, StringComparison.Ordinal))
        {
            currentOpts[entry.MusicGroupName] = newSelection;
            penumbraService.SetTemporaryModSettings(
                collectionId,
                entry.ModDirectory,
                enabled: true,
                priority: Math.Max(100_000, swapPriority - 100),
                options: currentOpts,
                modName: entry.ModName);
        }

        return true;
    }

    public bool SetEntryMusicOption(DanceEntry entry, string optionName)
    {
        if (!entry.HasMusicToggle || string.IsNullOrEmpty(entry.MusicGroupName) || entry.MusicIsMulti)
            return false;

        var (ok, collectionId, _) = penumbraService.GetCurrentCollection();
        if (!ok || collectionId == Guid.Empty)
            return false;

        var newSelection = new List<string> { optionName };
        if (!penumbraService.TrySetModSettings(collectionId, entry.ModDirectory, entry.MusicGroupName, newSelection, entry.ModName))
            return false;

        InvalidateModSettingsCache(entry.ModDirectory);
        entry.PreferredMusicOption = optionName;
        entry.IsMusicEnabled = true;
        configuration.Save();

        if (string.Equals(ActiveEntryId, entry.Id, StringComparison.Ordinal))
        {
            var (_, _, _, currentOpts) = penumbraService.GetCurrentModSettings(collectionId, entry.ModDirectory, entry.ModName);
            currentOpts[entry.MusicGroupName] = newSelection;
            penumbraService.SetTemporaryModSettings(
                collectionId,
                entry.ModDirectory,
                enabled: true,
                priority: Math.Max(100_000, swapPriority - 100),
                options: currentOpts,
                modName: entry.ModName);
        }

        return true;
    }

    public void PlayEntry(DanceEntry entry, Action<string>? onError = null)
    {
        LastError = null;

        if (!penumbraService.EnsureAvailable())
        {
            const string msg = "Penumbra is not available (API v5+ required).";
            LastError = msg;
            onError?.Invoke(msg);
            return;
        }

        var modRootDir = penumbraService.GetModDirectory();
        if (string.IsNullOrWhiteSpace(modRootDir))
        {
            const string msg = "Could not locate Penumbra mod root directory.";
            LastError = msg;
            onError?.Invoke(msg);
            return;
        }

        var modDirPath = Path.Combine(modRootDir, entry.ModDirectory);
        if (!Directory.Exists(modDirPath))
        {
            var msg = $"Mod folder missing: {entry.ModDirectory}";
            LastError = msg;
            onError?.Invoke(msg);
            return;
        }

        CancellationTokenSource cts;
        lock (this)
        {
            activeTransitionCts?.Cancel();
            activeTransitionCts?.Dispose();
            activeTransitionCts = new CancellationTokenSource();
            cts = activeTransitionCts;
        }

        var token = cts.Token;

        Task.Run(async () =>
        {
            try
            {
                await transitionLock.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            IsTransitioning = true;
            try
            {
                await ExecutePlayModAsync(entry, modDirPath, token, onError);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                log.Error($"[Playback] Failed to play '{entry.Name}': {ex}");
                LastError = ex.Message;
                onError?.Invoke(ex.Message);
            }
            finally
            {
                IsTransitioning = false;
                transitionLock.Release();
            }
        });
    }

    public void Stop()
    {
        lock (this)
        {
            activeTransitionCts?.Cancel();
            activeTransitionCts?.Dispose();
            activeTransitionCts = null;
        }

        ActiveEntryId = null;
        ActiveEntryName = null;
        ActiveCarrierId = 0;
        activeEmoteSeen = false;
        endCandidateFrames = 0;

        framework.RunOnFrameworkThread(() =>
        {
            var emoteId = ReadCurrentEmoteId();
            var mode = ReadCharacterMode();
            if (emoteId != 0 || mode != CharacterModes.Normal)
                StopActiveAnimationByJump();

            ClearSwap();
        });
    }

    public void Update()
    {
        if (string.IsNullOrEmpty(ActiveEntryId) || ActiveCarrierId == 0)
            return;

        var currentEmoteId = ReadCurrentEmoteId();
        if (!activeEmoteSeen)
        {
            if (currentEmoteId == ActiveCarrierId)
            {
                activeEmoteSeen = true;
                endCandidateFrames = 0;
            }
            return;
        }

        if (currentEmoteId == ActiveCarrierId)
        {
            endCandidateFrames = 0;
            return;
        }

        endCandidateFrames++;
        if (endCandidateFrames >= 15)
        {
            ActiveEntryId = null;
            ActiveEntryName = null;
            ActiveCarrierId = 0;
            activeEmoteSeen = false;
            endCandidateFrames = 0;
            ClearSwap();
        }
    }

    private async Task ExecutePlayModAsync(
        DanceEntry entry,
        string modDirPath,
        CancellationToken token,
        Action<string>? onError)
    {
        var (gotCollection, collectionId, collectionName) = penumbraService.GetCurrentCollection();
        if (!gotCollection || collectionId == Guid.Empty)
        {
            const string msg = "Could not determine active Penumbra collection.";
            LastError = msg;
            onError?.Invoke(msg);
            return;
        }

        // Read user's current Penumbra settings for this mod.
        var (gotSettings, _, _, currentOptions) = penumbraService.GetCurrentModSettings(
            collectionId, entry.ModDirectory, entry.ModName);

        if (entry.HasMusicToggle && !string.IsNullOrEmpty(entry.MusicGroupName))
        {
            var sel = PenumbraModResolver.GetEffectiveGroupSelection(modDirPath, entry.MusicGroupName, gotSettings ? currentOptions : null);
            var activeOn = sel.FirstOrDefault(s => entry.MusicOnOptions.Contains(s, StringComparer.OrdinalIgnoreCase));
            entry.IsMusicEnabled = activeOn != null;
            if (!string.IsNullOrEmpty(activeOn))
                entry.PreferredMusicOption = activeOn;
        }

        string? playerRaceCode = null;
        ushort currentEmoteId = 0;
        CharacterModes currentMode = CharacterModes.Normal;

        await framework.RunOnFrameworkThread(() =>
        {
            playerRaceCode = GetPlayerRaceCode();
            currentEmoteId = ReadCurrentEmoteId();
            currentMode = ReadCharacterMode();
        });

        token.ThrowIfCancellationRequested();

        var resolved = PenumbraModResolver.Resolve(modDirPath, currentOptions, playerRaceCode);
        if (string.IsNullOrEmpty(resolved.LoopPapPath) || !File.Exists(resolved.LoopPapPath))
        {
            const string msg = "No active dance/animation .pap found for current Penumbra settings.";
            LastError = msg;
            onError?.Invoke(msg);
            return;
        }

        var loopBytes = await File.ReadAllBytesAsync(resolved.LoopPapPath, token);
        var loopAnimName = PapInspector.ReadPrimaryAnimationName(loopBytes);
        if (string.IsNullOrEmpty(loopAnimName))
        {
            const string msg = "Could not read animation name from loop .pap.";
            LastError = msg;
            onError?.Invoke(msg);
            return;
        }

        byte[]? startBytes = null;
        string? startAnimName = null;
        if (!string.IsNullOrEmpty(resolved.StartPapPath) && File.Exists(resolved.StartPapPath))
        {
            startBytes = await File.ReadAllBytesAsync(resolved.StartPapPath, token);
            startAnimName = PapInspector.ReadPrimaryAnimationName(startBytes);
            if (string.IsNullOrEmpty(startAnimName))
                startBytes = null;
        }

        bool needsLoop = InferShouldLoop(entry, resolved, loopAnimName);

        CarrierInfo? carrier = null;
        bool useSequentialStartFallback = false;
        CarrierInfo? sequentialStartCarrier = null;

        await framework.RunOnFrameworkThread(() =>
        {
            if (startBytes != null && startAnimName != null)
            {
                carrier = FindStartAndLoopCarrier(startAnimName.Length, loopAnimName.Length, currentEmoteId);
                if (carrier == null)
                {
                    sequentialStartCarrier = FindSingleCarrier(false, startAnimName.Length, currentEmoteId);
                    carrier = FindSingleCarrier(needsLoop, loopAnimName.Length, sequentialStartCarrier?.EmoteId ?? currentEmoteId);
                    useSequentialStartFallback = sequentialStartCarrier != null && carrier != null;
                }
            }
            else
            {
                carrier = FindSingleCarrier(needsLoop, loopAnimName.Length, currentEmoteId);
            }
        });

        token.ThrowIfCancellationRequested();

        if (carrier == null)
        {
            var msg = $"No unlocked carrier emote fits animation '{loopAnimName}' ({loopAnimName.Length} chars).";
            LastError = msg;
            onError?.Invoke(msg);
            return;
        }

        priorityNonce ^= 1;
        int overridePriority = penumbraService.GetOverridePriority(collectionId, SwapModName) + priorityNonce;

        if (startBytes != null && startAnimName != null && !useSequentialStartFallback)
        {
            var (patchedLoop, rep0, _) = PapInspector.RewriteAnimationName(loopBytes, loopAnimName, carrier.Slot0PapName);
            var (patchedStart, rep1, _) = PapInspector.RewriteAnimationName(startBytes, startAnimName, carrier.Slot1PapName!);
            if (patchedLoop == null || rep0 == 0 || patchedStart == null || rep1 == 0)
            {
                const string msg = "Failed to patch Start/Loop .pap animation names.";
                LastError = msg;
                onError?.Invoke(msg);
                return;
            }

            if (!DeployModAndSwap(collectionId, entry, currentOptions, carrier, patchedLoop, patchedStart, overridePriority))
            {
                const string msg = "Failed to register Penumbra override.";
                LastError = msg;
                onError?.Invoke(msg);
                return;
            }
        }
        else if (useSequentialStartFallback && startBytes != null && startAnimName != null && sequentialStartCarrier != null)
        {
            var (patchedStart, repS, _) = PapInspector.RewriteAnimationName(startBytes, startAnimName, sequentialStartCarrier.Slot0PapName);
            if (patchedStart != null && repS > 0 &&
                DeployModAndSwap(collectionId, entry, currentOptions, sequentialStartCarrier, patchedStart, null, overridePriority))
            {
                await WaitForSwapReadyAsync(token);
                await StopIfRequiredAsync(currentMode, currentEmoteId, sequentialStartCarrier.EmoteId, token);

                ActiveEntryId = entry.Id;
                ActiveEntryName = entry.Name;
                ActiveCarrierId = sequentialStartCarrier.EmoteId;
                activeEmoteSeen = false;
                endCandidateFrames = 0;

                await framework.RunOnFrameworkThread(() =>
                    ExecuteEmoteCarrier(sequentialStartCarrier.EmoteId, sequentialStartCarrier.Command));

                var waitSw = Stopwatch.StartNew();
                bool sawStart = false;
                while (waitSw.ElapsedMilliseconds < 15_000)
                {
                    token.ThrowIfCancellationRequested();
                    var curId = await framework.RunOnFrameworkThread(ReadCurrentEmoteId);
                    if (curId == sequentialStartCarrier.EmoteId)
                        sawStart = true;
                    else if (sawStart || waitSw.ElapsedMilliseconds > 1200)
                        break;
                    await Task.Delay(50, token);
                }

                currentEmoteId = await framework.RunOnFrameworkThread(ReadCurrentEmoteId);
                currentMode = await framework.RunOnFrameworkThread(ReadCharacterMode);
            }

            var (patchedLoop, rep0, _) = PapInspector.RewriteAnimationName(loopBytes, loopAnimName, carrier.Slot0PapName);
            if (patchedLoop == null || rep0 == 0 ||
                !DeployModAndSwap(collectionId, entry, currentOptions, carrier, patchedLoop, null, overridePriority))
            {
                const string msg = "Failed to deploy Loop animation.";
                LastError = msg;
                onError?.Invoke(msg);
                return;
            }
        }
        else
        {
            var (patchedLoop, rep0, _) = PapInspector.RewriteAnimationName(loopBytes, loopAnimName, carrier.Slot0PapName);
            if (patchedLoop == null || rep0 == 0)
            {
                var msg = $"Failed to patch animation name '{loopAnimName}' -> '{carrier.Slot0PapName}'.";
                LastError = msg;
                onError?.Invoke(msg);
                return;
            }

            byte[]? slot1SuppressBytes = null;
            if (!string.IsNullOrEmpty(carrier.Slot1Key) && !string.IsNullOrEmpty(carrier.Slot1PapName))
            {
                var (rewrittenS1, rep1, _) = PapInspector.RewriteAnimationName(loopBytes, loopAnimName, carrier.Slot1PapName);
                if (rewrittenS1 != null && rep1 > 0)
                    slot1SuppressBytes = rewrittenS1;
            }

            if (!DeployModAndSwap(collectionId, entry, currentOptions, carrier, patchedLoop, slot1SuppressBytes, overridePriority))
            {
                const string msg = "Failed to register Penumbra override.";
                LastError = msg;
                onError?.Invoke(msg);
                return;
            }
        }

        token.ThrowIfCancellationRequested();

        await WaitForSwapReadyAsync(token);
        token.ThrowIfCancellationRequested();

        await StopIfRequiredAsync(currentMode, currentEmoteId, carrier.EmoteId, token);
        token.ThrowIfCancellationRequested();

        ActiveEntryId = entry.Id;
        ActiveEntryName = entry.Name;
        ActiveCarrierId = carrier.EmoteId;
        activeEmoteSeen = false;
        endCandidateFrames = 0;

        await framework.RunOnFrameworkThread(() => ExecuteEmoteCarrier(carrier.EmoteId, carrier.Command));
        log.Information($"[Playback] Playing '{entry.Name}' via {carrier.Command} (id={carrier.EmoteId}, start+loop={startBytes != null}, priority={overridePriority}, collection='{collectionName}')");

        var expectedCarrierId = carrier.EmoteId;
        var expectedCommand = carrier.Command;
        var expectedEntryId = entry.Id;

        await Task.Delay(250, token);
        await framework.RunOnFrameworkThread(() =>
        {
            if (ActiveEntryId != expectedEntryId)
                return;

            if (ReadCurrentEmoteId() != expectedCarrierId)
                ExecuteEmoteCarrier(expectedCarrierId, expectedCommand);
        });
    }

    private async Task StopIfRequiredAsync(
        CharacterModes currentMode,
        ushort currentEmoteId,
        ushort targetCarrierId,
        CancellationToken token)
    {
        bool mustJumpStop = currentMode == CharacterModes.InPositionLoop
                            || configuration.AlwaysJumpBeforeStart
                            || (currentEmoteId != 0 && currentEmoteId == targetCarrierId);

        if (!mustJumpStop)
            return;

        await framework.RunOnFrameworkThread(StopActiveAnimationByJump);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 800)
        {
            token.ThrowIfCancellationRequested();
            bool isClear = await framework.RunOnFrameworkThread(() =>
                ReadCurrentEmoteId() == 0 && ReadCharacterMode() == CharacterModes.Normal);
            if (isClear)
                break;
            await Task.Delay(30, token);
        }
        await Task.Delay(80, token);
    }

    private bool DeployModAndSwap(
        Guid collectionId,
        DanceEntry entry,
        Dictionary<string, List<string>> currentOptions,
        CarrierInfo carrier,
        byte[] slot0Bytes,
        byte[]? slot1Bytes,
        int priority)
    {
        ClearSwap(deleteFilesOnly: true);

        // Temp-enable the target mod with the user's current Penumbra options (including music On/Off, lip sync, etc.)
        // at high priority so Penumbra & FFXIV handle mod audio, physics, and facial assets directly.
        if (penumbraService.SetTemporaryModSettings(
                collectionId,
                entry.ModDirectory,
                enabled: true,
                priority: priority - 100,
                options: currentOptions,
                modName: entry.ModName))
        {
            activeTempMods.Add((collectionId, entry.ModDirectory, entry.ModName));
        }

        var swapCacheDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "swapcache");
        Directory.CreateDirectory(swapCacheDir);

        var slot0FileName = $"jd_{PapInspector.ContentTag(slot0Bytes)}.pap";
        var slot0CachePath = Path.Combine(swapCacheDir, slot0FileName);
        if (!File.Exists(slot0CachePath))
            File.WriteAllBytes(slot0CachePath, slot0Bytes);

        var allMappings = new Dictionary<string, string>();
        foreach (var path in GenerateAllPossiblePaths(carrier.Slot0Key))
            allMappings[path] = slot0CachePath;

        if (slot1Bytes != null && !string.IsNullOrEmpty(carrier.Slot1Key))
        {
            var slot1FileName = $"jd_s1_{PapInspector.ContentTag(slot1Bytes)}.pap";
            var slot1CachePath = Path.Combine(swapCacheDir, slot1FileName);
            if (!File.Exists(slot1CachePath))
                File.WriteAllBytes(slot1CachePath, slot1Bytes);

            foreach (var path in GenerateAllPossiblePaths(carrier.Slot1Key))
                allMappings[path] = slot1CachePath;
        }

        swapProbePath = allMappings.Keys.FirstOrDefault();
        swapCollectionId = collectionId;
        swapPriority = priority;

        var penumbraModDir = penumbraService.GetModDirectory();
        if (!string.IsNullOrEmpty(penumbraModDir))
        {
            try
            {
                var realModDir = Path.Combine(penumbraModDir, SwapModName);
                Directory.CreateDirectory(realModDir);

                var relMappings = new Dictionary<string, string>();
                foreach (var (gamePath, absPath) in allMappings)
                {
                    var fileName = Path.GetFileName(absPath);
                    var destPath = Path.Combine(realModDir, fileName);
                    if (!File.Exists(destPath))
                        File.Copy(absPath, destPath, overwrite: true);
                    relMappings[gamePath] = fileName;
                }

                var metaPath = Path.Combine(realModDir, "meta.json");
                if (IsV4Meta(metaPath))
                    WriteV4SwapDefaultData(realModDir, metaPath, relMappings);
                else
                    WriteV3ModFiles(realModDir, metaPath, relMappings);

                if (!swapModRegistered)
                {
                    if (penumbraService.AddMod(SwapModName))
                    {
                        swapModRegistered = true;
                        Thread.Sleep(50);
                        penumbraService.ReloadMod(SwapModName, SwapModName);
                    }
                }
                else
                {
                    penumbraService.ReloadMod(SwapModName, SwapModName);
                }

                if (swapModRegistered && penumbraService.SetTemporaryModSettings(
                        collectionId, SwapModName, enabled: true, priority: priority,
                        options: null, modName: SwapModName))
                {
                    usingTempModFallback = false;
                    activeTempMods.Add((collectionId, SwapModName, SwapModName));
                    PersistTempModKeys();
                    return true;
                }
            }
            catch (Exception ex)
            {
                log.Warning($"[Playback] Real swap mod setup failed, trying AddTemporaryMod fallback: {ex.Message}");
            }
        }

        if (penumbraService.AddTemporaryMod(TempModTag, collectionId, allMappings, priority))
        {
            usingTempModFallback = true;
            PersistTempModKeys();
            return true;
        }

        return false;
    }

    private void PersistTempModKeys()
    {
        configuration.LastTempModKeys = activeTempMods
            .Select(m => $"{m.collectionId}|{m.modDir}|{m.modName}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        configuration.Save();
    }

    private void ClearSwap(bool deleteFilesOnly = false)
    {
        if (penumbraService.IsAvailable)
        {
            if (usingTempModFallback && swapCollectionId != Guid.Empty)
                penumbraService.RemoveTemporaryMod(TempModTag, swapCollectionId, swapPriority);

            foreach (var (collId, modDir, modName) in activeTempMods)
            {
                if (deleteFilesOnly && string.Equals(modDir, SwapModName, StringComparison.OrdinalIgnoreCase))
                    continue;
                penumbraService.RemoveTemporaryModSettings(collId, modDir, modName);
            }

            if (!deleteFilesOnly)
            {
                var (ok, currentColl, _) = penumbraService.GetCurrentCollection();
                if (ok && currentColl != Guid.Empty && currentColl != swapCollectionId)
                    penumbraService.RemoveTemporaryModSettings(currentColl, SwapModName, SwapModName);
            }

            var penumbraModDir = penumbraService.GetModDirectory();
            if (!string.IsNullOrEmpty(penumbraModDir))
            {
                var realModDir = Path.Combine(penumbraModDir, SwapModName);
                if (Directory.Exists(realModDir))
                {
                    try
                    {
                        if (!deleteFilesOnly && swapModRegistered)
                        {
                            var metaPath = Path.Combine(realModDir, "meta.json");
                            if (IsV4Meta(metaPath))
                                WriteV4SwapDefaultData(realModDir, metaPath, new Dictionary<string, string>());
                            else
                                WriteV3ModFiles(realModDir, metaPath, new Dictionary<string, string>());

                            penumbraService.ReloadMod(SwapModName, SwapModName);
                        }

                        foreach (var file in Directory.GetFiles(realModDir, "jd_*.pap"))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        activeTempMods.Clear();

        if (!deleteFilesOnly)
        {
            swapModRegistered = false;
            usingTempModFallback = false;
            swapCollectionId = Guid.Empty;
            swapPriority = 0;
            swapProbePath = null;
            if (configuration.LastTempModKeys.Count > 0)
            {
                configuration.LastTempModKeys.Clear();
                configuration.Save();
            }
        }
    }

    private async Task WaitForSwapReadyAsync(CancellationToken token, int maxMs = 600)
    {
        var probe = swapProbePath;
        if (string.IsNullOrEmpty(probe))
        {
            await Task.Delay(120, token);
            return;
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            token.ThrowIfCancellationRequested();
            var resolved = await framework.RunOnFrameworkThread(() => penumbraService.ResolvePlayerPath(probe));
            if (!string.IsNullOrEmpty(resolved) &&
                !string.Equals(resolved, probe, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            await Task.Delay(16, token);
        }
    }

    private bool InferShouldLoop(DanceEntry entry, ResolvedModAssets resolved, string loopAnimName)
    {
        if (entry.LoopMode == PlaybackLoopMode.Loop)
            return true;
        if (entry.LoopMode == PlaybackLoopMode.OneShot)
            return false;

        if (!resolved.IsLoopHint)
            return false;

        var baseName = Path.GetFileNameWithoutExtension(resolved.LoopPapPath ?? "");
        if (!string.IsNullOrEmpty(baseName) && timelineKeyLoopHints.TryGetValue(baseName, out var byFile))
            return byFile;
        if (timelineKeyLoopHints.TryGetValue(loopAnimName, out var byAnim))
            return byAnim;

        return true;
    }

    private CarrierInfo? FindStartAndLoopCarrier(int maxStartLen, int maxLoopLen, ushort avoidEmoteId)
    {
        var match = FindStartLoopCore(maxStartLen, maxLoopLen, avoidEmoteId);
        if (match == null && avoidEmoteId != 0)
            match = FindStartLoopCore(maxStartLen, maxLoopLen, avoidEmoteId: 0);
        return match;
    }

    private CarrierInfo? FindStartLoopCore(int maxStartLen, int maxLoopLen, ushort avoidEmoteId)
    {
        foreach (var cmd in PreferredLoopingWithIntroCarriers)
        {
            if (!emoteCommandToId.TryGetValue(cmd, out var id))
                continue;
            var candidate = TryBuildStartLoopCarrier(id, cmd, maxStartLen, maxLoopLen, avoidEmoteId);
            if (candidate != null)
                return candidate;
        }

        foreach (var (id, _) in emoteIdToTimelines)
        {
            var cmd = FindShortestSlashCommand(id);
            if (cmd == null)
                continue;
            var candidate = TryBuildStartLoopCarrier(id, cmd, maxStartLen, maxLoopLen, avoidEmoteId);
            if (candidate != null)
                return candidate;
        }

        return null;
    }

    private CarrierInfo? TryBuildStartLoopCarrier(
        ushort emoteId,
        string command,
        int maxStartLen,
        int maxLoopLen,
        ushort avoidEmoteId)
    {
        if (emoteId == 0 || emoteId == avoidEmoteId)
            return null;
        if (ExcludedCarrierCommands.Contains(command) || ineligibleCarrierIds.Contains(emoteId))
            return null;
        if (!IsEmoteUnlocked(emoteId))
            return null;
        if (!emoteIdToTimelines.TryGetValue(emoteId, out var timelines))
            return null;

        var slot0 = timelines.FirstOrDefault(t => t.Slot == 0);
        var slot1 = timelines.FirstOrDefault(t => t.Slot == 1);
        if (slot0 == null || !slot0.IsLoop || string.IsNullOrEmpty(slot0.Key))
            return null;
        if (slot1 == null || string.IsNullOrEmpty(slot1.Key))
            return null;

        var slot0Name = GetVanillaPapName(slot0.Key);
        var slot1Name = GetVanillaPapName(slot1.Key);
        if (string.IsNullOrEmpty(slot0Name) || slot0Name.Length > maxLoopLen)
            return null;
        if (string.IsNullOrEmpty(slot1Name) || slot1Name.Length > maxStartLen)
            return null;

        return new CarrierInfo(emoteId, command, slot0.Key, slot0Name, slot0.IsLoop, slot1.Key, slot1Name);
    }

    private CarrierInfo? FindSingleCarrier(bool needsLoop, int maxNameLen, ushort avoidEmoteId)
    {
        var match = FindSingleCore(needsLoop, maxNameLen, avoidEmoteId, allowSlot1: false)
                 ?? FindSingleCore(needsLoop, maxNameLen, avoidEmoteId, allowSlot1: true);

        if (match == null && avoidEmoteId != 0)
        {
            match = FindSingleCore(needsLoop, maxNameLen, avoidEmoteId: 0, allowSlot1: false)
                 ?? FindSingleCore(needsLoop, maxNameLen, avoidEmoteId: 0, allowSlot1: true);
        }

        if (match == null)
        {
            match = FindSingleCore(null, maxNameLen, avoidEmoteId, allowSlot1: false)
                 ?? FindSingleCore(null, maxNameLen, avoidEmoteId, allowSlot1: true)
                 ?? FindSingleCore(null, maxNameLen, avoidEmoteId: 0, allowSlot1: true);
        }

        return match;
    }

    private CarrierInfo? FindSingleCore(bool? requireLoop, int maxNameLen, ushort avoidEmoteId, bool allowSlot1)
    {
        var preferredList = requireLoop switch
        {
            true => PreferredLoopingCarriers,
            false => PreferredOneShotCarriers,
            null => PreferredLoopingCarriers.Concat(PreferredOneShotCarriers).ToArray(),
        };

        foreach (var cmd in preferredList)
        {
            if (!emoteCommandToId.TryGetValue(cmd, out var id))
                continue;
            var candidate = TryBuildSingleCarrier(id, cmd, requireLoop, maxNameLen, avoidEmoteId, allowSlot1);
            if (candidate != null)
                return candidate;
        }

        foreach (var (id, _) in emoteIdToTimelines)
        {
            var cmd = FindShortestSlashCommand(id);
            if (cmd == null)
                continue;
            var candidate = TryBuildSingleCarrier(id, cmd, requireLoop, maxNameLen, avoidEmoteId, allowSlot1);
            if (candidate != null)
                return candidate;
        }

        return null;
    }

    private CarrierInfo? TryBuildSingleCarrier(
        ushort emoteId,
        string command,
        bool? requireLoop,
        int maxNameLen,
        ushort avoidEmoteId,
        bool allowSlot1)
    {
        if (emoteId == 0 || emoteId == avoidEmoteId)
            return null;
        if (ExcludedCarrierCommands.Contains(command) || ineligibleCarrierIds.Contains(emoteId))
            return null;
        if (!IsEmoteUnlocked(emoteId))
            return null;
        if (!emoteIdToTimelines.TryGetValue(emoteId, out var timelines))
            return null;

        var slot0 = timelines.FirstOrDefault(t => t.Slot == 0);
        if (slot0 == null || string.IsNullOrEmpty(slot0.Key))
            return null;
        if (requireLoop.HasValue && slot0.IsLoop != requireLoop.Value)
            return null;

        var slot0PapName = GetVanillaPapName(slot0.Key);
        if (string.IsNullOrEmpty(slot0PapName) || slot0PapName.Length > maxNameLen)
            return null;

        var slot1 = timelines.FirstOrDefault(t => t.Slot == 1);
        string? slot1Key = null;
        string? slot1PapName = null;

        if (slot1 != null && !string.IsNullOrEmpty(slot1.Key))
        {
            if (!allowSlot1)
                return null;

            slot1PapName = GetVanillaPapName(slot1.Key);
            if (!string.IsNullOrEmpty(slot1PapName))
            {
                if (slot1PapName.Length > maxNameLen)
                    return null;
                slot1Key = slot1.Key;
            }
        }

        return new CarrierInfo(emoteId, command, slot0.Key, slot0PapName, slot0.IsLoop, slot1Key, slot1PapName);
    }

    private string? FindShortestSlashCommand(ushort emoteId)
    {
        string? best = null;
        foreach (var (cmd, id) in emoteCommandToId)
        {
            if (id != emoteId || !cmd.StartsWith('/'))
                continue;
            if (best == null || cmd.Length < best.Length)
                best = cmd;
        }
        return best;
    }

    private string? GetVanillaPapName(string timelineKey)
    {
        if (carrierPapNameCache.TryGetValue(timelineKey, out var cached))
            return cached;

        var paths = ResolveEmotePaths(timelineKey);
        foreach (var path in paths)
        {
            var file = dataManager.GetFile(path);
            if (file == null)
                continue;

            var name = PapInspector.ReadPrimaryAnimationName(file.Data);
            if (!string.IsNullOrEmpty(name))
            {
                carrierPapNameCache[timelineKey] = name;
                return name;
            }
        }

        return null;
    }

    private List<string> ResolveEmotePaths(string timelineKey)
    {
        var paths = new List<string>();
        var globalPath = $"chara/animation/{timelineKey}.pap";
        if (dataManager.FileExists(globalPath))
            paths.Add(globalPath);

        var race = GetPlayerRaceCode();
        if (race != null)
        {
            foreach (var layer in new[] { "a0001", "a0002" })
            {
                foreach (var sub in AnimSubfolders)
                {
                    var p = $"chara/human/{race}/animation/{layer}/{sub}{timelineKey}.pap";
                    if (dataManager.FileExists(p))
                        paths.Add(p);
                }
            }
        }

        if (paths.Count == 0)
        {
            foreach (var r in RaceIds)
            {
                foreach (var layer in new[] { "a0001", "a0002" })
                {
                    foreach (var sub in CarrierPathSubfolders)
                    {
                        var p = $"chara/human/{r}/animation/{layer}/{sub}{timelineKey}.pap";
                        if (dataManager.FileExists(p))
                        {
                            paths.Add(p);
                            return paths;
                        }
                    }
                }
            }
        }

        return paths;
    }

    private static List<string> GenerateAllPossiblePaths(string timelineKey)
    {
        var paths = new List<string> { $"chara/animation/{timelineKey}.pap" };
        foreach (var race in RaceIds)
        {
            foreach (var layer in new[] { "a0001", "a0002" })
            {
                foreach (var sub in CarrierPathSubfolders)
                    paths.Add($"chara/human/{race}/animation/{layer}/{sub}{timelineKey}.pap");
            }
        }
        return paths;
    }

    private string? GetPlayerRaceCode()
    {
        try
        {
            var glamourer = pluginInterface.GetIpcSubscriber<int, uint, (int, JObject?)>("Glamourer.GetState");
            var (ec, state) = glamourer.InvokeFunc(0, 0u);
            if (ec == 0 && state?["Customize"] is JObject customize)
            {
                var r = customize["Race"]?["Value"]?.Value<byte>() ?? customize["Race"]?.Value<byte>();
                var g = customize["Gender"]?["Value"]?.Value<byte>() ?? customize["Gender"]?.Value<byte>();
                var t = customize["Clan"]?["Value"]?.Value<byte>() ?? customize["Clan"]?.Value<byte>();
                if (r.HasValue && g.HasValue && t.HasValue)
                {
                    var code = RaceToModelCode(r.Value, g.Value, t.Value);
                    if (code != null)
                        return code;
                }
            }
        }
        catch
        {
        }

        if (objectTable.LocalPlayer is ICharacter ch)
        {
            var cust = ch.Customize;
            return RaceToModelCode(cust[0], cust[1], cust[4]);
        }

        return null;
    }

    private static string? RaceToModelCode(byte race, byte gender, byte tribe)
    {
        int modelBase = race switch
        {
            1 when tribe == 1 => 1,
            1 => 3,
            2 => 5,
            3 => 11,
            4 => 7,
            5 => 9,
            6 => 13,
            7 => 15,
            8 => 17,
            _ => 0,
        };
        return modelBase == 0 ? null : $"c{(modelBase + gender):D2}01";
    }

    private unsafe void ExecuteEmoteCarrier(ushort emoteId, string command)
    {
        try
        {
            var manager = EmoteManager.Instance();
            if (manager != null && manager->ExecuteEmote(emoteId))
                return;
        }
        catch (Exception ex)
        {
            log.Debug($"[Playback] EmoteManager.ExecuteEmote({emoteId}) failed: {ex.Message}");
        }

        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule != null)
            {
                var cmd = command.StartsWith('/') ? command : "/" + command;
                using var str = new Utf8String(cmd);
                uiModule->ProcessChatBoxEntry(&str);
            }
        }
        catch (Exception ex)
        {
            log.Error($"[Playback] ChatBox fallback failed for '{command}': {ex.Message}");
        }
    }

    private unsafe void StopActiveAnimationByJump()
    {
        try
        {
            var am = ActionManager.Instance();
            if (am != null)
                am->UseAction(ActionType.GeneralAction, 2);
        }
        catch (Exception ex)
        {
            log.Debug($"[Playback] Jump stop failed: {ex.Message}");
        }
    }

    private unsafe ushort ReadCurrentEmoteId()
    {
        var lp = objectTable.LocalPlayer;
        if (lp == null || lp.Address == nint.Zero)
            return 0;
        var ch = (Character*)lp.Address;
        return ch != null ? ch->EmoteController.EmoteId : (ushort)0;
    }

    private unsafe CharacterModes ReadCharacterMode()
    {
        var lp = objectTable.LocalPlayer;
        if (lp == null || lp.Address == nint.Zero)
            return CharacterModes.Normal;
        var ch = (Character*)lp.Address;
        return ch != null ? ch->Mode : CharacterModes.Normal;
    }

    private static unsafe bool IsEmoteUnlocked(ushort emoteId)
    {
        try
        {
            var uiState = UIState.Instance();
            return uiState != null && uiState->IsEmoteUnlocked(emoteId);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsV4Meta(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
                return false;
            var meta = JObject.Parse(File.ReadAllText(metaPath));
            return (meta["FileVersion"]?.Value<int>() ?? 0) >= 4;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteV3ModFiles(string realModDir, string metaPath, Dictionary<string, string> relMappings)
    {
        var filesObj = new JObject();
        foreach (var (gamePath, fileName) in relMappings)
            filesObj[gamePath.Replace("\\", "/")] = fileName;

        var defaultMod = new JObject
        {
            ["Name"] = "",
            ["Priority"] = 0,
            ["Files"] = filesObj,
            ["FileSwaps"] = new JObject(),
            ["Manipulations"] = new JArray(),
        };
        File.WriteAllText(Path.Combine(realModDir, "default_mod.json"), defaultMod.ToString(Newtonsoft.Json.Formatting.Indented));

        if (!File.Exists(metaPath))
        {
            var meta = new JObject
            {
                ["FileVersion"] = 3,
                ["Name"] = SwapModName,
                ["Author"] = "JustDance",
                ["Description"] = "Temporary animation swap managed by JustDance.",
                ["Version"] = "1.0.0",
                ["Website"] = "",
                ["ModTags"] = new JArray(),
            };
            File.WriteAllText(metaPath, meta.ToString(Newtonsoft.Json.Formatting.Indented));
        }
    }

    private static void WriteV4SwapDefaultData(string realModDir, string metaPath, Dictionary<string, string> relMappings)
    {
        var meta = JObject.Parse(File.ReadAllText(metaPath));
        var files = new JObject();
        foreach (var (gamePath, fileName) in relMappings)
            files[gamePath.Replace("\\", "/")] = fileName;

        meta["DefaultData"] = new JObject
        {
            ["Files"] = files,
            ["FileSwaps"] = new JObject(),
            ["Manipulations"] = new JArray(),
        };
        File.WriteAllText(metaPath, meta.ToString(Newtonsoft.Json.Formatting.Indented));

        var legacyDefault = Path.Combine(realModDir, "default_mod.json");
        if (File.Exists(legacyDefault))
        {
            try { File.Delete(legacyDefault); } catch { }
        }
    }

    public void Dispose()
    {
        lock (this)
        {
            activeTransitionCts?.Cancel();
            activeTransitionCts?.Dispose();
            activeTransitionCts = null;
        }

        ClearSwap();
        transitionLock.Dispose();
    }
}
