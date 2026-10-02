$ErrorActionPreference = "Stop"

$GameRoot = "F:\SteamLibrary\steamapps\common\Stranded Deep"
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
    (Join-Path $PSScriptRoot "ModSettingsClient.cs")
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

# v0.13b is code-only. Do NOT overwrite installed map art.
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
Write-Host "v0.13b clean-production field test:"
Write-Host "  1. Settings > MODS contains exactly one КАРТА section."
Write-Host "  2. Navigation arrow toggle hides only the closed-map navigation HUD."
Write-Host "  3. Own-arrow size changes only the owner arrow on each player's own map."
Write-Host "  4. Manual/wreck visibility toggles never delete stored markers."
Write-Host "  5. Marker scale preserves existing zoom-proportional growth."
Write-Host "  6. Labels work as Off / 64x / 32x."
Write-Host "  7. Crossed-out notes work as Show / Dim / Hide."
Write-Host "  8. Partner toggle affects only the other player's map arrow."
Write-Host "  9. Center-on-open OFF preserves view; A/Cross tap still centers."
Write-Host " 10. Fog OFF visually reveals ordinary islands only; Discovery/Exploration remain untouched."
Write-Host " 11. Turn fog back ON and the real partially explored map must return."
Write-Host " 12. MISSION/CARRIER stay hidden even with fog OFF."
Write-Host " 13. North calibration: P1 faces exact N with compass, then Settings > MODS > КАРТА > КАЛИБРОВАТЬ."

Write-Host " 14. F8 is no longer handled by Split Map; it is free for StrandedDeepDiagnostics."
