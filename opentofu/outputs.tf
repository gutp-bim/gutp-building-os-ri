output "api_server_url" {
  description = "API Server Kubernetes Service address"
  value       = "http://${module.api_server.service_name}.${var.namespace}.svc.cluster.local"
}

output "minio_endpoint" {
  description = "MinIO S3 API endpoint"
  value       = "http://${module.minio.service_name}:${module.minio.api_port}"
}

output "minio_console_url" {
  description = "MinIO console endpoint"
  value       = "http://${module.minio.service_name}:${module.minio.console_port}"
}

output "nats_url" {
  description = "NATS client endpoint"
  value       = "nats://${module.nats.service_name}:${module.nats.client_port}"
}

output "oxigraph_endpoint" {
  description = "OxiGraph SPARQL HTTP endpoint"
  value       = "http://${module.oxigraph.service_name}:${module.oxigraph.http_port}"
}

output "harbor_url" {
  description = "Harbor registry URL"
  value       = "https://${var.harbor_hostname}"
}

output "grafana_service" {
  description = "Grafana service name"
  value       = module.monitoring.grafana_service
}

output "prometheus_service" {
  description = "Prometheus service name"
  value       = module.monitoring.prometheus_service
}
