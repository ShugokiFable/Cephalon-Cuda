# Cephalon Cuda 2.2 major UI architecture rework

This pass is a structural redesign, not another color reskin.

## Theme-system repair

The previous implementation depended on mutating shared WPF brushes. That approach can fail when a resource becomes frozen or when a control resolved a static resource before the mutation. The new engine replaces complete runtime resource objects, and every palette-dependent XAML reference now uses `DynamicResource`.

Theme selection updates the existing visual tree immediately. It covers the main shell, all workspaces, control templates, popups, dialogs, tables, overlays, tray icon, custom window chrome, and the embedded Advisor WebView. Selecting a preset clears the prior custom accent so the chosen palette is visible as designed.

## Major visual redesign

- custom high-end Windows chrome and caption controls
- theme-aware atmospheric backdrop
- command-rail navigation with active state rail and page context
- layered workspace framing and compact telemetry dock
- rebuilt typography hierarchy and spacing tokens
- redesigned cards, surfaces, pills, buttons, inputs, combo boxes, checkboxes, navigation, lists, tabs, tables, scrollbars, sliders, progress bars, and tooltips
- visual theme gallery with real palette previews
- upgraded Advisor conversation surface and composer
- density, scale, motion, and accent controls that apply live

## Regression protection

The Windows `--smoke` cycle now applies all eight palettes after all views have been instantiated and verifies that both the resource dictionary and an existing styled element changed to the expected accent. The offline validator rejects static color/effect resources, missing runtime tokens, missing theme-gallery wiring, and removal of the all-palette smoke gate.

## Build status

The source passes the packaged offline validator. The authoritative WPF compilation and runtime theme smoke test still run through `BUILD_V2.2.bat` on Windows with warnings treated as errors.
## R3.1 runtime correction

The Windows smoke run found that title-bar height, status-bar height, and navigation width were registered as raw doubles even though live WPF `DynamicResource` assignment requires `GridLength` for row/column geometry. All three tokens now use `GridLength`. Smoke-mode dispatcher exceptions now terminate the process with exit code 2, preventing a failed startup from being packaged.

