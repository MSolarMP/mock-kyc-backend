namespace Kyc.Domain;

public enum UploadedFileStatus
{
    // Upload URL issued, bytes not confirmed yet.
    PendingUpload,
    Uploaded,
    // Replaced by a newer upload, e.g. a recapture.
    Superseded,
    // File deleted by retention. Metadata kept for audit.
    Purged,
}
