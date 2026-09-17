# CC-04 - Shared CSV source reading

`Conduit.Readers.Csv` is a net8 library with CsvHelper 33.0.1 as its only package.
It owns streaming CSV parsing, header and row-width validation, and cancellation.
Both Conduit CsvSource and IC CsvDataSourceReader call CsvRecordReader directly.
IC embeds the DLL through a project reference; local mode needs no Conduit server,
database, credentials, scheduling or HTTP connection.

The caller supplies a TextReader and owns its disposal. The shared reader leaves
it open, including after early stopping or failure. It passes cancellation into
async buffer reads and checks it between buffered records. Use one reader per
enumeration, with sequential calls; callers must abort on a read error.

## Host policies

| Concern | Conduit source | IC HR import / preview |
|---|---|---|
| Configuration and file access | Existing tenant CredentialProtector, source/sink credential-name resolution, configured path and encoding | Existing HRConnectionConfig, uploaded path and encoding |
| Headers | Optional; headerless input exposes col0, col1, etc. | Required; trimmed names |
| Whitespace and blanks | Retain whitespace; omit empty fields | Trim fields; whitespace/empty values become explicit null |
| Row conversion | Existing case-insensitive attributes, object class and source-ID precedence | Existing HR dictionaries and field names |
| Limits | Stop parsing at MaxObjects; no whole-file completeness claim | One extra row distinguishes exactly-at-limit from IsTruncated; max preview limit remains 5,000 |
| Failure | Exception stops enumeration; connection check returns failed result | Failed import/preview result has no partial records; header discovery throws a safe error; connection check returns false |

Conduit ID precedence remains configured IdColumn, objectGuid, id, EmployeeId,
then row-N. Empty values retain the previous fallback behavior. Encoding aliases
remain host-specific; this slice does not change saved credentials or configuration.

## Intentional corrections

- Duplicate/blank headers, malformed quotes and changing column counts are rejected
  in both hosts. Conduit previously tolerated malformed data and could overwrite
  duplicate columns. Existing malformed feeds must be corrected.
- Connection tests actually open and parse the header/first row. Empty/header-only
  files fail the sample check; ordinary source reads still return zero records.
  Conduit's success message explicitly describes the sample. A valid first row
  does not certify later rows, which are validated during enumeration.
- Cancellation no longer becomes a failed connection result or an empty field list.
  IC header discovery distinguishes an unreadable source from a valid empty source.
- CsvReadException has a named reason and safe text without record contents,
  parser context or inner exceptions. Host diagnostics log error type only.

Conduit is still a streaming pump: a later read failure can occur after an earlier
batch was written by the orchestrator. This change propagates failure, does not
roll back prior effects, and does not claim full-run atomicity or complete-read
support. Existing cursor/delete guards remain unchanged. IC completes its HR read
before import and refuses any failed result.

## Delivery and verification

Local builds use the latest sibling source. Hosted IC consumers must publish and
pin a reviewed Conduit revision through CC-04 first; source layout checks refuse
an older checkout without Conduit.Readers.Csv. No publication is part of this work.
The shared-mapping workflow also runs the dedicated offline CSV tests.

Final isolated verification passed 109 focused tests: 43 Conduit (22 reader and
21 source adapter), 66 IC (12 CSV adapter plus 54 HR/mapping regressions), zero
failures/skips. Five source-layout/MSBuild guard cases passed and both changed YAML
files parsed. All 118 combined changed sources match tested overlays, and Conduit
CSV, IC API and WebPortal contain the identical shared reader DLL. Referenced
projects compiled with warnings; this is not a full Conduit.Web/full-suite claim.
Verification receipt: `../_review/conduit-csv-reader-20260915/README.md`, relative
to this repository root. IC tracks acceptance and remaining work in
`Documentation/Quality/conduit-csv-reader.md`. Live configured feeds, sink effects
and hosted CI are unverified. No migration, commit, publication or deployment ran.

Next: compare and share one bounded AD directory read path, retaining host bind
credentials, tenant context, object-class scope and incomplete-read safeguards.
Other connectors, full orchestration hosting, shared HR/group field projection,
remote previews and atomic external effects remain separate.
