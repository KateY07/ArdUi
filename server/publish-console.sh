#!/bin/sh
set -eu
cat /tmp/ardui-upload-v4/part-* > /tmp/ArdUi-console-v1.pre4.zip
actual=$(sha256sum /tmp/ArdUi-console-v1.pre4.zip | cut -d' ' -f1)
test "$actual" = cf7b3bd5cece730210778fd91c4fbc5367d67801c210718bcf5618725a1baf06
install -d -m 755 /var/www/f.visnova.cn/ardui
install -m 644 /tmp/ArdUi-console-v1.pre4.zip /var/www/f.visnova.cn/ardui/ArdUi-console-v1.pre4.zip
install -m 644 /tmp/install-v1.pre4.ps1 /var/www/f.visnova.cn/ardui/install.ps1
install -m 644 /tmp/ardui-index-v1.pre4.html /var/www/f.visnova.cn/ardui/index.html
install -m 644 /tmp/arduiserver-v1.pre4.py /opt/arduiserver/arduiserver.py
systemctl restart arduiserver
rm -rf /tmp/ardui-upload-v4
rm -f /tmp/ArdUi-console-v1.pre4.zip /tmp/install-v1.pre4.ps1 /tmp/ardui-index-v1.pre4.html /tmp/arduiserver-v1.pre4.py
i=0
until curl -fsS http://127.0.0.1:8091/api/health; do
    i=$((i+1)); test "$i" -lt 10; sleep 1
done
