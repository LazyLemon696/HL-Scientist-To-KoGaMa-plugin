# Half-Life scientist for OpenKogama

A server plugin (`IPlugin`, net10.0) that puts the Half-Life scientist into KoGaMa as an NPC.
`!scientist` spawns one next to you. Four head variants like the original: `glasses`, `einstein`, `luther`, `slick`.

## Install

1. Keep this folder anywhere, for example next to `openkogama-server-main`.
2. Build it. `ServerRoot` is the folder that contains the server's `api` folder, `PluginsDir` is optional and copies the dll for you:

       dotnet build -c Release -p:ServerRoot=C:\path\to\openkogama-server-main -p:PluginsDir=C:\path\to\server\plugins

   Without `PluginsDir`, copy `bin\Release\net10.0\scientist.dll` into the server's `plugins` folder yourself.
   Do not copy `OpenKogama.Api.dll` along with it.
3. Restart the server, or type `!reload` as an admin. Then type `!scientist`.

## Using him

| You do | He does |
|---|---|
| `!scientist [glasses\|einstein\|luther\|slick]` | spawns next to you (up to 10) |
| `!scientist remove` / `!scientist clear` (admins) | removes the nearest / all |
| wave at him (or `!scientist follow`) | follows you, wave again to make him wait |
| nod at him (or `!scientist heal`) | treats you |
| shake your head | he stays where he is |
| `!scientist hurt [amount]` | hurts you, so you can test the healing |
| walk into him | turns to face you; if you were running he steps out of your way |
| hurt him | `[Scientist]: YourName, WHAT ARE YOU DOING!`, runs for cover screaming, hides, remembers you for 90 s |

## How it maps to the original (scientist.cpp / talkmonster.cpp)

- Greets a player the first time he sees them, makes idle remarks, asks a colleague a question that the colleague answers, asks "can I help you?" when stared at.
- Hurt: remembers who did it, screams while running to a spot out of sight (line-of-sight checks), crouches there, runs again if the attacker shows up, calms down after 15 s without seeing them. No cover available: panics.
- Gunfire or a kill nearby makes him stop and jump. A friend being shot makes colleagues say "stop shooting him".
- Heals a player at or below half health, walks up, says a line, takes out the needle (he holds the Health item), then drops a health pickup on the player. One minute between treatments, refuses to treat whoever hurt him. Before treating he comments on how bad it looks (at half, a quarter and an eighth of the player's health, once each, like the original); when the minute is not up yet that comment is all you get.
- Moans when he is wounded (75 %, 50 %), dies with a scream, respawns at his spawn point after 20 s.
- Wanders around his spawn point (up to 60 blocks), looks around, paces, watches players.

## Player health

The stock plugin API cannot tell a plugin how much health a player has. Two options:

- **Without the patch**: the plugin estimates (100 at join/respawn, minus 18 per player-vs-player hit, 100 again after a health pickup or treatment). Nodding at him always treats you.
- **With `server-health.patch`** (a few added lines in `api/IPlayer.cs` and `Plugins/PluginPlayer.cs`): `IPlayer.Health` returns the health the client reported, and the plugin finds it by itself. Apply with `patch -p1 < server-health.patch` inside `openkogama-server-main`, rebuild the server. The plugin works with either server build.

Made by LazyLemon. Not affiliated with Valve.
