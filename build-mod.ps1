param(
    [string] $ModsDirectory = (Join-Path $PSScriptRoot '..\ue4ss\Mods'),
    [switch] $SkipNative)
# Builds the fully automatic UE4SS package:
#   <Mods>\KartDriftLocal\dlls\main.dll                hooks + autostart
#   <Mods>\KartDriftLocal\dlls\KartDriftLocal.Core.dll gateway (NativeAOT shared library)
#   <Mods>\KartDriftLocal\Localization, TrialTower     game data
#   <Mods>\KartDriftLocal\enabled.txt
$ErrorActionPreference = 'Stop'
$mods = [IO.Path]::GetFullPath($ModsDirectory)
$mod = Join-Path $mods 'KartDriftLocal'

$vs = $env:KARTDRIFT_VC_INSTALL
if (-not $vs) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $vs) { throw 'Visual C++ x64 build tools are required.' }
$vc = (Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkRoot = $env:KARTDRIFT_WINDOWS_SDK_ROOT
if (-not $sdkRoot) { $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10' }
if (-not (Test-Path -LiteralPath (Join-Path $sdkRoot 'Include'))) { throw ('Windows SDK not found: ' + $sdkRoot + '. Set KARTDRIFT_WINDOWS_SDK_ROOT.') }
$sdk = (Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$env:PATH = (@((Join-Path $vc 'bin/Hostx64/x64'), (Join-Path $sdkRoot ('bin/' + $sdk + '/x64'))) + $env:PATH) -join ';'
$env:INCLUDE = (@((Join-Path $vc 'include')) + @('ucrt','shared','um','winrt') | ForEach-Object { if ($_ -match '^(ucrt|shared|um|winrt)$') { Join-Path $sdkRoot ('Include/' + $sdk + '/' + $_) } else { $_ } }) -join ';'
$env:LIB = (@((Join-Path $vc 'lib/x64')) + @((Join-Path $sdkRoot ('Lib/' + $sdk + '/ucrt/x64')), (Join-Path $sdkRoot ('Lib/' + $sdk + '/um/x64')))) -join ';'

if (-not $SkipNative) { & (Join-Path $PSScriptRoot 'native/build.ps1') -OutputDirectory (Join-Path $PSScriptRoot 'build/native') -ModDirectory $mods }

$bundle = Join-Path $PSScriptRoot 'build/client/LocalData.zip'
if (-not (Test-Path -LiteralPath $bundle)) { throw 'LocalData.zip is missing; run rebuild.ps1 once first.' }
& dotnet publish (Join-Path $PSScriptRoot 'launcher-csharp/KartDriftLocal.Core.csproj') -c Release -r win-x64 --nologo `
    -p:NativeLib=Shared -p:PublishAot=true ('-p:LocalDataBundle=' + $bundle) -o (Join-Path $mod 'dlls')
if ($LASTEXITCODE -ne 0) { throw 'core publish failed' }
foreach ($name in @('Localization','TrialTower')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $mod -Recurse -Force }
$settings = Join-Path $mod 'settings.json'
if (-not (Test-Path -LiteralPath $settings)) { [IO.File]::WriteAllText($settings, '{"gameLanguage":"zh-Hans","language":"zh"}', [Text.UTF8Encoding]::new($false)) }
Write-Host ('UE4SS mod ready: ' + $mod)
