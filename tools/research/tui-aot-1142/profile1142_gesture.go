package panel

// Research-only probes injected into the pinned Go snapshot. Native checks,
// parsers, detail state/generation rules and terminal layout remain unchanged.
import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"
)

type profile1142Ingress struct {
	start time.Time
	action string
}

type profile1142Gesture struct {
	mu sync.Mutex
	Sequence int `json:"sequence"`
	Action string `json:"action"`
	ItemID int `json:"itemId"`
	Generation uint64 `json:"generation"`
	Columns int `json:"columns"`
	Rows int `json:"rows"`
	Outcome string `json:"outcome"`
	GestureToReadyFrameMs float64 `json:"gestureToReadyFrameMs"`
	FrameBytes int `json:"frameBytes"`
	Stages map[string]float64 `json:"stagesMs"`
	start time.Time
	response time.Time
	issued bool
	complete bool
}

type profile1142State struct {
	file *os.File
	sequence int
	current *profile1142Gesture
	lastSelection int
	lastReady bool
}

func profile1142Initialize(rt *runtime) error {
	path := os.Getenv("TWIG_1142_GO_PROBE")
	if path == "" { return nil }
	if os.Getenv("TWIG_USER_HOME") != "C:/Users/dangreen/.twig-tui-epic-tracking" {
		return fmt.Errorf("research probe requires approved metadata home")
	}
	approved, err := filepath.Abs("C:/Users/dangreen/projects/twig-worktrees/tui-aot-epic")
	if err != nil || !sameWorkspace(rt.cfg.Cwd, approved) { return fmt.Errorf("research probe requires approved workspace") }
	file, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil { return fmt.Errorf("create fresh numeric gesture evidence: %w", err) }
	rt.profile1142 = &profile1142State{file: file}
	return nil
}

func profile1142Receive(ev event) *profile1142Ingress {
	if os.Getenv("TWIG_1142_GO_PROBE") == "" || ev.kind != evKey { return nil }
	action := ""
	if ev.key.MatchString("enter") { action = "enter-detail" }
	if ev.key.MatchString("r", "R", "shift+r") { action = "R-cache-detail" }
	if action == "" { return nil }
	return &profile1142Ingress{start: time.Now(), action: action}
}

func profile1142Begin(rt *runtime, ingress *profile1142Ingress) {
	if rt.profile1142 == nil || ingress == nil { return }
	if rt.closing || rt.reconnectRequired || rt.mode != "bench" || rt.form != nil || rt.configuration != nil || rt.showBrowserHelp { return }
	id := 0
	if ingress.action == "enter-detail" && !rt.detail.active {
		if index := rt.browser.selectedIndex(); index >= 0 { id = rt.browser.rows[index].node.ID }
	} else if ingress.action == "R-cache-detail" && rt.detail.active && !rt.detail.loading {
		id = rt.detail.id
	}
	if id != 1141 && id != 1142 && id != 1143 { return }
	rt.profile1142.sequence++
	gesture := &profile1142Gesture{Sequence: rt.profile1142.sequence, Action: ingress.action, ItemID: id,
		Columns: rt.size.cols, Rows: rt.size.rows, Stages: map[string]float64{}, start: ingress.start}
	gesture.Stages["input-reader-to-dispatch"] = float64(time.Since(ingress.start).Nanoseconds()) / 1e6
	rt.profile1142.current = gesture
}

func profile1142Issue(rt *runtime, generation uint64) *profile1142Gesture {
	if rt.profile1142 == nil || rt.profile1142.current == nil { return nil }
	gesture := rt.profile1142.current
	if gesture.ItemID != rt.detail.id || gesture.complete { return nil }
	gesture.Generation = generation
	gesture.issued = true
	return gesture
}

func profile1142Stage(gesture *profile1142Gesture, name string, started time.Time) {
	if gesture == nil { return }
	elapsed := float64(time.Since(started).Nanoseconds()) / 1e6
	gesture.mu.Lock()
	gesture.Stages[name] = elapsed
	gesture.mu.Unlock()
}

func profile1142Queued(gesture *profile1142Gesture) {
	if gesture == nil { return }
	gesture.mu.Lock()
	gesture.response = time.Now()
	gesture.mu.Unlock()
}

func profile1142Consumer(rt *runtime, generation uint64) *profile1142Gesture {
	if rt.profile1142 == nil || rt.profile1142.current == nil { return nil }
	gesture := rt.profile1142.current
	if gesture.Generation != generation || !gesture.issued || gesture.complete { return nil }
	gesture.mu.Lock()
	if !gesture.response.IsZero() { gesture.Stages["response-event-queue"] = float64(time.Since(gesture.response).Nanoseconds()) / 1e6 }
	gesture.mu.Unlock()
	return gesture
}

func profile1142Write(rt *runtime, value any) {
	if rt.profile1142 == nil { return }
	// Evidence errors are reported, never change product authority/result behavior.
	if err := json.NewEncoder(rt.profile1142.file).Encode(value); err != nil { fmt.Fprintln(os.Stderr, "numeric research evidence write failed") }
}

func profile1142Paint(rt *runtime, started time.Time, bytes int, writeError error) {
	if rt.profile1142 == nil { return }
	ready := !rt.closing && !rt.reconnectRequired && rt.browser.snapshot != nil && rt.mode == "bench"
	selected := rt.browser.selectedID
	if ready != rt.profile1142.lastReady || selected != rt.profile1142.lastSelection {
		profile1142Write(rt, struct {
			Kind string `json:"kind"`
			Ready bool `json:"ready"`
			SelectedItemID int `json:"selectedItemId"`
			Columns int `json:"columns"`
			Rows int `json:"rows"`
		}{"browser-frame-state", ready, selected, rt.size.cols, rt.size.rows})
		rt.profile1142.lastReady, rt.profile1142.lastSelection = ready, selected
	}
	gesture := rt.profile1142.current
	if gesture == nil || gesture.complete || !gesture.issued || gesture.Generation != rt.detail.gen { return }
	if rt.reconnectRequired { gesture.Outcome = "authority-refused" } else if rt.detail.err != nil { gesture.Outcome = "detail-refused" } else if rt.detail.active && !rt.detail.loading && rt.detail.document != nil {
		gesture.Outcome = "ready-frame-written"
	} else { return }
	if writeError != nil { gesture.Outcome = "frame-write-error" }
	profile1142Stage(gesture, "ready-frame-layout-and-stdout-write", started)
	gesture.GestureToReadyFrameMs = float64(time.Since(gesture.start).Nanoseconds()) / 1e6
	gesture.FrameBytes = bytes
	gesture.complete = true
	profile1142Write(rt, gesture)
}

func profile1142Close(rt *runtime) {
	if rt.profile1142 == nil { return }
	if gesture := rt.profile1142.current; gesture != nil && !gesture.complete {
		gesture.Outcome = "closed-before-ready-frame"
		gesture.GestureToReadyFrameMs = float64(time.Since(gesture.start).Nanoseconds()) / 1e6
		profile1142Write(rt, gesture)
	}
	_ = rt.profile1142.file.Close()
}
