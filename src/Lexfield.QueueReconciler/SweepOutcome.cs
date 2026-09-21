namespace Lexfield.QueueReconciler;

internal enum SweepOutcome { Completed, NotLeaseHolder, Skipped, LeaseLost, Incomplete }
