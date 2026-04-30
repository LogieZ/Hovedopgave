namespace VideoArchiveManager.Models
{
    public sealed class FileRecord
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public long Duration { get; set; }
        public long SizeBytes { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public DateTime CreationTimeUtc { get; set; }
        public string? YoutubeId { get; set; }
    }
}