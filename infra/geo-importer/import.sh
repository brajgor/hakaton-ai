#!/usr/bin/env bash
set -euo pipefail

: "${POSTGIS_DSN:?POSTGIS_DSN must be set}"
SOURCE_ROOT="${DATA_SOURCES_PATH:-/sources}"
WORK_DIR="/tmp/vai-drikstu-geo-import"
# Optional scoping for faster local imports:
#   CADASTRE_ARCHIVE_PATTERN - extended regex matched against cadastral archive paths (default: all)
#   IMPORT_PARCELS           - set to "false" to keep already imported raw_cadastral_parcels
ARCHIVE_PATTERN="${CADASTRE_ARCHIVE_PATTERN:-.}"
IMPORT_PARCELS="${IMPORT_PARCELS:-true}"

mkdir -p "$WORK_DIR"

import_layer() {
  local table="$1"
  local dataset="$2"
  local layer="$3"

  echo "Importing ${table} from ${dataset} (${layer})"
  ogr2ogr -f PostgreSQL "$POSTGIS_DSN" "$dataset" "$layer" \
    -nln "$table" \
    -overwrite \
    -lco GEOMETRY_NAME=geom \
    -lco LAUNDER=YES \
    -lco SPATIAL_INDEX=GIST \
    -mapFieldType Real=String \
    -nlt PROMOTE_TO_MULTI \
    -t_srs EPSG:4326 \
    -progress
}

build_archive_vrt() {
  local layer_name="$1"
  local shape_name="$2"
  local archive="$3"
  local output="$4"
  local source_count=0

  printf '%s\n' '<OGRVRTDataSource>' > "$output"
  printf '  <OGRVRTUnionLayer name="%s">\n' "$layer_name" >> "$output"
  printf '%s\n' '    <FieldStrategy>Union</FieldStrategy>' >> "$output"

  while IFS= read -r member; do
    [ -z "$member" ] && continue
    source_count=$((source_count + 1))
    printf '    <OGRVRTLayer name="%s_%s">\n' "$layer_name" "$source_count" >> "$output"
    printf '      <SrcDataSource>/vsizip/%s/%s</SrcDataSource>\n' "$archive" "$member" >> "$output"
    printf '      <SrcLayer>%s</SrcLayer>\n' "$shape_name" >> "$output"
    printf '%s\n' '    </OGRVRTLayer>' >> "$output"
  done < <(unzip -Z1 "$archive" | grep "/${shape_name}\\.shp$" || true)

  printf '%s\n' '  </OGRVRTUnionLayer>' >> "$output"
  printf '%s\n' '</OGRVRTDataSource>' >> "$output"

  if [ "$source_count" -eq 0 ]; then
    echo "No ${shape_name}.shp layers found in cadastral archives." >&2
    exit 1
  fi

  echo "Prepared ${layer_name} VRT with ${source_count} source layers from $(basename "$archive")."
}

import_cadastral_geometry() {
  local table="$1"
  local layer_name="$2"
  local shape_name="$3"
  local first_batch=true
  local batch=0

  while IFS= read -r archive; do
    batch=$((batch + 1))
    local vrt_path="$WORK_DIR/${layer_name}-${batch}.vrt"
    build_archive_vrt "$layer_name" "$shape_name" "$archive" "$vrt_path"

    echo "Importing ${table}, archive batch ${batch}: $(basename "$archive")"
    if [ "$first_batch" = true ]; then
      ogr2ogr -f PostgreSQL "$POSTGIS_DSN" "$vrt_path" "$layer_name" \
        -nln "$table" \
        -overwrite \
        -lco GEOMETRY_NAME=geom \
        -lco LAUNDER=YES \
        -lco SPATIAL_INDEX=GIST \
        -mapFieldType Real=String \
        -nlt PROMOTE_TO_MULTI \
        -t_srs EPSG:4326 \
        -progress
      first_batch=false
    else
      ogr2ogr -f PostgreSQL "$POSTGIS_DSN" "$vrt_path" "$layer_name" \
        -nln "$table" \
        -append \
        -mapFieldType Real=String \
        -nlt PROMOTE_TO_MULTI \
        -t_srs EPSG:4326 \
        -progress
    fi
  done < <(find "$SOURCE_ROOT/data" -type f -name '*_kk_shp.zip' -print | grep -E "$ARCHIVE_PATTERN" | sort)
}

import_layer raw_nature_areas \
  "$SOURCE_ROOT/Ipasi_aizsargajamas_dabas_teritorijas/IADT_pamatteritorijas.shp" \
  IADT_pamatteritorijas

if [ "$IMPORT_PARCELS" = "true" ]; then
  import_cadastral_geometry raw_cadastral_parcels cadastral_parcels KKParcel
fi
import_cadastral_geometry raw_cadastral_parcel_parts cadastral_parcel_parts KKParcelPart

import_layer raw_riga_zoning \
  "$SOURCE_ROOT/downloaded/rigas-teritorijas-planojums/extracted/funkcionalais_zonejums.gpkg" \
  funkcionalais_zonejums

import_layer raw_riga_encumbrances \
  "$SOURCE_ROOT/downloaded/rigas-teritorijas-planojums/extracted/apgrutinatas_teritorijas.gpkg" \
  apgrutinatas_teritorijas

import_layer raw_rvc_core \
  "$SOURCE_ROOT/downloaded/rigas-vesturiskais-centrs/extracted/Robezas.gpkg" \
  RVC_NR_7442

import_layer raw_rvc_protection \
  "$SOURCE_ROOT/downloaded/rigas-vesturiskais-centrs/extracted/Robezas.gpkg" \
  RVC_NR_8327

import_layer raw_rvc_character_areas \
  "$SOURCE_ROOT/downloaded/rigas-vesturiskais-centrs/extracted/Apbuves_veidosanas_pamatnosacijumi.gpkg" \
  Kulturvesturiski_nozimiga_autentiska_pilsetvides_teritorija

echo "Geospatial import completed."
