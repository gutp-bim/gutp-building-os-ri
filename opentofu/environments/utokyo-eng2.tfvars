# OpenTofu variables — example environment ("utokyo-eng2").
# This is a REFERENCE CONFIGURATION for an on-premises Kubernetes deployment.
# Replace all *.buildingos.local hostnames with your own domain.
#
# Sensitive values (passwords, keys) must be supplied via:
#   - TF_VAR_<name> environment variables
#   - -var-file=<secrets.tfvars> (gitignored)
#   - CI: GitHub Actions secrets → OPENTOFU_VARS

environment   = "utokyo-eng2"
namespace     = "building-os"
storage_class = "local-path"

# Harbor registry — previously Azure Container Registry (UTokyoBuildingOSEng2ContainerRegistry)
harbor_hostname = "harbor.eng2.buildingos.local"
image_registry  = "harbor.eng2.buildingos.local/building-os"
image_tag       = "latest"

# Keycloak realm — previously Azure AD tenant
# keycloak_realm, keycloak_client_id_api, keycloak_client_id_web
# are set via TF_VAR_* or a secrets tfvars file

# IoT connectors — previously externalConnectorMessageQueues in Bicep
# Each connector maps to an Eclipse Hono MQTT adapter subscription.
iot_connectors = [
  {
    name         = "BACnet"
    mqtt_host    = "hono-adapter-mqtt.building-os.svc.cluster.local"
    mqtt_port    = 1883
    topic_filter = "telemetry/DEFAULT_TENANT/bacnet-#"
  },
  {
    name         = "Behavior"
    mqtt_host    = "hono-adapter-mqtt.building-os.svc.cluster.local"
    mqtt_port    = 1883
    topic_filter = "telemetry/DEFAULT_TENANT/behavior-#"
  },
  {
    name         = "Electric"
    mqtt_host    = "hono-adapter-mqtt.building-os.svc.cluster.local"
    mqtt_port    = 1883
    topic_filter = "telemetry/DEFAULT_TENANT/electric-#"
  },
  {
    name         = "Environmental"
    mqtt_host    = "hono-adapter-mqtt.building-os.svc.cluster.local"
    mqtt_port    = 1883
    topic_filter = "telemetry/DEFAULT_TENANT/env-#"
  },
  {
    name         = "Hvac"
    mqtt_host    = "hono-adapter-mqtt.building-os.svc.cluster.local"
    mqtt_port    = 1883
    topic_filter = "telemetry/DEFAULT_TENANT/hvac-#"
  },
]

alert_email_receivers = []
