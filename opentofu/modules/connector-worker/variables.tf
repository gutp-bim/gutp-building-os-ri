variable "namespace" { type = string }
variable "image" { type = string }
variable "replicas" { type = number; default = 1 }
variable "nats_url" { type = string }
variable "minio_endpoint" { type = string }
variable "minio_access_key" { type = string }
variable "minio_secret_key" { type = string; sensitive = true }
variable "cold_storage_bucket" { type = string; default = "cold-data" }
variable "labels" { type = map(string); default = {} }
