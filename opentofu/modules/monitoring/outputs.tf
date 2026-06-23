output "prometheus_service" {
  value = "kube-prometheus-stack-prometheus"
}

output "grafana_service" {
  value = "kube-prometheus-stack-grafana"
}

output "alertmanager_service" {
  value = "kube-prometheus-stack-alertmanager"
}
