using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Ims.Core.Connections;
using Ims.Core.Data;

namespace Ims.App;

/// <summary>Renders a result cell, including its null state (PR-4.4, PR-4.5).</summary>
public sealed class InformixValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is InformixValue informixValue
            ? informixValue.ToDisplayString()
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The grid is read-only in v1 — in-grid editing is Tier 2.");
}

/// <summary>True when the cell is SQL NULL, so the view can style it distinctly.</summary>
/// <remarks>
/// Drives an italic, muted rendering rather than a colour change alone. NFR-8
/// forbids relying on colour, and PR-4.4 requires NULL to be distinguishable from
/// an empty string and from zero — italic "(null)" is both.
/// </remarks>
public sealed class InformixValueIsNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is InformixValue { IsNull: true };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visibility from a boolean, with "Invert" as the parameter to flip it.</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;

        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Visible once a count reaches the threshold given as the parameter.
/// </summary>
/// <remarks>
/// Used for the result-set tab strip, which is only worth showing when there is
/// more than one result to switch between. The previous attempt pushed the result
/// object itself through <see cref="BooleanToVisibilityConverter"/>, which reads a
/// non-boolean as false — so the strip was collapsed always, and a script with two
/// result sets silently offered no way to reach the second.
/// </remarks>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int threshold = parameter is string text && int.TryParse(text, out int parsed) ? parsed : 1;
        int count = value is int actual ? actual : 0;

        return count >= threshold ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A background accent for the environment badge.
/// </summary>
/// <remarks>
/// Strictly secondary. The badge always carries the word — PRODUCTION, UAT, DEV —
/// because NFR-8 says no state may rely on colour alone, and PR-1.5 requires a
/// production connection to be unmistakable at a glance to everyone.
/// </remarks>
public sealed class EnvironmentBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is InformixEnvironment environment
            ? environment switch
            {
                InformixEnvironment.Production => "#FFB3261E",
                InformixEnvironment.Uat => "#FF8A6D00",
                InformixEnvironment.Development => "#FF3A6B35",
                _ => "#FF5F6368",
            }
            : "#FF5F6368";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Describes transaction state in words for the status bar (PR-3.7).</summary>
public sealed class TransactionStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TransactionState state
            ? state switch
            {
                TransactionState.AutoCommit => "Autocommit",
                TransactionState.Open => "Transaction open",
                TransactionState.Uncommitted => "UNCOMMITTED CHANGES",
                TransactionState.Failed => "Transaction failed — roll back",
                _ => "No transaction (unlogged database)",
            }
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formats a statement outcome for the messages pane (PR-3.4, PR-3.6).</summary>
public sealed class StatementOutcomeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not StatementOutcome outcome)
        {
            return string.Empty;
        }

        string prefix = $"Statement {outcome.Index + 1}";

        return outcome.Kind switch
        {
            StatementResultKind.Failed =>
                $"{prefix} FAILED — {outcome.Error}",
            StatementResultKind.RowsAffected =>
                $"{prefix}: {outcome.RowsAffected:N0} row(s) affected "
                + $"({outcome.Elapsed.TotalMilliseconds:N0} ms)",
            StatementResultKind.RowSet =>
                $"{prefix}: returned rows ({outcome.Elapsed.TotalMilliseconds:N0} ms)",
            StatementResultKind.Skipped =>
                $"{prefix}: not run",
            _ =>
                $"{prefix}: completed ({outcome.Elapsed.TotalMilliseconds:N0} ms)",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
