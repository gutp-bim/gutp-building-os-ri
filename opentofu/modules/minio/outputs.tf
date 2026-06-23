output "service_name" {
  value = kubernetes_service.minio.metadata[0].name
}

output "api_port" {
  value = 9000
}

output "console_port" {
  value = 9001
}
