namespace mk8.email.Infrastructure.Models;

public static class MailQueueStates
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Quarantined = "quarantined";
    public const string Dead = "dead";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending,
        Processing,
        Completed,
        Quarantined,
        Dead,
    };
}

public static class MailQueueScanStates
{
    public const string Pending = "pending";
    public const string Complete = "complete";
}

public static class MailQueueRecipientStates
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string PermanentFailure = "permanent_failure";
    public const string Quarantined = "quarantined";
}

public static class MailQueueDirections
{
    public const string Inbound = "inbound";
    public const string Submission = "submission";
}
