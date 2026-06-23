variable "namespace" { type = string }
variable "storage_class" { type = string }
variable "harbor_version" { type = string; default = "1.14.0" }
variable "admin_password" { type = string; sensitive = true }
variable "hostname" { type = string }
variable "tls_enabled" { type = bool; default = false }
variable "registry_storage_size" { type = string; default = "20Gi" }
variable "db_storage_size" { type = string; default = "5Gi" }
variable "labels" { type = map(string); default = {} }
