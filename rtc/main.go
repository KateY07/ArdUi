// ardrtc: the WebRTC link of ArdUi as a small child process (like ard.exe).
//
// It sets up one WebRTC peer connection with two negotiated data channels and bridges them to loopback:
//   - "stream" (id 0, reliable, ordered)  <->  one local TCP connection (a byte stream)
//   - "dgram"  (id 1, unordered, no retransmits) is opened and reported; the UDP bridge comes later
//
// It authenticates and encrypts nothing itself beyond what WebRTC does (DTLS): ArdUi runs its own signed, end-to-end encrypted
// handshake over the local TCP connection. Everything the child does is one-time: one remote description, one local connection, and
// the process ends with the link.
//
// Control (JSON lines): stdin <- {"cmd":"offer"|"answer","sdp":"..."}; stdout -> {"event":...}.
package main

import (
	"bufio"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/ice/v4"
	"github.com/pion/webrtc/v4"
)

const chunk = 16000

var (
	outMu sync.Mutex
	out   = json.NewEncoder(os.Stdout)
)

func emit(e map[string]any) {
	outMu.Lock()
	defer outMu.Unlock()
	_ = out.Encode(e)
}

func fail(message string) {
	emit(map[string]any{"event": "error", "message": message})
	emit(map[string]any{"event": "closed"})
	os.Exit(1)
}

func main() {
	role := flag.String("role", "", "host or join")
	capHex := flag.String("cap", "", "32 hex digits: the capability the local connection must present")
	stun := flag.String("stun", "", "comma separated STUN urls (stun:host:port)")
	turn := flag.String("turn", "", "TURN url")
	turnUser := flag.String("turn-user", "", "TURN user")
	turnPass := flag.String("turn-pass", "", "TURN credential")
	loopback := flag.Bool("loopback", false, "also use loopback candidates (tests)")
	wait := flag.Duration("wait", 90*time.Second, "give up when the link is not open by then")
	flag.Parse()
	capability, err := hex.DecodeString(*capHex)
	if err != nil || len(capability) != 16 || (*role != "host" && *role != "join") {
		fail("usage: ardrtc --role host|join --cap <32 hex digits> [--stun url,url] [--loopback]")
	}

	settings := webrtc.SettingEngine{}
	settings.SetICEMulticastDNSMode(ice.MulticastDNSModeDisabled)
	settings.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4, webrtc.NetworkTypeUDP6})
	settings.SetIncludeLoopbackCandidate(*loopback)
	settings.SetICETimeouts(6*time.Second, 20*time.Second, 1500*time.Millisecond)
	api := webrtc.NewAPI(webrtc.WithSettingEngine(settings))

	var servers []webrtc.ICEServer
	for _, url := range strings.Split(*stun, ",") {
		if url = strings.TrimSpace(url); url != "" {
			servers = append(servers, webrtc.ICEServer{URLs: []string{url}})
		}
	}
	if *turn != "" {
		servers = append(servers, webrtc.ICEServer{URLs: []string{*turn}, Username: *turnUser, Credential: *turnPass})
	}
	pc, err := api.NewPeerConnection(webrtc.Configuration{ICEServers: servers})
	if err != nil {
		fail("peer connection: " + err.Error())
	}

	yes, no := true, false
	zero, id0, id1 := uint16(0), uint16(0), uint16(1)
	stream, err := pc.CreateDataChannel("stream", &webrtc.DataChannelInit{Negotiated: &yes, ID: &id0})
	if err != nil {
		fail("stream channel: " + err.Error())
	}
	dgram, err := pc.CreateDataChannel("dgram", &webrtc.DataChannelInit{Negotiated: &yes, ID: &id1, Ordered: &no, MaxRetransmits: &zero})
	if err != nil {
		fail("dgram channel: " + err.Error())
	}

	var closing atomic.Bool
	finish := func(reason string) {
		if closing.Swap(true) {
			return
		}
		emit(map[string]any{"event": "closed", "reason": reason})
		_ = pc.Close()
		time.Sleep(50 * time.Millisecond)
		os.Exit(0)
	}

	pc.OnICEConnectionStateChange(func(state webrtc.ICEConnectionState) {
		emit(map[string]any{"event": "state", "ice": state.String()})
		switch state {
		case webrtc.ICEConnectionStateFailed, webrtc.ICEConnectionStateClosed:
			finish("ice " + state.String())
		}
	})
	pc.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
		if state == webrtc.PeerConnectionStateFailed || state == webrtc.PeerConnectionStateClosed {
			finish("connection " + state.String())
		}
	})
	dgram.OnOpen(func() { emit(map[string]any{"event": "dgram", "open": true}) })

	// ---- the local side: one TCP connection, bridged to the reliable channel --------------------------------------------------
	inbound := make(chan []byte, 4096) // what arrives before the local connection exists waits here
	var (
		opened    atomic.Bool
		lowWater  = make(chan struct{}, 1)
		listening sync.Once
	)
	stream.SetBufferedAmountLowThreshold(512 * 1024)
	stream.OnBufferedAmountLow(func() {
		select {
		case lowWater <- struct{}{}:
		default:
		}
	})
	stream.OnMessage(func(message webrtc.DataChannelMessage) {
		data := append([]byte(nil), message.Data...)
		select {
		case inbound <- data:
		default:
			finish("inbound overflow")
		}
	})
	stream.OnClose(func() { finish("stream closed") })

	serveLocal := func() {
		listener, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			fail("listen: " + err.Error())
		}
		emit(map[string]any{"event": "ready", "tcp": listener.Addr().(*net.TCPAddr).Port})
		go func() {
			conn, err := listener.Accept()
			_ = listener.Close() // one connection, ever
			if err != nil {
				finish("accept: " + err.Error())
				return
			}
			header := make([]byte, 16)
			_ = conn.SetReadDeadline(time.Now().Add(10 * time.Second))
			if _, err := io.ReadFull(conn, header); err != nil || subtle.ConstantTimeCompare(header, capability) != 1 {
				_ = conn.Close()
				finish("local connection refused")
				return
			}
			_ = conn.SetReadDeadline(time.Time{})
			opened.Store(true)
			go func() { // channel -> local
				for data := range inbound {
					if _, err := conn.Write(data); err != nil {
						finish("local write: " + err.Error())
						return
					}
				}
			}()
			buffer := make([]byte, chunk) // local -> channel
			for {
				n, err := conn.Read(buffer)
				if n > 0 {
					for stream.BufferedAmount() > 2*1024*1024 {
						select {
						case <-lowWater:
						case <-time.After(200 * time.Millisecond):
						}
					}
					if sendErr := stream.Send(append([]byte(nil), buffer[:n]...)); sendErr != nil {
						finish("send: " + sendErr.Error())
						return
					}
				}
				if err != nil {
					finish("local closed")
					return
				}
			}
		}()
	}
	stream.OnOpen(func() {
		listening.Do(func() {
			if sctp := pc.SCTP(); sctp != nil && sctp.Transport() != nil && sctp.Transport().ICETransport() != nil {
				if pair, err := sctp.Transport().ICETransport().GetSelectedCandidatePair(); err == nil && pair != nil {
					emit(map[string]any{"event": "path", "local": pair.Local.Typ.String(), "remote": pair.Remote.Typ.String(),
						"remoteAddress": fmt.Sprintf("%s:%d", pair.Remote.Address, pair.Remote.Port)})
				}
			}
			serveLocal()
		})
	})

	// the link must be up in time
	go func() {
		time.Sleep(*wait)
		if !opened.Load() {
			finish("timeout")
		}
	}()

	// ---- the remote description: once ------------------------------------------------------------------------------------------
	gathered := func(description webrtc.SessionDescription) string {
		done := webrtc.GatheringCompletePromise(pc)
		if err := pc.SetLocalDescription(description); err != nil {
			fail("local description: " + err.Error())
		}
		select {
		case <-done:
		case <-time.After(15 * time.Second):
		}
		return pc.LocalDescription().SDP
	}
	if *role == "host" {
		offer, err := pc.CreateOffer(nil)
		if err != nil {
			fail("offer: " + err.Error())
		}
		emit(map[string]any{"event": "offer", "sdp": gathered(offer)})
	}
	var used atomic.Bool
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 0, 64*1024), 1<<20)
	go func() {
		for scanner.Scan() {
			var command struct {
				Cmd string `json:"cmd"`
				SDP string `json:"sdp"`
			}
			if json.Unmarshal(scanner.Bytes(), &command) != nil {
				continue
			}
			switch {
			case command.Cmd == "answer" && *role == "host", command.Cmd == "offer" && *role == "join":
				if used.Swap(true) {
					emit(map[string]any{"event": "error", "message": "the one-time session was already used"})
					continue
				}
				kind := webrtc.SDPTypeAnswer
				if command.Cmd == "offer" {
					kind = webrtc.SDPTypeOffer
				}
				if err := pc.SetRemoteDescription(webrtc.SessionDescription{Type: kind, SDP: command.SDP}); err != nil {
					fail("remote description: " + err.Error())
				}
				if kind == webrtc.SDPTypeOffer {
					answer, err := pc.CreateAnswer(nil)
					if err != nil {
						fail("answer: " + err.Error())
					}
					emit(map[string]any{"event": "answer", "sdp": gathered(answer)})
				}
			}
		}
		finish("stdin closed") // the parent is gone
	}()
	select {}
}
