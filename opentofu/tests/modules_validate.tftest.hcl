# OpenTofu native tests — structural validation for all modules.
# Run with: tofu test -test-directory=tests/

run "timescaledb_defaults" {
  command = plan

  module {
    source = "../modules/timescaledb"
  }

  variables {
    namespace        = "building-os"
    storage_class    = "standard"
    storage_size     = "10Gi"
    postgres_version = "16"
  }

  assert {
    condition     = output.service_name != ""
    error_message = "timescaledb module must export service_name"
  }

  assert {
    condition     = output.port == 5432
    error_message = "timescaledb must expose port 5432"
  }
}

run "minio_defaults" {
  command = plan

  module {
    source = "../modules/minio"
  }

  variables {
    namespace     = "building-os"
    storage_class = "standard"
    storage_size  = "20Gi"
    access_key    = "minio"
    secret_key    = "minio123"
  }

  assert {
    condition     = output.service_name != ""
    error_message = "minio module must export service_name"
  }

  assert {
    condition     = output.api_port == 9000
    error_message = "minio must expose API port 9000"
  }

  assert {
    condition     = output.console_port == 9001
    error_message = "minio must expose console port 9001"
  }
}

run "nats_defaults" {
  command = plan

  module {
    source = "../modules/nats"
  }

  variables {
    namespace    = "building-os"
    storage_class = "standard"
    storage_size  = "5Gi"
    replicas     = 1
  }

  assert {
    condition     = output.service_name != ""
    error_message = "nats module must export service_name"
  }

  assert {
    condition     = output.client_port == 4222
    error_message = "nats must expose client port 4222"
  }
}

run "oxigraph_defaults" {
  command = plan

  module {
    source = "../modules/oxigraph"
  }

  variables {
    namespace     = "building-os"
    storage_class = "standard"
    storage_size  = "5Gi"
    image_tag     = "v0.3.22"
  }

  assert {
    condition     = output.service_name != ""
    error_message = "oxigraph module must export service_name"
  }

  assert {
    condition     = output.http_port == 7878
    error_message = "oxigraph must expose HTTP port 7878"
  }
}

run "harbor_defaults" {
  command = plan

  module {
    source = "../modules/harbor"
  }

  variables {
    namespace      = "building-os"
    storage_class  = "standard"
    harbor_version = "1.14.0"
    admin_password = "Harbor12345"
    hostname       = "harbor.local"
  }

  assert {
    condition     = output.service_name != ""
    error_message = "harbor module must export service_name"
  }
}

run "monitoring_defaults" {
  command = plan

  module {
    source = "../modules/monitoring"
  }

  variables {
    namespace        = "monitoring"
    grafana_password = "admin"
    storage_class    = "standard"
    storage_size     = "5Gi"
  }

  assert {
    condition     = output.prometheus_service != ""
    error_message = "monitoring module must export prometheus_service"
  }

  assert {
    condition     = output.grafana_service != ""
    error_message = "monitoring module must export grafana_service"
  }
}
