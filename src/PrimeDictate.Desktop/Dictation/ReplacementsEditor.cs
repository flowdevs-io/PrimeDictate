using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>The WPF "Replacements" grid: a Find column, a "Replace with" column, Add rule and Remove per row.</summary>
public sealed class ReplacementsEditor : StackPanel
{
    private readonly StackPanel rows = new() { Spacing = 6 };
    private readonly List<(Control Root, TextBox Find, TextBox Replace)> editors = [];

    public ReplacementsEditor(IEnumerable<ReplacementDto> rules)
    {
        this.Spacing = 8;
        this.Children.Add(FormParts.Note("After transcription, replace phrases in the final text before optional AI polish and typing. Matching is case-insensitive; longer phrases are applied first."));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto") };
        header.Children.Add(new TextBlock { Text = "Find", FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var replaceHeader = new TextBlock { Text = "Replace with", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(replaceHeader, 1);
        header.Children.Add(replaceHeader);
        this.Children.Add(header);
        this.Children.Add(this.rows);
        var add = new Button { Content = "Add rule", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => this.AddRow(new ReplacementDto());
        this.Children.Add(add);
        foreach (var rule in rules)
        {
            this.AddRow(rule);
        }
    }

    /// <summary>The rules to save: rows with nothing to find are dropped, the rest trimmed (as the WPF Save did).</summary>
    public List<ReplacementDto> Build() => this.editors
        .Where(e => !string.IsNullOrWhiteSpace(e.Find.Text))
        .Select(e => new ReplacementDto { Find = e.Find.Text!.Trim(), Replace = e.Replace.Text?.Trim() ?? string.Empty })
        .ToList();

    private void AddRow(ReplacementDto rule)
    {
        var find = new TextBox { Text = rule.Find, PlaceholderText = "spoken phrase" };
        var replace = new TextBox { Text = rule.Replace, PlaceholderText = "replacement", Margin = new Thickness(8, 0, 0, 0) };
        var remove = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(replace, 1);
        Grid.SetColumn(remove, 2);
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), Children = { find, replace, remove } };
        var row = (Control: (Control)root, Find: find, Replace: replace);
        remove.Click += (_, _) =>
        {
            this.editors.Remove(row);
            this.rows.Children.Remove(root);
        };
        this.editors.Add(row);
        this.rows.Children.Add(root);
    }
}
