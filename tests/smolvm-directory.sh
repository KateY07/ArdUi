#!/bin/sh
# Directory-server check inside a smolvm microVM (bundled Alpine image, no Docker Hub needed). Run from a terminal outside the agent harness:
#   smolvm machine run --dns <host DNS> -v D:/1/ard2/ArdUi:/src:ro -- sh /src/tests/smolvm-directory.sh
# It installs Python from the Alpine mirrors, runs the server regression tests, starts the server under gunicorn and queries /api/health.
set -eu
apk add --quiet python3 py3-cryptography py3-gunicorn >/dev/null
echo "python: $(python3 --version)"

echo "== presence_test.py"
python3 /src/tests/presence_test.py

echo "== gunicorn + /api/health"
export ARDUI_DATABASE=/tmp/devices.sqlite3
cd /src/server
gunicorn --bind 127.0.0.1:8091 --workers 1 --threads 8 --timeout 30 arduiserver:application >/tmp/gunicorn.log 2>&1 &
pid=$!
for _ in 1 2 3 4 5 6 7 8 9 10; do
  python3 - <<'PY' && break || sleep 1
import json, sys, urllib.request
try:
    body = json.load(urllib.request.urlopen('http://127.0.0.1:8091/api/health', timeout=2))
except Exception:
    sys.exit(1)
print('health:', body)
sys.exit(0 if body.get('ok') is True else 1)
PY
done
python3 - <<'PY'
import urllib.request, urllib.error
# a client without the version header must be refused (426), as in production
req = urllib.request.Request('http://127.0.0.1:8091/api/register', data=b'{}', method='POST')
try:
    urllib.request.urlopen(req, timeout=3); print('FAIL: old client accepted'); raise SystemExit(1)
except urllib.error.HTTPError as e:
    print('no version header ->', e.code); raise SystemExit(0 if e.code == 426 else 1)
PY
kill "$pid" 2>/dev/null || true
echo "directory server checks done"
