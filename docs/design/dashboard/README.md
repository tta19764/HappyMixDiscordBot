# HappyMix dashboard design (v2)

Annotated mockups, components and design tokens for the Discord-authenticated web dashboard:
server picker, availability (Today and Weekly timetable) and server settings, each on desktop and mobile,
plus a board of empty, error and saved states.

The live, viewable version is a private design canvas on claude.ai (open it from the owner's account, or ask
for it to be shared): <https://claude.ai/artifact/Up6dUP7qYRr8n4K11Y7ftf>. The files here are its source,
exported on 2026-11-14 so the design lives with the code.

## What is in `canvas/`

| File | Contents |
| --- | --- |
| `canvas.json` | Canvas layout: artboard positions and sizes, row titles and the annotation notes (numbered to match the pins on the desktop mockups). |
| `Main.dc.html` | Foundations: icon, color tokens with contrast ratios, type scale, spacing/radius/touch targets, component gallery, Razor partial mapping and the range-preview script. |
| `ServerPicker-*.dc.html` | Server picker, desktop and mobile. |
| `Today-*.dc.html` | Availability, Today view. Interactive in the canvas: changing times or presets updates the overnight preview. |
| `Weekly-*.dc.html` | Availability, Weekly timetable, with one day open for editing. |
| `Settings-*.dc.html` | Server settings: desktop shows a failed save with field errors, mobile shows the saved state. |
| `States.dc.html` | Empty, error and saved states for every screen. |
| `happymix.css` | The stylesheet the mockups use (tokens and `hm-*` components), including the canvas-only annotation pins. |
| `happymix.svg` | The app icon. |

The `.dc.html` files are written for the design canvas runtime (`support.js`, `<x-dc>`, `{{…}}` bindings), so they
don't render fully when opened directly in a browser. Use the canvas link to view them.

## Relationship to the code

- `src/HappyMix.Bot/wwwroot/css/dashboard.css` is the production version of `canvas/happymix.css`. It drops the
  annotation pins and adds the responsive and progressive-enhancement rules found during implementation.
  **Treat the production stylesheet as the source of truth for tokens.**
- `src/HappyMix.Bot/wwwroot/happymix.svg` is the same icon as `canvas/happymix.svg`.
- The Razor partials named on the Foundations board are in `src/HappyMix.Bot/Views/Shared`
  (`_ServerNav`, `_Banner`, `_TimeRange`, `_GameSettings`), and `wwwroot/js/dashboard.js` is the only script.
- Small differences from the mockups: the "Reset" links on settings were left out (choosing Inherit or the
  "Server default" option does the same), and the weekly "Saved" marker sits under the day name.
