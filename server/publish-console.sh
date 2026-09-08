#!/bin/sh
set -eu
cat /tmp/ardui-upload-v5/part-* > /tmp/ArdUi-console-v1.pre5.zip
actual=$(sha256sum /tmp/ArdUi-console-v1.pre5.zip | cut -d' ' -f1)
test "$actual" = 26973c22ef87e505fd3565c6320a2af76b19a878c1beaf20c5ef6586962bdb9f
install -d -m 755 /var/www/f.visnova.cn/ardui
install -m 644 /tmp/ArdUi-console-v1.pre5.zip /var/www/f.visnova.cn/ardui/ArdUi-console-v1.pre5.zip
install -m 644 /tmp/install-v1.pre5.ps1 /var/www/f.visnova.cn/ardui/install.ps1
install -m 644 /tmp/ardui-index-v1.pre5.html /var/www/f.visnova.cn/ardui/index.html
install -m 644 /tmp/arduiserver-v1.pre5.py /opt/arduiserver/arduiserver.py
systemctl restart arduiserver
rm -rf /tmp/ardui-upload-v5
rm -f /tmp/ArdUi-console-v1.pre5.zip /tmp/install-v1.pre5.ps1 /tmp/ardui-index-v1.pre5.html /tmp/arduiserver-v1.pre5.py
i=0
until curl -fsS http://127.0.0.1:8091/api/health; do
    i=$((i+1)); test "$i" -lt 10; sleep 1
done
