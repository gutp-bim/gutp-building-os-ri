output "service_name" {
  value = kubernetes_service.nats.metadata[0].name
}

output "client_port" {
  value = 4222
}

output "monitor_port" {
  value = 8222
}
