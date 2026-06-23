output "service_name" {
  value = kubernetes_service.oxigraph.metadata[0].name
}

output "http_port" {
  value = 7878
}
