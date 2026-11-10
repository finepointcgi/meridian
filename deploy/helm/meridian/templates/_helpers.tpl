{{- define "meridian.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "meridian.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{- define "meridian.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "meridian.selectorLabels" -}}
app.kubernetes.io/name: {{ include "meridian.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "meridian.labels" -}}
helm.sh/chart: {{ include "meridian.chart" . }}
{{ include "meridian.selectorLabels" . }}
app.kubernetes.io/version: {{ .Values.image.tag | default .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: meridian
{{- end }}

{{- define "meridian.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "meridian.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{- define "meridian.image" -}}
{{- if .Values.image.digest -}}
{{ .Values.image.repository }}@{{ .Values.image.digest }}
{{- else -}}
{{ .Values.image.repository }}:{{ .Values.image.tag | default .Chart.AppVersion }}
{{- end -}}
{{- end }}

{{/* Secret holding MERIDIAN_DB_PASSWORD and SSO client secrets created by this chart. */}}
{{- define "meridian.secretName" -}}
{{- printf "%s-secrets" (include "meridian.fullname" .) }}
{{- end }}

{{/* Database host/port/name/user, from the bundled chart or externalDatabase. */}}
{{- define "meridian.dbHost" -}}
{{- if .Values.postgresql.enabled }}{{ printf "%s-postgresql" .Release.Name }}{{ else }}{{ required "externalDatabase.host is required when postgresql.enabled=false and secrets.existingSecret is empty" .Values.externalDatabase.host }}{{ end }}
{{- end }}

{{/* Where the database password comes from (name + key). */}}
{{- define "meridian.dbPasswordSecret" -}}
{{- if .Values.postgresql.enabled -}}
name: {{ .Values.postgresql.auth.existingSecret | default (printf "%s-postgresql" .Release.Name) }}
key: password
{{- else -}}
name: {{ include "meridian.secretName" . }}
key: MERIDIAN_DB_PASSWORD
{{- end -}}
{{- end }}

{{- define "meridian.scaledOut" -}}
{{- if or .Values.autoscaling.enabled (gt (int .Values.replicaCount) 1) }}true{{ end }}
{{- end }}
