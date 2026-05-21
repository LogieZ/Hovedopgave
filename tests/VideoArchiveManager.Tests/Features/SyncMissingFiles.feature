Feature: Synkronisering af manglende filer
    For at sikre at det lokale videoarkiv er komplet
    Som systemadministrator
    Ønsker jeg, at manglende lokale filer markeres korrekt i databasen

Scenario: Linket fil markeres som Missing hvis den mangler på disken
    Given a video record exists in the database with status "Linked"
    And the file is missing on disk
    When the synchronization process starts
    Then the database entry should be marked as "Missing"