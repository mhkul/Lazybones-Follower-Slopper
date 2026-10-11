[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output,[Parameter(Mandatory)][string]$Python,[Parameter(Mandatory)][string]$RuntimeArchive,[string]$Dotnet='dotnet',[switch]$Offline)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot '../global.json')){$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))}
$Output=[IO.Path]::GetFullPath($Output)
function RequireNoLinks([string]$Path){for($current=[IO.Path]::GetFullPath($Path);$current;$current=[IO.Path]::GetDirectoryName($current)){if((Test-Path -LiteralPath $current)-and((Get-Item -LiteralPath $current -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw "Linked build path: $current"}}}
RequireNoLinks $Output
if(Test-Path -LiteralPath $Output){throw 'Choose a fresh owned build directory.'}
if($projectRoot.StartsWith($Output+'\',[StringComparison]::OrdinalIgnoreCase)-or $projectRoot-eq$Output){throw 'Output aliases source.'}
New-Item -ItemType Directory -Path $Output | Out-Null
$state=Join-Path $Output 'build-state'
$env:DOTNET_CLI_HOME=Join-Path $state 'dotnet-home'
$env:NUGET_SCRATCH=Join-Path $state 'nuget-scratch'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0'
$env:DOTNET_NOLOGO='1'
if(!$env:NUGET_PACKAGES){$env:NUGET_PACKAGES=Join-Path $env:USERPROFILE '.nuget/packages'}
New-Item -ItemType Directory -Path $state -Force | Out-Null
$config=Join-Path $state 'nuget.config'
if($Offline){'<configuration><packageSources><clear /></packageSources></configuration>'|Set-Content $config}else{'<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'|Set-Content $config}
$extra=@("-p:RestoreConfigFile=$config",'-p:NuGetAudit=false',"-p:PathMap=$projectRoot=/_/LazybonesFollowerSlopper",'-p:UseSharedCompilation=false','-p:RuntimeFrameworkVersion=8.0.28','-p:DebugType=embedded','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:PublishTrimmed=false')
$writer=Join-Path $state 'writer'
$writerProject=Join-Path $projectRoot 'prototypes/T08/PluginWriter/PluginWriter.csproj'
if(Test-Path -LiteralPath (Join-Path $projectRoot 'Writer/PluginWriter.csproj')){$writerProject=Join-Path $projectRoot 'Writer/PluginWriter.csproj'}
& $Dotnet publish $writerProject -c Release -r win-x64 --self-contained true -o $writer -m:1 @extra
if($LASTEXITCODE-ne 0){throw 'Self-contained writer publish failed.'}
$payload=Join-Path $state 'payload'
& $Python -B (Join-Path $projectRoot 'tools/package_stages.py') $payload
if($LASTEXITCODE-ne 0){throw 'Stage packaging failed.'}
$zip=Join-Path $state 'runtime.zip'
& $Python -B (Join-Path $projectRoot 'tools/pack_runtime.py') $payload $RuntimeArchive $writer $zip
if($LASTEXITCODE-ne 0){throw 'Private runtime packaging failed.'}
& $Dotnet publish (Join-Path $PSScriptRoot 'AutoFollower.Gui.csproj') -c Release -r win-x64 --self-contained true -o $Output -m:1 "-p:RuntimePayload=$zip" @extra
if($LASTEXITCODE-ne 0){throw 'Self-contained GUI publish failed.'}
$readme=Join-Path $projectRoot 'docs/RELEASE_README.txt'
if(!(Test-Path -LiteralPath $readme)){$readme=Join-Path $projectRoot 'README.txt'}
Copy-Item $readme (Join-Path $Output 'README.txt')
