namespace VideoArchiveManager.Models
{
    public sealed class FileRecord
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long Duration { get; set; }
        public long SizeBytes { get; set; }
    }
}