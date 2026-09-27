![Baballonia Promo](BaballoniaPromo.png)

# Baballonia

**Baballonia** is a cross-platform, hardware-agnostic XR eye and face tracking application.

## Installation

### Steam

Baballonia is now on Steam! [Download it here!](https://store.steampowered.com/app/4091970/Project_Babble_Baballonia/)

*This is the suggested install method. You will automatically recieve updates with this method.*

### Windows (Alternative)

Head to the releases tab and [download the latest installer](https://github.com/Project-Babble/Baballonia/releases/latest).

You may be prompted to download the .NET runtime for desktop apps, install it if need be.

### Linux (Alternative)

Head to the releases tab and [download the latest tarball](https://github.com/Project-Babble/Baballonia/releases/latest).

You may be prompted to download the .NET runtime for desktop apps, install it if need be.

### MacOS

Baballonia currently does not have an installer for MacOS. You will need to follow our build instructions and run it from source.

## Platform Compatibility

To get started, follow the [quickstart guide on our documentation page](https://docs.babble.diy/docs/babbleofficaltracker).

### VRChat

#### VRCFaceTracking

To use Baballonia with VRChat, you will need to use VRCFaceTracking with the `VRCFT-Babble` module.

1. Download and install the latest version of VRCFaceTracking from [Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/).
1. Install the `VRCFT-Babble` module within VRCFaceTracking.
1. Use Baballonia to set the module mode (eyes, face or both). Restart VRCFaceTracking to see your changes.

More information can be found on the [VRCFT Docs](https://docs.vrcft.io/docs/vrcft-software/vrcft\#module-registry)

#### VRC Native Eyelook

Alternatively, Baballonia also supports [VRC Native Eyelook](https://docs.vrchat.com/docs/osc-eye-tracking).

While this doesn't support lower face tracking, it supports (almost) all VRChat Avatars.

### Resonite 

Resonite works natively with Baballonia's eye and face tracking - no external tools necessary!

### ChilloutVR

ChilloutVR has a mod-based integration for Baballonia's eye and face tracking. Please refer to our docs (below) for installation.

*For more game information and setup, refer to our [ChilloutVR documentation page](https://docs.babble.diy/docs/software/integrations/chilloutVR).*

## Supported Hardware

Baballonia supports many kinds of hardware for eye and face tracking:

| Device                            | Eyes | Face | Notes                                                                                        |
|-----------------------------------| ----- | ----- |-------------------------------------------------------------------------------------------|
| Official Babble Face Tracker      | :x: | ✅ |                                                                                                |
| DIY and 3rd party Babble Trackers | :x: | ✅ |                                                                                                |
| Vive Facial Tracker               | :x: | ✅ |                                                                                                |
| DIY EyetrackVR                    | ✅ | :x: |                                                                                                |
| Bigscreen Beyond 2E               | ✅ | :x: | *On Linux*, requires [go-bsb-cams](https://github.com/LilliaElaine/go-bsb-cams)                |
| Vive Pro Eye                      | ✅ | :x: | Requires [Revision](https://github.com/Blue-Doggo/ReVision)                                    |
| Varjo Aero                        | ✅ | :x: | Requires the [Varjo Streamer](https://docs.babble.diy/docs/software/baballonia/varjo-streamer) |
| HP Reverb G2 Omnicept             | ✅ | :x: | Requires [BrokenEye](https://github.com/ghostiam/BrokenEye)                                    |
| Pimax Crystal                     | ✅ | :x: | Requires [BrokenEye](https://github.com/ghostiam/BrokenEye)                                    |
| Babble tracker on another device  | :x: | ✅ | Over the network with [FCAM](#network-face-cameras-fcam), e.g. on a Steam Frame                            |

*For more hardware information, refer to our [documentation pages](https://docs.babble.diy/docs/intro).*

## Network face cameras (FCAM)

Baballonia can receive a face camera over the network. This is for trackers that are plugged into another device than the PC running Baballonia, for example a Babble tracker on the USB-C port of a Steam Frame, whose stock kernel cannot use it as a camera. A small sender on that device reads the tracker and streams its JPEG frames to Baballonia over UDP with the FCAM protocol. FCAM is available in the desktop app (Windows, Linux and macOS).

### Setting it up

1. Start a sender on the device the tracker is plugged into:
   - For a Babble tracker on a Steam Frame, [Babble-Bridge](https://github.com/CyrusOtter/Babble-Bridge) runs as a SteamVR overlay on the headset. Its dashboard tab shows the address to enter below.
   - Any other program that implements [the protocol](src/Baballonia.FcamStreamCapture/PROTOCOL.md) works as well.
2. In Baballonia, go to the **Home** page and enter the sender's address under **Face Camera Address**, for example:
   ```
   fcam://192.0.2.10:8555
   ```
   Use the sender's IP address or host name. 8555 is the default port and can be left out.
3. A drop-down labelled **This is a...** appears below the address. Make sure it shows **FCAM UDP Stream**: other backends accept this address too, so select it if something else is shown.
4. Press **Start Camera**. The preview appears within a few seconds. Crop and calibrate as you would with a USB tracker.

If the connection drops or the sender restarts, Baballonia reconnects on its own. The preview pauses and resumes once the sender answers again; there is no need to restart the camera.

### Camera addresses

| Address | What it does |
|---|---|
| `fcam://192.0.2.10:8555` | Subscribes to the sender at that address and receives its frames. Recommended. |
| `fcam://192.0.2.10` | The same, on the default port 8555. |
| `fcam://headset.local:8555` | Host names work too; they are resolved when the camera starts. |
| `fcam://[fd00::10]:8555` | IPv6 addresses go in square brackets. |
| `fcam://:8555` | Listens on UDP 8555 for a sender that pushes frames to this PC instead. Needs a firewall rule, see below. |

### Network and firewall

- **No firewall rule is needed on the PC** for the recommended subscribe mode. Baballonia sends a small subscribe message to the sender every second, and the frames come back as replies to it.
- The sender must be reachable on UDP port 8555 from the PC. Both devices are usually on the same network; Wi-Fi is fine, since a tracker needs only about 0.1 to 0.4 MB/s.
- Listen-only mode (`fcam://:8555`) does need an inbound rule. On Windows, in an administrator PowerShell:
  ```
  New-NetFirewallRule -DisplayName "Baballonia FCAM" -Direction Inbound -Protocol UDP -LocalPort 8555 -Action Allow
  ```

### Troubleshooting

Baballonia logs every FCAM step with the prefix `FCAM:`. The log is on the **Output** page, which also opens the log folder. Set **Log Level Verbosity** to Debug on the **App Settings** page to also see frame statistics every 10 seconds.

| What you see | What to check |
|---|---|
| No preview, and the log shows `FCAM: subscribing to bridge …` but nothing after it | The sender cannot be reached or is not running: check its address, port and firewall, and that both devices are on the same network. |
| `FCAM: bridge … is no-source` | The sender is running but has no tracker: check that the tracker is plugged in and that the device has a driver for it. |
| `FCAM: bridge … is streaming`, but no `FCAM: receiving …` line | Status messages arrive but frames do not. Frames use datagrams of about 1.4 KB; a path with a smaller MTU (some VPNs) drops them. Lower the sender's chunk size or connect directly. |
| The camera fails to start with a different backend | Select **FCAM UDP Stream** in the drop-down below the address (step 3). |
| Stutter | Weak Wi-Fi. The debug statistics show dropped frames. |
| `FCAM: no data from bridge … reconnecting until it answers` | The sender stopped answering, for example because the headset went to sleep, Wi-Fi dropped or the bridge restarted. Baballonia keeps trying and resumes on its own; `FCAM: bridge … answers again` marks the recovery. |

To test a sender without the app, `tools/FcamProbe` receives its stream with the same capture module and prints the frame rate:

```
dotnet run --project tools/FcamProbe -- fcam://192.0.2.10:8555 10 --save frame.jpg
```

To write your own sender, see [PROTOCOL.md](src/Baballonia.FcamStreamCapture/PROTOCOL.md).

---

## Build Instructions

### Baballonia.Desktop

1. Run the associated ``download_dependencies`` script for your given platform (``.ps1`` on Windows, ``.sh`` on Linux).
2. If you are using an IDE, disable these projects:
- `VRCFaceTracking`
- `VRCFaceTracking.Core`
- `VRCFaceTracking.SDK`
- `VRCFaceTracking.Baballonia`
- `Baballonia.iOS`
- `Baballonia.Android`
3. Run ``dotnet build`` inside the ``src/Baballonia.Desktop`` directory, or build with your IDE

#### Publishing

If you want to publish a standalone installer for Baballonia, download [NSIS](https://github.com/negrutiu/nsis) here, or use your package manager. Then, run the 
`.nsi` script located at `src/Baballonia.Desktop/main.nsi`

### Baballonia.Android/iOS

1. If you are using an IDE, disable these projects:
- `VRCFaceTracking`
- `VRCFaceTracking.Core`
- `VRCFaceTracking.SDK`
- `VRCFaceTracking.Baballonia`
- `Baballonia.Desktop`
- `Baballonia.iOS`, if you are building for Android
- `Baballonia.Android`, if you are building for iOS
2. Run ``dotnet build`` inside the ``src/Baballonia.Android`` or ``src/Baballonia.iOS`` directory, or build with your IDE

### VRCFaceTracking.Baballonia

1. If you are using an IDE, disable all projects except the following:
- `VRCFaceTracking.Core`
- `VRCFaceTracking.SDK`
- `VRCFaceTracking.Baballonia`
2. Run ``dotnet build`` inside the ``src/VRCFaceTracking.Baballonia`` directory, or build with your IDE

This will create a `VRCFaceTracking.Baballonia.zip` module which you can install manually.

*For more build information, refer to our [build documentation page](https://docs.babble.diy/docs/software/integrations).*
