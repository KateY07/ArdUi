#!/bin/sh
set -eu
cat /tmp/ardui-upload/part-000 /tmp/ardui-upload/part-001 /tmp/ardui-upload/part-002 \
    /tmp/ardui-upload/part-003 /tmp/ardui-upload/part-004 /tmp/ardui-upload/part-005 \
    /tmp/ardui-upload/part-006 /tmp/ardui-upload/part-007 /tmp/ardui-upload/part-008 \
    > /tmp/ArdUi-console-v1pre.1.zip
actual=$(sha256sum /tmp/ArdUi-console-v1pre.1.zip | cut -d' ' -f1)
test "$actual" = bc51288a641303ec47e1f182336e9896858539ff498ec050e91721e54c1dcc60
install -m 644 /tmp/ArdUi-console-v1pre.1.zip /var/www/f.visnova.cn/ardui/ArdUi-console-v1pre.1.zip
install -m 644 /tmp/install.ps1 /var/www/f.visnova.cn/ardui/install.ps1
install -m 644 /tmp/arduiserver.py /opt/arduiserver/arduiserver.py
systemctl restart arduiserver
rm -rf /tmp/ardui-upload
rm -f /tmp/ArdUi-console-v1pre.1.zip
curl -fsS http://127.0.0.1:8091/api/health
