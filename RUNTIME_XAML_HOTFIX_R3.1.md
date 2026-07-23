# Cephalon Cuda 2.2 · Runtime XAML Hotfix R3.1

## Defect reproduced

The R3 build compiled and published successfully, then failed while loading `MainWindow.xaml` with:

`Set property 'System.Windows.Controls.RowDefinition.Height' threw an exception.`

## Root cause

The runtime theme rewrite correctly changed theme-dependent XAML references to `DynamicResource`, but three geometry resources were still registered as `double`:

- `Metric.TitleBarHeight`
- `Metric.StatusBarHeight`
- `Metric.NavWidth`

`RowDefinition.Height` and `ColumnDefinition.Width` require actual `GridLength` objects when supplied by a dynamic resource. WPF cannot dynamically assign a raw `double` to those properties.

## Corrections

- Registered all three geometry tokens as `GridLength`.
- Added runtime smoke assertions for the geometry resource types across every theme.
- Changed smoke-mode dispatcher handling to exit with code 2 instead of showing a modal error and continuing.
- Expanded the offline validator to reject raw numeric geometry tokens and non-failing smoke exception handling.

This is a runtime/XAML correction. It does not change databases, user settings, or live-data behavior.
