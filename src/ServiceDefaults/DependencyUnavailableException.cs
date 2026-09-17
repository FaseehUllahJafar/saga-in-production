namespace ServiceDefaults;

// Thrown only where silence is NOT safe: cleaning up an effect that landed after its
// step was cancelled. Normally a participant that can't reach its dependency just
// doesn't reply and lets the saga's timeout decide. But once the saga has written a step
// off, nobody else will ever come back for a late authorization or booking, so the
// cleanup must be retried by the transport until it succeeds or a human sees it.
public sealed class DependencyUnavailableException(string message) : Exception(message);

// Thrown when the honest answer is "not yet": the provider says it has no record, but a
// request we sent may still be inside it. Retried by the same patient policy.
public sealed class OutcomeNotSettledException(string message) : Exception(message);
