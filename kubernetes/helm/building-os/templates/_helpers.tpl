{{/*
Expand the name of the chart.
*/}}
{{- define "building-os.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "building-os.labels" -}}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}

{{/*
Selector labels for a given component
*/}}
{{- define "building-os.selectorLabels" -}}
app.kubernetes.io/name: {{ include "building-os.name" . }}
app.kubernetes.io/component: {{ .component }}
{{- end }}

{{/*
Image reference helper
*/}}
{{- define "building-os.image" -}}
{{- $registry := .global.imageRegistry -}}
{{- if $registry -}}
{{ $registry }}/{{ .image.repository }}:{{ .image.tag }}
{{- else -}}
{{ .image.repository }}:{{ .image.tag }}
{{- end -}}
{{- end }}
