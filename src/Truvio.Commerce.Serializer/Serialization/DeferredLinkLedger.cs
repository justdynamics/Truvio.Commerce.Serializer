using System.Text.Json;
using System.Text.RegularExpressions;
using Dynamicweb.Content;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>One deferred link occurrence: a field (locator) still holding a SOURCE page id.</summary>
public sealed record DeferredLinkRecord(string Locator, int SourceId);

/// <summary>
/// Persistence + finalization for cross-pass deferred links. A field that references a page
/// shipping in ANOTHER pass (other mode, or a later predicate of the same mode) cannot be
/// resolved when it is written — the target id does not exist yet. The resolver leaves the
/// SOURCE id in place and records (locator, sourceId) here; the end of the merge run rewrites
/// EXACTLY those occurrences. Rescanning fields instead would be unsound: an already-written
/// TARGET id is indistinguishable from a source id in the int id space.
///
/// Locator formats: <c>item|{itemType}|{itemId}|{field}</c>, <c>propitem|{pageId}|{field}</c>,
/// <c>shortcut|{pageId}</c>, <c>navsettings|{pageId}</c>, <c>modulesettings|{paragraphId}</c>.
/// The ledger file (<c>deferred-links.json</c>) lives in the mode root next to the manifest,
/// so a replace-only run leaves its deferrals on disk for the next merge run to finalize.
/// </summary>
public static class DeferredLinkLedger
{
    public const string FileName = "deferred-links.json";
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static void Append(string modeRoot, IReadOnlyList<DeferredLinkRecord> records)
    {
        if (records.Count == 0) return;
        var path = Path.Combine(modeRoot, FileName);
        var all = Read(modeRoot);
        all.AddRange(records);
        File.WriteAllText(path, JsonSerializer.Serialize(
            all.Select(r => new[] { (object)r.Locator, r.SourceId }).ToList(), _json));
    }

    public static List<DeferredLinkRecord> Read(string modeRoot)
    {
        var path = Path.Combine(modeRoot, FileName);
        if (!File.Exists(path)) return new List<DeferredLinkRecord>();
        try
        {
            var raw = JsonSerializer.Deserialize<List<JsonElement[]>>(File.ReadAllText(path));
            return raw?.Select(e => new DeferredLinkRecord(e[0].GetString() ?? "", e[1].GetInt32())).ToList()
                   ?? new List<DeferredLinkRecord>();
        }
        catch { return new List<DeferredLinkRecord>(); }
    }

    public static void Delete(string modeRoot)
    {
        var path = Path.Combine(modeRoot, FileName);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Rewrites every recorded occurrence whose source id resolves in <paramref name="map"/>,
    /// then deletes the ledger. Unresolvable records warn (strict mode escalates them — a
    /// deferred page that never shipped is a broken link).
    /// </summary>
    public static void Finalize(string modeRoot, IReadOnlyDictionary<int, int> map, Action<string>? log)
    {
        var records = Read(modeRoot);
        if (records.Count == 0) return;

        int rewritten = 0;
        foreach (var record in records)
        {
            if (!map.TryGetValue(record.SourceId, out var targetId))
            {
                log?.Invoke($"WARNING: deferred link {record.Locator} -> source page {record.SourceId} never arrived on target");
                continue;
            }
            try
            {
                if (ApplyRecord(record, targetId)) rewritten++;
            }
            catch (Exception ex)
            {
                log?.Invoke($"WARNING: deferred link rewrite failed for {record.Locator}: {ex.Message}");
            }
        }
        log?.Invoke($"Deferred link finalization ({Path.GetFileName(modeRoot)}): {rewritten}/{records.Count} occurrence(s) rewritten.");
        Delete(modeRoot);
    }

    /// <summary>Targeted, id-exact replacement — only the recorded source id is touched.</summary>
    internal static string ReplaceSourceId(string value, int sourceId, int targetId)
    {
        if (value.Trim() == sourceId.ToString())
            return targetId.ToString();
        // Engine issue #32: a CheckboxList option field over page ids stores "12,38,40".
        if (Regex.IsMatch(value, @"^\s*\d+(\s*,\s*\d+)*\s*$"))
            return Regex.Replace(value, $@"(?<!\d){sourceId}(?!\d)", targetId.ToString());
        value = Regex.Replace(value, $@"(Default\.aspx\?ID=){sourceId}(?!\d)", $"${{1}}{targetId}", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, $@"(""SelectedValue"":\s*""){sourceId}("")", $"${{1}}{targetId}$2");
        return value;
    }

    private static bool ApplyRecord(DeferredLinkRecord record, int targetId)
    {
        var parts = record.Locator.Split('|');
        switch (parts[0])
        {
            case "item":
            {
                var item = Services.Items.GetItem(parts[1], parts[2]);
                if (item is null) return false;
                var fields = new Dictionary<string, object?>();
                item.SerializeTo(fields);
                if (!fields.TryGetValue(parts[3], out var v) || v is not string s || s.Length == 0) return false;
                var replaced = ReplaceSourceId(s, record.SourceId, targetId);
                if (replaced == s) return false;
                fields[parts[3]] = replaced;
                // Engine issue #6: a rewritten ButtonData value comes back as a string; writing
                // it back as one binds to nothing and the deferred remap is silently dropped.
                ButtonDataFieldLookup.Apply(item.SystemName, fields);
                item.DeserializeFrom(fields);
                using (var ctx = new Dynamicweb.Content.Items.ItemContext())
                    item.Save(ctx);
                return true;
            }
            case "propitem":
            {
                var page = Services.Pages.GetPage(int.Parse(parts[1]));
                var propItem = page?.PropertyItem;
                if (propItem is null) return false;
                var fields = new Dictionary<string, object?>();
                propItem.SerializeTo(fields);
                if (!fields.TryGetValue(parts[2], out var v) || v is not string s || s.Length == 0) return false;
                var replaced = ReplaceSourceId(s, record.SourceId, targetId);
                if (replaced == s) return false;
                fields[parts[2]] = replaced;
                ButtonDataFieldLookup.Apply(propItem.SystemName, fields);
                propItem.DeserializeFrom(fields);
                using (var ctx = new Dynamicweb.Content.Items.ItemContext())
                    propItem.Save(ctx);
                return true;
            }
            case "shortcut":
            {
                var page = Services.Pages.GetPage(int.Parse(parts[1]));
                if (page is null || string.IsNullOrEmpty(page.ShortCut)) return false;
                var replaced = ReplaceSourceId(page.ShortCut, record.SourceId, targetId);
                if (replaced == page.ShortCut) return false;
                page.ShortCut = replaced;
                Services.Pages.SavePage(page, skipLanguages: true);
                return true;
            }
            case "navsettings":
            {
                var page = Services.Pages.GetPage(int.Parse(parts[1]));
                if (page?.NavigationSettings?.ProductPage is not { Length: > 0 } current) return false;
                var replaced = ReplaceSourceId(current, record.SourceId, targetId);
                if (replaced == current) return false;
                page!.NavigationSettings!.ProductPage = replaced;
                Services.Pages.SavePage(page, skipLanguages: true);
                return true;
            }
            case "modulesettings":
            {
                var para = Services.Paragraphs.GetParagraph(int.Parse(parts[1]));
                if (para is null || string.IsNullOrEmpty(para.ModuleSettings)) return false;
                var replaced = ReplaceSourceId(para.ModuleSettings, record.SourceId, targetId);
                if (replaced == para.ModuleSettings) return false;
                para.ModuleSettings = replaced;
                Services.Paragraphs.SaveParagraph(para);
                return true;
            }
            default:
                return false;
        }
    }
}
