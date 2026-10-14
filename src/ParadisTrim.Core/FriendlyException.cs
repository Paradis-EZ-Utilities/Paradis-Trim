namespace ParadisTrim.Core;

/// <summary>
/// An error with a message written for normal people.
/// <see cref="Exception.Message"/> is what the user sees; <see cref="TechnicalDetails"/> goes to the log.
/// </summary>
public class FriendlyException : Exception
{
    public string TechnicalDetails { get; }

    public FriendlyException(string userMessage, string technicalDetails, Exception? inner = null)
        : base(userMessage, inner)
    {
        TechnicalDetails = technicalDetails;
    }
}

/// <summary>
/// The output folder can't be used: it no longer exists (deleted, renamed, a USB drive that was
/// unplugged), or Paradis Trim isn't allowed to write there (read-only folder, no permission).
/// The window reacts by asking the user to pick another folder.
/// </summary>
public sealed class OutputFolderNotWritableException : FriendlyException
{
    public string Folder { get; }

    /// <summary>True if the folder doesn't exist at all, false if it exists but can't be written to.</summary>
    public bool IsMissing { get; }

    public OutputFolderNotWritableException(string folder, Exception? inner, bool isMissing = false)
        : base(isMissing
                   ? "The folder chosen for trimmed copies can't be found. It may have been moved or deleted, or be on a drive that isn't connected."
                   : "Paradis Trim isn't allowed to save files in that folder.",
               $"Cannot use output folder {folder}: {(isMissing ? "it does not exist" : inner?.Message)}", inner)
    {
        Folder = folder;
        IsMissing = isMissing;
    }
}
