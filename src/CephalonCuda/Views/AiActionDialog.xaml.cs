using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CephalonCuda.Services;

namespace CephalonCuda.Views;

/// <summary>
/// Confirmation dialog shown when the AI (or a screen scan) proposes database changes.
/// Each proposed action is listed with a checkbox; the user can deselect any they don't want.
/// On "Apply Selected", the checked actions are executed and the dialog closes with the results.
/// </summary>
public partial class AiActionDialog : Window
{
    private readonly List<(AiAction Action, CheckBox CheckBox)> _rows = [];
    private readonly AiCommandService _commands;

    /// <summary>Execution results populated after the user clicks Apply.</summary>
    public List<AiActionResult> Results { get; private set; } = [];

    public AiActionDialog(List<AiAction> actions, AiCommandService commands)
    {
        InitializeComponent();
        _commands = commands;

        foreach (var action in actions)
        {
            // Use TryFindResource so the dialog doesn't crash if theme brushes aren't loaded yet.
            var textBrush = TryFindResource("Brush.Text") as Brush ?? Brushes.White;
            var cb = new CheckBox
            {
                Content = AiCommandService.Describe(action),
                IsChecked = true,
                Margin = new Thickness(0, 3, 0, 3),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = textBrush,
            };
            ActionPanel.Children.Add(cb);
            _rows.Add((action, cb));
        }

        // Auto-size height to action count so the dialog doesn't feel oversized for 1-2 items.
        var desired = 120 + actions.Count * 28;
        Height = Math.Min(desired, 560);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var selected = _rows
            .Where(r => r.CheckBox.IsChecked == true)
            .Select(r => r.Action)
            .ToList();

        if (selected.Count == 0) { DialogResult = false; return; }

        Results = _commands.ExecuteAll(selected);
        DialogResult = true; // closes the dialog; caller reads Results
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}