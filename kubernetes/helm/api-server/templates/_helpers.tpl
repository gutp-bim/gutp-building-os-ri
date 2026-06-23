{{- define "api-server.fullname" -}}
{{- printf "%s-%s" .Release.Name .Chart.Name | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "api-server.labels" -}}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
app.kubernetes.io/name: api-server
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: building-os
{{- end }}

{{- define "api-server.selectorLabels" -}}
app.kubernetes.io/name: api-server
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}
