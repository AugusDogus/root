Tested on 2026-09-25: Root 2.1.5, Steam build 22238765, Unity 2022.3.62f2, Windows x64 IL2CPP, Proton Experimental (Wine 11.0).

BepInEx 6.0.0-be.788, commit 5b766a3b7f6c164d4798924a93f3acf4db769d06, successfully generated interop assemblies and loaded the included test plugin. The plugin read Unity and game versions, registered a MonoBehaviour, received Update callbacks, read the active scene, and requested application exit after 15 seconds of updates. Evidence was retained locally in `smoke-test.log`, which is excluded from this repository.

The active scene was still `Splash` at exit. Full menu navigation, matches, gameplay method patches, and multiplayer were not tested. BepInEx reported a Class::Init signature fallback warning but completed the test. The game also logged a Windows URL-protocol registration error in the isolated copy. This test establishes basic plugin compatibility only. MelonLoader was not tested.

The test used separate copies of the game and Proton prefix. No loader was installed into the Steam game directory. Temporary game, prefix, and SDK copies were removed after testing.

To build the plugin against an existing BepInEx IL2CPP installation after its first run:

```sh
dotnet build RootSmokeTest.csproj -p:GameDir=/absolute/path/to/test/game
```

Copy only `bin/Debug/net6.0/RootSmokeTest.dll` into that test game's `BepInEx/plugins` directory. This plugin automatically closes the game. Use a disposable test installation. Under Proton, the loader requires `WINEDLLOVERRIDES="winhttp=n,b"` in the launch environment.

Sources:

- [BepInEx build 788](https://builds.bepinex.dev/projects/bepinex_be/788)
- [IL2CPP installation guide](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)
- [Proton setup](https://docs.bepinex.dev/master/articles/advanced/steam_interop.html)
- [Root Steam page](https://store.steampowered.com/app/965580/Root/) and [publisher page](https://direwolfdigital.com/root/): no advertised mod API or Steam Workshop support found.
- [Unofficial Korean patch](https://github.com/dnjsalsl0708-ai/root-korean-patch): documents Root 2.1.5.2761+ and Unity 2022.3.62f2 support through asset and localization replacement. This community patch was inspected, not installed or validated.
- [Community discussion about text modding](https://steamcommunity.com/app/965580/discussions/0/802342156092132314/).
