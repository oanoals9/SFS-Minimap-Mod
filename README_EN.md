# Mini Map

A minimap mod for Spaceflight Simulator (PC, 1.6.x): a rectangular window in flight that shows the
planet, the current trajectory, apoapsis/periapsis and the vehicle, with the **map zoom following the
height above the ground** (the window itself never changes size).

> The current build is **v0.3.0**, the first proper release: a window in flight showing the planet,
> the current orbit, the apoapsis/periapsis, the vehicle and the other vehicles, and the other
> bodies - with the view centred on the orbit and the zoom fitting it. See
> [TESTING.md](TESTING.md) for the test plan and the known limits, and the end of this file for the
> version history.

---

## Installing

Requires **[UITools](https://github.com/cucumber-sp/UITools)** (1.1.6 or newer) to be installed first.

1. Open the `Mods\` folder inside the game directory.
2. Drop `Mini Map.dll` into `Mods\` itself, or put it at `Mods\Mini Map\Mini Map.dll`.
3. Start the game. The loader only reads `Mods\<folder>\<folder>.dll`, so a loose DLL in `Mods\` is
   copied into place on startup.

Close the game before replacing the file: while it is running the DLL is memory mapped and cannot
be overwritten.

## Using it

- Enter any flight and a window titled `Mini Map` appears. Drag the title bar to move it; the
  position is remembered.
- The line above the map is the **apoapsis** altitude, the line below it the **periapsis** altitude.
  A line is **taken away** when there is nothing to put in it: with no trajectory yet (sitting on the
  launch pad, say), or when the **periapsis is below the ground** (a negative altitude, i.e. an orbit
  that would go through the planet). The window becomes shorter, and the line comes back as soon as
  the orbit clears the ground again.
- The two text lines (apoapsis and periapsis) size themselves to the window title: they ask for 24px
  but never go above the size the title (`Mini Map`) actually uses. That size comes from the game's
  own window prefab, so it is measured at run time rather than assumed.
- The window is always exactly as tall as its contents, so it never leaves an empty strip. Make the
  map itself bigger with `Map resolution` (in `settings.txt`, 320 by default).
- The settings page (**Settings → Mods Settings → Mini Map**) has two things only: **toggling the
  window with a hotkey** (plus setting that hotkey) and **Debug output** (off by default). The window
  is simply always there until the hotkey is enabled.
  Every other option (window size and opacity, map resolution, zoom, lines, what is drawn) is still
  in `Mods\Mini Map\settings.txt` and still applies - it is simply not in the GUI any more. Close the
  game before editing that file.

## What is on the map

| | |
| --- | --- |
| Planet | the game's own map sprite and colour (`PlanetData.basics.mapColor`), including the **other planets of the same system** |
| Atmosphere | radius = planet radius + atmosphere height, the game's `(1,1,1,0.1)` |
| Sphere of influence | radius = the planet's SOI, the game's `SOI_Sprite` and `(1,1,1,0.08)` |
| Trajectory / orbit lines | copied out of the game's own map line pools (`Map.solidLine` / `Map.dashedLine`), so they always match the game |
| Apoapsis / periapsis points | two white dots on the orbit |
| Vehicle | the game's own map icon, turned to the ship's **attitude** (`GetRotation()`, exactly like the game's own map icon) |
| Other vehicles | the triangles of the other vehicles (including debris) at the same planet, also turned to their attitude |

The other bodies are placed and culled by their offset from the current planet: one that is entirely
off screen is not drawn at all, so it costs nothing. **`Other vehicles`** and **`Other bodies`**
(both on by default) turn each group off separately.

There is a **`Sphere shading`** option (off by default): off gives the flat disc in the planet's own
colour that the game's map uses, on shades that same colour like a ball lit from the upper left,
which is closer to the reference picture.

## Zoom and centre

**Below orbit** (periapsis inside the atmosphere)

- Centre: the vehicle
- Width shown ≈ the straight line from the launch point to the impact point, plus 20%

**In orbit** (a closed orbit whose periapsis is above the ground)

- Centre: **the middle of the apoapsis and periapsis points**, which for an ellipse is the centre of
  the ellipse - so the whole orbit sits around the middle of the window instead of the planet
- Half height = `Zoom × semi-major axis`, where the semi-major axis is **half the distance between
  the two apsis points**. Both apsis points therefore always stay inside the window (no point of an
  ellipse is further from its centre than that, and the vehicle is on the same ellipse)
- For a circular orbit this is exactly what the old rule gave (semi-major axis ≈ planet radius +
  height); the flatter the orbit, the further the view pulls back, until both points fit

> The default `Zoom` of 1.30 leaves 30% of margin around "just fits". 1.0 puts the points exactly on
> the edge; below 1.0 pushes them out of the window. **A nearly parabolic orbit pulls the view very
> far out**, because fitting both points means fitting the whole ellipse.

The two centres are blended with a smooth interpolation (0.6 s by default), so the change of view
slides instead of jumping. The `1.30`, the transition time, the margin, the centre mode and the
minimum/maximum ranges are all adjustable.

## Relationship to other mods

- **Aero Trajectory**: compatible, and its lines show up here. That mod draws its aerodynamic
  trajectory into the game's own map line pools; this one only *reads* those pools in order to copy
  the lines, and never writes back, never redraws and never triggers an extra map update — so it
  neither disturbs it nor multiplies its per frame cost. Install it and its trajectory appears in
  the minimap; uninstall it and the line disappears with it.
- **The game's own map**: the minimap hides itself while the map (M) is open. Nothing about the
  game's map state is modified.
- **UITools**: required, used for the window and the settings pages.

## How it works

The game's map view lives under `MapManager.mapSystemHolder`, which the game switches off while
flying, so it cannot be used as a minimap directly. This mod instead:

1. Creates **its own orthographic camera**, rendering into a RenderTexture that is shown on an
   ordinary RawImage inside the window. The camera only renders a Unity layer this mod owns, so the
   game cannot see it and it cannot see the game.
2. Draws the planet, atmosphere, sphere of influence and the markers itself, with the game's own
   sprites and colours, in the same coordinate system as the game's map (1 unit = 1 km).
3. **Copies** the lines the game has already computed and drawn this frame
   (`LineDrawer.pool`) and redraws them in its own coordinates — which is why the aerodynamic
   trajectory of Aero Trajectory shows up as well.

## Licence

**GNU General Public License v3.0** (GPL-3.0) - the full text is in [LICENSE](LICENSE).

```
Mini Map - a minimap window for Spaceflight Simulator
Copyright (C) 2026 oanoals9

This program is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
General Public License for more details.

You should have received a copy of the GNU General Public License along with this program.
If not, see <https://www.gnu.org/licenses/>.
```

> Because the licence is the GPL, **the source goes out with the binary**: besides `-source.zip`,
> the complete source ships with the repository, and it rebuilds a byte-for-byte identical
> `Mini Map.dll` with the build script that is included (no .NET SDK needed).
> **What is needed to build it, and which assemblies it references: see
> [BUILDING.md](BUILDING.md) in the source package.**

## Version history

| | |
| --- | --- |
| **0.3.0** | First proper release: triangles of the other vehicles, discs/atmospheres/spheres of influence of the other bodies, the map centring on the **centre of the ellipse** and **zooming to fit both apsis points**, the apsis rows hiding themselves when there is nothing to show, text sizing itself against the window title, the window fitting its contents, a two-item settings page, and a silent console unless debug output is on |
| 0.2.0 | The window grew tall enough for the map; the map rights itself while landing |
| 0.1.0 | First version: private camera into a RenderTexture, shown in a window; planet disc, atmosphere, sphere of influence, the current orbit, the apsis points and their numbers, the vehicle marker |

## Author

oanoals9
