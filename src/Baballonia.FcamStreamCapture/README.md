# FCAM UDP Stream capture

A capture module that receives face camera frames over the network with the
FCAM protocol ([PROTOCOL.md](PROTOCOL.md)). It exists for trackers that are
plugged into a device other than the PC running Baballonia, for example a
Babble tracker on the USB-C port of a Steam Frame, where a small bridge on the
headset forwards the tracker's JPEG stream over Wi-Fi.

## Use

Enter the sender's address as the camera address and start the camera:

```
fcam://<sender-ip>:8555
```

The preferred backend shows **FCAM UDP Stream**. Baballonia subscribes to the
sender once per second and receives the frames as replies, so no firewall rule
is needed on the PC. `fcam://:8555` listens instead, for senders that push to a
fixed address; that mode needs an inbound UDP rule.

## Debugging

`tools/FcamProbe` receives a stream with this module and prints frame
statistics, without starting the app:

```
dotnet run --project tools/FcamProbe -- fcam://<sender-ip>:8555 10 --save frame.jpg
```

The unit tests are in `src/Baballonia.Tests/FcamStreamCaptureTests.cs`
(`dotnet test src/Baballonia.Tests --filter Fcam`).
