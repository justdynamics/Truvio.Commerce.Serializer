using Truvio.Commerce.Serializer.Providers;
using Truvio.Commerce.Serializer.Reporting;

namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// Phase 44 / IN-01 (D-10): generates actionable advice from per-entry outcomes + run-level
/// errors. Public advice-text contract preserved verbatim from Phase 43 (pre-migration);
/// input type changes from <c>List&lt;ProviderDeserializeResult&gt;</c> to
/// <c>IReadOnlyList&lt;EntryOutcome&gt;</c> + <c>IReadOnlyList&lt;string&gt;</c> (run-level
/// errors). <see cref="EntryOutcome.EntryId"/> substitutes for
/// <see cref="ProviderDeserializeResult.TableName"/> as the per-error diagnostic anchor —
/// semantically the same (per-entry diagnostic context).
/// </summary>
public static class AdviceGenerator
{
    /// <summary>
    /// Convenience overload — preserves the existing single-argument call shape at the one
    /// consumer site (<c>SerializerDeserializeCommand.cs</c>) while routing through the new
    /// <c>(outcomes, runLevelErrors)</c> signature.
    /// </summary>
    public static List<string> GenerateAdvice(OrchestratorResult result)
        => GenerateAdvice(result.EntryOutcomes, result.Errors);

    /// <summary>
    /// Generate actionable advice from per-entry outcomes + run-level errors. Maps common
    /// error strings (FK constraint, missing group, duplicate key) to user-friendly guidance.
    /// </summary>
    public static List<string> GenerateAdvice(
        IReadOnlyList<EntryOutcome> outcomes,
        IReadOnlyList<string> runLevelErrors)
    {
        var advice = new List<string>();
        bool hasAnyFailed = false;

        foreach (var o in outcomes)
        {
            if (o.Counts.Failed > 0)
                hasAnyFailed = true;

            foreach (var error in o.Errors)
            {
                if (error.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
                {
                    advice.Add($"FK constraint failed on {o.EntryId} -- check that parent tables are deserialized first (verify predicate ordering)");
                }
                else if (error.Contains("group", StringComparison.OrdinalIgnoreCase) &&
                         error.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    advice.Add($"Missing group referenced in {o.EntryId} -- create it in Settings > Ecommerce before re-running");
                }
                else if (error.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
                {
                    advice.Add($"Duplicate key in {o.EntryId} -- check key uniqueness in source data (rows match by key, not NameColumn)");
                }
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    advice.Add($"Error in {o.EntryId}: {error}");
                }
            }
        }

        // Also check top-level orchestrator errors
        foreach (var error in runLevelErrors)
        {
            if (error.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
            {
                advice.Add("FK constraint failed -- check that parent tables are deserialized first (verify predicate ordering)");
            }
        }

        if (hasAnyFailed)
        {
            advice.Add("Re-run deserialization after fixing errors -- successfully applied rows will be skipped (source-wins idempotency)");
        }

        return advice.Distinct().ToList();
    }
}
