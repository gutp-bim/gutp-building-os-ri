variable "kubeconfig_path" {
  description = "Path to kubeconfig file"
  type        = string
  default     = "~/.kube/config"
}

variable "kube_context" {
  description = "Kubernetes context name"
  type        = string
  default     = null
}

variable "environment" {
  description = "Environment name (utokyo-eng2 / utokyo-eng10 / utokyo-eng13 / gutp)"
  type        = string
}

variable "namespace" {
  description = "Primary Kubernetes namespace for building-os components"
  type        = string
  default     = "building-os"
}

variable "storage_class" {
  description = "StorageClass for PersistentVolumeClaims (e.g. local-path for k3s)"
  type        = string
  default     = "local-path"
}

# ── Auth ──────────────────────────────────────────────────────────────────────
variable "keycloak_realm" {
  description = "Keycloak realm name"
  type        = string
  default     = "building-os"
}

variable "keycloak_client_id_api" {
  description = "Keycloak client ID for API Server"
  type        = string
}

variable "keycloak_client_id_web" {
  description = "Keycloak client ID for web-client"
  type        = string
}

# ── Database ──────────────────────────────────────────────────────────────────
variable "postgres_admin_password" {
  description = "PostgreSQL/TimescaleDB administrator password"
  type        = string
  sensitive   = true
}

# ── MinIO ─────────────────────────────────────────────────────────────────────
variable "minio_access_key" {
  description = "MinIO root access key"
  type        = string
  default     = "minio"
}

variable "minio_secret_key" {
  description = "MinIO root secret key"
  type        = string
  sensitive   = true
}

# ── Harbor ────────────────────────────────────────────────────────────────────
variable "harbor_hostname" {
  description = "Harbor registry hostname (must be resolvable from K8s nodes)"
  type        = string
}

variable "harbor_admin_password" {
  description = "Harbor admin password"
  type        = string
  sensitive   = true
}

# ── Container images ──────────────────────────────────────────────────────────
variable "image_registry" {
  description = "Container image registry prefix (e.g. harbor.local/building-os)"
  type        = string
}

variable "image_tag" {
  description = "Image tag to deploy for all building-os services"
  type        = string
  default     = "latest"
}

# ── Connector IoT sources ─────────────────────────────────────────────────────
variable "iot_connectors" {
  description = "List of IoT connector definitions. Each entry: {name, mqtt_host, mqtt_port, topic_filter}"
  type = list(object({
    name         = string
    mqtt_host    = string
    mqtt_port    = number
    topic_filter = string
  }))
  default = []
}

# ── Alert recipients ──────────────────────────────────────────────────────────
variable "alert_email_receivers" {
  description = "List of alert recipient objects: [{name, email}]"
  type = list(object({
    name  = string
    email = string
  }))
  default = []
}

variable "grafana_admin_password" {
  description = "Grafana admin password"
  type        = string
  sensitive   = true
  default     = "admin"
}
