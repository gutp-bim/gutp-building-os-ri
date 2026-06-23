variable "namespace" { type = string }
variable "grafana_password" { type = string; sensitive = true }
variable "storage_class" { type = string }
variable "storage_size" { type = string; default = "10Gi" }
variable "chart_version" { type = string; default = "60.0.0" }
variable "smtp_host" { type = string; default = "localhost:25" }
variable "alert_email_receivers" {
  type = list(object({ name = string; email = string }))
  default = []
}
variable "labels" { type = map(string); default = {} }
