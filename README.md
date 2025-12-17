This CS2 plugin outputs all major match and server data through rcon command css_players

Run the plugin once to generate default configs. The plugin will re-read from workshop.ini if it is changed while the
server is already running. Use the css_databaseon to see the status of match json database output.

In the config.ini (/steam/cs2/game/csgo/addons/counterstrikesharp/configs/plugins/ServerStats/config.ini), you must
set the collection ID for the server if you want workshop IDs to show in the css_players output and the json database.
A workshop.ini will be generated upon server startup that will read all of the vpk map files and associate them with
their respective workshop IDs. You must have them all downloaded to your CS2 server before this will work.
"UsesMatchLibrarian=true" means the plugin will generate a json database of last round data when each round ends. It
will also store a kill feed, objective data, and chat history with both team and all chat differentiation.
The database will be stored in game/csgo/addons/counterstrikesharp/configs/plugins/MatchLibrarian for use with the 
MatchLibrarian plugin, which facilitates communicating match history with an external webpage for historical purposes.

Note:
If a player(s) is in spectate, and nobody else is playing, the plugin will kick the player(s) in 30 seconds to prevent 
a malformed match json in the event that bots are enabled. Also, if the last player in a match leaves the match, the 
game will restart when a new player joins. This prevents match timelines that are too long.
