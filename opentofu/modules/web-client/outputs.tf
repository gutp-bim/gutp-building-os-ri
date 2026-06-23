output "web_client_service" {
  value = kubernetes_service.web_client.metadata[0].name
}
