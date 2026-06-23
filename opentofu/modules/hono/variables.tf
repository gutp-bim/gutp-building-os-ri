variable "namespace" { type = string }
variable "nats_url" { type = string }
variable "hono_version" { type = string; default = "2.6.0" }
variable "connectors" {
  type = list(object({
    name         = string
    mqtt_host    = string
    mqtt_port    = number
    topic_filter = string
  }))
  default = []
}
variable "labels" { type = map(string); default = {} }
