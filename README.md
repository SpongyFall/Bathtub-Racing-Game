# Southern Bathtub Race

**Southern Bathtub Race** was created by Caroline Roberson, Treonna Gardner, Jes Crouch, Jose Landaverde, and Jake Lashley for their SWE Capstone Project during Fall 
Semester 2025 at Kennesaw State University.
In Spring 2026, William Urvan, William Pitts, Joshua Young, and Thomas Powell took hold of the project and added multiplayer for their Senior Capstone.

### About

Beginning in the 1960s and running until the early 1990s, bathtub races were a central part of student life at Southern Polytechnic State University (SPSU) as they 
fostered a healthy blend of creativity and camaraderie from both their students and onlookers. Even after the annual races ended, they have remained a fondly remembered 
part of SPSU.

The goal of this project is to revive and commemorate the bathtub races in the form of a video game. You can view the project website, which contains more information 
about the historical races and the development team [here](https://multiplayertubracinggame.com/).

A google drive with the game build, known bugs, future ideas, and the first teams materials can be found [here](https://drive.google.com/drive/folders/1G7btSucIJ0RPtHZl6mf8qlb88kcH2-EU)

### Hardware Requirements

The game build requires **Windows** with at least **2 GB** of available storage and **4 GB RAM**.

The source code requires **6 GB of available storage space** and **8 GB RAM** to run in Unity.


## 🚀 Quick Start

### Playing the Game
1. Download the latest build [here](https://drive.google.com/file/d/1r9M3KoXAz2_Z7JfARiN0gKSw1jW8X89S/view?usp=drive_link)
2. Extract the folder
3. Run `SPSU Racing Game.exe`

### Source Code Install
1. Download [GitHub Desktop](https://desktop.github.com/download/)
2. Clone the reposity
3. Install [Unity Hub](https://docs.unity.com/en-us/hub/install-hub)
4. Download the Unity version [2023.2.22f1](https://unity.com/releases/editor/archive)
**Warning:** the source code may encounter errors if opened with a different Unity version.

### Running in Unity
1. Open Unity Hub
2. Add project and select Unity version **2023.2.22f1**
3. Open the bootstrap scene: `Assets/Scenes/Bootstrap.unity`
4. Press Play (GONet will sometimes cancel Play and recompile, just wait for the recompile and press Play again)


## 🎮 Controls

### Mouse and Keyboard:
- **AD** to turn.
- **WS** to speed up and slow down.
- **Space** to boost.
- **Shift** to drift (just makes you turn faster).

### Controller:
- **Left Stick** to turn.
- **RT** to speed up, **LT** to slow down.
- **A** (or button south) to boost.
- Press and **hold Left Stick** to drift.


## 🌐 Multiplayer
This project uses **GONet** for multiplayer synchronization.

- Host/Client architecture
- Steamworks.NET lobby integration
- See `/Docs/` for full details


## 📁 Project Structure
- `Assets/` → Unity game assets and scripts
- `Docs/` → Technical documentation (architecture, systems, networking)
- `ProjectSettings/` → Unity project configuration


## 🛠️ Tech Stack
- Unity 2023.2.22f1
- C#
- GONet (multiplayer networking)
- Steamworks.NET