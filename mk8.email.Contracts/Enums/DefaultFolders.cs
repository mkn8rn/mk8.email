namespace mk8.email.Contracts.Enums;

public static class DefaultFolders
{
    public const string Inbox = "Inbox";
    public const string Sent = "Sent";
    public const string Drafts = "Drafts";
    public const string Trash = "Trash";
    public const string Spam = "Spam";

    public static readonly IReadOnlyList<string> All = [Inbox, Sent, Drafts, Trash, Spam];
}
