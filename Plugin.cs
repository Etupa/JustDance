using System;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using JustDance.Services;
using JustDance.Windows;

namespace JustDance;

public sealed class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/justdance";
    private const string ShortCommand = "/jd";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    public Configuration Configuration { get; }
    public PenumbraService PenumbraService { get; }
    public FolderScannerService ScannerService { get; }
    public AnimationPlaybackService PlaybackService { get; }

    public WindowSystem WindowSystem { get; } = new("JustDance");
    private MainWindow MainWindow { get; }
    private PenumbraFolderPickerWindow FolderPickerWindow { get; }
    private ModSettingsWindow ModSettingsWindow { get; }

    private bool shortCommandRegistered;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        if (Configuration.Version < 2)
        {
            Configuration.Entries.RemoveAll(e => string.IsNullOrWhiteSpace(e.ModDirectory));
            Configuration.Version = 2;
            Configuration.Save();
        }

        PenumbraService = new PenumbraService(PluginInterface, Log);
        ScannerService = new FolderScannerService(Configuration, PenumbraService, Framework, Log);
        PlaybackService = new AnimationPlaybackService(
            PluginInterface, Framework, ObjectTable, DataManager, Log, Configuration, PenumbraService);

        FolderPickerWindow = new PenumbraFolderPickerWindow(PenumbraService, OnPenumbraFolderSelected);
        ModSettingsWindow = new ModSettingsWindow(Configuration, PenumbraService, PlaybackService);
        MainWindow = new MainWindow(
            Configuration, PenumbraService, ScannerService, PlaybackService, FolderPickerWindow, ModSettingsWindow);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(FolderPickerWindow);
        WindowSystem.AddWindow(ModSettingsWindow);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
        Framework.Update += OnFrameworkUpdate;

        CommandManager.AddHandler(MainCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open JustDance. Subcommands: stop, scan"
        });

        try
        {
            CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open JustDance. Subcommands: stop, scan"
            });
            shortCommandRegistered = true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not register {ShortCommand}: {ex.Message}");
        }

        Log.Information("JustDance loaded.");
    }

    private void OnPenumbraFolderSelected(string penumbraFolder)
    {
        Configuration.SelectedPenumbraFolder = penumbraFolder;
        Configuration.Save();
        ScannerService.ScanAsync(penumbraFolder, summary =>
        {
            if (Configuration.ShowChatMessages)
                PrintChat(summary);
        });
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim().ToLowerInvariant();
        switch (trimmed)
        {
            case "stop":
                PlaybackService.Stop();
                if (Configuration.ShowChatMessages)
                    PrintChat("Stopped animation.");
                break;

            case "scan":
            case "rescan":
                if (Configuration.SelectedPenumbraFolder == null)
                    MainWindow.IsOpen = true;
                else
                    ScannerService.ScanAsync(Configuration.SelectedPenumbraFolder, summary =>
                    {
                        if (Configuration.ShowChatMessages)
                            PrintChat(summary);
                    });
                break;

            default:
                ToggleMainWindow();
                break;
        }
    }

    private void ToggleMainWindow() => MainWindow.Toggle();

    private void OnFrameworkUpdate(IFramework fw)
    {
        PlaybackService.Update();
    }

    private void PrintChat(string message, XivChatType type = XivChatType.Echo)
    {
        var seString = new SeStringBuilder()
            .AddUiForeground("[JustDance] ", 45)
            .AddText(message)
            .Build();

        ChatGui.Print(new XivChatEntry
        {
            Message = seString,
            Type = type,
        });
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;

        CommandManager.RemoveHandler(MainCommand);
        if (shortCommandRegistered)
            CommandManager.RemoveHandler(ShortCommand);

        WindowSystem.RemoveAllWindows();
        PlaybackService.Dispose();
        PenumbraService.Dispose();
    }
}
