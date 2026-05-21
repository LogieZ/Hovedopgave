namespace VideoArchiveManager.Models
{
    public sealed class VideoEntry
    {
        public long Id { get; set; }
        // The YouTube video ID (e.g., "dQw4w9WgXcQ")
        public string YoutubeId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;
        // The duration of the video in seconds
        public long DurationSeconds { get; set; }

        // The date the video was uploaded
        public DateTime? UploadedDate { get; set; }
        // Absolute path to the linked local file, or null if not yet linked
        public string? LinkedFilePath { get; set; }

        public DateTime? LastVerified { get; set; }

        public long? FileSizeBytes { get; set; }

        public LinkStatus Status { get; set; } = LinkStatus.Unlinked;


        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Domain methods – status transitions should go through these so business rules stay
        // in the model and are not scattered across callers.

        public void MarkAsDownloading()
        {
            Status = LinkStatus.Downloading;
            UpdatedAt = DateTime.UtcNow;
        }

        public void MarkAsLinked(string? filePath = null, long? fileSizeBytes = null)
        {
            LinkedFilePath = filePath;
            FileSizeBytes = fileSizeBytes;
            Status = LinkStatus.Linked;
            LastVerified = DateTime.UtcNow;
            UpdatedAt = DateTime.UtcNow;
        }

        public void MarkAsDownloadFailed()
        {
            Status = LinkStatus.DownloadFailed;
            UpdatedAt = DateTime.UtcNow;
        }

        public void MarkAsMissing()
        {
            LinkedFilePath = null;
            FileSizeBytes = null;
            Status = LinkStatus.Missing;
            UpdatedAt = DateTime.UtcNow;
        }

        public void ResetFromStuckDownload()
        {
            Status = LinkStatus.Unlinked;
            UpdatedAt = DateTime.UtcNow;
        }
    }
}