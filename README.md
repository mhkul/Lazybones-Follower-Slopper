# Lazybones Follower Slopper

![Lazybones Follower Slopper](docs/assets/title.png)

Windows x64 application that creates a follower install ZIP from a Skyrim save
and matching finished RaceMenu head export. Review follower settings, race,
equipment and individual asset packaging choices in the GUI. Select Prebuilt,
OBody or both applicable outputs; both produce a FOMOD installer.

## Install and use

Download the [Windows x64 application packet](https://github.com/mhkul/Lazybones-Follower-Slopper/releases/download/v0.1.0/Lazybones-Follower-Slopper-0.1.0-windows-x64.zip)
from [release v0.1.0](https://github.com/mhkul/Lazybones-Follower-Slopper/releases/tag/v0.1.0). Extract outside game, mod and input folders. Run the EXE
directly for physical/deployed Data, or register it in MO2 with empty Arguments
and run through the intended existing profile. End-user Python and .NET are bundled.
This source repository does not contain the EXE or dependency binaries.

Choose the save and head NIF, then Continue. Resolve matching co-save, tint DDS
and JSlot when requested. Review settings and dependencies, choose outputs and
the Save ZIP to destination, then Build. Install the resulting follower ZIP
through your mod manager and follow its dependency instructions.
See the [installation, operation and rebuild guide](src/AGENT_GUIDE.md).

## Build from source

The complete build layout is under `src/`: C# GUI/Foundation/Writer, Python
Stages, packagers, pinned dependency metadata and branding resources.
See [BUILD.txt](src/BUILD.txt) for the archive hash and full requirements:
Windows x64, PowerShell, .NET SDK 8.0.422, developer Python 3.12+, NuGet access
or a populated developer cache, and the official Python 3.13.9 amd64 embeddable ZIP.

From repository `src/`, adapt these paths and choose a fresh output folder:

```powershell
./Gui/Build-Gui.ps1 -Python 'D:/Tools/Python/python.exe' -RuntimeArchive 'D:/Downloads/python-3.13.9-embed-amd64.zip' -Output 'D:/FreshBuild/LazybonesApplication'
```

`-Offline` requires the pinned NuGet packages and Windows x64/.NET 8.0.28 runtime
packs already cached. Build inputs require no game files or historical project
artifacts. The source guide and README.txt retain application-packet layout
descriptions; in this repository they live inside `src/`. Build output includes
private intermediates; distribute the application with its notices and source.
There is no public release-packaging or unattended follower-generation command.

## Current limits

User manual testing passed on Skyrim SE 1.5.97/MO2. AE, VR, other stores/managers
and actual Vortex setups remain unverified. The application is unsigned; versions
are informational and source/format checks still apply. Body overlays are
deferred with warnings. Recoverable missing references may retain items with
named warnings; appearance, audio or behavior can differ. Read Build details.

Non-OBody meshes remain as-is. Prebuilt XML/BodySlide work applies only to
OBody-preset-affected meshes; OBody uses captured winning installed meshes and
compatible morph files. Runtime SMP, retained assets/races and compatibility
rules require the dependencies named in the follower ZIP. The builder installs
no runtime facilities and edits no global OBody assignments.

## Licensing and redistribution

Application code is GPL-3.0-only, without warranty: [LICENSE](LICENSE).
The complete [license and notice appendix](src/LICENSES.txt) preserves application,
third-party and asset-responsibility notices and full license texts. Historical
notice filenames within that appendix identify its labeled sections, rather
than missing loose files. Bundled components and assets retain their own terms.
Creating a follower grants no right to redistribute its source assets.

When redistributing application binaries, supply matching application source
and access to `Lazybones-Follower-Slopper-0.1.0-thirdparty-source.zip`, the separate
dependency-source release asset containing full notices and exact binary/revision
correspondence indexes. This repository does not replace that companion.
Published upstream binaries were not independently rebuilt. Download matching
[application source](https://github.com/mhkul/Lazybones-Follower-Slopper/releases/download/v0.1.0/Lazybones-Follower-Slopper-0.1.0-source.zip),
[dependency source](https://github.com/mhkul/Lazybones-Follower-Slopper/releases/download/v0.1.0/Lazybones-Follower-Slopper-0.1.0-thirdparty-source.zip)
and [checksums](https://github.com/mhkul/Lazybones-Follower-Slopper/releases/download/v0.1.0/SHA256SUMS.txt)
from the same release.
