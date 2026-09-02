{{/*
  Kubernetes EnvVar.value is typed string, so every env value must render as a
  YAML string scalar. Without `| quote`, values that look like YAML scalars of
  another type (numbers, booleans, "null", "yes/no") get interpreted as that
  type and the apiserver rejects the Deployment with
  "cannot unmarshal number into Go struct field EnvVar.value of type string".
  Specifically: blueprint-seeded RtIds like 670000000000000000000002 are 24
  decimal digits and parse as numbers. `| quote` everywhere — env values are
  always strings.
*/}}
{{- define "octo-mesh.system-env" -}}
- name: OCTO_SYSTEM__DATABASEHOST
  value: {{ .Values.clusterDependencies.mongodbHost | quote }}
{{- if .Values.clusterDependencies.mongodbReplicaSet }}
- name: OCTO_SYSTEM__REPLICASETNAME
  value: {{ .Values.clusterDependencies.mongodbReplicaSet | quote }}
{{- end }}
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_SYSTEM__DATABASEUSERPASSWORD" "value" .Values.secrets.databaseUser "legacyKey" "databaseUser" "context" .) }}
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_SYSTEM__ADMINUSERPASSWORD" "value" .Values.secrets.databaseAdmin "legacyKey" "databaseAdmin" "context" .) }}
{{- end }}

{{- define "octo-mesh.broker-env" -}}
- name: {{ printf "%s__BROKERHOST" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.rabbitMqHost | quote }}
- name: {{ printf "%s__BROKERUSERNAME" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.rabbitMqUser | quote }}
{{ include "octo-mesh.secretEnv" (dict "envName" (printf "%s__BROKERPASSWORD" (upper .name)) "value" .global.Values.secrets.rabbitmq "legacyKey" "rabbitmq" "context" .global) }}
{{- end }}

{{- define "octo-mesh.streamdata-env" -}}
# Instance-level kill switch for StreamData. Read by
# StreamDataInstanceConfiguration (root "StreamData" config section, hence
# the fixed env-var name without a service prefix). Defaults to false so
# the feature is opt-in per cluster.
- name: OCTO_STREAMDATA__ENABLED
  value: {{ .global.Values.clusterDependencies.streamDataEnabled | quote }}
- name: {{ printf "%s__STREAMDATAHOST" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.streamDataHost | quote }}
- name: {{ printf "%s__STREAMDATAUSER" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.streamDataUser | quote }}
{{ include "octo-mesh.secretEnv" (dict "envName" (printf "%s__STREAMDATAPASSWORD" (upper .name)) "value" .global.Values.secrets.streamDataPassword "legacyKey" "streamDataPassword" "context" .global) }}
{{- end }}


{{- define "octo-mesh.env" -}}
- name: ASPNETCORE_URLS
  value: "http://+:80"
{{- $name := "OCTO_ADAPTER" }}
{{ include "octo-mesh.system-env" . }}
{{ include "octo-mesh.broker-env" (dict "global" . "name" $name) }}
{{ include "octo-mesh.streamdata-env" (dict "global" . "name" $name) }}
- name: OCTO_ADAPTER__INSTANCEPREFIX
  value: {{ .Values.instancePrefix | quote }}
- name: OCTO_ADAPTER__TENANTID
  value: {{ .Values.tenantId | quote }}
- name: OCTO_ADAPTER__COMMUNICATIONCONTROLLERSERVICESURI
  value: {{ .Values.communicationControllerServiceUri | quote }}
- name: OCTO_ADAPTER__ADAPTERCKTYPEID
  value: "System.Communication/Adapter"
- name: OCTO_ADAPTER__ADAPTERRTID
  value: {{ .Values.adapterRtId | quote }}
- name: OCTO_ADAPTER__REPORTINGSERVICEURL
  value: {{ .Values.reportingServiceUri | quote }}
{{/*
  AB#5072 -- the adapter's OUTBOUND credential: the identity it presents when it
  connects to /{tenantId}/adapterHub. Read by AdapterOptions in
  octo-communication-sdk, which every SDK-based adapter shares, so this block is
  deliberately byte-identical across the adapter charts.

  `authUri` needs no new plumbing: the communication operator writes it into the
  context values of EVERY workload (WorkloadContextValuesBuilder). It must be the
  PUBLIC issuer address -- OIDC discovery runs against it and the communication
  controller validates the issuer of the resulting token.

  Unlike octo-mesh-adapter there is no OCTO_ADAPTER__AUTHORITYURL beside this one:
  that key is the INBOUND issuer that secured FromHttpRequest@2 routes accept, and
  it lives on MeshAdapterConfiguration, which this adapter does not have.
*/}}
{{- if .Values.authUri }}
- name: OCTO_ADAPTER__ISSUERURI
  value: {{ .Values.authUri | quote }}
{{- end }}
{{/*
  Client id of the adapter's own confidential OAuth client -- the
  ServiceAccountConfiguration the communication controller provisions per adapter
  (AB#5027) and projects onto this exact path as a ValueOverride at deploy time.
  Omitted when unset: AdapterOptions.IsEnabled is `IssuerUri && ClientId`, so an
  unconfigured adapter acquires no token and connects anonymously -- which is what
  the whole fleet does today and must keep doing on upgrade.
*/}}
{{- if .Values.serviceAccountClientId }}
- name: OCTO_ADAPTER__CLIENTID
  value: {{ .Values.serviceAccountClientId | quote }}
{{- end }}
{{/*
  🔴 Secret-flagged on the controller side, so the operator materialises it into
  {release}-octo-secrets and hands this path a {valueFrom: {secretKeyRef: ...}} map
  instead of the plaintext -- octo-mesh.secretEnv accepts both shapes, exactly as
  secrets.rabbitmq does. Guarded by `if` because secretEnv FAILS on an empty value
  (deliberate for the four mandatory cluster secrets) while this one is optional by
  design -- see the ClientId note above.
*/}}
{{- if .Values.secrets.serviceAccountClientSecret }}
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_ADAPTER__CLIENTSECRET" "value" .Values.secrets.serviceAccountClientSecret "legacyKey" "serviceAccountClientSecret" "context" .) }}
{{- end }}
{{- end }}