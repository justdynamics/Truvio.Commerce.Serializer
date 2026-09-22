namespace Truvio.Commerce.Serializer.AdminUI.Models;

/// <summary>
/// Engine issue #10: the structured payload of a <see cref="Commands.DeserializeCommand"/>
/// response. The message line only ever carried totals, so a manifest/engine disagreement
/// ("across 10 entries" for a 9-entry manifest) could not be attributed from the response:
/// the walked entries were not named anywhere. <see cref="Entries"/> names every entry the
/// run walked with its own counts, so a caller can diff the response against the manifest it
/// handed over.
/// </summary>
public sealed class DeserializeResultModel
{
    /// <summary>"Replace" or "Merge".</summary>
    public string Mode { get; set; } = "";

    /// <summary>True when the run reported would-be work and wrote nothing.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Number of manifest entries walked — <see cref="Entries"/>.Count. Excludes the synthetic
    /// run-level outcome that strict-mode escalation appends (issue #10).
    /// </summary>
    public int EntryCount { get; set; }

    public int TotalCreated { get; set; }
    public int TotalUpdated { get; set; }
    public int TotalSkipped { get; set; }
    public int TotalFailed { get; set; }

    /// <summary>One element per walked manifest entry, in dispatch order.</summary>
    public List<DeserializeEntryModel> Entries { get; set; } = new();

    /// <summary>
    /// Every error of the run: run-level errors plus each failed entry's own error strings
    /// (issue #11). Matches the "Errors:" tail of the response message.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>Warnings reported but deliberately not failing the run.</summary>
    public List<string> QuarantinedWarnings { get; set; } = new();
}

/// <summary>Per-entry line of <see cref="DeserializeResultModel.Entries"/>.</summary>
public sealed class DeserializeEntryModel
{
    /// <summary>Manifest entry id, e.g. <c>sql/EcomGroups</c> or <c>content/area-1</c>.</summary>
    public string EntryId { get; set; } = "";

    /// <summary>"Content" or "SqlTable".</summary>
    public string ProviderType { get; set; } = "";

    /// <summary>"Succeeded", "Failed" or "Skipped".</summary>
    public string Status { get; set; } = "";

    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    /// <summary>The entry's own error strings — the ones that used to reach only the log file.</summary>
    public List<string> Errors { get; set; } = new();
}
