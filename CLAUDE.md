# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**RMVRControllerDemo** — A Meta Quest VR operator console for a RoboMaster competition "Engineer" robot (工程机器人). The operator wears a Quest headset to view game telemetry, monitor a robot camera feed, and control a 7-DOF robotic arm via IK.

- **Platform**: Android (Meta Quest), Unity 2022.3 LTS + URP 14
- **XR Backend**: OpenXR + Meta XR SDK 83.0.0 (OVRInput, OVRPassthroughLayer, Interaction SDK)
- **Key Libraries**: MQTTnet, Google.Protobuf (`RoboMaster` namespace), LibVLCSharp, TextMeshPro

## Build & Development Commands

This is a Unity project — all builds are done through the **Unity Editor** (no CLI build scripts exist).

- **Build APK**: Unity Editor → File → Build Settings → Android → Build
- **Target device**: Meta Quest (Android ARM64)
- **Scene**: `Assets/Scenes/SampleScene.unity` (single scene)

There are no automated tests, lint scripts, or CI configuration in this project.

## Architecture

### Core Data Flow

```
[Quest Controller] ──OVRInput──> EngineerVRInput  ──KeyboardMouseControl──>┐
                                                                            │
[VR Grab Handle] ──Grab events──> DirectGrabController                     │
                                       │ SetTarget()                        │
                                       v                                    │
                              RobotIKController (60fps)                     │
                              (solves J1-J7 angles)                         │
                                       │ GetPacketData()                    │
                                       v                                    │
                              RobotDataSender (50Hz) ──CustomControl──>     │
                                                                    DataManager (Singleton)
                                                                    ├── MQTT broker client
                                                                    ├── Protobuf deserializer
                                                                    └── Game state store
                                                                            │
                              EngineerUIManager / FullCockpitUI / DebugUI ──┘

[Robot Camera] ──UDP:3334──> FreeRMVideoPlayerTCP ──TCP:3335──> HEVCStreamReceiverFixed
                              (frame reassembly)                 (LibVLC → Texture2D)
```

### Key Scripts

**`DataManager.cs`** — Central singleton (DontDestroyOnLoad). Manages the MQTT connection, subscribes to ~24 RoboMaster referee system topics, deserializes Protobuf messages, and maintains the entire game state. All other scripts read from `DataManager.Instance`. Outbound commands: `SendCustomControl`, `SendKeyboardMouseControl`, `SendAssemblyCommand`, `SendCommonCommand`.

**`RobotIKController.cs`** — 7-DOF arm IK solver. **Currently under active development** (see recent commits). Two modes:
- `InverseKinematics`: full IK from a 6D target pose. Arm position uses 2-link geometric cosine law (J2/J3). Wrist orientation uses Y-Z-X-Z Euler decomposition with a discrete 1D search over J4 offsets (−90° to +90° in 10° steps) to minimize a weighted cost function (joint velocity + limit violations).
- `DirectAngleMapping`: VR handle position maps directly to the J4 platform; only wrist angles are IK-solved.
All joints use `SmoothDampAngle`. Output: `outAngles[]` (7 floats, degrees).

**`RobotDataSender.cs`** — Packs `outAngles[]` as 7× `short` (×100 fixed-point, 14 bytes) + gripper byte + checksum into a `CustomControl` Protobuf and sends at 50 Hz.

**`EngineerVRInput.cs`** — Handles chassis driving: left grip + controller rotation → mouse deltas; left thumbstick → WASD bitfield. Reads `ExternalUIMacroMask` (set by `VirtualMacroButton`) and merges into `KeyboardMouseControl` packets.

**`GlobalConfig.cs`** — Static class persisting server IP/port to `PlayerPrefs`. Read by all network code.

### Video Pipeline

Two-hop design: robot sends HEVC over UDP → `FreeRMVideoPlayerTCP` reassembles frames and re-serves on local TCP:3335 → `HEVCStreamReceiverFixed` (LibVLC) decodes and writes to a `Texture2D` on a Quad mesh.

### UI Systems

Two parallel HUD implementations exist:
- **`EngineerUIManager`** — Engineer-robot-specific HUD (HP, assembly state machine, team scores, damage flash)
- **`FullCockpitUI`** — More general cockpit with minimap (28×15m field, robot icons), module status lights

Five curved HUD scripts (`ArcVRHUD`, `CurvedHUD`, `CurvedCanvas`, `CurvedUICanvas`, `CurvedUIMesh`, `CurvedHUDRenderer`) are **experimental prototypes** — not production-finalized.

### Notable Quirks

- **`UdpAnalyzer.cs`** has a standalone `Main()` method — it is a dev diagnostic tool left in Assets. Not a MonoBehaviour; not instantiated in-scene.
- `Scripts_Backup_20260309_154402/` is a manual backup of scripts before the IK refactor — treat as reference only, not active code.
- The `.apk` files in the project root (0.0.1–0.0.7) are build artifacts, not source.
- `SharedInputDefinitions.cs` defines the `KeyboardBitMask` enum used across `EngineerVRInput`, `VirtualMacroButton`, and `MacroKeyboardSpawner` — do not modify without checking all three.

## Active Development Context

The IK solver (`RobotIKController.cs`) is the primary area of current work. The commit history shows iterative changes to the 7-axis wrist decomposition. The `outAngles[]` array is the interface between the solver and `RobotDataSender`/`OffsetTuner`.
