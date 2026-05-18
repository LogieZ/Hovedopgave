Feature: Synkronisering af manglende filer
    For at sikre at det lokale videoarkiv er komplet
    Som systemadministrator
    Ønkser jeg, at manglende lokale filer automatisk downloades fra YouTube igen

Scenario: Download genstartes hvis en linket fil mangler på disken
    Given a video record exists in the database with status "Linked"
    And the file is missing on disk
    When the synchronization process starts
    Then the YouTube downloader should be triggered for that video

Scenario: Download fejler - status bliver DownloadFailed
    Given a video record exists in the database with status "Linked"
    And the file is missing on disk
    And the YouTube downloader fails
    When the synchronization process starts
    Then the video status should be set to "DownloadFailed"
    And the database should be updated with the new status

Scenario: Flere filer mangler - alle skal downloades
    Given multiple video records exist with status "Linked" and missing files
    When the synchronization process starts
    Then all missing files should trigger a download attempt

Scenario: Fil med null YoutubeId håndteres gracefully
    Given a video record exists with a null LinkedFilePath
    When the synchronization process starts
    Then the video should be skipped gracefully

Scenario: Fil der findes på disk skal ikke downloades igen
    Given a video record exists in the database with status "Linked"
    And the file exists on disk
    When the synchronization process starts
    Then no download should be triggered for that video