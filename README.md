# Stranded Deep Split Map

An in-game exploration and navigation map for Stranded Deep, designed with local split-screen play in mind.

Current version: **0.13.1**

## Features

- Separate map view for both local split-screen players.
- Exploration-based map discovery.
- Fog-of-war style explored-area tracking.
- Per-player zoom and navigation state.
- Home Island marking.
- Navigation targets.
- Player direction indicators.
- Manual map markers.
- Marker categories for resources, food, plants, animals, equipment, terrain and other points of interest.
- Cross-out, restore and delete actions for manual markers.
- Wreck markers with dedicated wreck icons.
- Explored and Important states for wreck markers.
- Persistent map data stored separately from the native Stranded Deep save.

## Local split-screen

Split Map is specifically designed for two players sharing one computer.

Each player has their own map view and navigation state, while world exploration data and map annotations are shared where appropriate.

## Persistence

The mod does not modify the native Stranded Deep save file.

Map data is stored separately under:

`BepInEx\config\StrandedDeepMap\`

Persistent data includes exploration state, shoreline exploration cache, Home Island, navigation targets, north calibration, wreck markers and manual markers.

Because this data represents your explored map and annotations, back it up before updating or removing the mod.

## Optional integration

Stranded Deep Mod Settings provides an in-game settings page for Split Map.

The settings host is optional. Split Map continues to function without it.

## Installation

1. Install BepInEx for Stranded Deep.
2. Download `StrandedDeepSplitMap-0.13.1.zip` from the GitHub Release.
3. Extract it into the Stranded Deep game directory.
4. Allow the included `BepInEx` folder to merge with the existing `BepInEx` folder.
5. Fully restart Stranded Deep.

Requires BepInEx.
