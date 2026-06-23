# Harbor module — replaces azure/bicep/modules/container-registry.bicep
# Deploys Harbor v2 via the official Helm chart.

locals {
  name   = "harbor"
  labels = merge(var.labels, { "app.kubernetes.io/name" = "harbor" })
}

resource "helm_release" "harbor" {
  name       = local.name
  namespace  = var.namespace
  repository = "https://helm.goharbor.io"
  chart      = "harbor"
  version    = var.harbor_version

  values = [
    yamlencode({
      expose = {
        type = "ingress"
        ingress = {
          hosts = {
            core = var.hostname
          }
        }
        tls = {
          enabled    = var.tls_enabled
          certSource = "auto"
        }
      }

      externalURL = "${var.tls_enabled ? "https" : "http"}://${var.hostname}"

      harborAdminPassword = var.admin_password

      persistence = {
        enabled      = true
        resourcePolicy = "keep"
        persistentVolumeClaim = {
          registry  = { storageClass = var.storage_class, size = var.registry_storage_size }
          jobservice = { storageClass = var.storage_class, size = "1Gi" }
          database  = { storageClass = var.storage_class, size = var.db_storage_size }
          redis     = { storageClass = var.storage_class, size = "1Gi" }
          trivy     = { storageClass = var.storage_class, size = "5Gi" }
        }
      }

      metrics = { enabled = true }
    })
  ]
}
