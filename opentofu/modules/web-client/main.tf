# web-client module — replaces azure/bicep/modules/static-web-app.bicep
# Deploys the Next.js web-client as a Kubernetes Deployment. The admin console screens
# (users/groups management) now live in web-client's (admin) workspace.

locals {
  name_web = "web-client"
  labels   = merge(var.labels, { "app.kubernetes.io/component" = "frontend" })
}

# ── Web Client ────────────────────────────────────────────────────────────────

resource "kubernetes_deployment" "web_client" {
  metadata {
    name      = local.name_web
    namespace = var.namespace
    labels    = merge(local.labels, { "app.kubernetes.io/name" = "web-client" })
  }

  spec {
    replicas = var.replicas

    selector {
      match_labels = { "app.kubernetes.io/name" = "web-client" }
    }

    template {
      metadata {
        labels = merge(local.labels, { "app.kubernetes.io/name" = "web-client" })
      }

      spec {
        container {
          name  = "web-client"
          image = var.image

          port { container_port = 3000; name = "http" }

          env {
            name  = "NEXT_PUBLIC_API_BASE_URL"
            value = var.api_url
          }
          env {
            name  = "NEXT_PUBLIC_KEYCLOAK_CLIENT_ID"
            value = var.keycloak_client_id
          }
          env {
            name  = "NEXT_PUBLIC_KEYCLOAK_ISSUER"
            value = var.keycloak_issuer_uri
          }

          resources {
            requests = { memory = "128Mi", cpu = "50m" }
            limits   = { memory = "256Mi", cpu = "200m" }
          }
        }
      }
    }
  }
}

resource "kubernetes_service" "web_client" {
  metadata {
    name      = local.name_web
    namespace = var.namespace
    labels    = merge(local.labels, { "app.kubernetes.io/name" = "web-client" })
  }

  spec {
    selector = { "app.kubernetes.io/name" = "web-client" }

    port { port = 80; target_port = 3000; name = "http" }
  }
}
