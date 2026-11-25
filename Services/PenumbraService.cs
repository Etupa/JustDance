using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace JustDance.Services;

public sealed record PenumbraModInfo(
    string ModDirectory,
    string ModName,
    string PenumbraFolder,
    string SortName);

/// <summary>
/// IPC wrapper for interacting with Penumbra (API v5+).
/// </summary>
public sealed class PenumbraService : IDisposable
{
    private const int TabTypeMods = 1;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    private ICallGateSubscriber<int>? apiVersionSubscriber;
    private ICallGateSubscriber<Dictionary<string, string>>? getModListSubscriber;
    private ICallGateSubscriber<string, string, (int, string, bool, bool)>? getModPathSubscriber;
    private ICallGateSubscriber<byte, (Guid, string)?>? getCollectionSubscriber;
    private ICallGateSubscriber<int, (bool, bool, (Guid, string))>? getCollectionForObjectSubscriber;
    private ICallGateSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>? getCurrentModSettingsSubscriber;
    private ICallGateSubscriber<string, string, IReadOnlyDictionary<string, (string[], int)>?>? getAvailableModSettingsSubscriber;
    private ICallGateSubscriber<Guid, string, string, bool, int>? trySetModSubscriber;
    private ICallGateSubscriber<Guid, string, string, int, int>? trySetModPrioritySubscriber;
    private ICallGateSubscriber<Guid, string, string, string, IReadOnlyList<string>, int>? trySetModSettingsSubscriber;
    private ICallGateSubscriber<Guid, string, string,
        (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>),
        string, int, int>? setTemporaryModSettingsSubscriber;
    private ICallGateSubscriber<Guid, string, string, int, int>? removeTemporaryModSettingsSubscriber;
    private ICallGateSubscriber<string>? getModDirectorySubscriber;
    private ICallGateSubscriber<string, int>? addModSubscriber;
    private ICallGateSubscriber<string, string, int>? reloadModSubscriber;
    private ICallGateSubscriber<string, Guid, Dictionary<string, string>, string, int, int>? addTemporaryModSubscriber;
    private ICallGateSubscriber<string, Guid, int, int>? removeTemporaryModSubscriber;
    private ICallGateSubscriber<string, string>? resolvePlayerPathSubscriber;
    private ICallGateSubscriber<int, string, string, int>? openMainWindowSubscriber;

    public bool IsAvailable { get; private set; }
    public int ApiVersion { get; private set; }

    public PenumbraService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        TryInitialize();
    }

    public bool EnsureAvailable()
    {
        if (IsAvailable)
            return true;

        return TryInitialize();
    }

    private bool TryInitialize()
    {
        try
        {
            apiVersionSubscriber ??= pluginInterface.GetIpcSubscriber<int>("Penumbra.ApiVersion");
            ApiVersion = apiVersionSubscriber.InvokeFunc();
            IsAvailable = ApiVersion >= 5;

            if (!IsAvailable)
            {
                log.Warning($"[Penumbra] API version {ApiVersion} is unsupported (minimum 5).");
                return false;
            }

            getModListSubscriber ??= pluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
            getModPathSubscriber ??= pluginInterface.GetIpcSubscriber<string, string, (int, string, bool, bool)>("Penumbra.GetModPath.V5");
            getCollectionSubscriber ??= pluginInterface.GetIpcSubscriber<byte, (Guid, string)?>("Penumbra.GetCollection");
            getCollectionForObjectSubscriber ??= pluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
            getCurrentModSettingsSubscriber ??= pluginInterface.GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>("Penumbra.GetCurrentModSettings.V5");
            getAvailableModSettingsSubscriber ??= pluginInterface.GetIpcSubscriber<string, string, IReadOnlyDictionary<string, (string[], int)>?>("Penumbra.GetAvailableModSettings.V5");
            trySetModSubscriber ??= pluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5");
            trySetModPrioritySubscriber ??= pluginInterface.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.TrySetModPriority.V5");
            trySetModSettingsSubscriber ??= pluginInterface.GetIpcSubscriber<Guid, string, string, string, IReadOnlyList<string>, int>("Penumbra.TrySetModSettings.V5");
            setTemporaryModSettingsSubscriber ??= pluginInterface.GetIpcSubscriber<Guid, string, string,
                (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>),
                string, int, int>("Penumbra.SetTemporaryModSettings.V5");
            removeTemporaryModSettingsSubscriber ??= pluginInterface.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.RemoveTemporaryModSettings.V5");
            getModDirectorySubscriber ??= pluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
            addModSubscriber ??= pluginInterface.GetIpcSubscriber<string, int>("Penumbra.AddMod.V5");
            reloadModSubscriber ??= pluginInterface.GetIpcSubscriber<string, string, int>("Penumbra.ReloadMod.V5");
            addTemporaryModSubscriber ??= pluginInterface.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5");
            removeTemporaryModSubscriber ??= pluginInterface.GetIpcSubscriber<string, Guid, int, int>("Penumbra.RemoveTemporaryMod.V5");
            resolvePlayerPathSubscriber ??= pluginInterface.GetIpcSubscriber<string, string>("Penumbra.ResolvePlayerPath");
            openMainWindowSubscriber ??= pluginInterface.GetIpcSubscriber<int, string, string, int>("Penumbra.OpenMainWindow.V5");

            log.Information($"[Penumbra] IPC initialized (API v{ApiVersion}).");
            return true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            log.Debug($"[Penumbra] Not available yet: {ex.Message}");
            return false;
        }
    }

    public string? GetModDirectory()
    {
        if (!EnsureAvailable() || getModDirectorySubscriber == null)
            return null;

        try
        {
            var dir = getModDirectorySubscriber.InvokeFunc();
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch (Exception ex)
        {
            log.Debug($"[Penumbra] GetModDirectory failed: {ex.Message}");
            return null;
        }
    }

    public Dictionary<string, string> GetModList()
    {
        if (!EnsureAvailable() || getModListSubscriber == null)
            return new Dictionary<string, string>();

        try
        {
            return getModListSubscriber.InvokeFunc() ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            log.Debug($"[Penumbra] GetModList failed: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Queries Penumbra's live virtual folder organization via GetModPath.V5 and organization.json.
    /// </summary>
    public (List<PenumbraModInfo> mods, HashSet<string> folders) GetPenumbraHierarchy()
    {
        var mods = new List<PenumbraModInfo>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!EnsureAvailable())
            return (mods, folders);

        var modList = GetModList();
        foreach (var (modDir, modName) in modList)
        {
            if (string.Equals(modDir, AnimationPlaybackService.SwapModName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modDir, "_EncoreEmoteSwap", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (folder, sortName) = GetModVirtualFolder(modDir, modName);
            mods.Add(new PenumbraModInfo(modDir, modName, folder, sortName));

            AddFolderAncestors(folder, folders);
        }

        foreach (var extraFolder in ReadOrganizationFolders())
            AddFolderAncestors(extraFolder, folders);

        return (mods, folders);
    }

    private (string folder, string sortName) GetModVirtualFolder(string modDir, string modName)
    {
        if (getModPathSubscriber != null)
        {
            try
            {
                var (ec, fullPath, _, _) = getModPathSubscriber.InvokeFunc(modDir, modName);
                if (ec == 0 && !string.IsNullOrWhiteSpace(fullPath))
                {
                    int lastSlash = fullPath.LastIndexOf('/');
                    if (lastSlash > 0)
                        return (fullPath[..lastSlash], fullPath[(lastSlash + 1)..]);
                    return ("", fullPath);
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[Penumbra] GetModPath failed for '{modDir}': {ex.Message}");
            }
        }

        return ("", modName);
    }

    private static void AddFolderAncestors(string folderPath, HashSet<string> sink)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        var parts = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current = string.IsNullOrEmpty(current) ? part : $"{current}/{part}";
            sink.Add(current);
        }
    }

    private IEnumerable<string> ReadOrganizationFolders()
    {
        var results = new List<string>();
        try
        {
            var configRoot = Path.GetFullPath(Path.Combine(pluginInterface.GetPluginConfigDirectory(), "..", "Penumbra"));
            var orgPath = Path.Combine(configRoot, "mod_filesystem", "organization.json");
            if (File.Exists(orgPath))
            {
                var root = JObject.Parse(File.ReadAllText(orgPath));
                if (root["Folders"] is JObject foldersObj)
                {
                    foreach (var prop in foldersObj.Properties())
                    {
                        if (!string.IsNullOrWhiteSpace(prop.Name))
                            results.Add(prop.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Debug($"[Penumbra] Could not read mod_filesystem/organization.json: {ex.Message}");
        }

        return results;
    }

    public (bool success, Guid collectionId, string collectionName) GetCurrentCollection()
    {
        if (!EnsureAvailable())
            return (false, Guid.Empty, "");

        try
        {
            if (getCollectionForObjectSubscriber != null)
            {
                var (objectValid, _, (id, name)) = getCollectionForObjectSubscriber.InvokeFunc(0);
                if (objectValid && id != Guid.Empty)
                    return (true, id, name);
            }

            if (getCollectionSubscriber != null)
            {
                var result = getCollectionSubscriber.InvokeFunc(0xE2); // ApiCollectionType.Current
                if (result.HasValue && result.Value.Item1 != Guid.Empty)
                    return (true, result.Value.Item1, result.Value.Item2);
            }
        }
        catch (Exception ex)
        {
            log.Error($"[Penumbra] Failed to resolve current collection: {ex.Message}");
        }

        return (false, Guid.Empty, "");
    }

    /// <summary>
    /// Reads the user's current permanent/inherited Penumbra settings for a mod (ignoring temporary overrides).
    /// </summary>
    public (bool success, bool enabled, int priority, Dictionary<string, List<string>> options) GetCurrentModSettings(
        Guid collectionId, string modDirectory, string modName = "")
    {
        if (!EnsureAvailable() || getCurrentModSettingsSubscriber == null)
            return (false, false, 0, new Dictionary<string, List<string>>());

        try
        {
            var (resultCode, settings) = getCurrentModSettingsSubscriber.InvokeFunc(collectionId, modDirectory, modName, false);
            if (resultCode == 0 && settings.HasValue)
            {
                var (enabled, priority, options, _) = settings.Value;
                return (true, enabled, priority, options ?? new Dictionary<string, List<string>>());
            }
        }
        catch (Exception ex)
        {
            log.Debug($"[Penumbra] GetCurrentModSettings('{modDirectory}') failed: {ex.Message}");
        }

        return (false, false, 0, new Dictionary<string, List<string>>());
    }

    /// <summary>
    /// Updates a mod's permanent option selection in Penumbra for the given collection and group.
    /// </summary>
    public bool TrySetModSettings(
        Guid collectionId,
        string modDirectory,
        string optionGroupName,
        IReadOnlyList<string> optionNames,
        string modName = "")
    {
        if (!EnsureAvailable() || trySetModSettingsSubscriber == null)
            return false;

        try
        {
            var result = trySetModSettingsSubscriber.InvokeFunc(
                collectionId, modDirectory, modName, optionGroupName, optionNames);
            return result == 0 || result == 1;
        }
        catch (Exception ex)
        {
            log.Error($"[Penumbra] TrySetModSettings('{modDirectory}', '{optionGroupName}') failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns Penumbra's available option groups for a mod: groupName -> (optionNames[], GroupType).
    /// GroupType: 0 = Single, 1 = Multi, 2 = Imc, 3 = Combining.
    /// </summary>
    public IReadOnlyDictionary<string, (string[] Options, int GroupType)> GetAvailableModSettings(
        string modDirectory, string modName = "")
    {
        if (!EnsureAvailable() || getAvailableModSettingsSubscriber == null)
            return new Dictionary<string, (string[] Options, int GroupType)>();

        try
        {
            var raw = getAvailableModSettingsSubscriber.InvokeFunc(modDirectory, modName);
            if (raw == null)
                return new Dictionary<string, (string[] Options, int GroupType)>();

            var dict = new Dictionary<string, (string[] Options, int GroupType)>(StringComparer.Ordinal);
            foreach (var kv in raw)
                dict[kv.Key] = (kv.Value.Item1 ?? Array.Empty<string>(), kv.Value.Item2);
            return dict;
        }
        catch (Exception ex)
        {
            log.Debug($"[Penumbra] GetAvailableModSettings('{modDirectory}') failed: {ex.Message}");
            return new Dictionary<string, (string[] Options, int GroupType)>();
        }
    }

    public bool TrySetModEnabled(Guid collectionId, string modDirectory, bool enabled, string modName = "")
    {
        if (!EnsureAvailable() || trySetModSubscriber == null)
            return false;

        try
        {
            var result = trySetModSubscriber.InvokeFunc(collectionId, modDirectory, modName, enabled);
            return result == 0 || result == 1;
        }
        catch (Exception ex)
        {
            log.Error($"[Penumbra] TrySetMod('{modDirectory}', {enabled}) failed: {ex.Message}");
            return false;
        }
    }

    public bool TrySetModPriority(Guid collectionId, string modDirectory, int priority, string modName = "")
    {
        if (!EnsureAvailable() || trySetModPrioritySubscriber == null)
            return false;

        try
        {
            var result = trySetModPrioritySubscriber.InvokeFunc(collectionId, modDirectory, modName, priority);
            return result == 0 || result == 1;
        }
        catch (Exception ex)
        {
            log.Error($"[Penumbra] TrySetModPriority('{modDirectory}', {priority}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Opens Penumbra's Mods tab directly on the specified mod so the user can edit its settings.
    /// </summary>
    public bool OpenModInPenumbra(string modDirectory, string modName = "")
    {
        if (!EnsureAvailable() || openMainWindowSubscriber == null)
            return false;

        try
        {
            var result = openMainWindowSubscriber.InvokeFunc(TabTypeMods, modDirectory, modName);
            return result == 0;
        }
        catch (Exception ex)
        {
            log.Warning($"[Penumbra] OpenMainWindow('{modDirectory}') failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Computes a priority strictly above every mod in the given collection so JustDance overrides any user-set priority.
    /// </summary>
    public int GetOverridePriority(Guid collectionId, string? excludeModDirectory = null)
    {
        const int baseline = 1_000_000;
        long highest = 0;

        if (!EnsureAvailable() || getModListSubscriber == null || getCurrentModSettingsSubscriber == null)
            return baseline;

        try
        {
            var mods = getModListSubscriber.InvokeFunc();
            if (mods != null)
            {
                foreach (var (modDir, modName) in mods)
                {
                    if (!string.IsNullOrEmpty(excludeModDirectory) &&
                        string.Equals(modDir, excludeModDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var (code, settings) = getCurrentModSettingsSubscriber.InvokeFunc(collectionId, modDir, modName, true);
                    if (code == 0 && settings.HasValue && settings.Value.Item1)
                    {
                        long prio = settings.Value.Item2;
                        if (prio > highest)
                            highest = prio;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Debug($"[Penumbra] Failed to scan priorities: {ex.Message}");
        }

        long target = Math.Max(baseline, highest + 10_000);
        return (int)Math.Min(target, int.MaxValue - 1);
    }

    public bool AddMod(string modDirectory)
    {
        if (!EnsureAvailable() || addModSubscriber == null)
            return false;

        try
        {
            var result = addModSubscriber.InvokeFunc(modDirectory);
            return result == 0 || result == 1;
        }
        catch (Exception ex)
        {
            log.Warning($"[Penumbra] AddMod('{modDirectory}') failed: {ex.Message}");
            return false;
        }
    }

    public bool ReloadMod(string modDirectory, string modName = "")
    {
        if (!EnsureAvailable() || reloadModSubscriber == null)
            return false;

        try
        {
            var result = reloadModSubscriber.InvokeFunc(modDirectory, modName);
            return result == 0 || result == 1;
        }
        catch (Exception ex)
        {
            log.Warning($"[Penumbra] ReloadMod('{modDirectory}') failed: {ex.Message}");
            return false;
        }
    }

    public bool SetTemporaryModSettings(
        Guid collectionId,
        string modDirectory,
        bool enabled,
        int priority,
        Dictionary<string, List<string>>? options = null,
        string modName = "")
    {
        if (!EnsureAvailable() || setTemporaryModSettingsSubscriber == null)
            return false;

        try
        {
            var settings = new Dictionary<string, IReadOnlyList<string>>();
            if (options != null)
            {
                foreach (var (k, v) in options)
                    settings[k] = v;
            }

            var pseudoSettings = (false, enabled, priority, (IReadOnlyDictionary<string, IReadOnlyList<string>>)settings);
            var result = setTemporaryModSettingsSubscriber.InvokeFunc(
                collectionId, modDirectory, modName, pseudoSettings, "JustDance", 0);

            return result == 0 || result == 1;
        }
        catch (Exception ex)
        {
            log.Error($"[Penumbra] SetTemporaryModSettings('{modDirectory}') failed: {ex.Message}");
            return false;
        }
    }

    public bool RemoveTemporaryModSettings(Guid collectionId, string modDirectory, string modName = "")
    {
        if (!EnsureAvailable() || removeTemporaryModSettingsSubscriber == null)
            return false;

        try
        {
            var result = removeTemporaryModSettingsSubscriber.InvokeFunc(collectionId, modDirectory, modName, 0);
            return result == 0 || result == 1;
        }
        catch (Exception ex)
        {
            log.Debug($"[Penumbra] RemoveTemporaryModSettings('{modDirectory}') failed: {ex.Message}");
            return false;
        }
    }

    public bool AddTemporaryMod(string tag, Guid collectionId, Dictionary<string, string> paths, int priority)
    {
        if (!EnsureAvailable() || addTemporaryModSubscriber == null)
            return false;

        try
        {
            var result = addTemporaryModSubscriber.InvokeFunc(tag, collectionId, paths, "", priority);
            return result == 0;
        }
        catch (Exception ex)
        {
            log.Error($"[Penumbra] AddTemporaryMod('{tag}') failed: {ex.Message}");
            return false;
        }
    }

    public bool RemoveTemporaryMod(string tag, Guid collectionId, int priority)
    {
        if (!EnsureAvailable() || removeTemporaryModSubscriber == null)
            return false;

        try
        {
            var result = removeTemporaryModSubscriber.InvokeFunc(tag, collectionId, priority);
            return result == 0 || result == 1;
        }
        catch (Exception ex)
        {
            log.Debug($"[Penumbra] RemoveTemporaryMod('{tag}') failed: {ex.Message}");
            return false;
        }
    }

    public string? ResolvePlayerPath(string gamePath)
    {
        if (!EnsureAvailable() || resolvePlayerPathSubscriber == null)
            return null;

        try
        {
            return resolvePlayerPathSubscriber.InvokeFunc(gamePath);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
    }
}
