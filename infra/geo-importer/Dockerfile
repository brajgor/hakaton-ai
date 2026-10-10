FROM ghcr.io/osgeo/gdal:ubuntu-small-latest

COPY infra/geo-importer/import.sh /usr/local/bin/vai-drikstu-geo-import
RUN chmod +x /usr/local/bin/vai-drikstu-geo-import

ENTRYPOINT ["/usr/local/bin/vai-drikstu-geo-import"]
