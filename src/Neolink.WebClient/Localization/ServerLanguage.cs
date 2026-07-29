// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.WebClient.Localization;

/// <summary>
/// The server's default UI language: what a visitor gets before they have an
/// account (the sign-in and first-run screens) and what an account that has never
/// chosen one follows.
///
/// It is a live singleton rather than a config value read at startup so that
/// changing it in Server Settings reaches the very next render — the whole point
/// being that a language change never requires restarting the container.
/// </summary>
public sealed class ServerLanguage
{
    private string _code = Lang.Default;

    public string Code
    {
        get => Volatile.Read(ref _code);
        set => Volatile.Write(ref _code, Lang.Normalize(value));
    }
}
