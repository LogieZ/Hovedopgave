namespace VideoArchiveManager.Models
{
    public enum LinkStatus
    {
        Unlinked = 0, // No local file has been associated with this entry yet
        Linked = 1, // A local file has been found and linked to this entry
        Missing = 2, // The entry was previously linked but the file is now missing
        Downloading = 3, // The file is currently being downloaded from YouTube
        DownloadFailed = 4, // The download from YouTube failed
    }
}