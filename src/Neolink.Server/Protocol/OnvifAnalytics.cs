// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Globalization;
using System.Xml.Linq;

namespace Neolink.Protocol;

/// <summary>The camera's own motion grid, as its ONVIF cell motion detector keeps
/// it: <see cref="Cols"/> x <see cref="Rows"/> cells, row by row from the top left,
/// '1' where motion counts and '0' where it is ignored — the same reading Neolink's
/// zone editor uses for every other camera.</summary>
public sealed record OnvifCellZone(int Cols, int Rows, string Table);

/// <summary>What asking a camera for its motion grid settled. <see cref="Unknown"/>
/// is the one that is not an answer: the camera could not be asked.</summary>
public enum OnvifZoneAnswer { Unknown, Holds, None }

/// <summary>
/// The ONVIF analytics service, for the one thing Neolink edits there: the cell
/// motion detector's grid. The layout (how many cells across and down) belongs to
/// the analytics MODULE, the cells that count belong to the RULE, and both are
/// addressed by the video analytics configuration a media profile carries. The
/// cells travel as base64 of a PackBits-compressed bitmask — ONVIF Analytics
/// Service spec, "Cell Motion Detector".
///
/// A camera that offers no analytics service, no cell motion module or no such
/// rule holds no grid of its own; that is a lasting answer, and Neolink keeps the
/// zone for it instead. Nothing here is asked of a Reolink: the generic control
/// surface is the only caller.
/// </summary>
public sealed partial class OnvifClient
{
    private const string NsAnalytics = "http://www.onvif.org/ver20/analytics/wsdl";
    private string? _analyticsUrl;

    /// <summary>The camera's motion grid, or why there is none. Never throws but for
    /// cancellation: a zone read sits in the panel's load path.</summary>
    public async Task<(OnvifZoneAnswer Answer, OnvifCellZone? Zone)> ReadCellZoneAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false)) return (OnvifZoneAnswer.Unknown, null);
            var (answer, target) = await FindCellTargetAsync(ct).ConfigureAwait(false);
            if (target == null) return (answer, null);
            var cells = CellValue(target.Rule)?.Attribute("Value")?.Value;
            var table = cells == null ? null : DecodeActiveCells(cells, target.Cols, target.Rows);
            if (table == null)
            {
                // A grid Neolink cannot read is one it must not claim to show, nor
                // write over with a guess.
                Log.Info($"{_tag}: the camera's ONVIF motion grid could not be read (ActiveCells '{Truncate(cells ?? "", 40)}')" +
                         " — the zone is kept on Neolink instead");
                return (OnvifZoneAnswer.None, null);
            }
            return (OnvifZoneAnswer.Holds, new OnvifCellZone(target.Cols, target.Rows, table));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"{_tag}: ONVIF motion grid read failed: {Log.Flatten(ex)}");
            return (OnvifZoneAnswer.Unknown, null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Writes the cells that count. The rule is re-read first and sent back
    /// whole with only ActiveCells changed, so its other parameters (how many cells
    /// must move, the alarm delays) stay exactly as the camera had them.</summary>
    public async Task WriteCellZoneAsync(string table, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureDiscoveredAsync(ct).ConfigureAwait(false))
                throw new IOException("the camera's ONVIF service is not reachable");
            var (answer, target) = await FindCellTargetAsync(ct).ConfigureAwait(false);
            if (target == null)
                throw answer == OnvifZoneAnswer.Unknown
                    ? new IOException("the camera's ONVIF analytics service did not answer")
                    : new NotSupportedException("the camera no longer offers a motion grid over ONVIF");
            if (table.Length != target.Cols * target.Rows)
                throw new ArgumentException(
                    $"table must be {target.Cols}x{target.Rows} = {target.Cols * target.Rows} cells of '0'/'1'");
            var body = $"<tan:ConfigurationToken>{Esc(target.Config)}</tan:ConfigurationToken>" +
                       RuleForWrite(target.Rule, EncodeActiveCells(table));
            var (reply, _) = await SendAsync(_analyticsUrl!, NsAnalytics, "ModifyRules", body, ct).ConfigureAwait(false);
            if (reply == null)
                throw Refused("the camera refused the new motion grid");
        }
        finally { _gate.Release(); }
    }

    private sealed record CellTarget(string Config, XElement Rule, int Cols, int Rows);

    /// <summary>The configuration, rule and layout a grid read or write works on.
    /// Called under the gate, after discovery.</summary>
    private async Task<(OnvifZoneAnswer, CellTarget?)> FindCellTargetAsync(CancellationToken ct)
    {
        if (_analyticsUrl == null) return (OnvifZoneAnswer.None, null);
        _profiles ??= await ReadProfilesAsync(ct).ConfigureAwait(false);
        if (_profiles == null) return (OnvifZoneAnswer.Unknown, null);
        var config = _profiles.Select(p => p.AnalyticsToken).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        if (config == null) return (OnvifZoneAnswer.None, null);

        var scope = $"<tan:ConfigurationToken>{Esc(config)}</tan:ConfigurationToken>";
        var (modules, ms) = await SendAsync(_analyticsUrl, NsAnalytics, "GetAnalyticsModules", scope, ct)
            .ConfigureAwait(false);
        if (modules == null) return (Settled(ms), null);
        if (ParseCellLayout(modules) is not { } layout) return (OnvifZoneAnswer.None, null);
        var (rules, rs) = await SendAsync(_analyticsUrl, NsAnalytics, "GetRules", scope, ct).ConfigureAwait(false);
        if (rules == null) return (Settled(rs), null);
        if (FindCellRule(rules) is not { } rule) return (OnvifZoneAnswer.None, null);
        return (OnvifZoneAnswer.Holds, new CellTarget(config, rule, layout.Cols, layout.Rows));
    }

    /// <summary>A refusal the camera will repeat is an answer; anything else is not.
    /// See <see cref="IsLastingRefusal"/>.</summary>
    private static OnvifZoneAnswer Settled(int status) =>
        IsLastingRefusal(status) ? OnvifZoneAnswer.None : OnvifZoneAnswer.Unknown;

    /// <summary>Whether an error reply is one the camera will give again. A SOAP
    /// fault (400, or 500 — ONVIF sends a missing operation or configuration as a
    /// Receiver fault) or a 404/405 is the firmware saying it has no such thing. An
    /// authentication refusal (401/403) may be a lockout or a rejected clock, and a
    /// timeout, a 429 or a 5xx from something in between may pass: those, and no
    /// reply at all, are asked again rather than taken as the camera's word.</summary>
    internal static bool IsLastingRefusal(int status) => status is 400 or 404 or 405 or 500;

    // ------------------------------------------------------------ parsing

    /// <summary>The cell layout of the first cell motion module, or null when the
    /// camera has none. Types are QNames ("tt:CellMotionEngine"); only the local part
    /// is compared, since the prefix is the camera's choice.</summary>
    internal static (int Cols, int Rows)? ParseCellLayout(XElement root)
    {
        var module = root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "AnalyticsModule" && LocalType(e) == "CellMotionEngine");
        var layout = module?.Descendants().FirstOrDefault(e => e.Name.LocalName == "CellLayout");
        if (layout == null) return null;
        static int? Dim(XAttribute? a) =>
            int.TryParse(a?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v is > 0 and <= 256
                ? v : null;
        return Dim(layout.Attribute("Columns")) is { } cols && Dim(layout.Attribute("Rows")) is { } rows
            ? (cols, rows) : null;
    }

    /// <summary>The first cell motion rule that carries its cells, or null.</summary>
    internal static XElement? FindCellRule(XElement root) =>
        root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Rule" && LocalType(e) == "CellMotionDetector" && CellValue(e) != null);

    private static XElement? CellValue(XElement rule) =>
        rule.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "SimpleItem" && (string?)e.Attribute("Name") == "ActiveCells");

    private static string? LocalType(XElement e)
    {
        var type = e.Attribute("Type")?.Value?.Trim();
        return type == null ? null : type[(type.IndexOf(':') + 1)..];
    }

    /// <summary>The rule as ModifyRules wants it back: the camera's own element with
    /// the new cells in place. Its Type is a QName whose prefix was declared somewhere
    /// up the camera's reply — which does not travel with the element — so the
    /// namespace it named is declared on the rule itself.</summary>
    internal static string RuleForWrite(XElement rule, string activeCells)
    {
        var copy = new XElement(rule) { Name = XName.Get("Rule", NsAnalytics) };
        CellValue(copy)!.SetAttributeValue("Value", activeCells);
        var type = rule.Attribute("Type")?.Value?.Trim() ?? "CellMotionDetector";
        int colon = type.IndexOf(':');
        var ns = colon < 0
            ? rule.GetDefaultNamespace().NamespaceName
            : rule.GetNamespaceOfPrefix(type[..colon])?.NamespaceName;
        copy.SetAttributeValue(XNamespace.Xmlns + "nlr", string.IsNullOrEmpty(ns) ? NsSchema : ns);
        copy.SetAttributeValue("Type", "nlr:" + type[(colon + 1)..]);
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    // ------------------------------------------------------------ ActiveCells codec

    /// <summary>ActiveCells as a zone table, or null when it is not valid base64 or
    /// PackBits, or does not describe exactly this grid. Cells run left to right,
    /// top to bottom, most significant bit first. Strict on purpose: a grid read
    /// wrongly would be shown as the camera's, and written back over it.</summary>
    internal static string? DecodeActiveCells(string activeCells, int cols, int rows)
    {
        byte[] packed;
        try { packed = Convert.FromBase64String(activeCells.Trim()); }
        catch (FormatException) { return null; }
        int cells = cols * rows;
        var bits = UnpackBits(packed);
        if (bits == null || bits.Length != (cells + 7) / 8) return null;
        var table = new char[cells];
        for (int i = 0; i < cells; i++)
            table[i] = (bits[i >> 3] & (0x80 >> (i & 7))) != 0 ? '1' : '0';
        return new string(table);
    }

    /// <summary>A zone table as ActiveCells: one bit per cell, the last byte padded
    /// with zeros, PackBits-compressed, base64.</summary>
    internal static string EncodeActiveCells(string table)
    {
        var bits = new byte[(table.Length + 7) / 8];
        for (int i = 0; i < table.Length; i++)
            if (table[i] == '1') bits[i >> 3] |= (byte)(0x80 >> (i & 7));
        return Convert.ToBase64String(PackBits(bits));
    }

    /// <summary>PackBits (TIFF 6.0 / ISO 12369): a header byte n, then either n+1
    /// literal bytes (n 0..127) or one byte repeated 1-n times (n -127..-1).</summary>
    internal static byte[] PackBits(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length + data.Length / 64 + 2);
        int i = 0;
        while (i < data.Length)
        {
            int run = 1;
            while (i + run < data.Length && run < 128 && data[i + run] == data[i]) run++;
            if (run > 1)
            {
                output.Add((byte)(sbyte)(1 - run));
                output.Add(data[i]);
                i += run;
                continue;
            }
            // A literal stretch, ended by the start of a run or at 128 bytes.
            int start = i++;
            while (i < data.Length && i - start < 128 && !(i + 1 < data.Length && data[i] == data[i + 1])) i++;
            output.Add((byte)(i - start - 1));
            for (int k = start; k < i; k++) output.Add(data[k]);
        }
        return output.ToArray();
    }

    /// <summary>Undoes <see cref="PackBits"/>. Null when a header promises more than
    /// the data holds, or the output would be absurdly large for a cell grid.</summary>
    internal static byte[]? UnpackBits(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length * 2);
        int i = 0;
        while (i < data.Length)
        {
            if (output.Count > 65536) return null;
            var n = (sbyte)data[i++];
            if (n >= 0)
            {
                if (i + n + 1 > data.Length) return null;
                for (int k = 0; k <= n; k++) output.Add(data[i + k]);
                i += n + 1;
            }
            else if (n != -128) // -128 is a no-op by definition
            {
                if (i >= data.Length) return null;
                for (int k = 0; k < 1 - n; k++) output.Add(data[i]);
                i++;
            }
        }
        return output.ToArray();
    }
}
