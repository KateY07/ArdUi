#!/bin/sh
set -eu
cat /tmp/ardui-upload-v6/part-* > /tmp/ArdUi-console-v1.pre6.zip
actual=$(sha256sum /tmp/ArdUi-console-v1.pre6.zip | cut -d' ' -f1)
test "$actual" = dd7eb9310e0f38686f16aac38ea47dfc32b5a832c824587d793baf33b4086e92
install -d -m 755 /var/www/f.visnova.cn/ardui
install -m 644 /tmp/ArdUi-console-v1.pre6.zip /var/www/f.visnova.cn/ardui/ArdUi-console-v1.pre6.zip
install -m 644 /tmp/install-v1.pre6.ps1 /var/www/f.visnova.cn/ardui/install.ps1
install -m 644 /tmp/ardui-index-v1.pre6.html /var/www/f.visnova.cn/ardui/index.html
install -m 644 /tmp/arduiserver-v1.pre6.py /opt/arduiserver/arduiserver.py
systemctl restart arduiserver
rm -rf /tmp/ardui-upload-v6
rm -f /tmp/ArdUi-console-v1.pre6.zip /tmp/install-v1.pre6.ps1 /tmp/ardui-index-v1.pre6.html /tmp/arduiserver-v1.pre6.py
i=0
until curl -fsS http://127.0.0.1:8091/api/health; do
    i=$((i+1)); test "$i" -lt 10; sleep 1
done
