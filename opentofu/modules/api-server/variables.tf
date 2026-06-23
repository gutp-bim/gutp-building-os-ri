variable "namespace" { type = string }
variable "image" { type = string }
variable "replicas" { type = number; default = 1 }
variable "oxigraph_endpoint" { type = string }
variable "nats_url" { type = string }
variable "keycloak_realm" { type = string }
variable "keycloak_client_id" { type = string }
variable "keycloak_issuer_uri" { type = string; default = "" }
variable "labels" { type = map(string); default = {} }
