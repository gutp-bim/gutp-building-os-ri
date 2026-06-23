# OpenTofu variables — example environment ("gutp").
# This is a REFERENCE CONFIGURATION. Replace harbor_hostname, image_registry,
# and iot_connectors with values for your own infrastructure.
# Sensitive values (passwords, keys) must be supplied via TF_VAR_* or a
# gitignored *.secrets.tfvars file — never commit secrets here.

environment   = "gutp"
namespace     = "building-os"
storage_class = "local-path"

harbor_hostname = "harbor.gutp.buildingos.local"
image_registry  = "harbor.gutp.buildingos.local/building-os"
image_tag       = "latest"

iot_connectors = [
  {
    name         = "BACnet"
    mqtt_host    = "hono-adapter-mqtt.building-os.svc.cluster.local"
    mqtt_port    = 1883
    topic_filter = "telemetry/DEFAULT_TENANT/bacnet-#"
  },
]

alert_email_receivers = []
