# Third-party components

The offline installer carries the framework-dependent UI, official EasyTier runtime, legacy candidate-relay ARD and FRD. Actual component versions and distribution hashes are recorded in the generated components.json. FRD's notices and FFmpeg license are retained in its installation directory.

| Component | Version | Source / license |
| --- | --- | --- |
| EasyTier | See components.json | https://github.com/EasyTier/EasyTier — Apache-2.0; included easytier/LICENSE |
| ARD (legacy candidate relay) | See components.json | https://github.com/KateY07/ard2 |
| Avalonia | 11.3.13 | https://github.com/AvaloniaUI/Avalonia — MIT |
| NSec.Cryptography | 25.4.0 | https://github.com/ektrah/nsec — MIT |
| Tmds.DBus.Protocol | 0.21.3 | https://github.com/tmds/Tmds.DBus — MIT |
| SkiaSharp | Avalonia dependency | https://github.com/mono/SkiaSharp — MIT and native Skia notices |

Running ArdUi requires the Microsoft .NET 8 Desktop Runtime; bundled FRD requires .NET 10. The official EasyTier runtime includes DLL dependencies, including wintun.dll, but ArdUi uses no-TUN mode and does not create or install a virtual network adapter. No tun2socks is used.
