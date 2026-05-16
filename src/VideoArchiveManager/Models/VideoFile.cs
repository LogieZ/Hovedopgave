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
        // Aboslute path to the linked local file, or null if not yet linked
        public string? LinkedFilePath { get; set; }

        public DateTime? LastVerified { get; set; }

        public long? FileSizeBytes { get; set; }

        public LinkStatus Status { get; set; } = LinkStatus.Unlinked;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}