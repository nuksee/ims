using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Ims.App.ViewModels;
using Ims.App.Views;
using Ims.Core.Catalog;
using Ims.Core.Connections;
using Ims.Core.Data;
using Ims.Core.Export;
using Ims.Core.Sql;
using Ims.Data.Informix;
using Ims.Data.Informix.Security;
using Microsoft.Win32;

namespace Ims.App;

public partial class MainWindow : Window
{
    // PR-3.10 and PR-8.1. Routed commands rather than KeyBindings bound to the
    // DataContext: the gesture then works with focus inside the editor, and the
    // menu item picks up its own shortcut text automatically.
    public static readonly RoutedUICommand ExecuteCommand = new(
        "Execute", nameof(ExecuteCommand), typeof(MainWindow),
        [new KeyGesture(Key.F5)]);

    public static readonly RoutedUICommand ExecuteSelectionCommand = new(
        "Execute selection", nameof(ExecuteSelectionCommand), typeof(MainWindow),
        [new KeyGesture(Key.Enter, ModifierKeys.Control)]);

    public static readonly RoutedUICommand CancelExecutionCommand = new(
        "Cancel", nameof(CancelExecutionCommand), typeof(MainWindow),
        [new KeyGesture(Key.Cancel, ModifierKeys.Alt)]);

    public static readonly RoutedUICommand NewQueryCommand = new(
        "New query", nameof(NewQueryCommand), typeof(MainWindow),
        [new KeyGesture(Key.N, ModifierKeys.Control)]);

    public static readonly RoutedUICommand OpenFileCommand = new(
        "Open", nameof(OpenFileCommand), typeof(MainWindow),
        [new KeyGesture(Key.O, ModifierKeys.Control)]);

    public static readonly RoutedUICommand SaveFileCommand = new(
        "Save", nameof(SaveFileCommand), typeof(MainWindow),
        [new KeyGesture(Key.S, ModifierKeys.Control)]);

    private readonly MainViewModel _viewModel;
    private readonly ConnectionStore _connections;
    private readonly WindowsCredentialStore _credentials;
    private readonly CsdkDetectionResult _csdk;

    private EditorTabViewModel? _editorBoundTo;

    public MainWindow(
        MainViewModel viewModel,
        ConnectionStore connections,
        WindowsCredentialStore credentials,
        CsdkDetectionResult csdk)
    {
        _viewModel = viewModel;
        _connections = connections;
        _credentials = credentials;
        _csdk = csdk;

        InitializeComponent();

        DataContext = viewModel;

        // The shell owns the prompts; the view models stay testable without a UI.
        viewModel.ConfirmDestructive = ConfirmDestructive;
        viewModel.PromptForPassword = PromptForPassword;

        CsdkStatusText.Text = $"Client SDK {csdk.Version ?? "(unknown)"}";

        CommandBindings.Add(new CommandBinding(ExecuteCommand, async (_, _) => await ExecuteAsync(false)));
        CommandBindings.Add(new CommandBinding(ExecuteSelectionCommand, async (_, _) => await ExecuteAsync(true)));
        CommandBindings.Add(new CommandBinding(CancelExecutionCommand, OnCancel));
        CommandBindings.Add(new CommandBinding(NewQueryCommand, OnNewQuery));
        CommandBindings.Add(new CommandBinding(OpenFileCommand, OnOpenFile));
        CommandBindings.Add(new CommandBinding(SaveFileCommand, OnSaveFile));

        LoadSyntaxHighlighting();

        // PR-3.9: anything left by a run that did not close cleanly comes back.
        if (viewModel.RestoreAutosavedTabs() == 0)
        {
            viewModel.NewTab();
        }

        BindEditorToSelectedTab();
    }

    // ---- Editor plumbing -------------------------------------------------------

    /// <summary>
    /// Loads the Informix SQL and SPL definition (PR-3.1).
    /// </summary>
    /// <remarks>
    /// Failure here costs highlighting and nothing else, so it is caught: an editor
    /// with plain black text still works, and refusing to start would not.
    /// </remarks>
    private void LoadSyntaxHighlighting()
    {
        try
        {
            using Stream? stream = typeof(MainWindow).Assembly
                .GetManifestResourceStream("Ims.App.Resources.InformixSql.xshd");

            if (stream is null)
            {
                return;
            }

            using XmlReader reader = XmlReader.Create(stream);
            Editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (XmlException)
        {
        }
        catch (HighlightingDefinitionInvalidException)
        {
        }
    }

    /// <summary>
    /// Points the single editor control at the selected tab.
    /// </summary>
    /// <remarks>
    /// One AvalonEdit instance shared between tabs, rather than one per tab. It
    /// keeps memory flat with many tabs open and makes the undo stack behave
    /// predictably; the cost is having to move the text across by hand here.
    /// </remarks>
    private void BindEditorToSelectedTab()
    {
        // Persist whatever is on screen back to the tab it came from.
        if (_editorBoundTo is not null)
        {
            _editorBoundTo.Sql = Editor.Text;
        }

        _editorBoundTo = _viewModel.SelectedTab;

        Editor.Text = _editorBoundTo?.Sql ?? string.Empty;
        Editor.IsEnabled = _editorBoundTo is not null;

        RebuildResultColumns();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, TabHeaders))
        {
            return;
        }

        BindEditorToSelectedTab();
    }

    private void OnResultSetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ResultSetTabs))
        {
            RebuildResultColumns();
        }
    }

    /// <summary>
    /// Rebuilds the grid's columns for the selected result.
    /// </summary>
    /// <remarks>
    /// A result set's shape is not known until the statement runs, so the columns
    /// are built in code. Each binds to the row view model's indexer.
    /// </remarks>
    private void RebuildResultColumns()
    {
        ResultGrid.Columns.Clear();

        ResultSetViewModel? result = _viewModel.SelectedTab?.SelectedResult;

        if (result is null)
        {
            ResultGrid.ItemsSource = null;
            return;
        }

        var valueConverter = (IValueConverter)Resources["ValueConverter"];
        var isNullConverter = (IValueConverter)Resources["IsNullConverter"];

        for (int i = 0; i < result.Columns.Count; i++)
        {
            ResultColumn column = result.Columns[i];

            var cellStyle = new Style(typeof(TextBlock));

            // PR-4.4 and NFR-8: NULL reads as italic "(null)" — a shape difference,
            // not a colour difference, so it survives a monochrome screen.
            var nullTrigger = new DataTrigger
            {
                Binding = new Binding($"[{i}]") { Converter = isNullConverter },
                Value = true,
            };

            nullTrigger.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic));
            nullTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                System.Windows.Media.Brushes.Gray));

            cellStyle.Triggers.Add(nullTrigger);

            if (column.IsNumeric)
            {
                cellStyle.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty,
                    HorizontalAlignment.Right));
            }

            ResultGrid.Columns.Add(new DataGridTextColumn
            {
                // The server's own type name in the tooltip (PR-8.2).
                Header = new TextBlock
                {
                    Text = column.Name,
                    ToolTip = $"{column.Name} — {column.ServerTypeName}"
                              + (column.Qualifier is { } q ? $" {q}" : string.Empty)
                              + (column.IsNullable ? string.Empty : " NOT NULL"),
                },
                Binding = new Binding($"[{i}]") { Converter = valueConverter },
                ElementStyle = cellStyle,
                IsReadOnly = true,
            });
        }

        ResultGrid.ItemsSource = result.Rows;
    }

    // ---- Prompts ---------------------------------------------------------------

    /// <summary>PR-3.8. Warns, does not block — DEC-2 leaves the decision to privileges.</summary>
    private bool ConfirmDestructive(StatementWarning warning, string statement)
    {
        MessageBoxResult answer = MessageBox.Show(
            this,
            $"{warning.Detail}\n\n{Shorten(statement)}\n\nRun it anyway?",
            warning.Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    private string? PromptForPassword(ConnectionDescriptor descriptor)
    {
        var prompt = new PasswordPrompt(descriptor) { Owner = this };

        if (prompt.ShowDialog() != true || prompt.Password is null)
        {
            return null;
        }

        if (prompt.Remember)
        {
            _credentials.Save(descriptor, descriptor.UserName ?? string.Empty, prompt.Password);
        }

        return prompt.Password;
    }

    // ---- File menu -------------------------------------------------------------

    private void OnNewQuery(object sender, RoutedEventArgs e)
    {
        BindEditorToSelectedTab();
        _viewModel.NewTab();
        BindEditorToSelectedTab();
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*",
            Title = "Open SQL file",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(dialog.FileName);

            EditorTabViewModel tab = _viewModel.NewTab(
                sql: text,
                title: Path.GetFileName(dialog.FileName));

            tab.FilePath = dialog.FileName;
            tab.IsDirty = false;

            BindEditorToSelectedTab();
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open the file",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSaveFile(object sender, RoutedEventArgs e) => SaveCurrentTab(saveAs: false);

    private void OnSaveFileAs(object sender, RoutedEventArgs e) => SaveCurrentTab(saveAs: true);

    private void SaveCurrentTab(bool saveAs)
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        tab.Sql = Editor.Text;

        string? path = tab.FilePath;

        if (saveAs || path is null)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*",
                FileName = tab.Title.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                    ? tab.Title
                    : tab.Title + ".sql",
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            path = dialog.FileName;
        }

        try
        {
            File.WriteAllText(path, tab.Sql, new UTF8Encoding(false));

            tab.FilePath = path;
            tab.Title = Path.GetFileName(path);
            tab.IsDirty = false;

            _viewModel.StatusText = $"Saved {path}";
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save the file",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private async void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EditorTabViewModel tab)
        {
            await _viewModel.CloseTabAsync(tab);
            BindEditorToSelectedTab();
        }
    }

    // ---- Query menu ------------------------------------------------------------

    private async void OnExecute(object sender, RoutedEventArgs e) => await ExecuteAsync(selection: false);

    private async void OnExecuteSelection(object sender, RoutedEventArgs e) => await ExecuteAsync(selection: true);

    private async Task ExecuteAsync(bool selection)
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        tab.Sql = Editor.Text;

        if (tab.Session is null)
        {
            MessageBox.Show(
                this,
                "This tab is not connected to an instance. Choose a connection on the left "
                + "and connect first.",
                "Not connected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        // PR-3.3: the selection when there is one, otherwise the whole script.
        string? selectedText = selection || Editor.SelectionLength > 0
            ? Editor.SelectedText
            : null;

        await tab.ExecuteAsync(selectedText);

        RebuildResultColumns();
    }

    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTab is { } tab)
        {
            await tab.CancelAsync();
        }
    }

    private async void OnCommit(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTab?.Session is { } session)
        {
            await Task.Run(() => session.CommitAsync(CancellationToken.None));
            _viewModel.StatusText = "Committed.";
        }
    }

    private async void OnRollback(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTab?.Session is { } session)
        {
            await Task.Run(() => session.RollbackAsync(CancellationToken.None));
            _viewModel.StatusText = "Rolled back.";
        }
    }

    // ---- Connection menu -------------------------------------------------------

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        await _viewModel.ConnectAsync(_viewModel.SelectedConnection);
        BindEditorToSelectedTab();
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e) =>
        await _viewModel.DisconnectAsync(_viewModel.SelectedConnection);

    private void OnNewConnection(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog(null, _credentials) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is { } descriptor)
        {
            _connections.AddOrUpdate(descriptor);
            _connections.Save();
            _viewModel.RefreshConnectionList();
        }
    }

    private void OnEditConnection(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedConnection is not { } selected)
        {
            return;
        }

        var dialog = new ConnectionDialog(selected.Descriptor, _credentials) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is { } descriptor)
        {
            _connections.AddOrUpdate(descriptor);
            _connections.Save();
            _viewModel.RefreshConnectionList();
        }
    }

    private void OnRemoveConnection(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedConnection is not { } selected)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"Remove {selected.DisplayName} from the list?\n\n"
                + "Its stored password is removed from Windows Credential Manager too. "
                + "Nothing on the server is changed.",
                "Remove connection",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _credentials.Delete(selected.Descriptor);
        _connections.Remove(selected.Descriptor.Id);
        _connections.Save();
        _viewModel.RefreshConnectionList();
    }

    // ---- Results menu ----------------------------------------------------------

    private async void OnExportCsv(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Csv);

    private async void OnExportTsv(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Tsv);

    private async void OnExportJson(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Json);

    private async void OnExportExcel(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Excel);

    private async Task ExportAsync(ExportFormat format)
    {
        if (_viewModel.SelectedTab?.SelectedResult is not { } result)
        {
            return;
        }

        string extension = ResultExporter.ExtensionFor(format);

        var dialog = new SaveFileDialog
        {
            Filter = $"{format} (*{extension})|*{extension}|All files (*.*)|*.*",
            FileName = "results" + extension,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _viewModel.StatusText = "Exporting…";

        try
        {
            await ResultExporter.ExportToFileAsync(
                dialog.FileName,
                format,
                result.Columns,
                result.EnumerateForExportAsync(CancellationToken.None),
                CancellationToken.None);

            _viewModel.StatusText = $"Exported to {dialog.FileName}";
        }
        catch (IOException ex)
        {
            _viewModel.StatusText = "Export failed.";
            MessageBox.Show(this, ex.Message, "Could not export",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCopySelection(object sender, RoutedEventArgs e)
    {
        if (ResultGrid.SelectedCells.Count > 0)
        {
            System.Windows.Input.ApplicationCommands.Copy.Execute(null, ResultGrid);
        }
    }

    private async void OnFetchMore(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTab?.SelectedResult is { } result)
        {
            await result.FetchMoreAsync(CancellationToken.None);
        }
    }

    // ---- Object browser (Slice 2) ----------------------------------------------

    /// <summary>
    /// Loads a node's children the first time it is expanded (PR-2.2).
    /// </summary>
    /// <remarks>
    /// Hooked from the container style's Expanded event rather than a binding,
    /// because the load is asynchronous and a property setter cannot await it.
    /// </remarks>
    private async void OnTreeItemExpanded(object sender, RoutedEventArgs e)
    {
        if ((e.OriginalSource as TreeViewItem)?.DataContext is CatalogNodeViewModel node)
        {
            await node.EnsureLoadedAsync(CancellationToken.None);
        }
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_viewModel.ObjectTree is { } tree)
        {
            tree.SelectedNode = e.NewValue as CatalogNodeViewModel;
        }
    }

    /// <summary>PR-2.8: put a starting query in front of the user, do not run it.</summary>
    /// <remarks>
    /// It opens in an editor rather than executing, because PR-6.2 says IMS sends no
    /// statement the user did not type or explicitly request — and "I clicked a
    /// table in a tree" is not a request to query it.
    /// </remarks>
    private void OnSelectFirstRows(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ObjectTree?.SelectedObject is not { } schemaObject)
        {
            return;
        }

        // The menu item is disabled for anything else; this is the backstop.
        if (_viewModel.ObjectTree?.CanSelectRows != true)
        {
            return;
        }

        BindEditorToSelectedTab();

        _viewModel.NewTab(
            session: _viewModel.SelectedTab?.Session,
            sql: $"SELECT FIRST 100 *{Environment.NewLine}  FROM {schemaObject.QualifiedName};",
            title: schemaObject.Name);

        BindEditorToSelectedTab();
    }

    /// <summary>
    /// Scripts the selected object into a new editor tab (PR-2.6, PR-2.8).
    /// </summary>
    /// <remarks>
    /// Into an editor, not a read-only viewer: the point of scripting an object is
    /// usually to change something about it, and PR-6.2 means IMS will not run it
    /// either way until the user asks.
    /// </remarks>
    private async void OnScriptObject(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ObjectTree is not { } tree || tree.SelectedObject is not { } schemaObject)
        {
            return;
        }

        _viewModel.StatusText = $"Scripting {schemaObject.Name}…";

        ScriptResult result;

        try
        {
            result = await tree.ScriptSelectionAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NFR-4: a catalogue the user cannot fully read is an ordinary thing to
            // meet, and it should cost the script rather than the application.
            _viewModel.StatusText = $"Could not script {schemaObject.Name}: {ex.Message}";
            return;
        }

        if (result.Unsupported is { } reason)
        {
            _viewModel.StatusText = reason;
            return;
        }

        BindEditorToSelectedTab();

        _viewModel.NewTab(
            session: _viewModel.SelectedTab?.Session,
            sql: result.Sql,
            title: schemaObject.Name + " (DDL)");

        BindEditorToSelectedTab();

        _viewModel.StatusText = $"Scripted {schemaObject.QualifiedName}.";
    }

    private void OnCopyQualifiedName(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ObjectTree?.SelectedObject is not { } schemaObject)
        {
            return;
        }

        try
        {
            Clipboard.SetText(schemaObject.QualifiedName);
            _viewModel.StatusText = $"Copied {schemaObject.QualifiedName}";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    /// <summary>PR-8.2: never hide the server. Show the catalogue query behind the view.</summary>
    private void OnShowCatalogQuery(object sender, RoutedEventArgs e)
    {
        string? sql = _viewModel.ObjectTree?.SelectedQuery;

        if (string.IsNullOrWhiteSpace(sql))
        {
            _viewModel.StatusText = "That node has no catalogue query behind it yet — expand it first.";
            return;
        }

        BindEditorToSelectedTab();

        _viewModel.NewTab(
            session: _viewModel.SelectedTab?.Session,
            sql: sql,
            title: "Catalogue query");

        BindEditorToSelectedTab();
    }

    /// <summary>Switches to the detail pane and loads the selection (PR-2.4).</summary>
    private async void OnShowObjectDetail(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ObjectTree is not { } tree)
        {
            return;
        }

        ResultsArea.SelectedItem = ObjectDetailTab;
        tree.IsDetailVisible = true;

        await tree.RefreshDetailAsync(CancellationToken.None);
    }

    /// <summary>
    /// Tracks whether the detail pane is showing, so it only queries when visible.
    /// </summary>
    /// <remarks>
    /// PR-6.4: metadata queries must stay negligible on a production instance.
    /// Arrowing through 500 tables should not issue 500 rounds of six catalogue
    /// queries because a hidden pane was keeping up with the selection.
    /// </remarks>
    private async void OnBottomTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ResultsArea) || _viewModel.ObjectTree is not { } tree)
        {
            return;
        }

        bool visible = ReferenceEquals(ResultsArea.SelectedItem, ObjectDetailTab);
        tree.IsDetailVisible = visible;

        if (visible)
        {
            await tree.RefreshDetailAsync(CancellationToken.None);
        }
    }

    private async void OnRefreshTreeNode(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ObjectTree is { } tree)
        {
            await tree.RefreshNodeAsync(tree.SelectedNode);
        }
    }

    // ---- Help ------------------------------------------------------------------

    private void OnShowHistory(object sender, RoutedEventArgs e) =>
        new HistoryWindow(_viewModel) { Owner = this }.Show();

    private void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            "Informix Management Studio\n\n"
            + $"Client SDK : {_csdk.Version ?? "(unknown)"}\n"
            + $"INFORMIXDIR: {_csdk.InformixDir}\n"
            + $"ODBC driver: {_csdk.OdbcDriverName}\n\n"
            + "IMS sends no statement you did not type, makes no administrative change "
            + "of its own, and emits no telemetry.",
            "About IMS",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    // ---- Shutdown --------------------------------------------------------------

    private async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.SelectedTab is { } tab)
        {
            tab.Sql = Editor.Text;
        }

        await _viewModel.ShutdownAsync();
    }

    private static string Shorten(string text) =>
        text.Length <= 300 ? text : string.Concat(text.AsSpan(0, 300), "…");
}
