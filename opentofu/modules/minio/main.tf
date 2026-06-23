# MinIO module — replaces azure/bicep/modules/blob-storage.bicep
# Deploys MinIO single-node (dev) or multi-node (prod) object storage.

locals {
  name   = "minio"
  labels = merge(var.labels, { "app.kubernetes.io/name" = "minio" })
}

resource "kubernetes_secret" "minio" {
  metadata {
    name      = "${local.name}-credentials"
    namespace = var.namespace
    labels    = local.labels
  }

  data = {
    MINIO_ROOT_USER     = var.access_key
    MINIO_ROOT_PASSWORD = var.secret_key
  }
}

resource "kubernetes_persistent_volume_claim" "minio" {
  metadata {
    name      = "${local.name}-data"
    namespace = var.namespace
    labels    = local.labels
  }

  spec {
    access_modes       = ["ReadWriteOnce"]
    storage_class_name = var.storage_class

    resources {
      requests = { storage = var.storage_size }
    }
  }
}

resource "kubernetes_deployment" "minio" {
  metadata {
    name      = local.name
    namespace = var.namespace
    labels    = local.labels
  }

  spec {
    replicas = 1

    selector {
      match_labels = { "app.kubernetes.io/name" = "minio" }
    }

    template {
      metadata {
        labels = local.labels
        annotations = {
          "prometheus.io/scrape" = "true"
          "prometheus.io/port"   = "9000"
          "prometheus.io/path"   = "/minio/health/live"
        }
      }

      spec {
        container {
          name  = "minio"
          image = "minio/minio:${var.image_tag}"
          args  = ["server", "/data", "--console-address", ":9001"]

          port {
            container_port = 9000
            name           = "api"
          }
          port {
            container_port = 9001
            name           = "console"
          }

          env_from {
            secret_ref { name = kubernetes_secret.minio.metadata[0].name }
          }

          volume_mount {
            name       = "data"
            mount_path = "/data"
          }

          resources {
            requests = { memory = "256Mi", cpu = "100m" }
            limits   = { memory = "1Gi",   cpu = "500m" }
          }

          liveness_probe {
            http_get {
              path = "/minio/health/live"
              port = 9000
            }
            initial_delay_seconds = 30
            period_seconds        = 20
          }
        }

        volume {
          name = "data"
          persistent_volume_claim {
            claim_name = kubernetes_persistent_volume_claim.minio.metadata[0].name
          }
        }
      }
    }
  }
}

resource "kubernetes_service" "minio" {
  metadata {
    name      = local.name
    namespace = var.namespace
    labels    = local.labels
  }

  spec {
    selector = { "app.kubernetes.io/name" = "minio" }

    port {
      port        = 9000
      target_port = 9000
      name        = "api"
    }

    port {
      port        = 9001
      target_port = 9001
      name        = "console"
    }
  }
}
