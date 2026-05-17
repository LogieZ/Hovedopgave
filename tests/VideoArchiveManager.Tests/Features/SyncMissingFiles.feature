Feature: Synkronisering af manglende filer
    For at sikre at det lokale videoarkiv er komplet
    Som systemadministrator
    Ønkser jeg, at manglende lokale filer automatisk downloades fra YouTube igen

Scenario: Download genstartes hvis en linket fil mangler på disken
    Given a video record exists in the database with status "Linked"
    And the file is missing on disk
    When the synchronization process starts
    Then the YouTube downloader should be triggered for that video