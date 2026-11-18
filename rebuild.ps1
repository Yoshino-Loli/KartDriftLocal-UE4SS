param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'build/client'),
    [switch] $Tests,
    [switch] $SkipNative,
    [switch] $FrameworkDependent)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$nativeOutput = Join-Path $PSScriptRoot 'build/native'
if (-not $SkipNative) { & (Join-Path $PSScriptRoot 'native/build.ps1') -OutputDirectory $nativeOutput -Tests:$Tests }
$runtime = Join-Path $output 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Copy-Item -LiteralPath (Join-Path $nativeOutput 'KdLocalNative.dll') -Destination $runtime -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$bundle = Join-Path $output 'LocalData.zip'
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle }
$resourceRoot = Join-Path $PSScriptRoot 'resources'
$archive = [System.IO.Compression.ZipFile]::Open($bundle, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $resourceRoot -Recurse -File | ForEach-Object {
        $entryName = $_.FullName.Substring($resourceRoot.Length + 1).Replace('\', '/')
        if ($entryName -eq 'release/manifest.json') {
            $manifest = [IO.File]::ReadAllText($_.FullName) | ConvertFrom-Json
            $manifest | Add-Member -NotePropertyName nativeSha256 -NotePropertyValue ((Get-FileHash (Join-Path $runtime 'KdLocalNative.dll') -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
            $manifest.mode = 'local single-player native compatibility prerelease'
            $manifest.acceptance = 'Native contracts and bounded local login are separate from race acceptance; no Frida runtime'
            $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write(($manifest | ConvertTo-Json -Depth 8 -Compress)) } finally { $writer.Dispose() }
        } else {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
} finally { $archive.Dispose() }

# NativeAOT needs link.exe from MSVC plus the Windows SDK headers and libraries.
# vcvars is not used so the build also works when Visual Studio lives off the
# default drive; KARTDRIFT_VC_INSTALL / KARTDRIFT_WINDOWS_SDK_ROOT override it.
if (-not $FrameworkDependent) {
    $vs = $env:KARTDRIFT_VC_INSTALL
    if (-not $vs) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    }
    if (-not $vs) { throw 'NativeAOT needs Visual C++ x64 build tools. Set KARTDRIFT_VC_INSTALL or pass -FrameworkDependent.' }
    $vc = (Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
    $sdkRoot = $env:KARTDRIFT_WINDOWS_SDK_ROOT
    if (-not $sdkRoot) { $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10' }
    if (-not (Test-Path -LiteralPath (Join-Path $sdkRoot 'Include'))) { throw ('Windows SDK not found: ' + $sdkRoot + '. Set KARTDRIFT_WINDOWS_SDK_ROOT.') }
    $sdk = (Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
    $env:PATH = (@((Join-Path $vc 'bin/Hostx64/x64'), (Join-Path $sdkRoot ('bin/' + $sdk + '/x64'))) + $env:PATH) -join ';'
    $env:INCLUDE = (@((Join-Path $vc 'include')) + @('ucrt','shared','um','winrt') | ForEach-Object { if ($_ -match '^(ucrt|shared|um|winrt)$') { Join-Path $sdkRoot ('Include/' + $sdk + '/' + $_) } else { $_ } }) -join ';'
    $env:LIB = (@((Join-Path $vc 'lib/x64')) + @((Join-Path $sdkRoot ('Lib/' + $sdk + '/ucrt/x64')), (Join-Path $sdkRoot ('Lib/' + $sdk + '/um/x64')))) -join ';'
}

$publish = @('-c','Release','-r','win-x64','--nologo','-o',$output,('-p:LocalDataBundle=' + $bundle))
if ($FrameworkDependent) { $publish += @('-p:PublishSingleFile=true','-p:SelfContained=false') } else { $publish += '-p:PublishAot=true' }
& dotnet publish (Join-Path $PSScriptRoot 'launcher-csharp/KartDriftLocal.csproj') @publish
if ($LASTEXITCODE -ne 0) { throw 'C# publish failed' }
if (-not (Test-Path -LiteralPath (Join-Path $output 'KartDriftLocal.exe'))) { throw 'KartDriftLocal.exe was not produced' }
if ($output.TrimEnd('\') -ne $PSScriptRoot.TrimEnd('\')) {
    foreach ($name in @('Localization','TrialTower')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $output -Recurse -Force }
}
$licenses = Join-Path $output 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native/vendor/minhook/LICENSE.txt') -Destination (Join-Path $licenses 'MinHook-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native/vendor/nlohmann/LICENSE.MIT') -Destination (Join-Path $licenses 'nlohmann-json-LICENSE.MIT') -Force
if ($output.TrimEnd('\') -ne $PSScriptRoot.TrimEnd('\')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $output -Force }
