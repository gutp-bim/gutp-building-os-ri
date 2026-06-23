# monitoring module — replaces azure/bicep/modules/log-analytics-workspace.bicep,
#   app-insights.bicep, action-group.bicep, alert-rules.bicep.
# Deploys kube-prometheus-stack (Prometheus + Alertmanager + Grafana) via Helm.

locals {
  name   = "monitoring"
  labels = merge(var.labels, { "app.kubernetes.io/name" = "monitoring" })
}

resource "helm_release" "kube_prometheus_stack" {
  name       = "kube-prometheus-stack"
  namespace  = var.namespace
  repository = "https://prometheus-community.github.io/helm-charts"
  chart      = "kube-prometheus-stack"
  version    = var.chart_version

  values = [
    yamlencode({
      grafana = {
        enabled        = true
        adminPassword  = var.grafana_password
        persistence = {
          enabled      = true
          storageClass = var.storage_class
          size         = var.storage_size
        }
        service = { type = "ClusterIP" }
      }

      prometheus = {
        prometheusSpec = {
          storageSpec = {
            volumeClaimTemplate = {
              spec = {
                storageClassName = var.storage_class
                accessModes      = ["ReadWriteOnce"]
                resources        = { requests = { storage = var.storage_size } }
              }
            }
          }
          retention = "30d"
        }
        service = { type = "ClusterIP" }
      }

      alertmanager = {
        alertmanagerSpec = {
          storage = {
            volumeClaimTemplate = {
              spec = {
                storageClassName = var.storage_class
                accessModes      = ["ReadWriteOnce"]
                resources        = { requests = { storage = "2Gi" } }
              }
            }
          }
        }

        config = length(var.alert_email_receivers) > 0 ? {
          global = { smtp_smarthost = var.smtp_host, smtp_require_tls = false }
          receivers = concat(
            [{ name = "null" }],
            [for r in var.alert_email_receivers : {
              name = r.name
              email_configs = [{ to = r.email, from = "alertmanager@building-os.local" }]
            }]
          )
          route = {
            receiver = length(var.alert_email_receivers) > 0 ? var.alert_email_receivers[0].name : "null"
            group_by = ["alertname", "namespace"]
          }
        } : {}
      }

      nodeExporter = { enabled = true }
      kubeStateMetrics = { enabled = true }
    })
  ]
}
