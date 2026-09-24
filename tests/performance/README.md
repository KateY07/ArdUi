# Local performance comparison

This console harness links the current production `Core`, `Transport`, `Overlay`, `TransitClient`, and `Frd` source files without changing their behavior. It starts no FRD, RDP, or SMB application. All identities are created beneath a unique test directory. ARD and directory URLs must be loopback addresses.

Build with cached dependencies only:

```powershell
dotnet build tests/performance/Performance.csproj -c Release --configfile tests/performance/NuGet.Config -p:NuGetAudit=false
```

Run through `tests/performance_fixture.py`, which supplies isolated local services and the `ARDUI_TEST_*` environment. The harness accepts:

```text
--mode raw|ard|overlay|full|all --seconds 8 --samples 1000 --output result.json
```

The default runs all four modes sequentially against the same TCP/UDP receiver. `raw` uses loopback sockets; `ard` uses actual ARD open/forward; `overlay` uses the production Gateway/Overlay directly over loopback; `full` uses actual Engine, DirectoryClient, ARD, Gateway, Overlay, and LocalForwarder. Normal production background metrics remain enabled in `full`.

Use a comma list such as `--mode full,ard,raw` to select and order a repeat. Each row records measurement start/end UTC. Before/after business PIDs and tunnel IDs must match; any new same-tunnel relay selection, selected-path closure, or lost path-log event fails the run. Full mode also checks that both production sessions retain their original ARD process instances.

Each mode measures 64-byte TCP application echo on one established connection and 64/1200-byte UDP echo. Each test discards 50 warmups and reports nearest-rank p50/p95/p99 over successful samples, with UDP timeouts counted separately. TCP_NODELAY is set on the test sender and receiver; production forwarding socket settings are unchanged. These are application round trips, not TCP connect timings.

TCP upload and download each send for eight seconds by default. The receiver counts complete payload bytes; an end marker and explicit count acknowledgement drain queued data. Mbps uses receiver-confirmed bytes and the complete measured elapsed time including drain/acknowledgement, never merely accepted writes. These bulk intervals verify counts, not every payload byte; use the integrity regression separately. JSON also records the actual interval, target, entry, process CPU seconds, GC allocation/collection deltas, and overlay snapshots. `cpuSeconds` includes both in-process benchmark endpoints and linked C# code; `ardCpuSeconds` sums owned ARD children; `totalCpuSeconds` adds both, excluding directory/Relay. Compare CPU and allocation per receiver-confirmed payload byte, not raw totals when throughput differs.

ARD business tunnel IDs come from actual TCP acceptance or exposure-ready events. Only selected-path events or CONNECTED summaries for that same tunnel prove direct transport; helper connections do not qualify. Before/after evidence and complete captured ARD logs are retained. Missing direct proof or a selected non-base overlay path makes the run fail. UDP fixed-rate throughput/loss loading is not part of this initial harness.

`--loaded` adds a separate eight-second TCP download with concurrent 1200-byte UDP echoes. Probes are serial, wait 10 ms after each reply, and time out after two seconds; this is not fixed-rate sampling. Report successful RTT percentiles together with timeouts/requested. A timeout suppresses subsequent sampling, so these counts are not a network packet-loss rate.

All endpoints run on the same computer. ARD may select a loopback address or that computer's interface address; direct-path proof does not pin the address. Preserve the actual selected addresses when comparing variants. These figures measure local processing capacity, not WAN throughput or WAN speedup.

For isolated experiments, `ProductionSourceRoot` selects generated source copies. The fixture records source/binary hashes and requires an explicit SHA-256 for an alternate ARD executable. `tests/optimization/run_round1.py` and `--confirm` run variants sequentially; generated artifacts stay under `dist/optimization-round1`. Production binaries and the locked project READMEs are not replaced.
