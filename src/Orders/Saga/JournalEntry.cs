using Contracts;

namespace Orders.Saga;

public enum StepOutcome
{
    // Command sent, no definitive answer yet. This is the state that matters: after a
    // timeout the effect may or may not exist, so it must still be compensated.
    Unknown,
    Succeeded,
    // The participant said no (declined, out of stock). No effect exists.
    Rejected,
    Compensated,
    // Compensation ran and the participant found nothing to undo.
    NothingToCompensate
}

public sealed class JournalEntry
{
    public string Step { get; set; } = "";
    public StepOutcome Outcome { get; set; }
    public string? Reference { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

// The compensation journal. It exists for one reason: after a timeout the orchestrator
// does not know whether a step's effect exists, and "Unknown" must be compensated just
// like "Succeeded". Compensating something that never happened is a cheap no-op at the
// participant (see the tombstones there); NOT compensating something that did happen is
// a leaked authorization or leaked stock. So the journal errs towards compensating.
internal static class CompensationJournal
{
    public static void Record(this List<JournalEntry> journal, string step, StepOutcome outcome, DateTimeOffset now, string? reference = null)
    {
        var entry = journal.FirstOrDefault(e => e.Step == step);
        if (entry is null)
        {
            entry = new JournalEntry { Step = step };
            journal.Add(entry);
        }

        entry.Outcome = outcome;
        entry.Reference = reference ?? entry.Reference;
        entry.UpdatedUtc = now;
    }

    // Latest first (LIFO): undo in the reverse order the effects were created.
    // Capture has no compensation: it is the pivot. Once money is captured the saga
    // completes, and giving it back is a refund, which is a separate business flow.
    public static JournalEntry? NextToCompensate(this List<JournalEntry> journal) =>
        journal.LastOrDefault(e =>
            e.Step != StepNames.CapturePayment &&
            e.Outcome is StepOutcome.Succeeded or StepOutcome.Unknown);
}
