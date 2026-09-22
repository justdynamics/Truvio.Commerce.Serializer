using Truvio.Commerce.Serializer.AdminUI.Commands;
using Truvio.Commerce.Serializer.Providers;
using Truvio.Commerce.Serializer.Reporting;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Reporting;

/// <summary>
/// Engine issues #10 and #11: what the deserialize response says about the run.
///
/// <para>#10 — the summary counted <c>EntryOutcomes</c> including the synthetic
/// <see cref="EntryOutcome.RunLevelEntryId"/> outcome strict-mode escalation appends, so a
/// 9-entry manifest reported "across 10 entries" and nothing in the response named the
/// entries walked.</para>
///
/// <para>#11 — the message's "Errors:" tail was built from run-level
/// <see cref="OrchestratorResult.Errors"/> only, so a run reporting "1 failed" answered with an
/// empty list while the reason sat in the log file.</para>
/// </summary>
[Trait("Category", "Issue10")]
public class DeserializeReportTests
{
    private static EntryOutcome Succeeded(string entryId, int created = 0, int updated = 0, int skipped = 0) =>
        new()
        {
            EntryId = entryId,
            ProviderType = "SqlTable",
            Status = EntryStatus.Succeeded,
            Message = "ok",
            Counts = new ProviderCounts(created, updated, skipped, 0)
        };

    private static EntryOutcome Failed(string entryId, params string[] errors) =>
        new()
        {
            EntryId = entryId,
            ProviderType = "Content",
            Status = EntryStatus.Failed,
            Message = errors.FirstOrDefault() ?? "failed",
            Errors = errors,
            Counts = new ProviderCounts(0, 0, 0, 1)
        };

    // ---------------------------------------------------------------------
    // #10 — entry count and entries[]
    // ---------------------------------------------------------------------

    [Fact]
    public void Summary_WithRunLevelError_CountsManifestEntriesOnly()
    {
        var outcomes = Enumerable.Range(1, 9).Select(i => Succeeded($"sql/Table{i}", created: 1)).ToList();
        outcomes.Add(EntryOutcome.RunLevelError("Strict mode: 9 warning(s) escalated to failure"));

        var result = new OrchestratorResult { EntryOutcomes = outcomes };

        Assert.Contains("across 9 entries", result.Summary);
        Assert.DoesNotContain("across 10 entries", result.Summary);
    }

    [Fact]
    public void Summary_TotalsExcludeTheRunLevelOutcome()
    {
        var result = new OrchestratorResult
        {
            EntryOutcomes = new List<EntryOutcome>
            {
                Succeeded("sql/A", created: 194, updated: 8),
                EntryOutcome.RunLevelError("Strict mode: escalated")
            }
        };

        Assert.Contains("194 created, 8 updated, 0 skipped, 0 failed across 1 entries", result.Summary);
    }

    [Fact]
    public void ManifestEntryOutcomes_ExcludeTheSyntheticRunLevelOutcome()
    {
        var result = new OrchestratorResult
        {
            EntryOutcomes = new List<EntryOutcome>
            {
                Succeeded("sql/A"),
                EntryOutcome.RunLevelError("run-level")
            }
        };

        Assert.Single(result.ManifestEntryOutcomes);
        Assert.Equal("sql/A", result.ManifestEntryOutcomes[0].EntryId);
    }

    [Fact]
    public void BuildResultModel_NamesEveryWalkedEntry_WithItsOwnCounts()
    {
        var result = new OrchestratorResult
        {
            EntryOutcomes = new List<EntryOutcome>
            {
                Succeeded("sql/EcomOrderFlow", created: 3, updated: 2, skipped: 1),
                Succeeded("content/area-1", created: 191, updated: 6),
                EntryOutcome.RunLevelError("Strict mode: escalated")
            }
        };

        var model = DeserializeCommand.BuildResultModel(result, "merge", isDryRun: false);

        Assert.Equal(2, model.EntryCount);
        Assert.Equal(2, model.Entries.Count);
        Assert.Equal(new[] { "sql/EcomOrderFlow", "content/area-1" }, model.Entries.Select(e => e.EntryId));
        Assert.DoesNotContain(model.Entries, e => e.EntryId == EntryOutcome.RunLevelEntryId);

        var flow = model.Entries[0];
        Assert.Equal(3, flow.Created);
        Assert.Equal(2, flow.Updated);
        Assert.Equal(1, flow.Skipped);
        Assert.Equal(0, flow.Failed);
        Assert.Equal("Succeeded", flow.Status);

        Assert.Equal(194, model.TotalCreated);
        Assert.Equal(8, model.TotalUpdated);
        Assert.Equal("merge", model.Mode);
    }

    [Fact]
    public void BuildResultModel_EntryCountEqualsTheManifestEntryCount()
    {
        // The validation the issue asks for: a composition of 9 merge entries must never
        // report 10, and entries[] must name exactly those 9.
        var entryIds = Enumerable.Range(1, 9).Select(i => $"sql/Table{i}").ToList();
        var outcomes = entryIds.Select(id => Succeeded(id, created: 1)).ToList();
        outcomes.Add(EntryOutcome.RunLevelError("Strict mode: 9 warning(s) escalated to failure"));

        var model = DeserializeCommand.BuildResultModel(
            new OrchestratorResult { EntryOutcomes = outcomes }, "merge", isDryRun: false);

        Assert.Equal(9, model.EntryCount);
        Assert.Equal(entryIds, model.Entries.Select(e => e.EntryId));
    }

    // ---------------------------------------------------------------------
    // #11 — failed entry errors reach the message
    // ---------------------------------------------------------------------

    [Fact]
    public void AllErrors_IncludeFailedEntryErrors_PrefixedWithTheEntryId()
    {
        var result = new OrchestratorResult
        {
            EntryOutcomes = new List<EntryOutcome>
            {
                Succeeded("sql/A", created: 1),
                Failed("content/area-1", "Unable to resolve the item type. The item cannot be saved.")
            }
        };

        var all = result.AllErrors;

        Assert.Single(all);
        Assert.Contains("[content/area-1]", all[0]);
        Assert.Contains("Unable to resolve the item type", all[0]);
    }

    [Fact]
    public void AllErrors_DoNotDuplicateTheRunLevelError()
    {
        var result = new OrchestratorResult
        {
            Errors = new List<string> { "Strict mode: escalated" },
            EntryOutcomes = new List<EntryOutcome>
            {
                EntryOutcome.RunLevelError("Strict mode: escalated")
            }
        };

        Assert.Single(result.AllErrors);
    }

    [Fact]
    public void BuildMessage_OneFailedEntry_NamesTheReason_NotAnEmptyErrorsList()
    {
        // The reported shape: "[Replace] Deserialized: 1189 created, 10 updated, 33 skipped,
        // 1 failed across 20 entries Errors: " — with nothing after "Errors:".
        var result = new OrchestratorResult
        {
            EntryOutcomes = new List<EntryOutcome>
            {
                Succeeded("sql/A", created: 1189, updated: 10, skipped: 33),
                Failed("content/area-1", "Unable to resolve the item type. The item cannot be saved.")
            }
        };

        var message = DeserializeCommand.BuildMessage(result, "Replace", isDryRun: false, logFileName: null);

        Assert.Contains("Errors:", message);
        Assert.Contains("Unable to resolve the item type", message);
        Assert.DoesNotContain("Errors: \n", message);
        Assert.False(message.TrimEnd().EndsWith("Errors:", StringComparison.Ordinal),
            $"The Errors list must not be empty while an entry failed. Message: {message}");
    }

    [Fact]
    public void BuildMessage_NoErrors_HasNoErrorsTail()
    {
        var result = new OrchestratorResult
        {
            EntryOutcomes = new List<EntryOutcome> { Succeeded("sql/A", created: 1) }
        };

        var message = DeserializeCommand.BuildMessage(result, "Merge", isDryRun: false, logFileName: null);

        Assert.DoesNotContain("Errors:", message);
    }

    [Fact]
    public void BuildResultModel_CarriesTheFailedEntrysOwnErrorStrings()
    {
        var result = new OrchestratorResult
        {
            EntryOutcomes = new List<EntryOutcome>
            {
                Failed("content/area-1", "Unable to resolve the item type. The item cannot be saved.")
            }
        };

        var model = DeserializeCommand.BuildResultModel(result, "replace", isDryRun: false);

        Assert.Equal("Failed", model.Entries[0].Status);
        Assert.Contains("Unable to resolve the item type", Assert.Single(model.Entries[0].Errors));
        Assert.Contains(model.Errors, e => e.Contains("[content/area-1]"));
    }
}
