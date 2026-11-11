using System.Text.RegularExpressions;

namespace Ims.Core.Connections;

/// <summary>
/// The shape of an Informix GLS locale name, as used for <c>CLIENT_LOCALE</c> and
/// <c>DB_LOCALE</c> (NFR-9).
/// </summary>
/// <remarks>
/// <para>
/// Only the shape is checked — <c>language_territory.codeset</c>, with an optional
/// <c>@modifier</c> — not whether the locale is installed. Which locales exist is a
/// property of the Client SDK on this machine and of the server, and the CSDK reports
/// one it cannot load itself at connect time. What IMS can catch here is the typo
/// (a missing codeset, a stray space), at the field where it was made rather than as
/// a connection error later.
/// </para>
/// <para>
/// The codeset may be a name (<c>utf8</c>, <c>8859-1</c>, <c>CP1252</c>) or a number
/// (<c>819</c>, <c>57372</c>), and case is not significant to GLS, so both are accepted.
/// </para>
/// </remarks>
public static partial class InformixLocale
{
    /// <summary>
    /// Locales offered as suggestions in the connection dialog. Not a whitelist:
    /// any well-formed name is accepted.
    /// </summary>
    public static IReadOnlyList<string> CommonLocales { get; } =
    [
        "en_US.819",
        "en_US.8859-1",
        "en_US.CP1252",
        "en_US.utf8",
        "en_US.57372",
        "en_GB.819",
        "de_DE.819",
        "de_DE.CP1252",
        "fr_FR.819",
        "ru_RU.CP1251",
        "ru_RU.915",
        "ja_JP.utf8",
        "zh_CN.utf8",
    ];

    /// <summary>
    /// True when <paramref name="value"/> is shaped like a GLS locale name.
    /// </summary>
    public static bool IsWellFormed(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Shape().IsMatch(value);

    [GeneratedRegex(
        @"^[A-Za-z]{2,3}_[A-Za-z]{2}\.[A-Za-z0-9][A-Za-z0-9\-]*(@[A-Za-z0-9]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
