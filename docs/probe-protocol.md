# ArdUi network probe (protocol v1, token v2)

The "网络诊断" page can ask the directory server (M) to take part in the checks that public STUN servers cannot do: M sees what the outside
sees, answers from a second port, echoes and pushes measured traffic, and sends an **unsolicited** packet or opens a **TCP** connection to a
port that the client listens on. That is what proves that a UPnP / NAT-PMP mapping, a public address or an open port really works from the
outside. A router that merely answers the UPnP or NAT-PMP protocol proves nothing; only a packet that M sent from the internet and that
arrived does.

Code: server side `server/arduiserver.py` (`/api/v1/probe/start`) and `server/probe_udp.py`; client side `Core/NetProbe.cs`.
Tests: `tests/probe_test.py` (server), `ArdUi.exe --netprobe-test` (client against the real service), `--netdiag-test`.

## Flow

1. The client calls `POST /api/v1/probe/start` (signed with the device key like every directory call, registered devices only). The reply:
   `{"v":2,"token":"<56 hex>","expires":<unix>,"ip":"<the address the server saw over HTTPS>","host":"...","ports":[7843,7844]}`.
   The token lives 120 seconds.
2. The client talks UDP to `host:ports[0]` and `host:ports[1]`, every datagram carrying the token. Each **source address** must first prove
   itself (below); only then does the service answer anything else from it, or send anything to it.

## Token and address proof

`token = nonce(8) | expiry(4, unix seconds, big endian) | mac(16)`, `mac = HMAC-SHA256(key, "ARDP-token2\0" | nonce | expiry)[:16]`, with a
32-byte secret shared by the directory server and the probe service (`<database>.probe-key`). The token names **no address**: a machine behind
a router with several internet lines (or an address pool) leaves through different public addresses for different connections, so an address
fixed at the HTTPS request would reject its own UDP.

Instead every source address proves it can receive packets (the way QUIC does):

- `HELLO` (type 8) is answered with `cookie(16) = HMAC-SHA256(key, "ARDP-cookie\0" | nonce | address16 | port | time/30s)[:16]`.
- `AUTH` (type 9) carries the cookie back from the same address and port. If it matches (this or the previous 30-second window), that address is
  proven for this token for 180 seconds (at most 6 addresses per token). The answer says whether it worked and which endpoint M saw.

A spoofed source address never receives the cookie, so it can never prove itself. The service only ever sends to addresses that proved
themselves, and a HELLO answer is no larger than the HELLO request.

## Datagram

`"ARDP" | version(1)=1 | type(1) | flags(2)=0 | token(28) | seq(4) | timestamp(8) | body`. Requests are at least 96 bytes (pad with zeros).
Answers carry a zeroed token, the same `seq` and `timestamp`, and `type | 0x80`; an answer is never longer than its request (PUSH excepted).

| type | request body | answer |
|---|---|---|
| 8 HELLO | none | the cookie (16 bytes) |
| 9 AUTH | `cookie(16)` | `ok(1) family(1) addr(16) port(2) 0(2)`: the endpoint M saw |
| 1 BIND | none | `family(1) addr(16) port(2) server_port(2)`: the address and port M saw, and the server port it answered from |
| 2 ECHO | any payload ≤ 1400 | the same payload |
| 3 OTHER | none | like BIND, but sent from the **other** server port (port-restriction test) |
| 4 PUSH | `count(2) size(2) interval_ms(2)` | `count` datagrams of type 0x84 (`seq` = index), at most 300 packets / 300 KB, 3 per token |
| 5 PROBE_UDP | `port(2) from_other(1) nonce(8) [target(16)]` | an unsolicited type-0x90 datagram carrying the nonce, sent to `target` (another address of this token that has proven itself, e.g. the router's WAN address; default: the sender's own address) and `port` (≥ 1024) from the server port (or the other one); the requester gets an acknowledgement (type 0x85, body `nonce port`) |
| 6 CONFIRM | `nonce(8)` | `ok(1) family(1) addr(16) port(2) 0(2)`: whether the nonce was probed, and the endpoint the confirmation came from |
| 7 PROBE_TCP | `port(2) nonce(8) [target(16)]` | M connects to `target` (default: the sender) and `port` (≥ 1024), sends `"ARDPTCP1" | nonce`, reads the nonce back; the UDP answer is `connected(1) acked(1) nonce(8)` |

## What the client concludes

- BIND from both server ports: the same mapped endpoint means a cone NAT (or none); a different port means a symmetric NAT; a different
  **address** means several exits (the machine leaves by more than one line), which is not a NAT type at all. Several sockets (new source ports)
  are proven to collect every exit address.
- OTHER answered: no port filtering (full or address-restricted cone); not answered while BIND works: port-restricted.
- ECHO / PUSH: loss, round trip, jitter, throughput, path MTU (largest datagram that round-trips with "do not fragment").
- PROBE_UDP to a port that was never used: arrives only if the machine is directly on the internet.
- UPnP / NAT-PMP test: ask the router for a mapping, listen on the port, let M probe the mapped port **at the router's WAN address** (sockets are
  tried until one leaves from that address, which proves to M that it is ours), delete the mapping and probe again. The mapping is only credited
  when the packet arrives with it, and the second probe shows whether the port was open without it.

## Abuse protection

- No answer and no packet without a valid token **and** a proven source address; anything else is dropped silently (no reflection).
- Answers are never longer than requests; the only larger output (PUSH) is capped per request and per token and goes to a proven address.
- Unsolicited packets and TCP connections go **only to addresses that proved themselves**, only to ports ≥ 1024, and never to private,
  loopback or reserved addresses.
- Budgets per token (2000 packets, 4 MB out, 3 pushes, 8 UDP probes, 3 TCP probes, 6 proven addresses), a per-address rate limit (300 pps,
  burst 600), a global rate limit, a one-minute ban after 50 bad tokens, at most 10 concurrent TCP probes.
- Token minting is limited per device (12/hour), per address (30/hour) and globally (1500/hour); parameters are not accepted.
- The probe service needs no database or secrets beyond the shared key, runs as the service user with a read-only file system except its data
  directory, and has memory and task limits.
