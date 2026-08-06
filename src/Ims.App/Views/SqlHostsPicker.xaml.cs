using System.Windows;
using Ims.Data.Informix;

namespace Ims.App.Views;

/// <summary>Lets the user pick one entry from this machine's sqlhosts (PR-1.9).</summary>
public partial class SqlHostsPicker : Window
{
    public SqlHostsPicker(IReadOnlyList<SqlHostsEntry> entries)
    {
        InitializeComponent();
        EntriesGrid.ItemsSource = entries;
    }

    public SqlHostsEntry? Selected => EntriesGrid.SelectedItem as SqlHostsEntry;

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
        {
            return;
        }

        DialogResult = true;
    }

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Selected is not null)
        {
            DialogResult = true;
        }
    }
}
