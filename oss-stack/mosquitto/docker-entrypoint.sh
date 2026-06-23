#!/bin/sh
# Generates /mosquitto/config/passwd at container startup from MQTT_DEVICES_PASSWORD.
# The passwd file is never stored in the repository; this script creates it at runtime.
set -e
# Recreate from scratch each start; -c fails to overwrite a leftover file on restart.
rm -f /mosquitto/config/passwd
mosquitto_passwd -b -c /mosquitto/config/passwd devices "${MQTT_DEVICES_PASSWORD:-buildingos-devices}"
# mosquitto_passwd creates the file as root with mode 0600; the broker runs as the
# unprivileged `mosquitto` user and must be able to read it.
chown mosquitto:mosquitto /mosquitto/config/passwd
chmod 0640 /mosquitto/config/passwd
exec /usr/sbin/mosquitto "$@"
