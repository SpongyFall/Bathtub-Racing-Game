# Systems Handoff

This folder documents the project-specific systems that are easy to break or hard to infer from the scene hierarchy alone.

Read these first:

- `Bootstrap-And-Lifecycle.md`: persistent managers, startup order, scene flow.
- `InputManager.md`: shared input state and controller/UI selection behavior.
- `MainMenu-And-Lobby-Flow.md`: panel stack, Steam lobby UI flow, controller/mouse interaction.
- `Steamworks-Lobby-System.md`: Steam lobby ownership, chat, join flow, kick flow.
- `GONet-Session-Flow.md`: how Steam lobby state turns into a GONet game session.
- `RaceManager-Racetrack.md`: human kart spawn, ready handshake, race start/end logic.
- `Waypoint-And-Placement-System.md`: waypoint progression, lap counting, and racer ordering.
- `Customization-Save-Load.md`: kart cosmetics persistence and when cosmetics are applied.

Important runtime split:

- Steam lobby state is handled by `SteamManager`.
- Actual in-race networking is handled by `NetworkManager` + GONet.
- Cosmetic persistence is local JSON, not Steam-backed and not network-authoritative.
