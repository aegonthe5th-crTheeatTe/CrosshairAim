# CrosshairAim

Client-side [Pulsar](https://github.com/SpaceGT/Pulsar) plugin for **Space Engineers 1**.

Companion to **Over The Shoulder+** and **"Take cover or die"**.

While either of those camera mods is driving the camera, this plugin makes:

- Bullets / weapon aim follow the on-screen crosshair
- The use / interact key use the same ray
- Aim zoom (from Over The Shoulder+)
- Sprint while the cover mod runs you to a wall
- A/D walk along cover (W/S and Space blocked in cover)
- Jump blocked while in cover
- The game's crosshair hidden when the cover mod shows its red one

When those mods are not active the game is left completely alone.  


## Requirements

- Space Engineers
- [Pulsar](https://github.com/SpaceGT/Pulsar)

CrosshairAim – Manual Install Guide

This plugin is installed manually (not through the Plugin Hub).
Requirements

    Space Engineers
    [Pulsar](https://github.com/SpaceGT/Pulsar) (the current plugin loader)

Install steps

    Download the plugin zip and extract it.

    Unblock the files (important on Windows)
    Windows often blocks downloaded DLLs.
        Right-click the .dll file → Properties
        At the bottom, if you see a checkbox that says Unblock, tick it and click Apply → OK

    Copy the files into Pulsar’s Local folder:
        Open File Explorer and paste this into the address bar:
        %AppData%\Pulsar
        Go into either Legacy\Local or Interim\Local
        (use the one that matches how you launch the game)
        Paste the plugin files here.

    Enable the plugin
        Launch Space Engineers through Pulsar.
        On the main menu click Plugins.
        Find CrosshairAim, enable it, then click Apply and restart when prompted.

Notes

    The plugin only does anything when Over The Shoulder+ or Take Cover or Die is active.
    If the plugin doesn’t appear in the list, make sure the files were unblocked and placed in the correct Local folder.

## Local / development install

Copy a Release build of `CrosshairAim.dll` (and the accompanying `plugin.xml`) into:

```
%AppData%\Pulsar\Legacy\Local\CrosshairAim\
```

(or the equivalent Interim folder for the .NET 10 runtime). Then enable it in the Pulsar plugin list.

## Build

Based on the [client plugin template](https://github.com/CometWorks/client-plugin-template).

1. Install the prerequisites listed in the template (Python 3.12+, .NET SDK, etc.)
2. Run `python setup.py` once and enter `CrosshairAim` when asked for the project name (or just edit `Directory.Build.props.user` with your game / Pulsar paths)
3. `dotnet build ClientPlugin/ClientPlugin.csproj -c Release`


## License

See [LICENSE](LICENSE).
