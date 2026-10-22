# Unreleased

- Preserve P2 qualifiers when importing an existing CycloneDX BOM into SIT.Create.
- Infer the P2 type for imports and recognize it when matching SW360 components.
- Pass the first HTTP(S) distribution reference to new SW360 releases as binary
  download metadata without treating the artifact as a source attachment.
- Add import-to-release-payload and serialization regression coverage.
