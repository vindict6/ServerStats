This plugin outputs all major match and server data through rcon command css_players

Run the plugin once to generate default configs. The plugin will re-read from workshop.ini if it is changed while the
server is already running.

In the workshop.ini config (/steam/cs2/game/csgo/addons/counterstrikesharp/configs/plugins/ServerStats/workshop.ini), you must
set the collection ID as well as link map names (output in the css_players readout) to their respective
workshop ID. If the config.ini file contains the line "UsesMatchLibrarian=true", the plugin will generate a database of last 
round data when each round ends. The database will be stored in game/csgo/addons/counterstrikesharp/configs/plugins/MatchLibrarian
for use with the MatchLibrarian plugin, which manages communicating match history with an external webpage for browsing purposes.
