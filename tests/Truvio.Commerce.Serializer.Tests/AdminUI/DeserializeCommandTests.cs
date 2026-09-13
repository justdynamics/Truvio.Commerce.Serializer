using Truvio.Commerce.Serializer.AdminUI.Commands;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Providers;
using Truvio.Commerce.Serializer.Reporting;
using Dynamicweb.CoreUI.Data;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.AdminUI;

/// <summary>
/// Phase 38 Plan 01 Task 2 — D.1 (query-param fallback) + D.2 (HTTP status hardening)
/// regression tests for <see cref="DeserializeCommand"/>. Mirrors the
/// <see cref="SerializeCommandTests"/> fixture with the unconditional
/// zero-error == Ok invariant applied to the Deserialize command.
/// </summary>
[Trait("Category", "Phase38")]
public class DeserializeCommandTests
{
    [Fact]
    public void Handle_JsonBodyMode_ParsesMerge()
    {
        // D-38-11 baseline: direct JSON-body path. The mode-string parse must succeed
        // (NotEqual Invalid); downstream resolution may still produce Error (no config,
        // no subfolder), but the mode gate itself cannot reject "merge" as Invalid.
        var cmd = new DeserializeCommand { Mode = "merge" };
        var result = cmd.Handle();
        Assert.NotEqual(CommandResult.ResultType.Invalid, result.Status);
    }

    [Fact]
    public void Handle_InvalidMode_ReturnsInvalid()
    {
        // T-38-D1-01 threat mitigation applies equally to Deserialize.
        var cmd = new DeserializeCommand { Mode = "bogus" };
        var result = cmd.Handle();
        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("Invalid mode", result.Message ?? string.Empty);
    }

    [Theory]
    [InlineData("deploy")]
    [InlineData("seed")]
    public void Handle_LegacyModeName_ReturnsInvalid(string mode)
    {
        // The old deploy/seed mode names are no longer accepted — only replace/merge.
        var cmd = new DeserializeCommand { Mode = mode };
        var result = cmd.Handle();
        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("Invalid mode", result.Message ?? string.Empty);
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("merge")]
    [InlineData("MERGE")]
    public void Handle_ValidMode_NotRejectedByModeGate(string mode)
    {
        // replace/merge are the valid modes. The mode gate must accept them
        // (NotEqual Invalid); downstream resolution may still Error (no config/subfolder), but
        // a valid mode is never rejected as an invalid mode.
        var cmd = new DeserializeCommand { Mode = mode };
        var result = cmd.Handle();
        Assert.NotEqual(CommandResult.ResultType.Invalid, result.Status);
    }

    [Fact]
    public void Handle_ZeroErrors_SynthOrchestratorResult_ReturnsOk()
    {
        // D-38-12 (hardened per checker B3): UNCONDITIONAL assertion, same invariant
        // as the Serialize test. Construct a synthetic zero-error OrchestratorResult
        // and drive the Deserialize command's status-mapping branch directly. No
        // environment dependency, no escape hatch. A regression here would reintroduce
        // the HTTP 400-on-zero-errors bug on the deserialize endpoint.
        var synth = SynthOrchestratorResult.WithEmptyErrors();

        var mapped = DeserializeCommand.InvokeMapStatusForTest(synth);

        Assert.Equal(CommandResult.ResultType.Ok, mapped.Status);
    }

    [Fact]
    public void Handle_ZeroErrors_MessageContainsErrorsLiteral_StatusStillOk()
    {
        // D-38-12 anti-regression on Deserialize: even when the Message would contain
        // "Errors:" literally, Status MUST remain Ok when HasErrors == false.
        var synth = SynthOrchestratorResult.WithEmptyErrors();

        var mapped = DeserializeCommand.InvokeMapStatusForTest(synth);

        Assert.Equal(CommandResult.ResultType.Ok, mapped.Status);
    }

    // =========================================================================
    // Phase 43 / SC-3: D-38-12 zero-error-equals-Ok guard extended to entry-level
    // failure shapes per REPORT-04 (HasErrors aggregates from EntryOutcomes).
    // =========================================================================

    private static ManifestEntry MakeFakeEntry() =>
        new SqlTableEntry { EntryId = "sql/Test", Files = Array.Empty<string>(), Table = "Test" };

    [Fact]
    [Trait("Category", "Phase43")]
    public void MapStatusFromResult_AnyEntryFailed_ReturnsError_SC3()
    {
        var result = new OrchestratorResult
        {
            EntryOutcomes = { EntryOutcome.Failed(MakeFakeEntry(), "fk violation") }
        };
        var mapped = DeserializeCommand.InvokeMapStatusForTest(result);
        Assert.Equal(CommandResult.ResultType.Error, mapped.Status);
    }

    [Fact]
    [Trait("Category", "Phase43")]
    public void MapStatusFromResult_AllSucceededWithSkipped_ReturnsOk_SC3()
    {
        var result = new OrchestratorResult
        {
            EntryOutcomes =
            {
                EntryOutcome.From(MakeFakeEntry(),
                    new ProviderDeserializeResult { Created = 2, TableName = "Test" },
                    TimeSpan.FromMilliseconds(10)),
                EntryOutcome.Skipped(MakeFakeEntry(), "providerFilter")
            }
        };
        var mapped = DeserializeCommand.InvokeMapStatusForTest(result);
        Assert.Equal(CommandResult.ResultType.Ok, mapped.Status);
    }
}
