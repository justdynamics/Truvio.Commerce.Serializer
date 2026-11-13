namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: what a deserialize did (dry run: would do) to item lists, summed over
/// the documents of one entry. Reported with the entry's created/updated/skipped counts, so a
/// caller that reads only the run result (the Foundry's drift stop) sees list changes too.
/// </summary>
/// <param name="ListsCreated">Lists created for a field that had none.</param>
/// <param name="EntriesCreated">Entries created.</param>
/// <param name="EntriesUpdated">
/// Matched entries written source-wins. In a dry run, only the matched entries whose fields
/// would change.
/// </param>
/// <param name="EntriesFilled">Matched entries a Merge fill visited.</param>
/// <param name="FieldsFilled">Fields a Merge fill wrote into existing entries.</param>
/// <param name="EntriesHostOnly">Entries the host has and the document does not: left in place, never deleted.</param>
/// <param name="ListsReordered">Lists whose existing entries end up in another order.</param>
/// <param name="ListOptionsChanged">Lists whose <c>changedOnLanguage</c> flag changes.</param>
public sealed record ItemListCounts(
    int ListsCreated,
    int EntriesCreated,
    int EntriesUpdated,
    int EntriesFilled,
    int FieldsFilled,
    int EntriesHostOnly,
    int ListsReordered,
    int ListOptionsChanged)
{
    public static ItemListCounts Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>True when nothing was counted: no document of the entry carried a list the host differs on.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsZero => this == Zero;

    public ItemListCounts Add(ItemListWriteResult r) => this with
    {
        ListsCreated = ListsCreated + r.ListsCreated,
        EntriesCreated = EntriesCreated + r.Created,
        EntriesUpdated = EntriesUpdated + r.Overwritten,
        EntriesFilled = EntriesFilled + r.Filled,
        FieldsFilled = FieldsFilled + r.FilledFields,
        EntriesHostOnly = EntriesHostOnly + r.HostOnly,
        ListsReordered = ListsReordered + r.Reordered,
        ListOptionsChanged = ListOptionsChanged + r.OptionsChanged
    };

    public ItemListCounts Add(ItemListCounts other) => this with
    {
        ListsCreated = ListsCreated + other.ListsCreated,
        EntriesCreated = EntriesCreated + other.EntriesCreated,
        EntriesUpdated = EntriesUpdated + other.EntriesUpdated,
        EntriesFilled = EntriesFilled + other.EntriesFilled,
        FieldsFilled = FieldsFilled + other.FieldsFilled,
        EntriesHostOnly = EntriesHostOnly + other.EntriesHostOnly,
        ListsReordered = ListsReordered + other.ListsReordered,
        ListOptionsChanged = ListOptionsChanged + other.ListOptionsChanged
    };

    /// <summary>One log line, the same shape in the dry run and the real run.</summary>
    public string Summary(bool dryRun) =>
        $"{(dryRun ? "[DRY-RUN] " : "")}Item lists: {ListsCreated} list(s) {(dryRun ? "to create" : "created")}, " +
        $"{EntriesCreated} entr{(EntriesCreated == 1 ? "y" : "ies")} {(dryRun ? "to create" : "created")}, " +
        $"{EntriesUpdated} {(dryRun ? "to update" : "overwritten")}, {FieldsFilled} field(s) {(dryRun ? "to fill" : "filled")} " +
        $"in {EntriesFilled} matched entr{(EntriesFilled == 1 ? "y" : "ies")}, {EntriesHostOnly} host-only left in place, " +
        $"{ListsReordered} list(s) {(dryRun ? "to reorder" : "reordered")}, {ListOptionsChanged} changedOnLanguage flag(s) {(dryRun ? "to change" : "changed")}.";
}
