# connector-worker module — replaces azure/bicep/modules/functions.bicep
# Deploys the Building OS connector/consumer workers as a Kubernetes Deployment.

locals {
  name   = "connector-worker"
  labels = merge(var.labels, { "app.kubernetes.io/name" = "connector-worker" })
}

resource "kubernetes_secret" "connector_worker" {
  metadata {
    name      = "${local.name}-env"
    namespace = var.namespace
    labels    = local.labels
  }

  data = {
    NATS_URL               = var.nats_url
    MINIO_ENDPOINT         = var.minio_endpoint
    MINIO_ACCESS_KEY       = var.minio_access_key
    MINIO_SECRET_KEY       = var.minio_secret_key
    COLD_STORAGE_BUCKET    = var.cold_storage_bucket
  }
}

resource "kubernetes_deployment" "connector_worker" {
  metadata {
    name      = local.name
    namespace = var.namespace
    labels    = local.labels
  }

  spec {
    replicas = var.replicas

    selector {
      match_labels = { "app.kubernetes.io/name" = "connector-worker" }
    }

    template {
      metadata {
        labels = local.labels
        annotations = {
          "prometheus.io/scrape" = "true"
          "prometheus.io/port"   = "8081"
          "prometheus.io/path"   = "/metrics"
        }
      }

      spec {
        container {
          name  = "connector-worker"
          image = var.image

          env_from {
            secret_ref { name = kubernetes_secret.connector_worker.metadata[0].name }
          }

          env {
            name  = "DOTNET_ENVIRONMENT"
            value = "Production"
          }

          port {
            container_port = 8081
            name           = "metrics"
          }

          resources {
            requests = { memory = "128Mi", cpu = "100m" }
            limits   = { memory = "512Mi", cpu = "500m" }
          }

          liveness_probe {
            http_get {
              path = "/health"
              port = 8081
            }
            initial_delay_seconds = 20
            period_seconds        = 15
          }
        }
      }
    }
  }
}
