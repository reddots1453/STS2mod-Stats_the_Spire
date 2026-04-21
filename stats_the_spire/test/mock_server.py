"""
Community Stats Mock API Server
================================
Serves test_data.json via HTTP endpoints matching the mod's API contract.
Logs all requests for debugging. Also captures POST /runs uploads to uploaded_runs.json.

Usage:
    python mock_server.py [--port 8080]

Endpoints:
    GET  /v1/stats/bulk?char=<char>&ver=<ver>       → BulkStatsBundle (full test_data.json)
    GET  /v1/stats/cards?cards=<id1>,<id2>           → Dict<string, CardStats>
    GET  /v1/stats/relics?relics=<id1>,<id2>         → Dict<string, RelicStats>
    GET  /v1/stats/events/<eventId>                  → EventStats
    GET  /v1/stats/encounters?ids=<id1>,<id2>        → Dict<string, EncounterStats>
    GET  /v1/meta/versions                           → List<string>
    POST /v1/runs                                    → 200 OK (saves payload)
"""

import json
import sys
import os
from http.server import HTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs
from datetime import datetime

PORT = 8080
DATA_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "test_data.json")
UPLOAD_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "uploaded_runs.json")

# Load test data
with open(DATA_FILE, "r", encoding="utf-8") as f:
    TEST_DATA = json.load(f)


def log(msg):
    ts = datetime.now().strftime("%H:%M:%S")
    print(f"[{ts}] {msg}")


class MockHandler(BaseHTTPRequestHandler):

    def _send_json(self, data, status=200):
        body = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(body)

    def _send_error(self, status, message):
        self._send_json({"error": message}, status)

    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path
        qs = parse_qs(parsed.query)

        log(f"GET {self.path}")

        # ── Bulk Stats ────────────────────────────────────
        if path == "/v1/stats/bulk":
            char = qs.get("char", [""])[0]
            ver = qs.get("ver", [""])[0]
            log(f"  → bulk stats for char={char} ver={ver}")
            log(f"  → returning {len(TEST_DATA['cards'])} cards, {len(TEST_DATA['relics'])} relics, "
                f"{len(TEST_DATA['events'])} events, {len(TEST_DATA['encounters'])} encounters")
            self._send_json(TEST_DATA)
            return

        # ── Card Stats (batch) ────────────────────────────
        if path == "/v1/stats/cards":
            card_ids = qs.get("cards", [""])[0].split(",")
            card_ids = [c.strip() for c in card_ids if c.strip()]
            result = {}
            for cid in card_ids:
                if cid in TEST_DATA["cards"]:
                    result[cid] = TEST_DATA["cards"][cid]
            log(f"  → cards query: {card_ids} → found {len(result)}")
            self._send_json(result)
            return

        # ── Relic Stats (batch) ───────────────────────────
        if path == "/v1/stats/relics":
            relic_ids = qs.get("relics", [""])[0].split(",")
            relic_ids = [r.strip() for r in relic_ids if r.strip()]
            result = {}
            for rid in relic_ids:
                if rid in TEST_DATA["relics"]:
                    result[rid] = TEST_DATA["relics"][rid]
            log(f"  → relics query: {relic_ids} → found {len(result)}")
            self._send_json(result)
            return

        # ── Event Stats (single) ─────────────────────────
        if path.startswith("/v1/stats/events/"):
            event_id = path.split("/v1/stats/events/")[1]
            if event_id in TEST_DATA["events"]:
                log(f"  → event {event_id} → found")
                self._send_json(TEST_DATA["events"][event_id])
            else:
                log(f"  → event {event_id} → NOT FOUND")
                self._send_error(404, f"Event '{event_id}' not found in test data")
            return

        # ── Encounter Stats (batch) ──────────────────────
        if path == "/v1/stats/encounters":
            enc_ids = qs.get("ids", [""])[0].split(",")
            enc_ids = [e.strip() for e in enc_ids if e.strip()]
            result = {}
            for eid in enc_ids:
                if eid in TEST_DATA["encounters"]:
                    result[eid] = TEST_DATA["encounters"][eid]
            log(f"  → encounters query: {enc_ids} → found {len(result)}")
            self._send_json(result)
            return

        # ── Meta: versions ───────────────────────────────
        if path == "/v1/meta/versions":
            versions = ["v0.99.1", "v0.99.0", "v0.98.5", "v0.98.0"]
            log(f"  → versions: {versions}")
            self._send_json(versions)
            return

        # ── Fallback ─────────────────────────────────────
        log(f"  → 404 NOT FOUND")
        self._send_error(404, f"Unknown endpoint: {path}")

    def do_POST(self):
        parsed = urlparse(self.path)
        path = parsed.path
        content_len = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(content_len).decode("utf-8") if content_len > 0 else ""

        log(f"POST {self.path} ({content_len} bytes)")

        # ── Run Upload ───────────────────────────────────
        if path == "/v1/runs":
            try:
                payload = json.loads(body)
                # Pretty-print summary
                char = payload.get("character", "?")
                win = payload.get("win", False)
                floor = payload.get("floor_reached", 0)
                asc = payload.get("ascension", 0)
                n_cards = len(payload.get("card_choices", []))
                n_events = len(payload.get("event_choices", []))
                n_encounters = len(payload.get("encounters", []))
                n_contributions = len(payload.get("contributions", []))

                log(f"  ★ RUN UPLOADED: {char} A{asc} → floor {floor} ({'WIN' if win else 'LOSS'})")
                log(f"    card_choices={n_cards} events={n_events} encounters={n_encounters} contributions={n_contributions}")
                log(f"    final_deck={len(payload.get('final_deck', []))} cards, "
                    f"final_relics={len(payload.get('final_relics', []))}")

                # Save to file
                uploads = []
                if os.path.exists(UPLOAD_FILE):
                    try:
                        with open(UPLOAD_FILE, "r", encoding="utf-8") as f:
                            uploads = json.load(f)
                    except Exception:
                        uploads = []
                uploads.append({
                    "received_at": datetime.now().isoformat(),
                    "payload": payload
                })
                with open(UPLOAD_FILE, "w", encoding="utf-8") as f:
                    json.dump(uploads, f, indent=2, ensure_ascii=False)
                log(f"    → saved to uploaded_runs.json (total: {len(uploads)} runs)")

                self._send_json({"status": "ok", "message": "Run received"})

            except json.JSONDecodeError as e:
                log(f"  → INVALID JSON: {e}")
                self._send_error(400, f"Invalid JSON: {e}")
            return

        log(f"  → 404 NOT FOUND")
        self._send_error(404, f"Unknown endpoint: {path}")

    def do_OPTIONS(self):
        """Handle CORS preflight."""
        self.send_response(200)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, X-Mod-Version")
        self.end_headers()

    def log_message(self, format, *args):
        """Suppress default logging (we use our own)."""
        pass


def main():
    port = PORT
    if "--port" in sys.argv:
        idx = sys.argv.index("--port")
        port = int(sys.argv[idx + 1])

    server = HTTPServer(("0.0.0.0", port), MockHandler)
    print("=" * 60)
    print(f"  Community Stats Mock API Server")
    print(f"  Listening on http://localhost:{port}/v1/")
    print(f"  Test data: {DATA_FILE}")
    print(f"  Upload log: {UPLOAD_FILE}")
    print("=" * 60)
    print(f"  Cards:      {len(TEST_DATA['cards'])}")
    print(f"  Relics:     {len(TEST_DATA['relics'])}")
    print(f"  Events:     {len(TEST_DATA['events'])}")
    print(f"  Encounters: {len(TEST_DATA['encounters'])}")
    print("=" * 60)
    print("  Waiting for requests... (Ctrl+C to stop)")
    print()

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nShutting down.")
        server.server_close()


if __name__ == "__main__":
    main()
