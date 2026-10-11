# Lazybones Follower Slopper 0.1.0: agent guide

The Windows x64 application builds a follower ZIP from a selected Skyrim save
and matching finished RaceMenu export. Operate its WinForms GUI. There is no
supported unattended follower-build API. Internal evidence switches, startup
observers and historical runners are diagnostics, not public automation
interfaces. An agent without suitable GUI control should explain that limit
and let the user operate the app; launch success does not prove build completion.

## Install and select the environment

Extract to a user-owned application folder outside Skyrim, mod and input
trees. Keep the EXE, README, guide and `src/` together. End users install no
Python or .NET: both runtimes are bundled with automatic private extraction.
The application is unsigned.

For MO2, register `Lazybones Follower Slopper.exe` as an ordinary executable,
leave Arguments empty, and Run through the intended existing profile. An
open MO2 alone does not select its merged Data environment. Direct launch
uses physical/deployed Data and effective INIs/plugin order; choose the Skyrim
folder if discovery needs help. Check the environment displayed by the app.
Startup, input and build checks validate selected source identity/winners.
Save discovery uses the effective save location; RaceMenu discovery uses
`Data/SKSE/Plugins/CharGen` in the selected environment, rather than assuming
Overwrite. Choose files explicitly when needed. Do not change profiles,
activate/install mods or launch Skyrim to continue without user authorization.

User manual testing passed on the current Skyrim SE 1.5.97/MO2 setup.
Accepted appearance/SMP behavior remains the baseline.
Other game/store/manager combinations, AE, VR and actual Vortex
setups are unverified. Versions are informational; required source/format
checks still apply and do not establish universal compatibility.

## Operate the GUI

1. Choose the save and finished RaceMenu head NIF. Continue resolves/checks
   the co-save, tint DDS and JSlot. Use companion recovery when requested;
   `Change RaceMenu preset…` replaces an incorrect automatic JSlot match.
   A shared filename does not prove association. Obtain the user's decision
   for unresolved character/export choices; never rename inputs or guess
   matches to bypass validation. `Cancel checking` stops preparation.
2. Review Basic/Advanced settings: identity, voice, stats/level behavior,
   curated spell/perk packages and optional marriage. Marriage starts unchecked;
   blank Maximum imposes no user cap. Hover or F1 provides help. Advanced Race
   defaults to a new private custom race. Existing captured race retains its
   provider's behavior/dependency and packages no skeleton. Respect the shown
   dependency/compatibility consequences.
3. Advanced Equipment / `Choose items…` includes armor and weapons by default.
   Omissions apply to every chosen output, preserve shared inputs and persist
   for the current save/environment; changing character/setup resets them.
   Review individual `Package standalone` / `Retain dependency` asset choices.
   Retained assets require their actual providers. Standalone selection does
   not prove complete closure when unresolved-reference warnings remain.
4. Select applicable Prebuilt, OBody or both and acknowledge asset-sharing
   responsibility before Build. One output makes a plain ZIP; both use FOMOD.
   Non-OBody capture uses as-is meshes and manufactures no OBody route.
   Prebuilt uses authored XML and compatible BodySlide projects only for
   actual OBody-preset-affected meshes. Unaffected meshes/base hands/feet stay
   as-is. OBody uses captured winning installed meshes with compatible morph
   files and private reference edits. If the preset source or compatible
   project is ambiguous, obtain the user's choice instead of inventing it.
5. `Save ZIP to` / Change chooses delivery (default: Documents / Lazybones
   Follower Slopper / ZIPs). Only the finished ZIP is delivered there; earlier
   ZIPs remain. Build revalidates the environment. Use `Cancel build` and wait.
   Read precise input/operation errors and correct the identified problem.
   If optional-item omission is offered, obtain the user's choice instead of
   silently excluding requested content or inventing data.
6. Completion shows the actual ZIP path and warnings. `Show ZIP in folder`
   selects it in Explorer; `Edit follower settings`, `Choose another character`
   and `Exit` support the next action. `Retry saving ZIP…` recovers failed
   delivery without rebuilding. Confirm the ZIP exists before reporting success.
   Read `Build details` for named warnings and local receipts.

Recoverable missing textures, sounds and other references generally retain
requested items/proved assets with original safe unresolved calls and named
warnings. Appearance, audio or behavior may differ; do not claim exact or fully
self-contained reproduction. Corrupt/unreadable structures, unsafe writes,
invalid mandatory links and indispensable generation inputs stop the concrete
operation. Body overlays remain deferred. The identified mouth conflict uses
the finished export's material with a warning; unrelated mismatches remain
checked. Cached RaceMenu/Heels Fix lift is discarded while original footwear
adjustment metadata remains for the runtime.

## Install the follower and troubleshoot

Read the generated ZIP's instructions/dependencies, then install through the
user's mod manager only when authorized. A FOMOD chooses one variant. Use MO2
Replace when switching variants to avoid stale files. Required game/DLC,
retained masters and physics hosts depend on the capture/choices. SMP needs
its compatible installed runtime. Follower-scoped RSV rules need compatible
RSV/SPID facilities when applicable; OBody assignment/exclusion needs compatible
OBody/PDA at runtime. The builder emits configuration, installs none of those
facilities and edits no global JSON. Old saved OBody assignment/exclusion
state may need installed tools' supported follower-specific controls when
switching; ask before changing it. No custom DLL/PEX adapter is packaged.

Attempts/logs remain in LocalApplicationData / Lazybones Follower Slopper /
Builds. Private runtime extraction uses a hash-named temporary cache. Diagnose
with the named error and Build details. Do not delete broad caches, change
source projects/zaps, substitute captured geometry or edit global settings as
speculative repairs. Omission history is local builder evidence, not installer
README content. Building a package grants no source-asset redistribution rights.

## Rebuild from delivered source

`src/AGENT_GUIDE.md` is the source-packet guide master; the copy beside the EXE
is identical. `src/BUILD.txt` maintains the full command. Work inside delivered
`src/`, with Windows x64, PowerShell, pinned .NET SDK **8.0.422**, developer
Python **3.12+**, and the official Python **3.13.9** amd64 embeddable archive:
https://www.python.org/ftp/python/3.13.9/python-3.13.9-embed-amd64.zip

Its SHA-256 must be
`91D828C2DA3A029B41699E918674A0CB379C02CF20DAB9C501306885F837402A`.
The script verifies it and bundles .NET **8.0.28** plus selected Python runtime
components. Adapt these example paths to your developer setup:

```powershell
./Gui/Build-Gui.ps1 -Python 'D:/Tools/Python/python.exe' -RuntimeArchive 'D:/Downloads/python-3.13.9-embed-amd64.zip' -Output 'D:/FreshBuild/LazybonesApplication'
```

Output must be fresh and must not alias source. `-Dotnet` can select the SDK
executable; `global.json` requires the pinned version. Online restore uses
NuGet. `-Offline` needs a populated cache with pinned packages and Windows
x64/.NET 8.0.28 runtime packs. Set `NUGET_PACKAGES` to your own isolated cache
if desired; default is the developer user's `.nuget/packages`. End users
need none of these developer prerequisites. No game files, private reports,
tests or fixtures are build inputs.

Expected output includes `Lazybones Follower Slopper.exe`, README and private
`build-state` intermediates: writer, stage payload and runtime ZIP. The EXE
embeds that payload. Build-state, bin/obj and caches are developer artifacts,
not public source. Editable code is in Gui/Foundation/Writer, Python stages
in Stages, packagers in tools, and portrait/ICO in `assets/branding/icon-v2`.

Keep `src/LICENSES.txt` and matching dependency-source access when redistributing.
`Lazybones-Follower-Slopper-0.1.0-thirdparty-source.zip` contains dependency
sources, full notices and binary/revision correspondence indexes. Published
upstream binaries were not independently rebuilt; their own prerequisites
apply. Mutagen upstream PreBuild deletes cache folders: use an isolated
upstream profile/cache. This guide adds no public release-packaging command
or unattended follower-build interface.
