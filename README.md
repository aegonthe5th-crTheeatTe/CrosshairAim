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

## Install 
1. Start the game through Pulsar
2. Open the **Plugins** list
3. Search for **CrosshairAim**, enable it
4. Apply and restart when prompted

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
