using System.Text.RegularExpressions;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Stateless helper that rewrites Default.aspx?ID=NNN patterns in strings
/// using a source-to-target page ID map. Boundary-aware regex ensures
/// ID=1 does not corrupt ID=12 (greedy \d+ captures full number).
/// Case-insensitive to handle default.aspx?id=NNN variants.
/// </summary>
public class InternalLinkResolver
{
    private readonly Dictionary<int, int> _sourceToTargetPageIds;
    private readonly Dictionary<int, int> _sourceToTargetParagraphIds;
    private readonly IReadOnlySet<int>? _deferredSourcePageIds;
    private readonly IReadOnlySet<int>? _acknowledgedSourcePageIds;
    private readonly HashSet<int> _localPageIds;
    private readonly Action<string>? _log;
    private int _resolvedCount;
    private int _unresolvedCount;
    private int _deferredCount;
    private int _alreadyLocalCount;
    private int _paragraphResolvedCount;
    private int _paragraphUnresolvedCount;

    /// <summary>
    /// Boundary-aware regex: matches Default.aspx?ID=NNN optionally followed by #PPP.
    /// Group 1 = prefix (Default.aspx?ID=), Group 2 = page ID digits,
    /// Group 3 = full fragment (#PPP), Group 4 = paragraph ID digits.
    /// Greedy \d+ naturally captures the full number.
    /// IgnoreCase handles default.aspx?id= variants.
    /// </summary>
    private static readonly Regex InternalLinkPattern = new(
        @"(Default\.aspx\?ID=)(\d+)(#(\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches JSON "SelectedValue": "NNN" patterns in ButtonEditor serialized values.
    /// Group 1 = prefix ("SelectedValue": "), Group 2 = digits, Group 3 = closing quote.
    /// </summary>
    private static readonly Regex SelectedValuePattern = new(
        @"(""SelectedValue"":\s*"")(\d+)("")",
        RegexOptions.Compiled);

    /// <param name="deferredSourcePageIds">
    /// Source page IDs known to ship via a sibling mode in the same run (e.g. merge pages
    /// while deserializing replace). Links to these are left unchanged and logged as deferred
    /// (NOT as WARNING — they are rewritten during the sibling mode's own pass and must not
    /// escalate under strict mode).
    /// </param>
    /// <param name="acknowledgedSourcePageIds">
    /// Source page IDs the owning predicate acknowledges as known-broken
    /// (ContentEntry.AcknowledgedOrphanPageIds). Links to these are left unchanged and logged
    /// without the WARNING prefix so they do not escalate under strict mode. DW stringifies
    /// raw link-typed integers only when items are read back, so these orphans are invisible
    /// to the serialize-side sweep and MUST be handled here.
    /// </param>
    /// <param name="localPageIds">
    /// Engine issue #13: the host pages this composition owns (pages whose GUID is in the YAML
    /// set being deserialized, see <c>ContentDeserializer.OwnedLocalPageIds</c>), NOT every page
    /// on the host. A Merge run over its own earlier output re-reads link values FROM THE
    /// DESTINATION, where the previous run already rewrote them to local ids. Those values are
    /// not source ids and must not be re-resolved: an id in this set is reported as
    /// already-resolved and left alone instead of escalating under strict mode as "Unresolvable
    /// page ID". The map's own target ids are always treated this way; this set adds owned pages
    /// the map misses (no SourcePageId). Passing every host page id would let an unresolvable
    /// source id that collides with an unrelated host page pass silently (PR #26 review).
    /// </param>
    public InternalLinkResolver(
        Dictionary<int, int> sourceToTargetPageIds,
        Action<string>? log = null,
        Dictionary<int, int>? sourceToTargetParagraphIds = null,
        IReadOnlySet<int>? deferredSourcePageIds = null,
        IReadOnlySet<int>? acknowledgedSourcePageIds = null,
        IReadOnlySet<int>? localPageIds = null)
    {
        _sourceToTargetPageIds = sourceToTargetPageIds;
        _log = log;
        _sourceToTargetParagraphIds = sourceToTargetParagraphIds ?? new Dictionary<int, int>();
        _deferredSourcePageIds = deferredSourcePageIds;
        _acknowledgedSourcePageIds = acknowledgedSourcePageIds;
        _localPageIds = new HashSet<int>(sourceToTargetPageIds.Values);
        if (localPageIds is not null)
            _localPageIds.UnionWith(localPageIds);
    }

    /// <summary>
    /// Engine issue #13: the entry currently being deserialized (e.g. <c>"swift-content"</c>).
    /// Named in every Unresolvable warning so a strict-mode failure says WHICH entry produced it.
    /// </summary>
    public string? CurrentEntry { get; set; }

    /// <summary>
    /// Engine issue #13: the document currently being resolved (e.g. the page path or
    /// <c>page.yml</c> location). Named in every Unresolvable warning alongside
    /// <see cref="CurrentEntry"/> and <see cref="CurrentLocator"/> (the field).
    /// </summary>
    public string? CurrentDocument { get; set; }

    /// <summary>
    /// "entry '…', document '…', field '…'" — as much of the three as is known. Empty when
    /// nothing is set, so the warning degrades to its pre-#13 text rather than printing blanks.
    /// </summary>
    private string Where()
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(CurrentEntry)) parts.Add($"entry '{CurrentEntry}'");
        if (!string.IsNullOrEmpty(CurrentDocument)) parts.Add($"document '{CurrentDocument}'");
        if (!string.IsNullOrEmpty(CurrentLocator)) parts.Add($"field '{CurrentLocator}'");
        return parts.Count == 0 ? string.Empty : " [" + string.Join(", ", parts) + "]";
    }

    /// <summary>
    /// True when <paramref name="pageId"/> is a host page this composition owns, so a link
    /// holding it was written by an earlier pass and must be left exactly as it is.
    /// </summary>
    private bool IsAlreadyLocal(int pageId) => _localPageIds.Contains(pageId);

    /// <summary>
    /// Phase 37-05 / LINK-02 pass 2 alias: call sites in <see cref="Providers.SqlTable.SqlTableWriter"/>
    /// invoke this to signal "rewriting the string value of a SqlTable column", which is
    /// semantically distinct from the content-layer item-field pathway even though both
    /// reduce to the same regex / map logic. No behavior change vs <see cref="ResolveLinks"/>.
    /// </summary>
    public string? ResolveInStringColumn(string? value) => ResolveLinks(value);

    /// <summary>
    /// Engine issue #27: resolve a page id held in an INTEGER SqlTable column (e.g.
    /// <c>EmailMarketingEmail.EmailPageId</c>). Same decision ladder as a
    /// <c>Default.aspx?ID=N</c> link: mapped source id -> target id; sibling-mode (deferred) and
    /// acknowledged ids and ids that are already local are left unchanged without a warning;
    /// anything else logs <c>WARNING: Unresolvable page ID N</c> (strict mode escalates it) and is
    /// returned unchanged. 0 and negative values mean "no page" and are returned untouched.
    /// </summary>
    public int ResolvePageId(int sourcePageId)
    {
        if (sourcePageId <= 0)
            return sourcePageId;

        if (_sourceToTargetPageIds.TryGetValue(sourcePageId, out var targetPageId))
        {
            _resolvedCount++;
            return targetPageId;
        }
        if (_deferredSourcePageIds?.Contains(sourcePageId) == true)
        {
            _log?.Invoke($"  Link deferred: page ID {sourcePageId} (int column) ships via another pass in this run");
            _deferredCount++;
            RecordDeferred(sourcePageId);
            return sourcePageId;
        }
        if (_acknowledgedSourcePageIds?.Contains(sourcePageId) == true)
        {
            _log?.Invoke($"  Acknowledged orphan link: page ID {sourcePageId} — left as-is per the predicate's acknowledgedOrphanPageIds");
            _deferredCount++;
            return sourcePageId;
        }
        if (IsAlreadyLocal(sourcePageId))
        {
            _log?.Invoke($"  Link already resolved: page ID {sourcePageId} is a local page id — left unchanged{Where()}");
            _alreadyLocalCount++;
            return sourcePageId;
        }

        _log?.Invoke($"  WARNING: Unresolvable page ID {sourcePageId} in int column{Where()}");
        _unresolvedCount++;
        return sourcePageId;
    }

    /// <summary>
    /// Engine issue #15: a bare number is only a page reference when the FIELD says so.
    /// Callers that know the field is declared as a page/link/paragraph reference in the
    /// item-type XML pass <c>allowRawNumericPageIds: true</c>; every other field keeps its
    /// literal (an <c>ImageAspectRatio</c> of "0" is a ratio, not page 0).
    /// </summary>
    public string? ResolveLinks(string? fieldValue, bool allowRawNumericPageIds) =>
        ResolveLinksCore(fieldValue, allowRawNumericPageIds);

    /// <summary>
    /// Scans the input string for Default.aspx?ID=NNN patterns and rewrites
    /// source page IDs to target page IDs using the injected map.
    /// Unresolvable IDs are preserved unchanged and a warning is logged.
    /// Returns null for null input, empty for empty input.
    /// </summary>
    public string? ResolveLinks(string? fieldValue) => ResolveLinksCore(fieldValue, allowRawNumericPageIds: true);

    private string? ResolveLinksCore(string? fieldValue, bool allowRawNumericPageIds)
    {
        if (string.IsNullOrEmpty(fieldValue))
            return fieldValue;

        // Handle raw numeric page IDs (e.g., LinkEditor stores "121" instead of "Default.aspx?ID=121")
        // Only match if the ENTIRE string is a pure number that exists in our source-to-target map.
        // Engine issue #15: gated on the field being a declared reference — otherwise a literal
        // "0" / "12" in an ordinary text field is silently rewritten into a page id. Page id 0 is
        // never a link target, so it is never mapped.
        if (allowRawNumericPageIds && int.TryParse(fieldValue.Trim(), out var rawPageId) && rawPageId != 0)
        {
            if (_sourceToTargetPageIds.TryGetValue(rawPageId, out var rawTargetId))
            {
                _resolvedCount++;
                return rawTargetId.ToString();
            }
            // Raw numeric pointing at a sibling-mode page that is not on target yet: defer
            // with bookkeeping (these were previously left behind SILENTLY — e.g. the area's
            // HeaderDesktop binding when the chrome ships via merge).
            if (_deferredSourcePageIds?.Contains(rawPageId) == true)
            {
                _log?.Invoke($"  Link deferred: page ID {rawPageId} (raw numeric) ships via another pass in this run");
                _deferredCount++;
                RecordDeferred(rawPageId);
                return fieldValue;
            }
        }

        // Also resolve "SelectedValue": "NNN" in ButtonEditor JSON
        fieldValue = SelectedValuePattern.Replace(fieldValue, match =>
        {
            var sourceId = int.Parse(match.Groups[2].Value);
            if (_sourceToTargetPageIds.TryGetValue(sourceId, out var targetId))
                return match.Groups[1].Value + targetId.ToString() + match.Groups[3].Value;
            return match.Value;
        });

        return InternalLinkPattern.Replace(fieldValue, match =>
        {
            var sourcePageId = int.Parse(match.Groups[2].Value);
            var hasFragment = match.Groups[4].Success;

            // Engine issue #15: ID=0 is "no page", never a mapping key.
            if (sourcePageId == 0)
                return match.Value;

            if (_sourceToTargetPageIds.TryGetValue(sourcePageId, out var targetPageId))
            {
                _resolvedCount++;
                var result = match.Groups[1].Value + targetPageId.ToString();

                if (hasFragment)
                {
                    var sourceParagraphId = int.Parse(match.Groups[4].Value);
                    if (_sourceToTargetParagraphIds.TryGetValue(sourceParagraphId, out var targetParagraphId))
                    {
                        _paragraphResolvedCount++;
                        result += "#" + targetParagraphId.ToString();
                    }
                    else
                    {
                        _log?.Invoke($"  WARNING: Unresolvable paragraph ID {sourceParagraphId} in anchor link{Where()}");
                        _paragraphUnresolvedCount++;
                        result += "#" + sourceParagraphId.ToString();
                    }
                }

                return result;
            }
            else if (_deferredSourcePageIds?.Contains(sourcePageId) == true)
            {
                _log?.Invoke($"  Link deferred: page ID {sourcePageId} ships via another pass in this run — finalized at the end of the merge run");
                _deferredCount++;
                RecordDeferred(sourcePageId);
                return match.Value;
            }
            else if (_acknowledgedSourcePageIds?.Contains(sourcePageId) == true)
            {
                _log?.Invoke($"  Acknowledged orphan link: page ID {sourcePageId} — left as-is per the predicate's acknowledgedOrphanPageIds");
                _deferredCount++;
                return match.Value;
            }
            else if (IsAlreadyLocal(sourcePageId))
            {
                // Engine issue #13: the value came back from the DESTINATION, where an earlier
                // pass already rewrote it to this host's page id. Re-resolving it against the
                // source map is what made Merge-over-its-own-output fail under strict mode.
                _log?.Invoke($"  Link already resolved: page ID {sourcePageId} is a local page id" +
                             $" — left unchanged{Where()}");
                _alreadyLocalCount++;
                return match.Value;
            }
            else
            {
                _log?.Invoke($"  WARNING: Unresolvable page ID {sourcePageId} in link{Where()}");
                _unresolvedCount++;
                return match.Value;
            }
        });
    }

    /// <summary>
    /// Builds a source-to-target page ID mapping from serialized pages and
    /// the PageGuidCache. Combines: SourcePageId (from YAML) -> PageUniqueId (GUID)
    /// -> target page ID (from PageGuidCache). Recursively flattens children.
    /// Pages without SourcePageId or not found in cache are skipped.
    /// </summary>
    public static Dictionary<int, int> BuildSourceToTargetMap(
        List<SerializedPage> pages,
        Dictionary<Guid, int> pageGuidCache)
    {
        var map = new Dictionary<int, int>();
        CollectSourcePageIds(pages, pageGuidCache, map);
        return map;
    }

    /// <summary>
    /// Returns cumulative (resolved, unresolved, paragraphResolved, paragraphUnresolved)
    /// link counts across all ResolveLinks calls on this instance.
    /// </summary>
    public (int resolved, int unresolved, int paragraphResolved, int paragraphUnresolved) GetStats() =>
        (_resolvedCount, _unresolvedCount, _paragraphResolvedCount, _paragraphUnresolvedCount);

    /// <summary>Links left unchanged because their target ships via a sibling mode in the same run.</summary>
    public int DeferredCount => _deferredCount;

    /// <summary>
    /// Engine issue #13: links left unchanged because they already held a LOCAL page id
    /// (destination-read value from an earlier pass). Not warnings; not failures.
    /// </summary>
    public int AlreadyLocalCount => _alreadyLocalCount;

    /// <summary>
    /// Field locator for deferral bookkeeping. When set, every deferred link is recorded in
    /// <see cref="DeferredRecords"/> as (locator, sourceId) so an end-of-run pass can rewrite
    /// EXACTLY those occurrences once the deferred pages exist on target — without rescanning
    /// (and misinterpreting) fields that already hold rewritten target ids.
    /// </summary>
    public string? CurrentLocator { get; set; }

    /// <summary>Deferred link occurrences recorded while <see cref="CurrentLocator"/> was set.</summary>
    public List<DeferredLinkRecord> DeferredRecords { get; } = new();

    private void RecordDeferred(int sourcePageId)
    {
        if (CurrentLocator is not null)
            DeferredRecords.Add(new DeferredLinkRecord(CurrentLocator, sourcePageId));
    }

    /// <summary>
    /// Builds a source-to-target paragraph ID mapping by recursively walking
    /// pages -> GridRows -> Columns -> Paragraphs. For each paragraph with
    /// SourceParagraphId and ParagraphUniqueId found in the cache:
    /// map[SourceParagraphId] = paragraphGuidCache[ParagraphUniqueId].
    /// </summary>
    public static Dictionary<int, int> BuildSourceToTargetParagraphMap(
        List<SerializedPage> pages,
        Dictionary<Guid, int> paragraphGuidCache)
    {
        // Phase 38.1 W6 (D-38.1-14): shared ParagraphIdCollector replaces the
        // previous private walker that duplicated BaselineLinkSweeper's shape.
        var map = new Dictionary<int, int>();
        ParagraphIdCollector.Visit(pages, para =>
        {
            if (para.SourceParagraphId.HasValue &&
                paragraphGuidCache.TryGetValue(para.ParagraphUniqueId, out var targetId))
            {
                map[para.SourceParagraphId.Value] = targetId;
            }
        });
        return map;
    }

    private static void CollectSourcePageIds(
        List<SerializedPage> pages,
        Dictionary<Guid, int> pageGuidCache,
        Dictionary<int, int> map)
    {
        foreach (var page in pages)
        {
            // Engine issue #15: SourcePageId 0 means "no source page" — mapping it turns every
            // literal "0" in a resolved field into a page id.
            if (page.SourcePageId is > 0 &&
                pageGuidCache.TryGetValue(page.PageUniqueId, out var targetId))
            {
                map[page.SourcePageId.Value] = targetId;
            }

            if (page.Children.Count > 0)
            {
                CollectSourcePageIds(page.Children, pageGuidCache, map);
            }
        }
    }
}
