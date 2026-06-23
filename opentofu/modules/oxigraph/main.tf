# OxiGraph module — replaces azure/bicep/modules/digital-twins.bicep
# Deploys OxiGraph SPARQL 1.1 triplestore as a StatefulSet.

locals {
  name   = "oxigraph"
  labels = merge(var.labels, { "app.kubernetes.io/name" = "oxigraph" })
}

resource "kubernetes_persistent_volume_claim" "oxigraph" {
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

resource "kubernetes_stateful_set" "oxigraph" {
  metadata {
    name      = local.name
    namespace = var.namespace
    labels    = local.labels
  }

  spec {
    service_name = local.name
    replicas     = 1

    selector {
      match_labels = { "app.kubernetes.io/name" = "oxigraph" }
    }

    template {
      metadata { labels = local.labels }

      spec {
        container {
          name  = "oxigraph"
          image = "ghcr.io/oxigraph/oxigraph:${var.image_tag}"
          args  = ["serve", "--location", "/data", "--bind", "0.0.0.0:7878"]

          port {
            container_port = 7878
            name           = "http"
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
              path = "/protocol"
              port = 7878
            }
            initial_delay_seconds = 10
            period_seconds        = 15
          }
        }

        volume {
          name = "data"
          persistent_volume_claim {
            claim_name = kubernetes_persistent_volume_claim.oxigraph.metadata[0].name
          }
        }
      }
    }
  }
}

resource "kubernetes_service" "oxigraph" {
  metadata {
    name      = local.name
    namespace = var.namespace
    labels    = local.labels
  }

  spec {
    selector   = { "app.kubernetes.io/name" = "oxigraph" }
    cluster_ip = "None"

    port {
      port        = 7878
      target_port = 7878
      name        = "http"
    }
  }
}
