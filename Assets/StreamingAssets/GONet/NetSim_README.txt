================================================================================
                    GONet Network Simulation Launch Scripts
================================================================================

These scripts launch your game with simulated network conditions for testing.
Useful for testing how your game handles latency, jitter, and packet loss.

IMPORTANT: Network simulation only works in Development builds!
(The feature compiles out of Release builds for zero overhead)

NOTE: GONet auto-detects whether each instance should be server or client,
so you only need ONE script per network condition. Launch multiple instances
and the first becomes server, subsequent ones become clients automatically.

--------------------------------------------------------------------------------
                              SETUP INSTRUCTIONS
--------------------------------------------------------------------------------

WINDOWS:
  1. Copy the NetSim_*_BAT.txt files to your build directory
  2. Rename: Change _BAT.txt to .bat (e.g., NetSim_Internet_BAT.txt -> NetSim_Internet.bat)
  3. Edit the .bat file to change "GONetSandbox.exe" to your game's executable name
  4. Double-click the .bat file to launch with that network condition
  5. Double-click again to launch additional instances (auto-detect handles roles)

MAC/LINUX:
  1. Copy the NetSim_*_SH.txt files to your build directory
  2. Rename: Change _SH.txt to .sh (e.g., NetSim_Internet_SH.txt -> NetSim_Internet.sh)
  3. Edit the .sh file to change "./GONetSandbox" to your game's executable name
  4. Make executable: chmod +x NetSim_Internet.sh
  5. Run: ./NetSim_Internet.sh (run multiple times for multiple instances)

--------------------------------------------------------------------------------
                              AVAILABLE PRESETS
--------------------------------------------------------------------------------

  Preset        | Latency | Jitter | Loss  | Description
  --------------|---------|--------|-------|----------------------------------
  LAN           |    2ms  |   1ms  |  0.0% | Local network, no issues
  GoodWiFi      |   15ms  |   5ms  |  0.5% | Stable home WiFi
  Internet      |   50ms  |  15ms  |  1.0% | Typical internet connection
  PoorWiFi      |  100ms  |  50ms  |  5.0% | Congested/weak WiFi
  BadNetwork    |  200ms  | 100ms  | 10.0% | Mobile/satellite/very poor

--------------------------------------------------------------------------------
                           CUSTOM CONFIGURATIONS
--------------------------------------------------------------------------------

You can also specify individual parameters:

  -netsim              Enable network simulation
  -latency [ms]        One-way latency in milliseconds (0-500)
  -jitter [ms]         Random latency variance +/- milliseconds (0-200)
  -loss [percent]      Packet loss percentage (0-20)
  -duplicate [percent] Packet duplication percentage (0-10)
  -netsim-preset [name] Use a preset (lan, goodwifi, internet, poorwifi, badnetwork)

Examples:
  MyGame.exe -netsim-preset internet
  MyGame.exe -netsim -latency 75 -jitter 25 -loss 3
  MyGame.exe -netsim-preset poorwifi -loss 8

Note: Individual parameters override preset values when both are specified.

--------------------------------------------------------------------------------
                              TIPS FOR TESTING
--------------------------------------------------------------------------------

1. Launch MULTIPLE instances with the same script - auto-detect handles roles

2. Start with presets, then fine-tune with individual parameters

3. For competitive games, test with "Internet" preset as baseline

4. For mobile games, test with "PoorWiFi" and "BadNetwork" presets

5. Monitor logs for "[NetworkSimulation]" entries to verify configuration

6. Works with Lobby flow - command-line args apply when GONetGlobal is created

================================================================================
