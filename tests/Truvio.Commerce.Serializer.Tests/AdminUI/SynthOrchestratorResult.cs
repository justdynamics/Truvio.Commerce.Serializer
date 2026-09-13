using System.Collections.Generic;
using Truvio.Commerce.Serializer.Providers;
using Truvio.Commerce.Serializer.Reporting;

namespace Truvio.Commerce.Serializer.Tests.AdminUI;

/// <summary>
/// Phase 38 D-38-12 (B3 hardening): synthetic <see cref="OrchestratorResult"/> factory for
/// driving the zero-error status-mapping branch of SerializeCommand.Handle()
/// (and DeserializeCommand.Handle()) without touching the DW DB, filesystem,
/// or HTTP stack. Produces a result where <see cref="OrchestratorResult.HasErrors"/>
/// evaluates to <c>false</c> — any implementation that maps that state to anything
/// other than <see cref="Dynamicweb.CoreUI.Data.CommandResult.ResultType.Ok"/> has
/// regressed D-38-12.
///
/// <para>Phase 44 / IN-01: <c>OrchestratorResult.DeserializeResults</c> was deleted; this
/// factory now seeds the empty <c>EntryOutcomes</c> list, which carries the same zero-error
/// invariant via <see cref="EntryStatus.Failed"/> aggregation.</para>
/// </summary>
internal static class SynthOrchestratorResult
{
    /// <summary>
    /// Construct an <see cref="OrchestratorResult"/> whose <c>Errors</c> list,
    /// <c>SerializeResults</c> collection, and <c>EntryOutcomes</c> collection are all
    /// empty. The computed <c>HasErrors</c> expression is therefore guaranteed to be
    /// <c>false</c>.
    /// </summary>
    public static OrchestratorResult WithEmptyErrors()
    {
        return new OrchestratorResult
        {
            Errors = new List<string>(),
            SerializeResults = new List<SerializeResult>(),
            EntryOutcomes = new List<EntryOutcome>()
        };
    }
}
