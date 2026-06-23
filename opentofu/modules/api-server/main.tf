# api-server module — replaces azure/bicep/modules/api-server.bicep
# Deploys the Building OS API server as a Kubernetes Deployment.

locals {
  name   = "api-server"
  labels = merge(var.labels, { "app.kubernetes.io/name" = "api-server" })
}

resource "kubernetes_secret" "api_server" {
  metadata {
    name      = "${local.name}-env"
    namespace = var.namespace
    labels    = local.labels
  }

  data = {
    NATS_URL                    = var.nats_url
    OXIGRAPH_ENDPOINT           = var.oxigraph_endpoint
    KEYCLOAK_REALM              = var.keycloak_realm
    KEYCLOAK_CLIENT_ID          = var.keycloak_client_id
    KEYCLOAK_ISSUER_URI         = var.keycloak_issuer_uri
  }
}

resource "kubernetes_deployment" "api_server" {
  metadata {
    name      = local.name
    namespace = var.namespace
    labels    = local.labels
  }

  spec {
    replicas = var.replicas

    selector {
      match_labels = { "app.kubernetes.io/name" = "api-server" }
    }

    template {
      metadata {
        labels = local.labels
        annotations = {
          "prometheus.io/scrape" = "true"
          "prometheus.io/port"   = "8080"
          "prometheus.io/path"   = "/metrics"
        }
      }

      spec {
        container {
          name  = "api-server"
          image = var.image

          port {
            container_port = 8080
            name           = "http"
          }

          env_from {
            secret_ref { name = kubernetes_secret.api_server.metadata[0].name }
          }

          env {
            name  = "ASPNETCORE_ENVIRONMENT"
            value = "Production"
          }

          env {
            name  = "PORT"
            value = "8080"
          }

          resources {
            requests = { memory = "256Mi", cpu = "100m" }
            limits   = { memory = "512Mi", cpu = "500m" }
          }

          liveness_probe {
            http_get {
              path = "/health/liveness"
              port = 8080
            }
            initial_delay_seconds = 20
            period_seconds        = 15
          }

          readiness_probe {
            http_get {
              path = "/health/readiness"
              port = 8080
            }
            initial_delay_seconds = 10
            period_seconds        = 10
          }
        }
      }
    }
  }
}

resource "kubernetes_service" "api_server" {
  metadata {
    name      = local.name
    namespace = var.namespace
    labels    = local.labels
  }

  spec {
    selector = { "app.kubernetes.io/name" = "api-server" }

    port {
      port        = 80
      target_port = 8080
      name        = "http"
    }
  }
}
