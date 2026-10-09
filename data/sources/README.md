# Demo Open-Data Snapshots

These files are intentionally tiny curated MVP fixtures. They mirror the shapes
needed by the service while keeping the hackathon prototype quick to run.

Production ingestion should replace them with official open-data exports:

- VZD Valsts adrešu reģistrs open data for addresses.
- NKMP Valsts aizsargājamo nekustamo pieminekļu saraksts for cultural heritage checks.
- DAP/ĢeoLatvija protected nature WFS/WMS exports for nature area checks.

The importer fails when required geometry is missing instead of inventing
coordinates or polygons.
