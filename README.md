# JustDance

A lightweight Dalamud plugin for browsing and playing Penumbra dance/animation mods directly from an organized folder tree.

## Requirements
- [Penumbra](https://github.com/xivdev/Penumbra) (API v5+)

## Installation
Add the following URL to your Dalamud Plugin Repositories:
`https://raw.githubusercontent.com/Etupa/Dalamud_Plugins/master/pluginmaster.json`

## Commands
- `/justdance` (or `/jd`): Toggle the JustDance window.
- `/justdance stop`: Stop the currently playing dance animation.
- `/justdance scan`: Rescan the currently selected Penumbra folder.

## Features
- **Penumbra Folder Browser**: Select any folder inside Penumbra and mirror its subfolder structure into JustDance.
- **One-Click Playback**: Automatically pairs Start + Loop `.pap` animations and overrides Penumbra priorities cleanly during playback without altering your saved mod priorities.
- **Penumbra Settings Bridge**: Right-click any dance to open JustDance's Mod Settings sub-window (mirroring Penumbra's Enabled state, Priority, and all option groups), and pin any dropdown or checkbox directly onto the main window.
- **Music Toggle**: Displays a speaker toggle for mods that provide a Penumbra music setting.
- **Organization**: Custom folders/subfolders, drag-and-drop sorting, search filter, and favorites.
