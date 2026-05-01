# Steamworks Lobby System

## Scope

`Assets/Scripts/Managers/SteamManager.cs` handles Steam lobby presence and social UI. It does not run the race networking itself.

Responsibilities:

- Create/join/leave lobbies
- Keep current lobby ID
- Publish lobby metadata
- Track lobby membership
- Lobby chat
- Persona name/avatar refresh
- "Join random lobby" polling
- Kick signaling

## Core state

Important static state:

- `LobbyId`
- `InSteamLobby`
- `IsLobbyOwner`
- `LastMatchListLobbyIds`

Lobby member order is normalized by `GetLobbyPlayerIds()`, which sorts players by Steam ID. That sorted order is later used as deterministic spawn-slot order in the race.

## Lobby metadata contract

The project currently relies on these Steam lobby data keys:

- `hostSteamId`
- `state`
- `kickedPlayerId`

Current meanings:

- `hostSteamId`: Steam ID string clients use to connect to host over GONet Steam transport
- `state = "starting"`: host has started the game and clients should connect to GONet
- `kickedPlayerId`: soft kick signal checked by all members

If these strings change, both Steam and GONet join flow break.

## Event/callback flow

`SteamManager.OrderedAwake()` wires Steamworks callbacks for:

- lobby creation
- lobby join requests
- lobby entry
- lobby kicked/disconnected
- lobby chat membership updates
- persona changes
- avatar image load
- lobby chat messages
- lobby metadata updates

Game UI mostly reacts through `SteamManager` events rather than talking to Steamworks directly.

## Kicking

There is no direct authoritative Steam "kick player from lobby and explain why" implementation here.

Current behavior:

1. Host sets `kickedPlayerId` in lobby metadata.
2. Every client receives `OnLobbyDataUpdate`.
3. If the local Steam ID matches that value, the client leaves the lobby.
4. If currently connected to GONet, the client also disconnects from GONet.

`NetworkManager.KickGONetPlayer()` does both halves:

- disconnects the client from GONet by authority ID
- calls `SteamManager.KickPlayer()` so the Steam-side lobby also removes them

## Chat and avatars

Lobby chat uses Steam lobby chat messages and is echoed back to sender through the same callback path.

Avatar flow:

- `RequestPlayerInfo()` calls `SteamFriends.RequestUserInformation`
- `PersonaStateChange` then requests large avatars
- `AvatarImageLoaded` builds `Texture2D` and `Sprite`
- Sprites are cached by Steam ID

The cache replaces old sprite/texture instances when refreshed, so UI should not hold long-lived assumptions about sprite identity.

## Random join behavior

`StartJoiningRandomLobby()` starts a coroutine that:

- requests a lobby list
- waits 4 seconds
- repeats until in a lobby

`LobbyMatchList()` picks a random lobby from the returned list when random-join mode is active.

## App ID note

The checked-in project currently uses Steam App ID `480` in:

- `steam_appid.txt`
- the `GONetSteamManager` instance in `Bootstrap`

GONet source comments reference `4168160`, but the project configuration in this repo is currently `480`. Treat the live project config as authoritative unless the Steam setup is intentionally being migrated.
