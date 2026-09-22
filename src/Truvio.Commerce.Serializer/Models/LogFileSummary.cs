namespace Truvio.Commerce.Serializer.Models;

public record LogFileSummary
{
    public string Operation { get; init; } = "";

    /// <summary>Serializer mode of the run: "replace" or "merge" (lowercase).</summary>
    public string Mode { get; init; } = "";

    /// <summary>True when the run was a dry-run preview — nothing was written.</summary>
    public bool DryRun { get; init; }

    public DateTime Timestamp { get; init; }
    public List<PredicateSummary> Predicates { get; init; } = new();
    public int TotalCreated { get; init; }
    public int TotalUpdated { get; init; }
    public int TotalSkipped { get; init; }
    public int TotalFailed { get; init; }

    /// <summary>Rows deleted across the run. Non-zero only for an opted-in replaceStrategy: truncate entry.</summary>
    public int TotalDeleted { get; init; }

    public List<string> Errors { get; init; } = new();
    public List<string> Advice { get; init; } = new();
}

public record PredicateSummary
{
    public string Name { get; init; } = "";
    public string Table { get; init; } = "";
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }

    /// <summary>Rows deleted for this entry. Non-zero only for an opted-in replaceStrategy: truncate entry.</summary>
    public int Deleted { get; init; }

    public List<string> Errors { get; init; } = new();

    /// <summary>Non-fatal diagnostics, e.g. the key resolution used for a table with no primary key.</summary>
    public List<string> Warnings { get; init; } = new();
}
