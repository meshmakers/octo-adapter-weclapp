# Container image for the WeClapp Mesh Adapter.
# Pattern: octo-mesh-adapter/src/MeshAdapter/Dockerfile (aspnet base for the
# WebAdapterBuilder host, native-platform SDK build, OCTO_*_VERSION guards so
# r-tag builds with deliberately unset version args fall back to
# Directory.Build.props instead of passing empty -p values — NU1015).
# The OCR system libraries (libtesseract/libpng12) from the mesh-adapter image
# are intentionally omitted: this adapter's pipelines do not use
# PdfOcrExtractionNode. Add them back if OCR nodes are ever deployed here.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# Build on the native build platform (IL is portable) so multi-arch builds don't
# emulate the SDK; the runtime arch is selected by the `base`/`final` stages.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG OCTO_PRIVATE_NUGET_SERVICE
ARG OCTO_PRIVATE_NUGET_CERTIFICATE
ARG OCTO_VERSION
ARG OCTO_CORE_LIB_VERSION
ARG OCTO_COMM_VERSION

COPY ${OCTO_PRIVATE_NUGET_CERTIFICATE} /usr/local/share/ca-certificates/nugetca.crt
RUN chmod 644 /usr/local/share/ca-certificates/nugetca.crt && update-ca-certificates

WORKDIR /src
COPY . .
ENV NUGET_XMLDOC_MODE=none
WORKDIR "/src/src/AdapterMeshWeClapp"
RUN dotnet build "AdapterMeshWeClapp.csproj" -c $BUILD_CONFIGURATION -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} ${OCTO_VERSION:+-p:OctoVersion=${OCTO_VERSION}} ${OCTO_CORE_LIB_VERSION:+-p:OctoCoreLibVersion=${OCTO_CORE_LIB_VERSION}} ${OCTO_COMM_VERSION:+-p:OctoCommVersion=${OCTO_COMM_VERSION}} -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "AdapterMeshWeClapp.csproj" -c $BUILD_CONFIGURATION -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} ${OCTO_VERSION:+-p:OctoVersion=${OCTO_VERSION}} ${OCTO_CORE_LIB_VERSION:+-p:OctoCoreLibVersion=${OCTO_CORE_LIB_VERSION}} ${OCTO_COMM_VERSION:+-p:OctoCommVersion=${OCTO_COMM_VERSION}} -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Meshmakers.Octo.Communication.MeshAdapter.WeClapp.dll"]
