# NATS JetStream module — replaces azure/bicep Event Hub (data bus tier).
# Deployed as a StatefulSet for durable JetStream storage.

locals {
  name   = "nats"
  labels = merge(var.labels, { "app.kubernetes.io/name" = "nats" })
}

resource "kubernetes_config_map" "nats" {
  metadata {
    name      = "${local.name}-config"
    namespace = var.namespace
    labels    = local.labels
  }

  data = {
    "nats.conf" = <<-EOF
      port: 4222
      http_port: 8222

      jetstream {
        store_dir: /data
        max_memory_store: 1GB
        max_file_store: ${var.storage_size}
      }

      cluster {
        name: building-os-cluster
        port: 6222

        routes: [
          ${join("\n          ", [for i in range(var.replicas) : "nats://${local.name}-${i}.${local.name}.${var.namespace}.svc.cluster.local:6222"])}
        ]
      }
    EOF
  }
}

resource "kubernetes_persistent_volume_claim" "nats" {
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

resource "kubernetes_stateful_set" "nats" {
  metadata {
    name      = local.name
    namespace = var.namespace
    labels    = local.labels
  }

  spec {
    service_name = local.name
    replicas     = var.replicas

    selector {
      match_labels = { "app.kubernetes.io/name" = "nats" }
    }

    template {
      metadata {
        labels = local.labels
        annotations = {
          "prometheus.io/scrape" = "true"
          "prometheus.io/port"   = "8222"
          "prometheus.io/path"   = "/metrics"
        }
      }

      spec {
        container {
          name  = "nats"
          image = "nats:${var.image_tag}"
          args  = ["-c", "/etc/nats/nats.conf"]

          port { container_port = 4222; name = "client" }
          port { container_port = 6222; name = "cluster" }
          port { container_port = 8222; name = "monitor" }

          volume_mount {
            name       = "config"
            mount_path = "/etc/nats"
          }

          volume_mount {
            name       = "data"
            mount_path = "/data"
          }

          resources {
            requests = { memory = "128Mi", cpu = "50m" }
            limits   = { memory = "512Mi", cpu = "250m" }
          }

          liveness_probe {
            http_get {
              path = "/healthz"
              port = 8222
            }
            initial_delay_seconds = 10
            period_seconds        = 10
          }
        }

        volume {
          name = "config"
          config_map { name = kubernetes_config_map.nats.metadata[0].name }
        }

        volume {
          name = "data"
          persistent_volume_claim {
            claim_name = kubernetes_persistent_volume_claim.nats.metadata[0].name
          }
        }
      }
    }
  }
}

resource "kubernetes_service" "nats" {
  metadata {
    name      = local.name
    namespace = var.namespace
    labels    = local.labels
  }

  spec {
    selector   = { "app.kubernetes.io/name" = "nats" }
    cluster_ip = "None"

    port { port = 4222; name = "client" }
    port { port = 6222; name = "cluster" }
    port { port = 8222; name = "monitor" }
  }
}
