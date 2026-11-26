# Third-party notices

LoL Config Sync is licensed under the [MIT License](LICENSE). The released app bundles the following
third-party software, each under its own license.

| Component | Used for | License |
|-----------|----------|---------|
| [.NET runtime and WPF](https://github.com/dotnet/runtime) (Microsoft) | Runs the app; bundled into the self-contained `LolConfigSync.exe` | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| [H.NotifyIcon, H.NotifyIcon.Wpf, H.GeneratedIcons.System.Drawing](https://github.com/HavenDV/H.NotifyIcon) (havendv) | Tray icon and notifications | [MIT](https://github.com/HavenDV/H.NotifyIcon/blob/master/LICENSE) |

Build-time tools that are not shipped inside the app:

| Tool | Used for | License |
|------|----------|---------|
| [Inno Setup](https://jrsoftware.org/isinfo.php) | Builds the installer | [Inno Setup License](https://jrsoftware.org/files/is/license.txt), free for commercial and non-commercial use |
| [xUnit](https://github.com/xunit/xunit), [Microsoft.Extensions.TimeProvider.Testing](https://github.com/dotnet/extensions) | Unit tests | Apache-2.0 / MIT |

## Riot Games

LoL Config Sync isn't endorsed by Riot Games and doesn't reflect the views or opinions of Riot Games or anyone
officially involved in producing or managing Riot Games properties. Riot Games, and all associated properties are
trademarks or registered trademarks of Riot Games, Inc.

The app uses the League client's local interface (the "LCU API") on your own computer. It does not read or modify
the game process, and it uses no Riot artwork or fonts. The color palette is inspired by the League client.
