# Third-party components

ArdUi application logic is implemented in `ArdUi.cs`. The framework-dependent single-file UI contains its managed and native UI dependencies; `ard.exe` is downloaded and verified separately by the installer.

| Component | Version | Source / license |
| --- | --- | --- |
| ARD | 2.0.0-pre.6 | https://github.com/KateY07/ard2 |
| Avalonia | 11.3.13 | https://github.com/AvaloniaUI/Avalonia — MIT |
| NSec.Cryptography | 25.4.0 | https://github.com/ektrah/nsec — MIT |
| ZXing.Net | 0.16.11 | https://github.com/micjahn/ZXing.Net — Apache-2.0 |
| Tmds.DBus.Protocol | 0.21.3 | https://github.com/tmds/Tmds.DBus — MIT |
| SkiaSharp | Avalonia dependency | https://github.com/mono/SkiaSharp — MIT and native Skia notices |

Running ArdUi requires the Microsoft .NET 8 Desktop Runtime. ArdUi does not distribute or call Wintun or tun2socks.
