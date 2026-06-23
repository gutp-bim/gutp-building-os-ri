output "service_name" {
  value = kubernetes_service.api_server.metadata[0].name
}

output "port" {
  value = 80
}
