<!--Modular
C# Project located within ~/.gitrepos/
Things to Fix:

1) GameBanana Backend is present in NexusMods Panel. Re-Locate to GameBanana Panel and add it as default backend.

2) Search Feature for GameBanana has yet to be implemented, currently displays placeholders.

3) Searching for Mods through GameBanana Backend produces empty results. Searching for mods should fuzzy match entries provided in a similar/same manner to NexusMods implementation.

4) Profiles Panel has 0 usable functionality in it's current form. It should also be a sub-menu item within settings which when pressed would open to a new page within the same window with an arrow button indicating 'back' for when finished. Within this new page would be a create button to create and remove profiles for per game use cases like differing loadouts/mods required between single player use vs multiplayer.

5) Collections is currently unusable. It requires the ability to query for collections online on a per backend selection. It must also provide a way to download collections and perform installations for collection mods into per game directories in a manner that simulates actual installation to steam game installation directories, but for the sake of storage as a collection record for future deployment when desired. up to 3 collections per game should be allowed collection record storage. Collections are primarly a NexusMods feature so Collections should be a NexusMods Panel Tab instead of the Backups Panel.

6) A File Picker should be the preferred way for browsing to exported profiles for importing of profiles.

7) Plugins is a Developer component and should be hidden somewhere within the settings panel.
 
 -->


1) GameBanana Search should provide searchable entries based on GameDomain and a fuzzy match of the Name for which mod being searched for. Currently searching anything results in the same unfiltered listing of mods unrelated to the single search query

2) NexusMods Collections should use the API to search for and list available collections corresponding with the GameDomain id, it should provide the name, description, total number of mods, and if possible a list of dependencies/requirements, it should also provide the option to download selected collections.

"It must also provide a way to download collections and perform installations for collection mods into per game directories in a manner that simulates actual installation to steam game installation directories, but for the sake of storage as a collection record for future deployment when desired. up to 3 collections per game should be allowed collection record storage. Collections are primarly a NexusMods feature so Collections should be a NexusMods Panel Tab instead of the Backups Panel."

Consult relevant API Documentation for GameBanana and NexusMods when needed.
