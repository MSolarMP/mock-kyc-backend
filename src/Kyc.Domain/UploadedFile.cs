namespace Kyc.Domain;

// Metadata only. The bytes live in object storage.
public class UploadedFile
{
    private UploadedFile() { }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public UploadedFileType Type { get; private set; }
    public UploadedFileStatus Status { get; private set; }
    public string StorageKey { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public long DeclaredSizeBytes { get; private set; }
    // Re-checked against the stored bytes before vendors see the file.
    public string DeclaredSha256 { get; private set; } = default!;
    public bool IntegrityVerified { get; private set; }
    // Which device uploaded it.
    public Guid UploadedByTokenId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UploadedAt { get; private set; }
    public DateTimeOffset? PurgedAt { get; private set; }

    internal static UploadedFile Create(Guid sessionId, UploadedFileType type, string contentType, long size, string sha256,
        Guid tokenId, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7(now);
        return new UploadedFile
        {
            Id = id,
            SessionId = sessionId,
            Type = type,
            Status = UploadedFileStatus.PendingUpload,
            StorageKey = BuildStorageKey(sessionId, type, $"{id:N}.{Extension(contentType)}"),
            ContentType = contentType,
            DeclaredSizeBytes = size,
            DeclaredSha256 = sha256.ToLowerInvariant(),
            UploadedByTokenId = tokenId,
            CreatedAt = now,
        };
    }

    public void MarkUploaded(DateTimeOffset now) => MarkUploaded(StorageKey, now);

    // Marks the upload complete and repoints the uploaded file at its sealed key (one the client has no upload URL for).
    public void MarkUploaded(string sealedStorageKey, DateTimeOffset now)
    {
        if (Status != UploadedFileStatus.PendingUpload)
            throw new SessionStateConflictException("upload_not_pending", $"This upload is {Status}.");
        StorageKey = sealedStorageKey;
        Status = UploadedFileStatus.Uploaded;
        UploadedAt = now;
    }

    public static string Extension(string contentType) => contentType == "image/png" ? "png" : "jpg";

    // No PII in the key. Session prefix groups a session's files.
    public static string BuildStorageKey(Guid sessionId, UploadedFileType type, string fileName) =>
        $"sessions/{sessionId:N}/{type.ToString().ToLowerInvariant()}/{fileName}";

    public void MarkIntegrityVerified() => IntegrityVerified = true;
    public void MarkSuperseded() => Status = UploadedFileStatus.Superseded;

    public void MarkPurged(DateTimeOffset now)
    {
        Status = UploadedFileStatus.Purged;
        PurgedAt = now;
    }
}
