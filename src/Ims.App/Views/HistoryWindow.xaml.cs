using System.Windows;
using System.Windows.Controls;
using Ims.App.ViewModels;
using Ims.Core.History;

namespace Ims.App.Views;

/// <summary>Browses local query history (PR-3.12).</summary>
public partial class HistoryWindow : Window
{
    private readonly MainViewModel _main;
    private readonly QueryHistory _history;

    public HistoryWindow(MainViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _history = new QueryHistory(QueryHistory.DefaultPath);

        InitializeComponent();

        Refresh();
    }

    private void Refresh()
    {
        IReadOnlyList<QueryHistoryEntry> entries = _history.Search(SearchBox.Text);

        HistoryGrid.ItemsSource = entries;
        CountText.Text = $"{entries.Count:N0} entries. Stored locally at {QueryHistory.DefaultPath}";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnOpenInEditor(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not QueryHistoryEntry entry)
        {
            return;
        }

        _main.NewTab(sql: entry.Sql, title: "From history");
        Close();
    }

    private void OnOpenInEditor(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OnOpenInEditor(sender, (RoutedEventArgs)e);
}
