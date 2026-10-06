param(
    [string]$GameRoot = $env:STRANDED_DEEP_GAME_ROOT
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    throw "GameRoot was not supplied. Pass -GameRoot or set STRANDED_DEEP_GAME_ROOT."
}
$Managed = Join-Path $GameRoot "Stranded_Deep_Data\Managed"
$BepInExCore = Join-Path $GameRoot "BepInEx\core"
$Plugins = Join-Path $GameRoot "BepInEx\plugins"

$Compiler = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$Sources = @(
    (Join-Path $PSScriptRoot "StrandedDeepSplitMap.cs"),
    (Join-Path $PSScriptRoot "WreckNotes.cs"),
    (Join-Path $PSScriptRoot "ManualNotes.cs"),
    (Join-Path $PSScriptRoot "VisualDiscovery.cs"),
    (Join-Path $PSScriptRoot "ModSettingsIntegration.cs"),
    (Join-Path $PSScriptRoot "SDK\ModSettingsClient.cs")
)

$SourceAssets = Join-Path $PSScriptRoot "assets"
$SourceWreckAssets = Join-Path $SourceAssets "wrecks"
$SourceSheets = Join-Path $SourceAssets "source-sheets"

$PluginDir = Join-Path $Plugins "StrandedDeepSplitMap"
$PluginAssets = Join-Path $PluginDir "assets"
$PluginWreckAssets = Join-Path $PluginAssets "wrecks"
$PluginSourceSheets = Join-Path $PluginAssets "source-sheets"
$Output = Join-Path $PluginDir "StrandedDeepSplitMap.dll"

$WreckAssetNames = @(
    "ROWBOAT_3.png",
    "SHIPWRECK_2A.png",
    "SHIPWRECK_3A.png",
    "SHIPWRECK_4A.png",
    "SHIPWRECK_5A.png",
    "SHIPWRECK_6A.png",
    "SHIPWRECK_7A.png",
    "SHIPWRECK_8A.png",
    "SHIPWRECK_12A.png",
    "SHIPWRECK_13A.png",
    "PLANEWRECK_1A.png"
)

$SheetAssetNames = @(
    "map_icons_basic.png",
    "map_icons_animals.png",
    "map_icons_plants.png",
    "map_icons_terrain.png"
)

if (-not (Test-Path $Compiler)) {
    throw "C# compiler not found: $Compiler"
}

foreach ($Source in $Sources) {
    if (-not (Test-Path $Source)) {
        throw "Source file not found: $Source"
    }
}

New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
New-Item -ItemType Directory -Force -Path $PluginAssets | Out-Null
New-Item -ItemType Directory -Force -Path $PluginWreckAssets | Out-Null
New-Item -ItemType Directory -Force -Path $PluginSourceSheets | Out-Null

$References = New-Object System.Collections.Generic.List[string]

$BepInExDll = Join-Path $BepInExCore "BepInEx.dll"
$AssemblyCSharp = Join-Path $Managed "Assembly-CSharp.dll"
$BoltDll = Join-Path $Managed "bolt.dll"
$BoltUserDll = Join-Path $Managed "bolt.user.dll"
$RewiredCoreDll = Join-Path $Managed "Rewired_Core.dll"

foreach ($Required in @($BepInExDll, $AssemblyCSharp, $BoltDll, $BoltUserDll, $RewiredCoreDll)) {
    if (-not (Test-Path $Required)) {
        throw "Required assembly not found: $Required"
    }

    $References.Add($Required)
}

Get-ChildItem -Path $Managed -Filter "UnityEngine*.dll" |
    Sort-Object Name |
    ForEach-Object { $References.Add($_.FullName) }

$Args = New-Object System.Collections.Generic.List[string]
$Args.Add("/nologo")
$Args.Add("/target:library")
$Args.Add("/optimize+")
$Args.Add("/debug:pdbonly")
$Args.Add("/out:$Output")

foreach ($Reference in $References) {
    $Args.Add("/reference:$Reference")
}

foreach ($Source in $Sources) {
    $Args.Add($Source)
}

Write-Host "Compiler: $Compiler"
Write-Host "Sources:"
foreach ($Source in $Sources) {
    Write-Host "  $Source"
}
Write-Host "Output:   $Output"
Write-Host "References: $($References.Count)"
Write-Host ""

& $Compiler $Args.ToArray()

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $Output)) {
    throw "Build reported success but DLL was not created: $Output"
}

function Require-InstalledAsset {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        throw "Required installed runtime asset is missing: $Path"
    }
}

# v0.13.3 is code-only. Do NOT overwrite installed map art.
# Preserve the current high-resolution parchment and all existing icon PNGs.
Require-InstalledAsset (Join-Path $PluginAssets "parchment.png")
Require-InstalledAsset (Join-Path $PluginAssets "compass_rose.png")

foreach ($AssetName in $WreckAssetNames) {
    Require-InstalledAsset (Join-Path $PluginWreckAssets $AssetName)
}

foreach ($AssetName in $SheetAssetNames) {
    Require-InstalledAsset (Join-Path $PluginSourceSheets $AssetName)
}


$LegacyDll = Join-Path $Plugins "StrandedDeepSplitMap.dll"
if (Test-Path $LegacyDll) {
    Remove-Item -Force $LegacyDll
    Write-Host "Removed legacy DLL:"
    Write-Host $LegacyDll
}

Write-Host ""
Write-Host "Build OK:"
Write-Host $Output
Write-Host ""
Write-Host "v0.13.3 keyboard-parity field test:"
Write-Host "  1. M opens/closes P1 map; N remains P2 map for local split-screen."
Write-Host "  2. Arrow keys pan; =/Keypad+ zoom in; -/Keypad- zoom out."
Write-Host "  3. Home centers the owning player; Backspace resets to 1x + center."
Write-Host "  4. L opens Add Marker."
Write-Host "  5. T sets/clears Navigation Target at map center."
Write-Host "  6. H sets Home Island at map center."
Write-Host "  7. Enter opens manual/wreck context at map center; menu Enter still confirms."
Write-Host "  8. C is NOT handled by Split Map; vanilla Crafting remains free."
Write-Host "  9. F8 remains free for StrandedDeepDiagnostics."
Write-Host " 10. Mouse buttons/wheel are not captured by Split Map."
Write-Host " 11. Existing controller controls and split-screen behavior remain unchanged."
Write-Host " 12. Fog, markers, wrecks, navigation, Home and persistence remain unchanged."
