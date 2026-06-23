# Hono module — replaces azure/bicep/modules/iot-hub.bicep (one per connector).
# Deploys Eclipse Hono dispatch router + adapters via Helm.
# MQTT adapter exposes port 1883; northbound messages forwarded to NATS.

locals {
  name   = "hono"
  labels = merge(var.labels, { "app.kubernetes.io/name" = "hono" })
}

resource "helm_release" "hono" {
  name       = local.name
  namespace  = var.namespace
  repository = "https://eclipse.org/packages/charts"
  chart      = "hono"
  version    = var.hono_version

  values = [
    yamlencode({
      messagingNetworkTypes = ["amqp"]

      adapters = {
        mqtt = {
          enabled = true
          hono = {
            mqtt = {
              insecurePortEnabled = true
              insecurePort        = 1883
            }
          }
        }
        http = { enabled = false }
        coap = { enabled = false }
        amqp = { enabled = false }
        lora = { enabled = false }
      }

      prometheus = { createInstance = false }

      deviceRegistryExample = {
        enabled             = true
        type                = "file"
        addExampleData      = false
      }
    })
  ]
}

# Hono northbound → NATS は ConnectorWorker の AmqpIngressWorker（Hono AMQP 1.0 Northbound）が担う。
# connector-worker の Helm リリースで HONO_AMQP_HOST="${local.name}-adapter-amqp" を設定して有効化すること
# （旧 hono-nats-bridge Python Deployment は廃止）。
# 注: MQTT_HOST は汎用 MQTT ブローカー（Mosquitto）シナリオ用で、Hono 取込には使わない。
