locals {
  common_labels = {
    "app.kubernetes.io/managed-by" = "opentofu"
    "app.kubernetes.io/part-of"    = "building-os"
    environment                    = var.environment
  }
}

resource "kubernetes_namespace" "building_os" {
  metadata {
    name   = var.namespace
    labels = local.common_labels
  }
}

resource "kubernetes_namespace" "monitoring" {
  metadata {
    name   = "monitoring"
    labels = local.common_labels
  }
}

# ── MinIO (replaces Azure Blob Storage) ──────────────────────────────────────
module "minio" {
  source = "./modules/minio"

  namespace     = kubernetes_namespace.building_os.metadata[0].name
  storage_class = var.storage_class
  storage_size  = "50Gi"
  access_key    = var.minio_access_key
  secret_key    = var.minio_secret_key
  labels        = local.common_labels
}

# ── NATS JetStream (replaces Azure Event Hub / Data Bus) ─────────────────────
module "nats" {
  source = "./modules/nats"

  namespace     = kubernetes_namespace.building_os.metadata[0].name
  storage_class = var.storage_class
  storage_size  = "10Gi"
  replicas      = 1
  labels        = local.common_labels
}

# ── OxiGraph (replaces Azure Digital Twins) ──────────────────────────────────
module "oxigraph" {
  source = "./modules/oxigraph"

  namespace     = kubernetes_namespace.building_os.metadata[0].name
  storage_class = var.storage_class
  storage_size  = "5Gi"
  image_tag     = "v0.3.22"
  labels        = local.common_labels
}

# ── Harbor (replaces Azure Container Registry) ───────────────────────────────
module "harbor" {
  source = "./modules/harbor"

  namespace      = kubernetes_namespace.building_os.metadata[0].name
  storage_class  = var.storage_class
  harbor_version = "1.14.0"
  admin_password = var.harbor_admin_password
  hostname       = var.harbor_hostname
  labels         = local.common_labels
}

# ── Eclipse Hono (replaces Azure IoT Hub) ────────────────────────────────────
module "hono" {
  source = "./modules/hono"

  namespace     = kubernetes_namespace.building_os.metadata[0].name
  nats_url      = "nats://${module.nats.service_name}:${module.nats.client_port}"
  connectors    = var.iot_connectors
  labels        = local.common_labels

  depends_on = [module.nats]
}

# ── API Server (replaces Azure App Service) ───────────────────────────────────
module "api_server" {
  source = "./modules/api-server"

  namespace          = kubernetes_namespace.building_os.metadata[0].name
  image              = "${var.image_registry}/building-os-api-server:${var.image_tag}"
  oxigraph_endpoint  = "http://${module.oxigraph.service_name}:${module.oxigraph.http_port}"
  nats_url           = "nats://${module.nats.service_name}:${module.nats.client_port}"
  keycloak_realm     = var.keycloak_realm
  keycloak_client_id = var.keycloak_client_id_api
  labels             = local.common_labels

  depends_on = [module.oxigraph, module.nats]
}

# ── Connector Workers (replaces Azure Functions) ──────────────────────────────
module "connector_worker" {
  source = "./modules/connector-worker"

  namespace          = kubernetes_namespace.building_os.metadata[0].name
  image              = "${var.image_registry}/building-os-connector-worker:${var.image_tag}"
  nats_url           = "nats://${module.nats.service_name}:${module.nats.client_port}"
  minio_endpoint     = "http://${module.minio.service_name}:${module.minio.api_port}"
  minio_access_key   = var.minio_access_key
  minio_secret_key   = var.minio_secret_key
  labels             = local.common_labels

  depends_on = [module.nats, module.minio]
}

# ── Web Client (replaces Azure Static Web App) ───────────────────────────────
module "web_client" {
  source = "./modules/web-client"

  namespace          = kubernetes_namespace.building_os.metadata[0].name
  image              = "${var.image_registry}/building-os-web-client:${var.image_tag}"
  api_url            = "http://${module.api_server.service_name}"
  keycloak_client_id = var.keycloak_client_id_web
  labels             = local.common_labels

  depends_on = [module.api_server]
}

# ── Prometheus + Grafana (replaces Log Analytics + App Insights) ──────────────
module "monitoring" {
  source = "./modules/monitoring"

  namespace        = kubernetes_namespace.monitoring.metadata[0].name
  grafana_password = var.grafana_admin_password
  storage_class    = var.storage_class
  storage_size     = "10Gi"
  labels           = local.common_labels
}
