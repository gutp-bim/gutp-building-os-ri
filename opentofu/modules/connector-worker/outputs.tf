output "deployment_name" {
  value = kubernetes_deployment.connector_worker.metadata[0].name
}
