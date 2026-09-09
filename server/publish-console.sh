#!/bin/sh
set -eu
cat /tmp/ardui-upload-v7/part-* > /tmp/ArdUi-console-v1.pre7.zip
actual=$(sha256sum /tmp/ArdUi-console-v1.pre7.zip | cut -d' ' -f1)
test "$actual" = 82c7b1d169d0c3832ea7efdc5171669d417e96fe58c243f3acefc007987d8aae
install -d -m 755 /var/www/f.visnova.cn/ardui
install -m 644 /tmp/ArdUi-console-v1.pre7.zip /var/www/f.visnova.cn/ardui/ArdUi-console-v1.pre7.zip
install -m 644 /tmp/install-v1.pre7.ps1 /var/www/f.visnova.cn/ardui/install.ps1
install -m 644 /tmp/ardui-index-v1.pre7.html /var/www/f.visnova.cn/ardui/index.html
install -m 644 /tmp/arduiserver-v1.pre7.py /opt/arduiserver/arduiserver.py
systemctl restart arduiserver
rm -rf /tmp/ardui-upload-v7
rm -f /tmp/ArdUi-console-v1.pre7.zip /tmp/install-v1.pre7.ps1 /tmp/ardui-index-v1.pre7.html /tmp/arduiserver-v1.pre7.py
i=0
until curl -fsS http://127.0.0.1:8091/api/health; do
    i=$((i+1)); test "$i" -lt 10; sleep 1
done
